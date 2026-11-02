using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GeBar.App;

internal static partial class GitHubTokenStore
{
    private const string Target = "g-eBAR/GitHub";
    private const uint Generic = 1;
    private const uint LocalMachine = 2;
    private const int NotFound = 1168;

    public static string? Read()
    {
        if (!CredRead(Target, Generic, 0, out IntPtr pointer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Write(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            if (!CredDelete(Target, Generic, 0) && Marshal.GetLastWin32Error() != NotFound) throw new Win32Exception(Marshal.GetLastWin32Error());
            return;
        }
        string value = token.Trim();
        IntPtr blob = Marshal.StringToCoTaskMemUni(value);
        IntPtr native = IntPtr.Zero;
        try
        {
            var credential = new NativeCredential
            {
                Type = Generic,
                TargetName = Target,
                CredentialBlobSize = checked((uint)(value.Length * 2)),
                CredentialBlob = blob,
                Persist = LocalMachine,
                UserName = Environment.UserName
            };
            native = Marshal.AllocCoTaskMem(Marshal.SizeOf<NativeCredential>());
            Marshal.StructureToPtr(credential, native, false);
            if (!CredWrite(native, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (native != IntPtr.Zero)
            {
                Marshal.DestroyStructure<NativeCredential>(native);
                Marshal.FreeCoTaskMem(native);
            }
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(IntPtr credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr credential);
}
