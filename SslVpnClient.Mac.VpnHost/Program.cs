using SslVpnClient.Mac.Vpn;

namespace SslVpnClient.Mac;

/// <summary>
/// 无 UI 的特权 VPN worker 入口。绝不引用 Avalonia。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var session = Environment.GetEnvironmentVariable("OCG_VPN_SESSION");

        if (string.IsNullOrWhiteSpace(session))
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is "--vpn-worker" or "vpn-worker")
                {
                    if (i + 1 < args.Length)
                    {
                        session = args[i + 1];
                    }

                    break;
                }

                if (args[i].StartsWith("--vpn-worker=", StringComparison.Ordinal))
                {
                    session = args[i]["--vpn-worker=".Length..];
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(session) && args.Length == 1 && !args[0].StartsWith('-'))
        {
            session = args[0];
        }

        if (string.IsNullOrWhiteSpace(session))
        {
            Console.Error.WriteLine("usage: ocg-vpnhost <sessionDir>");
            Console.Error.WriteLine("   or: OCG_VPN_SESSION=<dir> ocg-vpnhost");
            return 2;
        }

        Environment.SetEnvironmentVariable("OCG_VPN_WORKER", "1");
        Environment.SetEnvironmentVariable("OCG_VPN_SESSION", session);
        return OpenConnectLibEngine.RunWorker(session);
    }
}
