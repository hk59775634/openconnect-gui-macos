using System.Runtime.InteropServices;

namespace SslVpnClient.Mac.Native;

/// <summary>
/// OpenConnect libopenconnect P/Invoke（方案 A，OpenConnect 9.x / macOS）。
/// </summary>
internal static class OpenConnectNative
{
    private const string DllName = "libopenconnect";

    public const int ReconnectIntervalMin = 10;
    public const byte CmdCancel = (byte)'x';

    public const int ProgressLevelError = 0;
    public const int ProgressLevelInfo = 1;
    public const int ProgressLevelTrace = 3;
    public const int ProgressLevelSilent = -1;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void openconnect_set_loglevel(IntPtr vpninfo, int level);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int openconnect_init_ssl();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr openconnect_vpninfo_new(
        [MarshalAs(UnmanagedType.LPStr)] string? useragent,
        ValidateCertCallback? validateCertFn,
        WriteNewConfigCallback? writeNewConfigFn,
        ProcessAuthFormCallback? processAuthFormFn,
        IntPtr progressFn,
        IntPtr privdata);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void openconnect_vpninfo_free(IntPtr vpninfo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int openconnect_set_protocol(IntPtr vpninfo, [MarshalAs(UnmanagedType.LPStr)] string protocol);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int openconnect_parse_url(IntPtr vpninfo, [MarshalAs(UnmanagedType.LPStr)] string url);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int openconnect_set_option_value(IntPtr opt, [MarshalAs(UnmanagedType.LPStr)] string value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int openconnect_obtain_cookie(IntPtr vpninfo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int openconnect_make_cstp_connection(IntPtr vpninfo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int openconnect_setup_dtls(IntPtr vpninfo, int dtlsAttemptPeriod);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int openconnect_mainloop(IntPtr vpninfo, int reconnectTimeout, int reconnectInterval);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int openconnect_setup_tun_device(
        IntPtr vpninfo,
        [MarshalAs(UnmanagedType.LPStr)] string? vpncScript,
        [MarshalAs(UnmanagedType.LPStr)] string? ifname);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr openconnect_get_ifname(IntPtr vpninfo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr openconnect_setup_cmd_pipe(IntPtr vpninfo);

    public static bool IsValidCmdPipe(IntPtr cmdPipeWrite) =>
        cmdPipeWrite != IntPtr.Zero && cmdPipeWrite != new IntPtr(-1);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ValidateCertCallback(IntPtr privdata, [MarshalAs(UnmanagedType.LPStr)] string reason);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteNewConfigCallback(IntPtr privdata, [MarshalAs(UnmanagedType.LPStr)] string buf, int buflen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ProcessAuthFormCallback(IntPtr privdata, IntPtr form);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate void ProgressCallback(IntPtr privdata, int level, string fmt);
}
