"""Request the original bootloader from the experimental firmware, without flash writes.

Requires the maintenance command introduced in trial version 1.02.
The physical FW/reset method remains available independently of this command.
"""

import ctypes as c
from ctypes import wintypes as w
import time

from hid_windows import Api, enumerate_devices, is_bootloader


def main():
    matches = [d for d in enumerate_devices() if d["usb_id"] in ("1209:0001", "1209:0002", "10C4:8B02")
               and d["product"] == "OpenPedalControl Pedals"
               and d["input_bytes"] in (7, 8, 9, 13, 17) and d["feature_bytes"] in (7, 45, 57)]
    if len(matches) != 1:
        raise RuntimeError(f"expected one experimental controller, found {len(matches)}")
    api = Api()
    api.h.HidD_GetFeature.argtypes = [w.HANDLE, c.c_void_p, w.ULONG]
    api.h.HidD_SetFeature.argtypes = [w.HANDLE, c.c_void_p, w.ULONG]
    handle = api.open(matches[0]["path"], 0xC0000000)
    try:
        size = matches[0]["feature_bytes"]
        report_id = 0 if size in (7, 57) else 2
        profile = c.create_string_buffer(bytes([report_id]) + bytes(size - 1), size)
        if not api.h.HidD_GetFeature(handle, profile, size):
            raise c.WinError(c.get_last_error())
        if profile.raw[:6] != bytes([report_id]) + b"OPC\x01\x03":
            raise RuntimeError("unexpected profile protocol")
        command = c.create_string_buffer(bytes([report_id]) + b"BOOT\x01" + bytes(size - 6), size)
        if not api.h.HidD_SetFeature(handle, command, size):
            raise c.WinError(c.get_last_error())
    finally:
        api.k.CloseHandle(handle)
    for _ in range(25):
        time.sleep(0.2)
        if sum(is_bootloader(d) for d in enumerate_devices()) == 1:
            print("Original HE SIM PEDALS bootloader detected. No flash erase/program command sent.")
            return
    raise RuntimeError("bootloader did not appear; use the physical FW/reset method")


if __name__ == "__main__":
    main()
