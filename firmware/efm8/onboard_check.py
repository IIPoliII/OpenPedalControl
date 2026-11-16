"""Read the experimental controller's profile and raw/cooked axes over USB.

No SET_FEATURE or flash operations are sent. Useful after the first hardware
test and after a power cycle to check the profile saved with the desktop app.
"""

import argparse
import ctypes as c
from ctypes import wintypes as w
import json
import time

from hid_windows import Api, enumerate_devices


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seconds", type=float, default=1)
    args = parser.parse_args()
    if not 0 < args.seconds <= 60:
        parser.error("sample duration must be between zero and 60 seconds")
    matches = [d for d in enumerate_devices() if d["usb_id"] in ("1209:0001", "1209:0002")
               and d["product"] == "OpenPedalControl Pedals" and d["usage_page"] == 1
               and d["input_bytes"] in (7, 8, 9, 13, 17) and d["feature_bytes"] in (45, 57)]
    if len(matches) != 1:
        parser.exit(1, f"Expected one experimental OpenPedalControl controller, found {len(matches)}\n")
    api = Api()
    api.h.HidD_GetFeature.argtypes = [w.HANDLE, c.c_void_p, w.ULONG]
    api.h.HidD_GetInputReport.argtypes = [w.HANDLE, c.c_void_p, w.ULONG]
    handle = api.open(matches[0]["path"], 0xC0000000)
    try:
        feature_size = matches[0]["feature_bytes"]
        profile_id = 0 if feature_size == 57 else 2
        profile = c.create_string_buffer(bytes([profile_id]) + bytes(feature_size - 1), feature_size)
        if not api.h.HidD_GetFeature(handle, profile, feature_size):
            raise c.WinError(c.get_last_error())
        raw = profile.raw
        if raw[:6] != bytes([profile_id]) + b"OPC\x01\x03":
            raise ValueError("unexpected profile header")
        print(json.dumps(dict(stage="profile-read", profile_hex=raw.hex(), device=matches[0])), flush=True)
        until = time.monotonic() + args.seconds
        previous = None
        while True:
            if feature_size == 57:
                report = c.create_string_buffer(57)
                if not api.h.HidD_GetFeature(handle, report, 57):
                    raise c.WinError(c.get_last_error())
                data = b"\x03" + report.raw[45:57]
                report_id = 3
            else:
                use_snapshot = matches[0]["input_bytes"] in (7, 8)
                length, report_id = (45, 3) if use_snapshot else (13, 1)
                report = c.create_string_buffer(bytes([report_id]) + bytes(length - 1), length)
                getter = api.h.HidD_GetFeature if use_snapshot else api.h.HidD_GetInputReport
                if not getter(handle, report, length):
                    raise c.WinError(c.get_last_error())
                data = report.raw[:13]
            if data[0] != report_id:
                raise ValueError("unexpected input report ID")
            values = [int.from_bytes(data[i:i + 2], "little") for i in range(1, 13, 2)]
            if any(value > 4095 for value in values):
                raise ValueError("axis outside the supported range")
            if values != previous:
                print(json.dumps(dict(stage="sample", cooked_xyz=values[:3], raw_lc123=values[3:])), flush=True)
                previous = values
            if time.monotonic() >= until:
                break
            time.sleep(min(0.2, max(0, until - time.monotonic())))
    finally:
        api.k.CloseHandle(handle)


if __name__ == "__main__":
    main()
