using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SslVpnClient.Abstractions;
using SslVpnClient.Mac.Native;
using SslVpnClient.Models;
using SslVpnClient.Services;
using SslVpnClient.Vpn;

namespace SslVpnClient.Mac.Vpn;

/// <summary>
/// 方案 A：内置 libopenconnect。
/// GUI 进程不直接开 utun（会 Operation not permitted）；
/// 由特权助手以 root 拉起 --vpn-worker 运行库模式主循环。
/// </summary>
public sealed class OpenConnectLibVpnConnection : IVpnConnection
{
    public const string HelperPath = "/Library/OpenConnectGui/oc-run";
    public const string VpnHostPath = "/Library/OpenConnectGui/ocg-vpnhost";
    public const string SplitScriptPath = "/Library/OpenConnectGui/ocg-vpnc-script";
    public const string StockVpncPath = "/Library/OpenConnectGui/vpnc-script";
    public const string LibDir = "/Library/OpenConnectGui/lib";
    public const int RequiredHelperVersion = 13;

    private readonly ILogger<OpenConnectLibVpnConnection> _logger;
    private readonly ChnRoutesService _chnRoutes;
    private readonly object _sync = new();

    private int? _pid;
    private string? _sessionDir;
    private bool _needsNetworkRestore;
    private CancellationTokenSource? _monitorCts;
    private VpnConnectionState _state = VpnConnectionState.Disconnected;

    public OpenConnectLibVpnConnection(
        ILogger<OpenConnectLibVpnConnection> logger,
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
        GetHelperVersion() >= RequiredHelperVersion
        && File.Exists(SplitScriptPath)
        && File.Exists(StockVpncPath)
        && File.Exists(VpnHostPath)
        && File.Exists("/Library/OpenConnectGui/vpnhost/ocg-vpnhost");

    public async Task<(bool Ok, string Message)> InstallHelperAsync()
    {
        var bundledRun = FindBundled("oc-run.sh");
        var bundledVpnc = FindBundled("ocg-vpnc-script.sh");
        var bundledStock = FindBundled("vpnc-script") ?? NativeLibraryBootstrap.FindVpncScript();
        if (bundledRun is null || bundledVpnc is null || bundledStock is null)
        {
            return (false, "找不到内置助手/vpnc-script，请先运行 scripts/vendor-macos-native.sh");
        }

        var vpnhostDir = FindPublishedVpnHostDir();
        if (vpnhostDir is null)
        {
            return (false,
                "找不到无 UI 的 ocg-vpnhost。请先运行: ./scripts/install-macos-helper.sh");
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var tmpPath = Path.Combine("/tmp", "ocg-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpPath);
        try
        {
            var ocRun = Path.Combine(tmpPath, "oc-run");
            var vpnc = Path.Combine(tmpPath, "ocg-vpnc-script");
            var stock = Path.Combine(tmpPath, "vpnc-script");
            var host = Path.Combine(tmpPath, "ocg-vpnhost");
            var sudoers = Path.Combine(tmpPath, "sudoers");
            var installSh = Path.Combine(tmpPath, "install.sh");
            var libSrc = Path.Combine(tmpPath, "lib");
            var hostAppSrc = Path.Combine(tmpPath, "vpnhost");
            Directory.CreateDirectory(libSrc);
            Directory.CreateDirectory(hostAppSrc);

            File.Copy(bundledRun, ocRun, overwrite: true);
            File.Copy(bundledVpnc, vpnc, overwrite: true);
            File.Copy(bundledStock, stock, overwrite: true);

            foreach (var f in Directory.GetFiles(vpnhostDir))
            {
                File.Copy(f, Path.Combine(hostAppSrc, Path.GetFileName(f)), overwrite: true);
            }

            foreach (var dir in new[]
                     {
                         Path.Combine(baseDir, "Native"),
                         Path.Combine(baseDir, "Native", "lib", "osx-arm64"),
                         Path.Combine(baseDir, "Native", "lib", "osx-x64"),
                     })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var f in Directory.GetFiles(dir, "*.dylib"))
                {
                    File.Copy(f, Path.Combine(libSrc, Path.GetFileName(f)), overwrite: true);
                }

                var vpncInNative = Path.Combine(dir, "vpnc-script");
                if (File.Exists(vpncInNative))
                {
                    File.Copy(vpncInNative, stock, overwrite: true);
                }
            }

            await File.WriteAllTextAsync(host,
                "#!/bin/bash\n" +
                "set -euo pipefail\n" +
                "SESSION=\"${1:-}\"\n" +
                "[[ -n \"$SESSION\" ]] || { echo \"usage: ocg-vpnhost <session>\" >&2; exit 2; }\n" +
                "LIB=\"/Library/OpenConnectGui/lib\"\n" +
                "APP=\"/Library/OpenConnectGui/vpnhost/ocg-vpnhost\"\n" +
                "[[ -x \"$APP\" ]] || { echo \"missing $APP\" >&2; exit 3; }\n" +
                "export DYLD_LIBRARY_PATH=\"$LIB${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}\"\n" +
                "export DYLD_FALLBACK_LIBRARY_PATH=\"$LIB${DYLD_FALLBACK_LIBRARY_PATH:+:$DYLD_FALLBACK_LIBRARY_PATH}\"\n" +
                "export OCG_VPN_WORKER=1\n" +
                "export OCG_VPN_SESSION=\"$SESSION\"\n" +
                "exec \"$APP\" --vpn-worker \"$SESSION\"\n").ConfigureAwait(false);

            TryChmod(ocRun, "755");
            TryChmod(vpnc, "755");
            TryChmod(stock, "755");
            TryChmod(host, "755");
            TryChmod(Path.Combine(hostAppSrc, "ocg-vpnhost"), "755");

            await File.WriteAllTextAsync(sudoers,
                "# OpenConnect Gui — Avalonia-free libopenconnect worker\n" +
                $"Defaults!{SplitScriptPath} !env_reset,!secure_path\n" +
                $"ALL ALL=(root) NOPASSWD:SETENV: {SplitScriptPath}\n" +
                $"ALL ALL=(root) NOPASSWD: {HelperPath}\n").ConfigureAwait(false);
            TryChmod(sudoers, "440");

            var installBody =
                "#!/bin/bash\n" +
                "set -euo pipefail\n" +
                "mkdir -p /Library/OpenConnectGui/lib /Library/OpenConnectGui/vpnhost\n" +
                $"cp '{EscapeSingle(ocRun)}' '{EscapeSingle(HelperPath)}'\n" +
                $"cp '{EscapeSingle(vpnc)}' '{EscapeSingle(SplitScriptPath)}'\n" +
                $"cp '{EscapeSingle(stock)}' '{EscapeSingle(StockVpncPath)}'\n" +
                $"cp '{EscapeSingle(host)}' '{EscapeSingle(VpnHostPath)}'\n" +
                $"rsync -a --delete '{EscapeSingle(hostAppSrc)}/' /Library/OpenConnectGui/vpnhost/\n" +
                $"cp -f '{EscapeSingle(libSrc)}'/*.dylib /Library/OpenConnectGui/lib/ 2>/dev/null || true\n" +
                "rm -f /Library/OpenConnectGui/host.env\n" +
                "chown -R root:wheel /Library/OpenConnectGui\n" +
                $"chmod 755 '{EscapeSingle(HelperPath)}' '{EscapeSingle(SplitScriptPath)}' '{EscapeSingle(StockVpncPath)}' '{EscapeSingle(VpnHostPath)}'\n" +
                "chmod 755 /Library/OpenConnectGui/vpnhost/ocg-vpnhost\n" +
                "chmod 755 /Library/OpenConnectGui/lib/*.dylib 2>/dev/null || true\n" +
                $"cp '{EscapeSingle(sudoers)}' /etc/sudoers.d/openconnect-gui\n" +
                "chown root:wheel /etc/sudoers.d/openconnect-gui\n" +
                "chmod 440 /etc/sudoers.d/openconnect-gui\n" +
                "/usr/sbin/visudo -cf /etc/sudoers.d/openconnect-gui\n" +
                "echo installed\n";

            await File.WriteAllTextAsync(installSh, installBody).ConfigureAwait(false);
            TryChmod(installSh, "755");

            RaiseLog("Info", "正在安装/升级权限助手 v13（无 UI worker，需输入一次密码）…");
            var result = await RunOsascriptAsync($"/bin/bash '{EscapeSingle(installSh)}'").ConfigureAwait(false);
            if (!result.Ok)
            {
                return (false, string.IsNullOrWhiteSpace(result.Error) ? "安装已取消或失败" : result.Error);
            }

            if (!IsHelperReady())
            {
                return (false, "助手已写入但版本校验失败（需要 v13+）");
            }

            RaiseLog("Info", "权限助手 v13 已就绪（Avalonia-free vpnhost）");
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

    private static string? FindPublishedVpnHostDir()
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                  == System.Runtime.InteropServices.Architecture.Arm64
            ? "osx-arm64"
            : "osx-x64";
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        // DMG/.app：Contents/MacOS/vpnhost；开发机：repo dist/vpnhost-$RID
        var candidates = new[]
        {
            Path.Combine(baseDir, "vpnhost"),
            Path.Combine(baseDir, "VpnHost"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "dist", $"vpnhost-{rid}")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "dist", $"vpnhost-{rid}")),
            Path.Combine(Directory.GetCurrentDirectory(), "dist", $"vpnhost-{rid}"),
        };
        return candidates.FirstOrDefault(d =>
            Directory.Exists(d) && File.Exists(Path.Combine(d, "ocg-vpnhost")));
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

        options ??= new VpnConnectOptions();
        SetState(VpnConnectionState.Connecting, "正在连接…");

        try
        {
            if (!IsHelperReady())
            {
                RaiseLog("Info", "权限助手未安装或需升级…");
                var install = await InstallHelperAsync().ConfigureAwait(false);
                if (!install.Ok)
                {
                    SetState(VpnConnectionState.Error, "需要安装/升级权限助手：" + install.Message);
                    return false;
                }
            }

            var split = options.SplitTunnelEnabled;
            var connectUrl = ResolveConnectUrl(server, options.GatewayNodeAddress);
            RaiseLog("Info", "模式=libopenconnect（root worker）");
            RaiseLog("Info", $"连接目标={connectUrl}");
            RaiseLog("Info", split ? "模式=智能分流" : "模式=全局");

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
            var connectedFile = Path.Combine(sessionDir, "connected.flag");

            await File.WriteAllTextAsync(passFile, password + "\n", new UTF8Encoding(false)).ConfigureAwait(false);
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

            var prep = await RunHelperAsync("prepare", sessionDir).ConfigureAwait(false);
            if (!prep.Ok)
            {
                SetState(VpnConnectionState.Error, "准备网络失败: " + prep.Error);
                return false;
            }

            RaiseLog("Info", "通过权限助手启动 libopenconnect worker（root）…");
            var started = await RunHelperAsync("lib-connect", sessionDir).ConfigureAwait(false);
            if (!started.Ok)
            {
                var helperErr = await ReadFileAsync(Path.Combine(sessionDir, "helper.err")).ConfigureAwait(false);
                SetState(VpnConnectionState.Error, "启动失败: " + FirstNonEmpty(helperErr, started.Error));
                return false;
            }

            var pid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            if (pid is null)
            {
                var log = await ReadLogTailAsync(logFile, 40).ConfigureAwait(false);
                SetState(VpnConnectionState.Error,
                    string.IsNullOrWhiteSpace(log) ? "worker 未能启动" : Truncate(log, 500));
                return false;
            }

            lock (_sync)
            {
                _pid = pid;
                _needsNetworkRestore = true;
            }

            RaiseLog("Info", $"worker pid={pid}");
            _monitorCts = new CancellationTokenSource();
            _ = TailLogAsync(logFile, _monitorCts.Token);
            _ = MonitorPidAsync(pid.Value, _monitorCts.Token);

            var timeout = split ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(90);
            var ok = await WaitUntilConnectedAsync(connectedFile, logFile, pid.Value, timeout).ConfigureAwait(false);

            try
            {
                File.Delete(passFile);
            }
            catch
            {
                // ignore
            }

            if (ok)
            {
                var tun = await ReadFileAsync(Path.Combine(sessionDir, "tundev.txt")).ConfigureAwait(false);
                TunInterfaceName = string.IsNullOrWhiteSpace(tun) ? "utun" : tun.Trim();
                SetState(VpnConnectionState.Connected, split ? "已连接（分流）" : "已连接");
                RaiseLog("Info", $"VPN 隧道已建立 ({TunInterfaceName})");
                return true;
            }

            await DisconnectAsync().ConfigureAwait(false);
            var err = await ReadFileAsync(Path.Combine(sessionDir, "helper.err")).ConfigureAwait(false);
            var tail = await ReadLogTailAsync(logFile, 30).ConfigureAwait(false);
            SetState(VpnConnectionState.Error, FirstNonEmpty(err, tail, "连接失败"));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "连接失败");
            SetState(VpnConnectionState.Error, "连接失败: " + ex.Message);
            return false;
        }
    }

    public async Task DisconnectAsync(string? savedServerUrl = null)
    {
        SetState(VpnConnectionState.Disconnecting, "正在断开…");
        RaiseLog("Info", "正在断开 VPN…");

        int? pid;
        string? session;
        lock (_sync)
        {
            pid = _pid;
            session = _sessionDir;
            _monitorCts?.Cancel();
        }

        if (!string.IsNullOrEmpty(session))
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(session, "stop.flag"), "1").ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        if (pid is > 0 && !string.IsNullOrEmpty(session))
        {
            await RunHelperAsync("disconnect", pid.Value.ToString(), session).ConfigureAwait(false);
            await RunHelperAsync("purge-routes", session).ConfigureAwait(false);
        }
        else if (!string.IsNullOrEmpty(session))
        {
            await RunHelperAsync("restore", session).ConfigureAwait(false);
            await RunHelperAsync("purge-routes", session).ConfigureAwait(false);
        }
        else
        {
            await RunHelperAsync("purge-routes").ConfigureAwait(false);
        }

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
            bool needs;
            lock (_sync)
            {
                needs = _needsNetworkRestore || _pid is > 0 ||
                        _state is VpnConnectionState.Connected or VpnConnectionState.Connecting;
            }

            if (needs)
            {
                DisconnectAsync().GetAwaiter().GetResult();
            }
        }
        catch
        {
            // ignore
        }
    }

    private async Task<bool> WaitUntilConnectedAsync(
        string connectedFile, string logFile, int pid, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (File.Exists(connectedFile))
            {
                return true;
            }

            if (!IsPidAlive(pid))
            {
                return false;
            }

            var err = await ReadFileAsync(Path.Combine(Path.GetDirectoryName(connectedFile)!, "helper.err"))
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(err))
            {
                return false;
            }

            await Task.Delay(400).ConfigureAwait(false);
        }

        _ = logFile;
        return File.Exists(connectedFile);
    }

    private async Task TailLogAsync(string logFile, CancellationToken ct)
    {
        long pos = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(logFile))
                {
                    await using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (fs.Length > pos)
                    {
                        fs.Seek(pos, SeekOrigin.Begin);
                        using var reader = new StreamReader(fs);
                        string? line;
                        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                        {
                            if (line.Contains("[Error]", StringComparison.Ordinal) ||
                                line.Contains("[Info]", StringComparison.Ordinal) ||
                                line.Contains("[Warning]", StringComparison.Ordinal))
                            {
                                var level = line.Contains("[Error]", StringComparison.Ordinal) ? "Error"
                                    : line.Contains("[Warning]", StringComparison.Ordinal) ? "Warning" : "Info";
                                var msg = line;
                                var idx = line.LastIndexOf("] ", StringComparison.Ordinal);
                                if (idx > 0 && idx + 2 < line.Length)
                                {
                                    msg = line[(idx + 2)..];
                                }

                                RaiseLog(level, msg);
                            }
                        }

                        pos = fs.Position;
                    }
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task MonitorPidAsync(int pid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!IsPidAlive(pid))
            {
                if (CurrentState == VpnConnectionState.Connected)
                {
                    SetState(VpnConnectionState.Error, "VPN worker 已退出");
                }

                break;
            }

            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<(bool Ok, string Error)> RunHelperAsync(
        string command, string? arg1 = null, string? arg2 = null)
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
                return (false, FirstNonEmpty(stderr, stdout, $"helper exit={p.ExitCode}"));
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                RaiseLog("Info", stdout.Trim());
            }

            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<(bool Ok, string Error)> RunOsascriptAsync(string shellCommand)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/osascript",
            ArgumentList =
            {
                "-e",
                $"do shell script \"{shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges"
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi);
        if (p is null)
        {
            return (false, "无法启动 osascript");
        }

        var err = await p.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        return p.ExitCode == 0 ? (true, "") : (false, err.Trim());
    }

    private static async Task<int?> WaitForPidAsync(string pidFile, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (File.Exists(pidFile) &&
                int.TryParse((await File.ReadAllTextAsync(pidFile).ConfigureAwait(false)).Trim(), out var pid) &&
                pid > 0)
            {
                return pid;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }

        return null;
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> ReadFileAsync(string path)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> ReadLogTailAsync(string path, int lines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            var all = await File.ReadAllLinesAsync(path).ConfigureAwait(false);
            return string.Join('\n', all.TakeLast(lines));
        }
        catch
        {
            return "";
        }
    }

    private static string? FindBundled(string fileName)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Native", fileName),
            Path.Combine(baseDir, fileName),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Native", fileName)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ResolveConnectUrl(string server, string? gateway)
    {
        if (string.IsNullOrWhiteSpace(gateway))
        {
            return server;
        }

        if (gateway.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            gateway.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return gateway;
        }

        var scheme = server.Contains("://", StringComparison.Ordinal)
            ? server.Split("://")[0]
            : "https";
        return $"{scheme}://{gateway}";
    }

    private static void TryChmod(string path, string mode)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/chmod",
                ArgumentList = { mode, path },
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(2000);
        }
        catch
        {
            // ignore
        }
    }

    private static string EscapeSingle(string s) => s.Replace("'", "'\\''");
    private static string ShellQuote(string s) => "'" + EscapeSingle(s) + "'";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private void SetState(VpnConnectionState state, string message)
    {
        lock (_sync)
        {
            _state = state;
        }

        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
        {
            State = state,
            Message = message,
            IsError = state == VpnConnectionState.Error
        });
    }

    private void RaiseLog(string level, string message) =>
        LogMessage?.Invoke(this, new LogMessageEventArgs
        {
            Level = level,
            Message = message,
            Timestamp = DateTime.Now
        });
}
