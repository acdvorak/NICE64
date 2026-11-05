namespace PicoSwitchController;

using System.Security.Cryptography;
using System.Text;

public sealed class BridgeApiProtectedTokenStore(string path) : IBridgeApiTokenProvider
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("NICE64.Bridge.LocalApi.Token.v1");
    private readonly object _sync = new();
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A token-store path is required.", nameof(path))
        : Path.GetFullPath(path);
    private string? _cachedToken;

    public string GetToken()
    {
        lock (_sync)
        {
            if (_cachedToken is not null) return _cachedToken;
            if (File.Exists(_path))
            {
                byte[] protectedBytes = File.ReadAllBytes(_path);
                byte[] tokenBytes = ProtectedData.Unprotect(
                    protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                string token = Encoding.UTF8.GetString(tokenBytes);
                if (token.Length < 32)
                    throw new InvalidDataException("The protected local API token is invalid.");
                return _cachedToken = token;
            }

            return _cachedToken = GenerateAndPersist();
        }
    }

    public string RotateToken()
    {
        lock (_sync)
        {
            return _cachedToken = GenerateAndPersist();
        }
    }

    private string GenerateAndPersist()
    {
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The token-store path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, protectedBytes);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
        return token;
    }
}
