using System.Runtime.InteropServices;
using Avalonia;
using SslVpnClient.Mac.Vpn;

namespace SslVpnClient.Mac;

sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 1) 环境变量（推荐：避免 dotnet exec 吞掉 --vpn-worker）
        var sessionFromEnv = Environment.GetEnvironmentVariable("OCG_VPN_SESSION");
        if (string.Equals(Environment.GetEnvironmentVariable("OCG_VPN_WORKER"), "1", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(sessionFromEnv))
        {
            return OpenConnectLibEngine.RunWorker(sessionFromEnv);
        }

        // 2) 命令行：兼容 --vpn-worker <dir> / --vpn-worker=<dir>
        if (TryParseVpnWorkerArgs(args, out var sessionFromArgs))
        {
            return OpenConnectLibEngine.RunWorker(sessionFromArgs!);
        }

        // 3) 安全阀：root 下禁止启动 GUI（否则会出现「第二个界面」并乱改路由）
        if (GetEuid() == 0)
        {
            Console.Error.WriteLine(
                "Refusing to start Avalonia UI as root. " +
                "Use: OCG_VPN_WORKER=1 OCG_VPN_SESSION=<dir> or --vpn-worker <dir>");
            return 99;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static bool TryParseVpnWorkerArgs(string[] args, out string? session)
    {
        session = null;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--vpn-worker" or "vpn-worker")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    session = args[i + 1];
                    return !string.IsNullOrWhiteSpace(session);
                }

                return false;
            }

            const string prefix = "--vpn-worker=";
            if (a.StartsWith(prefix, StringComparison.Ordinal))
            {
                session = a[prefix.Length..];
                return !string.IsNullOrWhiteSpace(session);
            }
        }

        return false;
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static uint GetEuid()
    {
        try
        {
            return geteuid();
        }
        catch
        {
            return 1;
        }
    }
}
