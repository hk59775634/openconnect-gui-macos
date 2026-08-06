using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SslVpnClient.Abstractions;
using SslVpnClient.Models;
using SslVpnClient.Services;
using SslVpnClient.Vpn;

namespace SslVpnClient.Mac.Vpn;

/// <summary>
/// macOS：openconnect CLI + 特权助手。支持全局 / 智能分流（chnroutes）。
/// </summary>
public sealed class OpenConnectCliVpnConnection : IVpnConnection
{
    public const string HelperPath = "/Library/OpenConnectGui/oc-run";
    public const string SplitScriptPath = "/Library/OpenConnectGui/ocg-vpnc-script";
    public const int RequiredHelperVersion = 8;

    private readonly ILogger<OpenConnectCliVpnConnection> _logger;
    private readonly ChnRoutesService _chnRoutes;
    private readonly object _sync = new();
    private int? _pid;
    private string? _sessionDir;
    private VpnConnectionState _state = VpnConnectionState.Disconnected;
    private bool _needsNetworkRestore;
    private CancellationTokenSource? _monitorCts;

    public OpenConnectCliVpnConnection(
        ILogger<OpenConnectCliVpnConnection> logger,
        ChnRoutesService chnRoutes)
    {
        _logger = logger;
        _chnRoutes = chnRoutes;
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
    public event EventHandler<LogMessageEventArgs>? LogMessage;

    public VpnConnectionState CurrentState
    {
        get { lock (_sync) return _state; }
    }

    public bool IsConnected => CurrentState == VpnConnectionState.Connected;

    public bool NeedsNetworkRestore
    {
        get { lock (_sync) return _needsNetworkRestore; }
    }

    public string? TunInterfaceName { get; private set; }

    public static int GetHelperVersion()
    {
        if (!File.Exists(HelperPath))
        {
            return 0;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/sudo",
                ArgumentList = { "-n", HelperPath, "version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return 0;
            }

            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(3000);
            return p.ExitCode == 0 && int.TryParse(output, out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsHelperReady() =>
        GetHelperVersion() >= RequiredHelperVersion && File.Exists(SplitScriptPath);

    public async Task<(bool Ok, string Message)> InstallHelperAsync()
    {
        var bundledRun = FindBundled("oc-run.sh");
        var bundledVpnc = FindBundled("ocg-vpnc-script.sh");
        if (bundledRun is null || bundledVpnc is null)
        {
            return (false, "找不到内置助手脚本，请运行 scripts/install-macos-helper.sh");
        }

        var tmpPath = Path.Combine("/tmp", "ocg-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpPath);
        try
        {
            var ocRun = Path.Combine(tmpPath, "oc-run");
            var vpnc = Path.Combine(tmpPath, "ocg-vpnc-script");
            var sudoers = Path.Combine(tmpPath, "sudoers");
            var installSh = Path.Combine(tmpPath, "install.sh");

            File.Copy(bundledRun, ocRun, overwrite: true);
            File.Copy(bundledVpnc, vpnc, overwrite: true);
            TryChmod(ocRun, "755");
            TryChmod(vpnc, "755");

            await File.WriteAllTextAsync(sudoers,
                "# OpenConnect Gui passwordless helper\n" +
                $"ALL ALL=(root) NOPASSWD: {HelperPath}\n").ConfigureAwait(false);
            TryChmod(sudoers, "440");

            var installBody =
                "#!/bin/bash\n" +
                "set -euo pipefail\n" +
                "mkdir -p /Library/OpenConnectGui\n" +
                $"cp '{EscapeSingle(ocRun)}' '{EscapeSingle(HelperPath)}'\n" +
                $"cp '{EscapeSingle(vpnc)}' '{EscapeSingle(SplitScriptPath)}'\n" +
                $"chown root:wheel '{EscapeSingle(HelperPath)}' '{EscapeSingle(SplitScriptPath)}'\n" +
                $"chmod 755 '{EscapeSingle(HelperPath)}' '{EscapeSingle(SplitScriptPath)}'\n" +
                $"cp '{EscapeSingle(sudoers)}' /etc/sudoers.d/openconnect-gui\n" +
                "chown root:wheel /etc/sudoers.d/openconnect-gui\n" +
                "chmod 440 /etc/sudoers.d/openconnect-gui\n" +
                "/usr/sbin/visudo -cf /etc/sudoers.d/openconnect-gui\n" +
                "echo installed\n";

            await File.WriteAllTextAsync(installSh, installBody).ConfigureAwait(false);
            TryChmod(installSh, "755");

            RaiseLog("Info", "正在安装/升级权限助手（需输入一次 Mac 登录密码）…");
            var result = await RunOsascriptAsync($"/bin/bash '{EscapeSingle(installSh)}'").ConfigureAwait(false);
            if (!result.Ok)
            {
                return (false, string.IsNullOrWhiteSpace(result.Error)
                    ? "安装已取消或失败"
                    : result.Error);
            }

            if (!IsHelperReady())
            {
                return (false, "助手已写入但版本校验失败");
            }

            RaiseLog("Info", $"权限助手 v{RequiredHelperVersion} 已就绪（含智能分流）");
            return (true, "权限助手已安装");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tmpPath))
                {
                    Directory.Delete(tmpPath, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    public async Task<bool> ConnectAsync(
        string server,
        string username,
        string password,
        VpnConnectOptions? options = null)
    {
        if (IsConnected || CurrentState == VpnConnectionState.Connecting)
        {
            return false;
        }

        if (string.IsNullOrEmpty(password))
        {
            SetState(VpnConnectionState.Error, "本地密码为空，请返回登录页重新输入并保存");
            return false;
        }

        SetState(VpnConnectionState.Connecting, "正在连接…");

        var openconnect = FindOpenConnect();
        if (openconnect is null)
        {
            SetState(VpnConnectionState.Error, "未找到 openconnect，请先执行: brew install openconnect");
            return false;
        }

        var split = options?.SplitTunnelEnabled == true;
        var connectUrl = ResolveConnectUrl(server, options?.GatewayNodeAddress);
        RaiseLog("Info", $"openconnect={openconnect}");
        RaiseLog("Info", $"连接目标={connectUrl}");
        RaiseLog("Info", split ? "模式=智能分流" : "模式=全局");

        try
        {
            if (!IsHelperReady())
            {
                RaiseLog("Info", "权限助手未安装或需升级…");
                var install = await InstallHelperAsync().ConfigureAwait(false);
                if (!install.Ok)
                {
                    SetState(VpnConnectionState.Error,
                        "需要安装/升级权限助手：" + install.Message);
                    return false;
                }
            }

            string? chnPath = null;
            if (split)
            {
                RaiseLog("Info", "准备 chnroutes…");
                chnPath = await _chnRoutes.EnsureIPv4RoutesAsync().ConfigureAwait(false);
            }

            var sessionDir = Path.Combine("/tmp", "ocg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sessionDir);
            TryChmod(sessionDir, "755");

            var passFile = Path.Combine(sessionDir, "pass.txt");
            var metaFile = Path.Combine(sessionDir, "meta.env");
            var logFile = Path.Combine(sessionDir, "openconnect.log");
            var pidFile = Path.Combine(sessionDir, "openconnect.pid");

            await File.WriteAllTextAsync(passFile, password + "\n", new UTF8Encoding(false))
                .ConfigureAwait(false);
            TryChmod(passFile, "644");

            var meta = new StringBuilder();
            meta.Append("OCG_USER=").Append(ShellQuote(username)).Append('\n');
            meta.Append("OCG_URL=").Append(ShellQuote(connectUrl)).Append('\n');
            meta.Append("OCG_SPLIT=").Append(split ? "1" : "0").Append('\n');
            if (split && chnPath is not null)
            {
                var sessionChn = Path.Combine(sessionDir, "chnroutes-v4");
                File.Copy(chnPath, sessionChn, overwrite: true);
                TryChmod(sessionChn, "644");
                meta.Append("OCG_CHNROUTES=").Append(ShellQuote(sessionChn)).Append('\n');
            }

            await File.WriteAllTextAsync(metaFile, meta.ToString()).ConfigureAwait(false);
            TryChmod(metaFile, "644");

            lock (_sync)
            {
                _sessionDir = sessionDir;
            }

            RaiseLog("Info", "通过权限助手启动 openconnect…");
            var started = await RunHelperAsync("connect", sessionDir).ConfigureAwait(false);
            if (!started.Ok)
            {
                var helperErr = await ReadHelperErrAsync(sessionDir).ConfigureAwait(false);
                var detail = FirstNonEmpty(helperErr, started.Error, "未知错误");
                SetState(VpnConnectionState.Error, "启动失败: " + detail);
                RaiseLog("Error", detail);
                CleanupSecrets(sessionDir);
                return false;
            }

            var pid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (pid is null)
            {
                var log = await ReadLogTailAsync(logFile, 80).ConfigureAwait(false);
                SetState(VpnConnectionState.Error,
                    string.IsNullOrWhiteSpace(log) ? "openconnect 未能启动" : Truncate(log, 500));
                CleanupSecrets(sessionDir);
                return false;
            }

            lock (_sync)
            {
                _pid = pid;
                _needsNetworkRestore = true;
            }

            RaiseLog("Info", $"openconnect pid={pid}");
            _monitorCts = new CancellationTokenSource();
            _ = TailLogAsync(logFile, _monitorCts.Token);
            _ = MonitorPidAsync(pid.Value, _monitorCts.Token);

            var timeout = split ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(60);
            var result = await WaitUntilConnectedAsync(logFile, pid.Value, timeout, split).ConfigureAwait(false);

            CleanupSecrets(sessionDir);

            if (result.Success)
            {
                TunInterfaceName = result.TunName ?? "utun";
                SetState(VpnConnectionState.Connected, split ? "已连接（分流）" : "已连接");
                RaiseLog("Info", $"VPN 隧道已建立 ({TunInterfaceName})");
                return true;
            }

            await DisconnectHelperAsync(pid.Value, sessionDir).ConfigureAwait(false);
            SetState(VpnConnectionState.Error, result.ErrorMessage ?? "连接失败");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接失败");
            RaiseLog("Error", ex.Message);
            SetState(VpnConnectionState.Error, $"连接失败: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync(string? savedServerUrl = null)
    {
        SetState(VpnConnectionState.Disconnecting, "正在断开…");
        RaiseLog("Info", "正在断开 VPN（先恢复 DNS）…");

        int? pid;
        string? session;
        lock (_sync)
        {
            pid = _pid;
            session = _sessionDir;
            _monitorCts?.Cancel();
        }

        // helper v5：disconnect 内先 restore DNS，再 kill，chnroutes 后台删；只调一次
        await DisconnectHelperAsync(pid, session).ConfigureAwait(false);

        lock (_sync)
        {
            _pid = null;
            _sessionDir = null;
            _needsNetworkRestore = false;
        }

        TunInterfaceName = null;
        SetState(VpnConnectionState.Disconnected, "已断开");
    }

    public void Dispose()
    {
        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }
    }

    private async Task DisconnectHelperAsync(int? pid, string? session)
    {
        if (pid is > 0 && !string.IsNullOrEmpty(session))
        {
            await RunHelperAsync("disconnect", pid.Value.ToString(), session).ConfigureAwait(false);
        }
        else if (pid is > 0)
        {
            await RunHelperAsync("disconnect", pid.Value.ToString()).ConfigureAwait(false);
        }
        else
        {
            await RunHelperAsync("disconnect").ConfigureAwait(false);
        }
    }

    private async Task<(bool Ok, string Error)> RunHelperAsync(string command, string? arg1 = null, string? arg2 = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/sudo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(HelperPath);
        psi.ArgumentList.Add(command);
        if (!string.IsNullOrEmpty(arg1))
        {
            psi.ArgumentList.Add(arg1);
        }

        if (!string.IsNullOrEmpty(arg2))
        {
            psi.ArgumentList.Add(arg2);
        }

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "无法启动 sudo");
            }

            var stdout = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = $"helper exit={p.ExitCode}";
                }

                return (false, detail);
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                RaiseLog("Info", stdout.Trim());
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool Ok, string Error)> RunOsascriptAsync(string shellCommand)
    {
        var apple = $"do shell script \"{EscapeAppleScript(shellCommand)}\" with administrator privileges";
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            ArgumentList = { "-e", apple },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return (false, "无法启动 osascript");
            }

            var stderr = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);
            if (p.ExitCode != 0)
            {
                return (false, string.IsNullOrWhiteSpace(stderr) ? "管理员授权已取消或失败" : stderr.Trim());
            }

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string? TunName, string? ErrorMessage)> WaitUntilConnectedAsync(
        string logFile,
        int pid,
        TimeSpan timeout,
        bool split)
    {
        var sw = Stopwatch.StartNew();
        string? lastLog = null;

        while (sw.Elapsed < timeout)
        {
            if (CurrentState == VpnConnectionState.Error)
            {
                return (false, null, "连接出错");
            }

            lastLog = await ReadLogTailAsync(logFile, 200).ConfigureAwait(false);

            if (LooksLikeAuthFailure(lastLog))
            {
                return (false, null, "认证失败，请返回登录页确认账号密码后重试");
            }

            var tunnelUp = LooksLikeConnected(lastLog);
            var splitReady = !split ||
                             lastLog.Contains("half-defaults", StringComparison.OrdinalIgnoreCase) ||
                             lastLog.Contains("chnroutes via", StringComparison.OrdinalIgnoreCase);

            if (tunnelUp && splitReady)
            {
                await Task.Delay(split ? 1500 : 500).ConfigureAwait(false);
                var tun = await FindVpnUtunAsync().ConfigureAwait(false);
                return (true, tun, null);
            }

            if (!IsProcessAlive(pid))
            {
                return (false, null,
                    string.IsNullOrWhiteSpace(lastLog)
                        ? "openconnect 已退出"
                        : Truncate(lastLog, 500));
            }

            var earlyTun = await FindVpnUtunAsync().ConfigureAwait(false);
            if (earlyTun is not null && !split && sw.Elapsed > TimeSpan.FromSeconds(5))
            {
                return (true, earlyTun, null);
            }

            // 分流：隧道已起但路由还在批量添加
            if (split && tunnelUp && earlyTun is not null && sw.Elapsed > TimeSpan.FromSeconds(30))
            {
                RaiseLog("Warning", "分流路由仍在应用中，先标记已连接");
                return (true, earlyTun, null);
            }

            await Task.Delay(400).ConfigureAwait(false);
        }

        if (IsProcessAlive(pid))
        {
            var tun = await FindVpnUtunAsync().ConfigureAwait(false);
            if (tun is not null)
            {
                return (true, tun, null);
            }
        }

        return (false, null,
            string.IsNullOrWhiteSpace(lastLog) ? "连接超时" : Truncate(lastLog, 500));
    }

    private async Task TailLogAsync(string logFile, CancellationToken ct)
    {
        long offset = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (File.Exists(logFile))
                {
                    await using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length > offset)
                    {
                        fs.Seek(offset, SeekOrigin.Begin);
                        using var reader = new StreamReader(fs, Encoding.UTF8);
                        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                var level = line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                            line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                                    ? "Error"
                                    : "Info";
                                RaiseLog(level, line);
                                if (LooksLikeAuthFailure(line))
                                {
                                    SetState(VpnConnectionState.Error, "认证失败，请检查账号密码");
                                }
                            }
                        }

                        offset = fs.Position;
                    }
                }

                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "tail log ended");
        }
    }

    private async Task MonitorPidAsync(int pid, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!IsProcessAlive(pid))
                {
                    if (CurrentState is VpnConnectionState.Connected or VpnConnectionState.Connecting)
                    {
                        SetState(VpnConnectionState.Disconnected, "连接已结束");
                    }

                    lock (_sync)
                    {
                        if (_pid == pid)
                        {
                            _pid = null;
                            _needsNetworkRestore = false;
                        }
                    }

                    return;
                }

                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    private static bool LooksLikeConnected(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("Connected as", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Configured as", StringComparison.OrdinalIgnoreCase)
               || text.Contains("ESP session established", StringComparison.OrdinalIgnoreCase)
               || text.Contains("DTLS handshake successful", StringComparison.OrdinalIgnoreCase)
               || text.Contains("CSTP connected", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Continuing in background", StringComparison.OrdinalIgnoreCase)
               || text.Contains("已配置为", StringComparison.Ordinal)
               || text.Contains("已连接为", StringComparison.Ordinal)
               || text.Contains("half-defaults", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAuthFailure(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains("Login failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("auth failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Login error", StringComparison.OrdinalIgnoreCase)
               || text.Contains("无法完成身份验证", StringComparison.Ordinal)
               || text.Contains("登录失败", StringComparison.Ordinal);
    }

    private static async Task<string?> FindVpnUtunAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/sbin/ifconfig",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var output = await p.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await p.WaitForExitAsync().ConfigureAwait(false);

            string? current = null;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw;
                if (!line.StartsWith('\t') && !line.StartsWith(' ') && line.Contains(':'))
                {
                    current = line.Split(':')[0].Trim();
                }

                if (current is null || !current.StartsWith("utun", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("inet ") && !line.Contains("inet6"))
                {
                    return current;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static async Task<int?> WaitForPidAsync(string pidFile, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(pidFile))
            {
                var text = (await File.ReadAllTextAsync(pidFile).ConfigureAwait(false)).Trim();
                if (int.TryParse(text, out var pid) && pid > 0)
                {
                    return pid;
                }
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task<string> ReadLogTailAsync(string logFile, int maxLines)
    {
        try
        {
            if (!File.Exists(logFile))
            {
                return string.Empty;
            }

            var lines = await File.ReadAllLinesAsync(logFile).ConfigureAwait(false);
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            var start = Math.Max(0, lines.Length - maxLines);
            return string.Join('\n', lines[start..]);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadHelperErrAsync(string sessionDir)
    {
        try
        {
            var path = Path.Combine(sessionDir, "helper.err");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return (await File.ReadAllTextAsync(path).ConfigureAwait(false)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return string.Empty;
    }

    private static void CleanupSecrets(string sessionDir)
    {
        try
        {
            var pass = Path.Combine(sessionDir, "pass.txt");
            if (File.Exists(pass))
            {
                File.Delete(pass);
            }
        }
        catch
        {
            // ignore
        }
    }

    private void SetState(VpnConnectionState state, string message)
    {
        lock (_sync)
        {
            _state = state;
            if (state == VpnConnectionState.Connected)
            {
                _needsNetworkRestore = true;
            }
            else if (state == VpnConnectionState.Disconnected)
            {
                _needsNetworkRestore = false;
            }
        }

        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            State = state,
            Message = message,
            IsError = state == VpnConnectionState.Error
        });
    }

    private void RaiseLog(string level, string message)
    {
        _logger.LogInformation("[{Level}] {Message}", level, message);
        LogMessage?.Invoke(this, new LogMessageEventArgs
        {
            Level = level,
            Message = message,
            Timestamp = DateTime.Now
        });
    }

    private static string? FindBundled(string fileName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Native", fileName),
            Path.Combine(baseDir, fileName),
            // .app bundle: Contents/MacOS → Contents/Resources/Native
            Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "Native", fileName)),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "SslVpnClient.Mac", "Native", fileName)),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "SslVpnClient.Mac", "Native", fileName)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOpenConnect()
    {
        foreach (var candidate in new[]
                 {
                     "/opt/homebrew/bin/openconnect",
                     "/usr/local/bin/openconnect"
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ResolveConnectUrl(string server, string? gatewayAddress)
    {
        if (!string.IsNullOrWhiteSpace(gatewayAddress))
        {
            return gatewayAddress.Trim();
        }

        return server.Trim();
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryChmod(string path, string mode)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/chmod",
                ArgumentList = { mode, path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(3000);
        }
        catch
        {
            // ignore
        }
    }

    private static string ShellQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";

    private static string EscapeSingle(string value) =>
        value.Replace("'", "'\\''");

    private static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[^max..];
}
