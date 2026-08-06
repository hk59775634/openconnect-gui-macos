namespace SslVpnClient.Abstractions;

public interface IPasswordProtector
{
    string Protect(string? password);
    string Unprotect(string? protectedPassword, string? legacyPlainPassword = null);
}
