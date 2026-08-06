using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SslVpnClient.Mac.Native;

namespace SslVpnClient.Mac.Vpn;

/// <summary>
/// 在特权进程（root）内运行的 libopenconnect 引擎。
/// macOS 创建 utun 需要 root；GUI 进程仅负责拉起本引擎。
/// </summary>
internal static class OpenConnectLibEngine
{
    private const int DtlsAttemptPeriod = 60;

    public static int RunWorker(string sessionDir)
    {
        if (!Directory.Exists(sessionDir))
        {
            Console.Error.WriteLine("missing session dir");
            return 2;
        }

        if (geteuid() != 0)
        {
            Console.Error.WriteLine("ocg-vpnhost must run as root (via oc-run)");
            return 3;
        }

        var logPath = Path.Combine(sessionDir, "openconnect.log");
        var pidPath = Path.Combine(sessionDir, "openconnect.pid");
        var connectedPath = Path.Combine(sessionDir, "connected.flag");
        var stopPath = Path.Combine(sessionDir, "stop.flag");
        var errPath = Path.Combine(sessionDir, "helper.err");

        try
        {
            File.WriteAllText(pidPath, Environment.ProcessId.ToString());
            TryChmod(pidPath, "644");
        }
        catch
        {
            // ignore
        }

        void Log(string level, string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {msg}";
            // 只写文件：stdout 已被 oc-run nohup 重定向到同一日志，避免重复
            try
            {
                File.AppendAllText(logPath, line + "\n");
            }
            catch
            {
                Console.WriteLine(line);
            }
        }

        try
        {
            // Prefer installed libs next to host /Library/OpenConnectGui/lib
            var libDir = "/Library/OpenConnectGui/lib";
            if (Directory.Exists(libDir))
            {
                var dyld = Environment.GetEnvironmentVariable("DYLD_LIBRARY_PATH") ?? "";
                Environment.SetEnvironmentVariable(
                    "DYLD_LIBRARY_PATH",
                    string.IsNullOrEmpty(dyld) ? libDir : libDir + ":" + dyld);
            }

            NativeLibraryBootstrap.Initialize();
            NativeLibraryBootstrap.EnsureOpenConnectAvailable();

            var meta = ParseMeta(Path.Combine(sessionDir, "meta.env"));
            var user = meta.GetValueOrDefault("OCG_USER") ?? "";
            var url = meta.GetValueOrDefault("OCG_URL") ?? "";
            var split = meta.GetValueOrDefault("OCG_SPLIT") == "1";
            var password = File.Exists(Path.Combine(sessionDir, "pass.txt"))
                ? File.ReadAllText(Path.Combine(sessionDir, "pass.txt")).TrimEnd('\n', '\r')
                : "";

            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(password))
            {
                Log("Error", "missing user/url/password in session");
                File.WriteAllText(errPath, "missing credentials in session");
                return 4;
            }

            Environment.SetEnvironmentVariable("OCG_SPLIT", split ? "1" : "0");
            Environment.SetEnvironmentVariable("OCG_SESSION_DIR", sessionDir);
            Environment.SetEnvironmentVariable("OCG_ROUTE_LIST", Path.Combine(sessionDir, "routes.list"));
            var chn = meta.GetValueOrDefault("OCG_CHNROUTES");
            if (!string.IsNullOrEmpty(chn))
            {
                Environment.SetEnvironmentVariable("OCG_CHNROUTES", chn);
            }

            LoadPhysEnv(sessionDir);

            Log("Info", "模式=libopenconnect worker (root)");
            Log("Info", $"连接目标={url}");
            Log("Info", split ? "模式=智能分流" : "模式=全局");
            Log("Info", $"euid={geteuid()}");

            string lastError = "";
            OpenConnectNative.ValidateCertCallback validateCert = (_, reason) =>
            {
                Log("Warning", "证书提示（已继续）: " + reason);
                return 0;
            };

            var authCtx = new AuthFormContext(user, password);
            var authHandle = GCHandle.Alloc(authCtx);
            OpenConnectNative.ProcessAuthFormCallback processAuth = (priv, form) =>
            {
                try
                {
                    var h = GCHandle.FromIntPtr(priv);
                    if (h.Target is not AuthFormContext ctx)
                    {
                        return OpenConnectAuthStructs.FormResultErr;
                    }

                    return OpenConnectAuthFormHandler.ProcessAuthForm(priv, form, ctx.Username, ctx.Password);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    return OpenConnectAuthStructs.FormResultErr;
                }
            };

            var progressFn = OpenConnectProgressBridge.RegisterHandler((_, level, message) =>
            {
                if (string.IsNullOrEmpty(message))
                {
                    return;
                }

                message = message.TrimEnd('\r', '\n');
                if (level <= OpenConnectNative.ProgressLevelInfo &&
                    (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("not permitted", StringComparison.OrdinalIgnoreCase)))
                {
                    lastError = message;
                }

                if (level <= OpenConnectNative.ProgressLevelInfo)
                {
                    Log(level <= OpenConnectNative.ProgressLevelError ? "Error" : "Info", message);
                }
            });

            var vpnInfo = OpenConnectNative.openconnect_vpninfo_new(
                "AnyConnect-compatible OpenConnect VPN Agent",
                validateCert,
                null,
                processAuth,
                progressFn,
                GCHandle.ToIntPtr(authHandle));

            if (vpnInfo == IntPtr.Zero)
            {
                Log("Error", "无法创建 OpenConnect 上下文");
                return 5;
            }

            var cmdPipe = OpenConnectNative.openconnect_setup_cmd_pipe(vpnInfo);
            OpenConnectNative.openconnect_set_loglevel(vpnInfo, OpenConnectNative.ProgressLevelInfo);

            if (OpenConnectNative.openconnect_set_protocol(vpnInfo, "anyconnect") != 0 ||
                OpenConnectNative.openconnect_parse_url(vpnInfo, url) != 0)
            {
                Log("Error", string.IsNullOrEmpty(lastError) ? "解析 URL / 协议失败" : lastError);
                OpenConnectNative.openconnect_vpninfo_free(vpnInfo);
                authHandle.Free();
                return 6;
            }

            Log("Info", "正在进行身份验证…");
            if (OpenConnectNative.openconnect_obtain_cookie(vpnInfo) != 0)
            {
                Log("Error", string.IsNullOrEmpty(lastError) ? "身份验证失败" : lastError);
                File.WriteAllText(errPath, lastError);
                OpenConnectNative.openconnect_vpninfo_free(vpnInfo);
                authHandle.Free();
                return 7;
            }

            Log("Info", "正在建立安全通道 (TLS)…");
            if (OpenConnectNative.openconnect_make_cstp_connection(vpnInfo) != 0)
            {
                Log("Error", string.IsNullOrEmpty(lastError) ? "建立 TLS 失败" : lastError);
                File.WriteAllText(errPath, lastError);
                OpenConnectNative.openconnect_vpninfo_free(vpnInfo);
                authHandle.Free();
                return 8;
            }

            if (OpenConnectNative.openconnect_setup_dtls(vpnInfo, DtlsAttemptPeriod) != 0)
            {
                Log("Warning", "DTLS 失败，已回退 TCP/TLS");
            }

            // openconnect 调用 vpnc-script 时会重建环境，OCG_* 传不进去。
            // 用「会话目录内 wrapper」：从 $0 还原 session，再 source ocg.env。
            WriteSessionOcgEnv(sessionDir, split, meta.GetValueOrDefault("OCG_CHNROUTES"));
            var script = WriteSessionVpncWrapper(sessionDir);
            Log("Info", "正在创建 TUN（root）…");
            Log("Info", split
                ? $"vpnc-script={script} (split=1, chnroutes via session)"
                : $"vpnc-script={script} (split=0)");
            if (OpenConnectNative.openconnect_setup_tun_device(vpnInfo, script, null) != 0)
            {
                var err = string.IsNullOrEmpty(lastError) ? "TUN/路由配置失败" : lastError;
                Log("Error", err);
                File.WriteAllText(errPath, err);
                OpenConnectNative.openconnect_vpninfo_free(vpnInfo);
                authHandle.Free();
                return 9;
            }

            // 分流校验：应出现 half-defaults / chnroutes.path
            if (split)
            {
                var chnPathFile = Path.Combine(sessionDir, "chnroutes.path");
                var routesList = Path.Combine(sessionDir, "routes.list");
                if (!File.Exists(chnPathFile) && !File.Exists(routesList))
                {
                    Log("Warning", "分流路由似乎未写入（缺少 chnroutes.path/routes.list），请检查 ocg-vpnc 日志");
                }
                else
                {
                    Log("Info", "分流路由已应用（检测到 session 路由标记）");
                }
            }

            var ifPtr = OpenConnectNative.openconnect_get_ifname(vpnInfo);
            var ifName = ifPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ifPtr) : "utun";
            if (!string.IsNullOrEmpty(ifName))
            {
                File.WriteAllText(Path.Combine(sessionDir, "tundev.txt"), ifName);
            }

            File.WriteAllText(connectedPath, $"ok if={ifName}\n");
            TryChmod(connectedPath, "644");
            Log("Info", $"VPN 隧道已建立 ({ifName}) — libopenconnect worker");

            // Remove password from disk once connected
            try
            {
                File.Delete(Path.Combine(sessionDir, "pass.txt"));
            }
            catch
            {
                // ignore
            }

            while (!File.Exists(stopPath))
            {
                var result = OpenConnectNative.openconnect_mainloop(
                    vpnInfo, 5, OpenConnectNative.ReconnectIntervalMin);
                if (result < 0)
                {
                    Log("Warning", "mainloop exited");
                    break;
                }
            }

            if (OpenConnectNative.IsValidCmdPipe(cmdPipe))
            {
                var cmd = new[] { OpenConnectNative.CmdCancel };
                _ = write(cmdPipe.ToInt32(), cmd, cmd.Length);
            }

            OpenConnectNative.openconnect_vpninfo_free(vpnInfo);
            authHandle.Free();
            Log("Info", "worker stopped");
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(errPath, ex.Message);
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] [Error] {ex}\n");
            }
            catch
            {
                // ignore
            }

            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void WriteSessionOcgEnv(string sessionDir, bool split, string? chnRoutes)
    {
        var gw = File.Exists(Path.Combine(sessionDir, "phys-gw.txt"))
            ? File.ReadAllText(Path.Combine(sessionDir, "phys-gw.txt")).Trim()
            : "";
        var iff = File.Exists(Path.Combine(sessionDir, "phys-if.txt"))
            ? File.ReadAllText(Path.Combine(sessionDir, "phys-if.txt")).Trim()
            : "";
        if (string.IsNullOrEmpty(chnRoutes))
        {
            var local = Path.Combine(sessionDir, "chnroutes-v4");
            if (File.Exists(local))
            {
                chnRoutes = local;
            }
        }

        var sb = new StringBuilder();
        sb.Append("OCG_SESSION_DIR='").Append(EscapeSh(sessionDir)).Append("'\n");
        sb.Append("OCG_SPLIT='").Append(split ? "1" : "0").Append("'\n");
        sb.Append("OCG_ROUTE_LIST='").Append(EscapeSh(Path.Combine(sessionDir, "routes.list"))).Append("'\n");
        if (!string.IsNullOrEmpty(chnRoutes))
        {
            sb.Append("OCG_CHNROUTES='").Append(EscapeSh(chnRoutes)).Append("'\n");
        }

        if (!string.IsNullOrEmpty(gw))
        {
            sb.Append("OCG_PHYS_GW='").Append(EscapeSh(gw)).Append("'\n");
        }

        if (!string.IsNullOrEmpty(iff))
        {
            sb.Append("OCG_PHYS_IF='").Append(EscapeSh(iff)).Append("'\n");
        }

        File.WriteAllText(Path.Combine(sessionDir, "ocg.env"), sb.ToString());
        File.WriteAllText(Path.Combine(sessionDir, "split.flag"), split ? "1\n" : "0\n");
        TryChmod(Path.Combine(sessionDir, "ocg.env"), "644");
        TryChmod(Path.Combine(sessionDir, "split.flag"), "644");
    }

    private static string WriteSessionVpncWrapper(string sessionDir)
    {
        var wrap = Path.Combine(sessionDir, "ocg-vpnc-wrap");
        // $0 在 openconnect 清环境后仍可用 → 用 dirname 找回 session
        var body =
            "#!/bin/bash\n" +
            "set -euo pipefail\n" +
            "SESSION=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\n" +
            "export OCG_SESSION_DIR=\"$SESSION\"\n" +
            "if [[ -f \"$SESSION/ocg.env\" ]]; then\n" +
            "  set -a\n" +
            "  # shellcheck disable=SC1091\n" +
            "  source \"$SESSION/ocg.env\"\n" +
            "  set +a\n" +
            "fi\n" +
            "export OCG_SESSION_DIR=\"$SESSION\"\n" +
            "LOG=\"$SESSION/openconnect.log\"\n" +
            "{\n" +
            "  echo \"[ocg-wrap] session=$SESSION split=${OCG_SPLIT:-?} chn=${OCG_CHNROUTES:-}\"\n" +
            "} >> \"$LOG\" 2>/dev/null || true\n" +
            "exec /Library/OpenConnectGui/ocg-vpnc-script\n";
        File.WriteAllText(wrap, body);
        TryChmod(wrap, "755");
        return wrap;
    }

    private static string EscapeSh(string s) => s.Replace("'", "'\\''");

    private static Dictionary<string, string> ParseMeta(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return dict;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq];
            var val = line[(eq + 1)..].Trim();
            if (val.Length >= 2 && val[0] == '\'' && val[^1] == '\'')
            {
                val = val[1..^1].Replace("'\\''", "'");
            }

            dict[key] = val;
        }

        return dict;
    }

    private static void LoadPhysEnv(string sessionDir)
    {
        static string? Read(string path) =>
            File.Exists(path) ? File.ReadAllText(path).Trim() : null;

        var gw = Read(Path.Combine(sessionDir, "phys-gw.txt"));
        var iff = Read(Path.Combine(sessionDir, "phys-if.txt"));
        if (!string.IsNullOrEmpty(gw))
        {
            Environment.SetEnvironmentVariable("OCG_PHYS_GW", gw);
        }

        if (!string.IsNullOrEmpty(iff))
        {
            Environment.SetEnvironmentVariable("OCG_PHYS_IF", iff);
        }
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

    private sealed class AuthFormContext(string username, string password)
    {
        public string Username { get; } = username;
        public string Password { get; } = password;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);
}
