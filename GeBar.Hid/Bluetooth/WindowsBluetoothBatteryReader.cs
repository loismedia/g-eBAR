using System.Runtime.InteropServices;

namespace GeBar.Hid;

internal static partial class WindowsBluetoothBatteryReader
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static readonly DevPropKey BatteryChargePercent = new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);

    public static int? TryRead(IReadOnlyList<string> nameHints)
    {
        IntPtr devices = SetupDiGetClassDevs(IntPtr.Zero, "BTHENUM", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (devices == InvalidHandleValue) return null;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(devices, index, ref data))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    continue;
                }
                string? instanceId = GetInstanceId(devices, ref data);
                if (instanceId is null || !instanceId.Contains("{0000111E-0000-1000-8000-00805F9B34FB}", StringComparison.OrdinalIgnoreCase)) continue;
                string? name = GetFriendlyName(devices, ref data);
                if (name is null || !nameHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase))) continue;
                uint size = 1;
                IntPtr value = Marshal.AllocHGlobal(1);
                try
                {
                    int result = CmGetDevNodeProperty(data.DevInst, in BatteryChargePercent, out _, value, ref size, 0);
                    if (result != 0 || size != 1) continue;
                    int percentage = Marshal.ReadByte(value);
                    if (percentage is >= 1 and <= 100) return percentage;
                }
                finally
                {
                    Marshal.FreeHGlobal(value);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devices);
        }
        return null;
    }

    private static string? GetInstanceId(IntPtr devices, ref SpDevInfoData data)
    {
        const int capacity = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            return SetupDiGetDeviceInstanceId(devices, ref data, buffer, capacity, out _) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? GetFriendlyName(IntPtr devices, ref SpDevInfoData data)
    {
        const int capacity = 1024;
        IntPtr buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            return SetupDiGetDeviceRegistryProperty(devices, ref data, SpdrpFriendlyName, out _, buffer, capacity * sizeof(char), out _)
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr devices, uint index, ref SpDevInfoData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceId(IntPtr devices, ref SpDevInfoData data, IntPtr buffer, int size, out int requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryProperty(IntPtr devices, ref SpDevInfoData data, uint property, out uint propertyType, IntPtr buffer, int size, out int requiredSize);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiDestroyDeviceInfoList")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial int CmGetDevNodeProperty(uint device, in DevPropKey propertyKey, out uint propertyType, IntPtr buffer, ref uint size, uint flags);
}
