using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using SslVpnClient.Abstractions;

namespace SslVpnClient.Services;

/// <summary>
/// 采样本次连接期间 VPN 接口上下行速率（仅内存，断开即清空）。
/// </summary>
public sealed partial class VpnTrafficMonitorService : ObservableObject, IDisposable
{
    private const int MaxSamples = 60;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _sync = new();
    private Timer? _timer;
    private string? _interfaceName;
    private long _lastRx;
    private long _lastTx;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private bool _hasBaseline;
    private long _sessionRx;
    private long _sessionTx;

    public ObservableCollection<double> DownloadSamples { get; } = new();
    public ObservableCollection<double> UploadSamples { get; } = new();

    [ObservableProperty]
    private string _downloadRateText = "↓ 0 Mbps";

    [ObservableProperty]
    private string _uploadRateText = "↑ 0 Mbps";

    [ObservableProperty]
    private string _totalTrafficText = "本次 ↓0 B / ↑0 B";

    [ObservableProperty]
    private bool _isActive;

    public VpnTrafficMonitorService(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start(string? interfaceName)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return;
        }

        _interfaceName = interfaceName;
        _hasBaseline = false;
        _sessionRx = 0;
        _sessionTx = 0;
        _lastSampleUtc = DateTime.UtcNow;
        IsActive = true;
        ClearSamplesOnUi();

        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _interfaceName = null;
        _hasBaseline = false;
        IsActive = false;
        DownloadRateText = "↓ 0 Mbps";
        UploadRateText = "↑ 0 Mbps";
        TotalTrafficText = "本次 ↓0 B / ↑0 B";
        ClearSamplesOnUi();
    }

    public void Dispose() => Stop();

    private void Sample()
    {
        var name = _interfaceName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!TryReadCounters(name, out var rx, out var tx))
        {
            return;
        }

        var now = DateTime.UtcNow;
        double downBps = 0;
        double upBps = 0;

        lock (_sync)
        {
            if (_hasBaseline)
            {
                var seconds = Math.Max((now - _lastSampleUtc).TotalSeconds, 0.001);
                var dRx = Math.Max(0, rx - _lastRx);
                var dTx = Math.Max(0, tx - _lastTx);
                _sessionRx += dRx;
                _sessionTx += dTx;
                downBps = dRx / seconds;
                upBps = dTx / seconds;
            }

            _lastRx = rx;
            _lastTx = tx;
            _lastSampleUtc = now;
            _hasBaseline = true;
        }

        var sessionRx = _sessionRx;
        var sessionTx = _sessionTx;
        var downMbps = BytesPerSecToMbps(downBps);
        var upMbps = BytesPerSecToMbps(upBps);

        _dispatcher.Post(() =>
        {
            PushSample(DownloadSamples, downMbps);
            PushSample(UploadSamples, upMbps);
            DownloadRateText = "↓ " + FormatMbps(downMbps);
            UploadRateText = "↑ " + FormatMbps(upMbps);
            TotalTrafficText = $"本次  ↓{FormatBytes(sessionRx)} / ↑{FormatBytes(sessionTx)}";
            OnPropertyChanged(nameof(DownloadSamples));
            OnPropertyChanged(nameof(UploadSamples));
        });
    }

    private void ClearSamplesOnUi()
    {
        void Clear()
        {
            DownloadSamples.Clear();
            UploadSamples.Clear();
        }

        if (_dispatcher.CheckAccess())
        {
            Clear();
        }
        else
        {
            _dispatcher.Post(Clear);
        }
    }

    private static void PushSample(ObservableCollection<double> samples, double value)
    {
        samples.Add(value);
        while (samples.Count > MaxSamples)
        {
            samples.RemoveAt(0);
        }
    }

    private static bool TryReadCounters(string interfaceName, out long bytesReceived, out long bytesSent)
    {
        bytesReceived = 0;
        bytesSent = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up &&
                    nic.OperationalStatus != OperationalStatus.Unknown)
                {
                    continue;
                }

                if (!string.Equals(nic.Name, interfaceName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(nic.Description, interfaceName, StringComparison.OrdinalIgnoreCase) &&
                    !nic.Name.Contains(interfaceName, StringComparison.OrdinalIgnoreCase) &&
                    !nic.Description.Contains(interfaceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stats = nic.GetIPStatistics();
                bytesReceived = stats.BytesReceived;
                bytesSent = stats.BytesSent;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static double BytesPerSecToMbps(double bytesPerSecond) =>
        bytesPerSecond * 8.0 / 1_000_000.0;

    private static string FormatMbps(double mbps) => $"{mbps:0.##} Mbps";

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.0} KB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024.0 * 1024):0.00} MB";
        }

        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }
}
