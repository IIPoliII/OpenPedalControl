using System;
using System.Runtime.InteropServices;

namespace OpenPedalControl.DirectInput;

// Just enough of DirectInput 8 to enumerate game controllers and read/write their axis calibration.
// Interface methods must stay in vtable order; trailing methods that are never called are left out.

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int EnumDevicesCallback(IntPtr deviceInstance, IntPtr context);

[ComImport, Guid("BF798031-483A-4DA2-AA99-5D64ED369700"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectInput8W
{
    [PreserveSig] int CreateDevice(ref Guid instanceGuid, [MarshalAs(UnmanagedType.Interface)] out IDirectInputDevice8W? device, IntPtr outer);
    // Without FunctionPtr, COM interop would pass the delegate as a COM object instead of a callback address.
    [PreserveSig] int EnumDevices(uint deviceType, [MarshalAs(UnmanagedType.FunctionPtr)] EnumDevicesCallback callback, IntPtr context, uint flags);
}

[ComImport, Guid("54D41081-DC15-4833-A41B-748F73A38179"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirectInputDevice8W
{
    [PreserveSig] int GetCapabilities(IntPtr capabilities);
    [PreserveSig] int EnumObjects(IntPtr callback, IntPtr context, uint flags);
    [PreserveSig] int GetProperty(IntPtr property, IntPtr header);
    [PreserveSig] int SetProperty(IntPtr property, IntPtr header);
}

internal static class DirectInputNative
{
    public const uint DIRECTINPUT_VERSION = 0x0800;
    public const uint DI8DEVCLASS_GAMECTRL = 4;
    public const uint DIEDFL_ATTACHEDONLY = 1;
    public const int DIENUM_CONTINUE = 1;

    public const uint DIPH_DEVICE = 0;
    public const uint DIPH_BYUSAGE = 3;

    // MAKEDIPROP(n): properties are identified by small integers passed in place of a GUID pointer.
    public static readonly IntPtr DIPROP_CALIBRATION = new IntPtr(11);
    public static readonly IntPtr DIPROP_GUIDANDPATH = new IntPtr(12);

    public const int PropHeaderSize = 16;          // DIPROPHEADER
    public const int PropCalSize = 28;             // DIPROPCAL: header + lMin, lCenter, lMax
    public const int PropGuidAndPathSize = 552;    // DIPROPGUIDANDPATH: header + GUID + WCHAR[260]

    // Field offsets inside DIDEVICEINSTANCEW.
    public const int InstanceGuidOffset = 4;
    public const int ProductGuidOffset = 20;
    public const int InstanceNameOffset = 40;
    public const int ProductNameOffset = 560;

    [DllImport("dinput8.dll")]
    public static extern int DirectInput8Create(IntPtr instance, uint version, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IDirectInput8W? directInput, IntPtr outer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);
}
