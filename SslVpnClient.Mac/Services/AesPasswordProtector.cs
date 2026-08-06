using System.Security.Cryptography;
using SslVpnClient.Abstractions;

namespace SslVpnClient.Mac.Services;

/// <summary>
/// 使用 AES-GCM 加密本地密码；密钥存放于 Application Support，权限 0600。
/// </summary>
public sealed class AesPasswordProtector : IPasswordProtector
{
    private readonly byte[] _key;

    public AesPasswordProtector()
    {
        var dir = GetMacConfigDirectory();
        Directory.CreateDirectory(dir);
        var keyPath = Path.Combine(dir, "secret.key");
        _key = LoadOrCreateKey(keyPath);
    }

    public string Protect(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return string.Empty;
        }

        var plaintext = System.Text.Encoding.UTF8.GetBytes(password);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(payload);
    }

    public string Unprotect(string? protectedPassword, string? legacyPlainPassword = null)
    {
        if (string.IsNullOrEmpty(protectedPassword))
        {
            return legacyPlainPassword ?? string.Empty;
        }

        try
        {
            var payload = Convert.FromBase64String(protectedPassword);
            if (payload.Length < 12 + 16)
            {
                return string.Empty;
            }

            var nonce = payload.AsSpan(0, 12);
            var tag = payload.AsSpan(12, 16);
            var ciphertext = payload.AsSpan(28);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetMacConfigDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, "Library", "Application Support", "OpenConnectGui");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        File.WriteAllBytes(path, key);
        TryChmod600(path);
        return key;
    }

    private static void TryChmod600(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/chmod",
                ArgumentList = { "600", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch
        {
            // ignore
        }
    }
}
