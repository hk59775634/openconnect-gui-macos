namespace SslVpnClient.Services;

public static class AppPaths
{
    public static string GetConfigDirectory()
    {
        string dir;
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dir = Path.Combine(home, "Library", "Application Support", "OpenConnectGui");
        }
        else
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OpenConnectGui");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ConnectionConfigPath =>
        Path.Combine(GetConfigDirectory(), "connection-config.json");

    public static string GatewayNodesCachePath =>
        Path.Combine(GetConfigDirectory(), "gateway-nodes-cache.json");

    public static string SecretKeyPath =>
        Path.Combine(GetConfigDirectory(), "secret.key");
}
