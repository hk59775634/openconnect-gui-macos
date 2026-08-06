using Avalonia.Threading;
using SslVpnClient.Abstractions;

namespace SslVpnClient.Mac.Services;

/// <summary>
/// Avalonia UI 线程调度器封装。
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
