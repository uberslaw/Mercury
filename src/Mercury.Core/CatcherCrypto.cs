using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Mercury;

public static class CatcherCrypto
{
    public static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Length < CatcherScheme.MinPassphraseLength)
        {
            throw new ArgumentException(
                $"Passphrase must be at least {CatcherScheme.MinPassphraseLength} characters. It wraps the Catcher TLS key and authenticates the sender.");
        }
    }

    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    public static string ToBase64(byte[] data) => Convert.ToBase64String(data);

    public static byte[] FromBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Catcher file is not valid Base64.", ex);
        }
    }

    public static string NormalizeFingerprint(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return "";
        }

        var chars = fingerprint.Where(c => !char.IsWhiteSpace(c) && c != ':').ToArray();
        return new string(chars).ToUpperInvariant();
    }

    public static bool FingerprintsEqual(string? left, string? right)
    {
        var a = Encoding.UTF8.GetBytes(NormalizeFingerprint(left ?? ""));
        var b = Encoding.UTF8.GetBytes(NormalizeFingerprint(right ?? ""));
        if (a.Length != b.Length || a.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string FingerprintSha256(X509Certificate2 cert)
    {
        var hash = SHA256.HashData(cert.RawData);
        return Convert.ToHexString(hash);
    }

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            HashUtil.BufferSize,
            FileOptions.SequentialScan);
        return Sha256Stream(stream);
    }

    public static string Sha256Stream(Stream stream)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashUtil.BufferSize];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations, int size)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            size);
    }

    public static string DeriveAuthToken(string passphrase, string authSaltBase64)
    {
        ValidatePassphrase(passphrase);
        var salt = FromBase64(authSaltBase64);
        var key = DeriveKey(passphrase, salt, CatcherScheme.KdfIterations, 32);
        return ToBase64(key);
    }

    public static bool TokensEqual(string? left, string? right)
    {
        var a = Encoding.UTF8.GetBytes(left ?? "");
        var b = Encoding.UTF8.GetBytes(right ?? "");
        if (a.Length != b.Length || a.Length == 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static (byte[] ciphertext, byte[] nonce, byte[] tag) Wrap(byte[] plaintext, string passphrase, byte[] salt, int iterations)
    {
        var key = DeriveKey(passphrase, salt, iterations, 32);
        var nonce = RandomBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        CryptographicOperations.ZeroMemory(key);
        return (ciphertext, nonce, tag);
    }

    public static byte[] Unwrap(byte[] ciphertext, byte[] nonce, byte[] tag, string passphrase, byte[] salt, int iterations)
    {
        var key = DeriveKey(passphrase, salt, iterations, 32);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }
        catch (CryptographicException ex)
        {
            throw new CatcherAuthException("Wrong passphrase or the Catcher file is damaged.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static X509Certificate2 CreateSelfSigned(string publicHost)
    {
        var cn = string.IsNullOrWhiteSpace(publicHost) ? "Mercury-Catcher" : publicHost.Trim();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={EscapeCn(cn)}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        var eku = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: false));
        request.CertificateExtensions.Add(BuildSan(publicHost).Build());

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));
        var password = Convert.ToHexString(RandomBytes(32));
        var pfx = cert.Export(X509ContentType.Pfx, password);
        cert.Dispose();
        return new X509Certificate2(
            pfx,
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    public static (byte[] pfx, string password) ExportPfx(X509Certificate2 cert)
    {
        var password = Convert.ToHexString(RandomBytes(32));
        var pfx = cert.Export(X509ContentType.Pfx, password);
        return (pfx, password);
    }

    public static X509Certificate2 ImportPfx(byte[] pfx, string password) =>
        new(pfx, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

    public static IPAddress ParseBindAddress(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return IPAddress.Any;
        }

        var trimmed = bindAddress.Trim();
        if (trimmed is "*" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (trimmed is "::" or "[::]")
        {
            return IPAddress.IPv6Any;
        }

        if (!IPAddress.TryParse(trimmed, out var ip))
        {
            throw new ArgumentException($"Bind address is not a valid IP: {trimmed}");
        }

        return ip;
    }

    public static bool IsLoopbackOnly(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return false;
        }

        if (!IPAddress.TryParse(bindAddress.Trim(), out var ip))
        {
            return false;
        }

        return IPAddress.IsLoopback(ip);
    }

    public static string FormatBaseUrl(string host, int port)
    {
        var trimmed = (host ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("Catcher public host is required.");
        }

        if (IPAddress.TryParse(trimmed, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"https://[{ip}]:{port}";
        }

        return $"https://{trimmed}:{port}";
    }

    public static string FormatDestination(string host, int port, string? name)
    {
        var url = FormatBaseUrl(host, port);
        return string.IsNullOrWhiteSpace(name) ? url : $"{url} ({name})";
    }

    public static string SafeFileName(string? name)
    {
        var file = Path.GetFileName(name ?? "");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "mercury-catch.bin";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            file = file.Replace(c, '_');
        }

        if (file is "." or ".." || file.Contains("..", StringComparison.Ordinal))
        {
            return "mercury-catch.bin";
        }

        return file;
    }

    private static SubjectAlternativeNameBuilder BuildSan(string publicHost)
    {
        var san = new SubjectAlternativeNameBuilder();
        var host = (publicHost ?? "").Trim();
        if (IPAddress.TryParse(host, out var ip))
        {
            san.AddIpAddress(ip);
        }
        else if (!string.IsNullOrWhiteSpace(host))
        {
            san.AddDnsName(host);
        }
        else
        {
            san.AddDnsName("Mercury-Catcher");
        }

        return san;
    }

    private static string EscapeCn(string cn) =>
        cn.Replace(",", " ").Replace("=", " ").Replace("\"", "");
}
