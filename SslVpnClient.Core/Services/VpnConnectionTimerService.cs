using System.Diagnostics;

namespace SslVpnClient.Services;

public class VpnConnectionTimerService : IDisposable
{
    private readonly Stopwatch _stopwatch = new();
    private Timer? _timer;
    private bool _isRunning;

    public event EventHandler<string>? DurationChanged;

    public string FormattedDuration =>
        _isRunning ? FormatDuration(_stopwatch.Elapsed) : "00:00:00";

    public void Start()
    {
        _stopwatch.Restart();
        _isRunning = true;
        _timer?.Dispose();
        _timer = new Timer(_ =>
        {
            DurationChanged?.Invoke(this, FormatDuration(_stopwatch.Elapsed));
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
        _stopwatch.Reset();
        DurationChanged?.Invoke(this, "00:00:00");
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopwatch.Stop();
    }

    private static string FormatDuration(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
}
