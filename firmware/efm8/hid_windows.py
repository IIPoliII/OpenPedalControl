"""Small Windows HID transport with overlapped I/O; no third-party packages.

Enumeration uses metadata handles only. The transport opens exclusively the
observed HE SIM PEDALS bootloader report shape, never the normal joystick.
"""

import ctypes as c
from ctypes import wintypes as w
import os


class GUID(c.Structure):
    _fields_ = [("a", w.DWORD), ("b", w.WORD), ("d", w.WORD), ("e", w.BYTE * 8)]


class Interface(c.Structure):
    _fields_ = [("size", w.DWORD), ("guid", GUID), ("flags", w.DWORD), ("reserved", c.c_void_p)]


class Attributes(c.Structure):
    _fields_ = [("size", w.ULONG), ("vid", w.WORD), ("pid", w.WORD), ("version", w.WORD)]


class Overlapped(c.Structure):
    _fields_ = [("internal", c.c_size_t), ("internal_high", c.c_size_t),
                ("offset", w.DWORD), ("offset_high", w.DWORD), ("event", w.HANDLE)]


class Api:
    def __init__(self):
        if os.name != "nt":
            raise OSError("USB transport currently requires Windows")
        self.k = k = c.WinDLL("kernel32", use_last_error=True)
        self.h = h = c.WinDLL("hid", use_last_error=True)
        self.s = s = c.WinDLL("setupapi", use_last_error=True)
        k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, c.c_void_p, w.DWORD, w.DWORD, c.c_void_p]
        k.CreateFileW.restype = w.HANDLE
        k.CloseHandle.argtypes = [w.HANDLE]
        k.CreateEventW.argtypes = [c.c_void_p, w.BOOL, w.BOOL, w.LPCWSTR]
        k.CreateEventW.restype = w.HANDLE
        k.ReadFile.argtypes = [w.HANDLE, c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.POINTER(Overlapped)]
        k.WriteFile.argtypes = k.ReadFile.argtypes
        k.WaitForSingleObject.argtypes = [w.HANDLE, w.DWORD]
        k.GetOverlappedResult.argtypes = [w.HANDLE, c.POINTER(Overlapped), c.POINTER(w.DWORD), w.BOOL]
        k.CancelIoEx.argtypes = [w.HANDLE, c.POINTER(Overlapped)]
        h.HidD_GetHidGuid.argtypes = [c.POINTER(GUID)]
        h.HidD_GetAttributes.argtypes = [w.HANDLE, c.POINTER(Attributes)]
        h.HidD_GetPreparsedData.argtypes = [w.HANDLE, c.POINTER(c.c_void_p)]
        h.HidD_FreePreparsedData.argtypes = [c.c_void_p]
        h.HidP_GetCaps.argtypes = [c.c_void_p, c.c_void_p]
        h.HidD_FlushQueue.argtypes = [w.HANDLE]
        for name in ("HidD_GetProductString", "HidD_GetManufacturerString", "HidD_GetSerialNumberString"):
            getattr(h, name).argtypes = [w.HANDLE, c.c_void_p, w.ULONG]
        s.SetupDiGetClassDevsW.argtypes = [c.POINTER(GUID), w.LPCWSTR, c.c_void_p, w.DWORD]
        s.SetupDiGetClassDevsW.restype = c.c_void_p
        s.SetupDiEnumDeviceInterfaces.argtypes = [c.c_void_p, c.c_void_p, c.POINTER(GUID), w.DWORD, c.POINTER(Interface)]
        s.SetupDiGetDeviceInterfaceDetailW.argtypes = [c.c_void_p, c.POINTER(Interface), c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.c_void_p]
        s.SetupDiDestroyDeviceInfoList.argtypes = [c.c_void_p]

    def open(self, path, access=0, flags=0):
        handle = self.k.CreateFileW(path, access, 3, None, 3, flags, None)
        if handle == c.c_void_p(-1).value:
            raise c.WinError(c.get_last_error())
        return handle

    def metadata(self, handle, path):
        attr = Attributes()
        attr.size = c.sizeof(attr)
        if not self.h.HidD_GetAttributes(handle, c.byref(attr)):
            raise c.WinError(c.get_last_error())
        pp = c.c_void_p()
        if not self.h.HidD_GetPreparsedData(handle, c.byref(pp)):
            raise c.WinError(c.get_last_error())
        try:
            caps = (w.USHORT * 32)()
            if self.h.HidP_GetCaps(pp, caps) != 0x110000:
                raise OSError("could not inspect HID capabilities")
        finally:
            self.h.HidD_FreePreparsedData(pp)
        result = dict(path=path, usb_id=f"{attr.vid:04X}:{attr.pid:04X}",
                      usage=caps[0], usage_page=caps[1], input_bytes=caps[2],
                      output_bytes=caps[3], feature_bytes=caps[4])
        for label, name in (("product", "HidD_GetProductString"), ("serial", "HidD_GetSerialNumberString")):
            text = c.create_unicode_buffer(256)
            result[label] = text.value if getattr(self.h, name)(handle, text, c.sizeof(text)) else ""
        return result


def is_bootloader(device):
    return (device["usb_id"] == "10C4:8B02" and device["usage_page"] == 0xFF00
            and device["usage"] == 1 and device["input_bytes"] == 5
            and device["output_bytes"] == 65 and device["feature_bytes"] == 0)


def enumerate_devices():
    api = Api()
    guid = GUID()
    api.h.HidD_GetHidGuid(c.byref(guid))
    devices = api.s.SetupDiGetClassDevsW(c.byref(guid), None, None, 0x12)
    if devices == c.c_void_p(-1).value:
        raise c.WinError(c.get_last_error())
    result = []
    try:
        index = 0
        while True:
            info = Interface()
            info.size = c.sizeof(info)
            if not api.s.SetupDiEnumDeviceInterfaces(devices, None, c.byref(guid), index, c.byref(info)):
                if c.get_last_error() != 259:
                    raise c.WinError(c.get_last_error())
                break
            index += 1
            size = w.DWORD()
            api.s.SetupDiGetDeviceInterfaceDetailW(devices, c.byref(info), None, 0, c.byref(size), None)
            detail = c.create_string_buffer(size.value)
            c.cast(detail, c.POINTER(w.DWORD))[0] = 8 if c.sizeof(c.c_void_p) == 8 else 6
            if not api.s.SetupDiGetDeviceInterfaceDetailW(devices, c.byref(info), detail, size, c.byref(size), None):
                raise c.WinError(c.get_last_error())
            path = c.wstring_at(c.addressof(detail) + 4)
            if not any(value in path.lower() for value in ("vid_10c4&pid_8b02", "vid_1209&pid_0001", "vid_1209&pid_0002", "vid_16c0&pid_05b7")):
                continue
            handle = api.open(path)
            try:
                result.append(api.metadata(handle, path))
            finally:
                api.k.CloseHandle(handle)
    finally:
        api.s.SetupDiDestroyDeviceInfoList(devices)
    return result


class HidTransport:
    def __init__(self):
        self.api = Api()
        self.handle = None
        matches = [d for d in enumerate_devices() if is_bootloader(d)]
        if len(matches) != 1:
            raise OSError(f"expected exactly one HE SIM PEDALS bootloader, found {len(matches)}; hold FW/reset while reconnecting its USB")
        self.handle = self.api.open(matches[0]["path"], 0xC0000000, 0x40000000)
        try:
            self.device = self.api.metadata(self.handle, matches[0]["path"])
            if not is_bootloader(self.device):
                raise OSError("opened device is not the expected bootloader")
            if not self.api.h.HidD_FlushQueue(self.handle):
                raise c.WinError(c.get_last_error())
            # Empty both the driver's queue and any pending on-device replies.
            for _ in range(8):
                if self._io(None, 5, timeout=100, quiet_timeout=True) is None:
                    break
            else:
                raise OSError("bootloader reply queue did not become idle")
        except BaseException:
            self.close()
            raise

    def _io(self, data, length, *, timeout=3000, quiet_timeout=False):
        k = self.api.k
        buffer = c.create_string_buffer(length) if data is None else c.create_string_buffer(data, length)
        ov = Overlapped()
        ov.event = k.CreateEventW(None, True, False, None)
        if not ov.event:
            raise c.WinError(c.get_last_error())
        done = w.DWORD()
        pending = False
        try:
            fn = k.ReadFile if data is None else k.WriteFile
            ok = fn(self.handle, buffer, length, c.byref(done), c.byref(ov))
            if not ok:
                if c.get_last_error() != 997:
                    raise c.WinError(c.get_last_error())
                pending = True
                wait = k.WaitForSingleObject(ov.event, timeout)
                if wait != 0:
                    k.CancelIoEx(self.handle, c.byref(ov))
                    # Join cancellation before releasing the OVERLAPPED and buffer.
                    k.GetOverlappedResult(self.handle, c.byref(ov), c.byref(done), True)
                    pending = False
                    if quiet_timeout and wait == 258:
                        return None
                    raise TimeoutError(f"HID {'read' if data is None else 'write'} wait status {wait}")
            if not k.GetOverlappedResult(self.handle, c.byref(ov), c.byref(done), False):
                raise c.WinError(c.get_last_error())
            pending = False
            if done.value != length:
                raise OSError(f"short HID transfer: {done.value}/{length}")
            return buffer.raw if data is None else done.value
        finally:
            if pending:
                k.CancelIoEx(self.handle, c.byref(ov))
                k.GetOverlappedResult(self.handle, c.byref(ov), c.byref(done), True)
            k.CloseHandle(ov.event)

    def exchange(self, report):
        if len(report) != 65 or report[0] != 0:
            raise ValueError("expected a 65-byte HID output report")
        self._io(report, 65)
        return self._io(None, 5)

    def close(self):
        if self.handle is not None:
            self.api.k.CloseHandle(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
