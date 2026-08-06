using SslVpnClient.Models;
using SslVpnClient.Vpn;

namespace SslVpnClient.Abstractions;

public interface IVpnConnection : IDisposable
{
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<LogMessageEventArgs>? LogMessage;

    VpnConnectionState CurrentState { get; }
    bool IsConnected { get; }
    bool NeedsNetworkRestore { get; }
    string? TunInterfaceName { get; }

    Task<bool> ConnectAsync(string server, string username, string password, VpnConnectOptions? options = null);
    Task DisconnectAsync(string? savedServerUrl = null);
}
