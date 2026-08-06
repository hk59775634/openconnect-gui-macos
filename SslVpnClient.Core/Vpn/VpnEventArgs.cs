using SslVpnClient.Models;

namespace SslVpnClient.Vpn;

public class ConnectionStateChangedEventArgs : EventArgs
{
    public VpnConnectionState State { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool IsError { get; init; }
}

public class LogMessageEventArgs : EventArgs
{
    public string Level { get; init; } = "Info";
    public string Message { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

public enum VpnConnectionErrorType
{
    None,
    Timeout,
    AuthenticationFailed,
    ServerUnreachable,
    CertificateError,
    Unknown
}
