using System.Runtime.InteropServices;

namespace SslVpnClient.Mac.Native;

internal static class OpenConnectAuthStructs
{
    public const int FormOptText = 1;
    public const int FormOptPassword = 2;
    public const int FormResultErr = -1;
    public const int FormResultOk = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct OcFormOpt
    {
        public IntPtr Next;
        public int Type;
        public IntPtr Name;
        public IntPtr Label;
        public IntPtr Value;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OcAuthForm
    {
        public IntPtr Banner;
        public IntPtr Message;
        public IntPtr Error;
        public IntPtr AuthId;
        public IntPtr Method;
        public IntPtr Action;
        public IntPtr Opts;
        public IntPtr AuthgroupOpt;
        public int AuthgroupSelection;
    }
}

internal static class OpenConnectAuthFormHandler
{
    public static int ProcessAuthForm(IntPtr privdata, IntPtr formPtr, string username, string password)
    {
        if (formPtr == IntPtr.Zero)
        {
            return OpenConnectAuthStructs.FormResultErr;
        }

        var form = Marshal.PtrToStructure<OpenConnectAuthStructs.OcAuthForm>(formPtr);
        if (form.Error != IntPtr.Zero)
        {
            var err = Marshal.PtrToStringAnsi(form.Error);
            if (!string.IsNullOrWhiteSpace(err))
            {
                return OpenConnectAuthStructs.FormResultErr;
            }
        }

        if (form.AuthId == IntPtr.Zero)
        {
            return OpenConnectAuthStructs.FormResultErr;
        }

        var optPtr = form.Opts;
        while (optPtr != IntPtr.Zero)
        {
            var opt = Marshal.PtrToStructure<OpenConnectAuthStructs.OcFormOpt>(optPtr);
            var name = Marshal.PtrToStringAnsi(opt.Name) ?? string.Empty;

            if (opt.Type == OpenConnectAuthStructs.FormOptText &&
                (name.StartsWith("user", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("uname", StringComparison.OrdinalIgnoreCase)))
            {
                OpenConnectNative.openconnect_set_option_value(optPtr, username);
            }
            else if (opt.Type == OpenConnectAuthStructs.FormOptPassword)
            {
                OpenConnectNative.openconnect_set_option_value(optPtr, password);
            }

            optPtr = opt.Next;
        }

        return OpenConnectAuthStructs.FormResultOk;
    }
}
