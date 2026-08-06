using System.Runtime.InteropServices;

namespace SslVpnClient.Mac.Native;

/// <summary>
/// OpenConnect progress 回调桥接：原生 variadic printf 在 C 层格式化后再回调托管代码。
/// </summary>
internal static class OpenConnectProgressBridge
{
    private const string BridgeDll = "oc_progress_bridge";

    private static OpenConnectNative.ProgressCallback? _handler;
    private static OpenConnectNative.ProgressCallback? _pinnedHandler;

    public static IntPtr RegisterHandler(OpenConnectNative.ProgressCallback handler)
    {
        _handler = handler;
        _pinnedHandler = StaticProgressHandler;
        oc_set_progress_handler(_pinnedHandler);
        return oc_get_progress_callback();
    }

    [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void oc_set_progress_handler(OpenConnectNative.ProgressCallback fn);

    [DllImport(BridgeDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr oc_get_progress_callback();

    private static void StaticProgressHandler(IntPtr privdata, int level, string message) =>
        _handler?.Invoke(privdata, level, message);
}
