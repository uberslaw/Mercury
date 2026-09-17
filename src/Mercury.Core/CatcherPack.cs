using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Mercury;

public sealed class CatcherUnwrapped : IDisposable
{
    public CatcherEnvelope Envelope { get; init; } = new();
    public X509Certificate2 Certificate { get; init; } = null!;
    public string AuthToken { get; init; } = "";

    public void Dispose() => Certificate.Dispose();
}

public static class CatcherPack
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static CatcherEnvelope Create(
        string name,
        string publicHost,
        int publicPort,
        int internalListenPort,
        string? bindAddress,
        string passphrase)
    {
        CatcherCrypto.ValidatePassphrase(passphrase);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Template name is required.");
        }

        if (string.IsNullOrWhiteSpace(publicHost))
        {
            throw new ArgumentException(
                "Public host / WAN IP is required. Use the hostname or public IP the sender will connect to — not a blank field. 127.0.0.1 only works on this PC.");
        }

        if (publicPort is < 1 or > 65535 || internalListenPort is < 1 or > 65535)
        {
            throw new ArgumentException("Ports must be between 1 and 65535.");
        }

        using var cert = CatcherCrypto.CreateSelfSigned(publicHost.Trim());
        return Wrap(new CatcherEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Scheme = CatcherScheme.HttpsTls13,
            PublicHost = publicHost.Trim(),
            PublicPort = publicPort,
            InternalListenPort = internalListenPort,
            BindAddress = (bindAddress ?? "").Trim(),
            FingerprintSha256 = CatcherCrypto.FingerprintSha256(cert),
            AuthSalt = CatcherCrypto.ToBase64(CatcherCrypto.RandomBytes(16))
        }, cert, passphrase);
    }

    public static CatcherEnvelope Wrap(CatcherEnvelope envelope, X509Certificate2 cert, string passphrase)
    {
        CatcherCrypto.ValidatePassphrase(passphrase);
        var (pfx, pfxPassword) = CatcherCrypto.ExportPfx(cert);
        var secretsJson = JsonSerializer.Serialize(new SecretPayload
        {
            Pfx = CatcherCrypto.ToBase64(pfx),
            PfxPassword = pfxPassword
        }, Json);

        var salt = CatcherCrypto.RandomBytes(16);
        var (ciphertext, nonce, tag) = CatcherCrypto.Wrap(
            Encoding.UTF8.GetBytes(secretsJson),
            passphrase,
            salt,
            CatcherScheme.KdfIterations);

        envelope.Format = CatcherScheme.Format;
        envelope.Version = CatcherScheme.Version;
        envelope.Scheme = CatcherScheme.HttpsTls13;
        envelope.FingerprintSha256 = CatcherCrypto.FingerprintSha256(cert);
        if (string.IsNullOrWhiteSpace(envelope.AuthSalt))
        {
            envelope.AuthSalt = CatcherCrypto.ToBase64(CatcherCrypto.RandomBytes(16));
        }

        envelope.Kdf = new CatcherKdf
        {
            Alg = "PBKDF2-SHA256",
            Iterations = CatcherScheme.KdfIterations,
            Salt = CatcherCrypto.ToBase64(salt)
        };
        envelope.Secrets = new CatcherSecretBlob
        {
            Alg = "AES-256-GCM",
            Nonce = CatcherCrypto.ToBase64(nonce),
            Ciphertext = CatcherCrypto.ToBase64(ciphertext),
            Tag = CatcherCrypto.ToBase64(tag)
        };
        return envelope;
    }

    public static CatcherUnwrapped Unwrap(CatcherEnvelope envelope, string passphrase)
    {
        CatcherCrypto.ValidatePassphrase(passphrase);
        ValidateEnvelope(envelope);
        var plaintext = CatcherCrypto.Unwrap(
            CatcherCrypto.FromBase64(envelope.Secrets.Ciphertext),
            CatcherCrypto.FromBase64(envelope.Secrets.Nonce),
            CatcherCrypto.FromBase64(envelope.Secrets.Tag),
            passphrase,
            CatcherCrypto.FromBase64(envelope.Kdf.Salt),
            envelope.Kdf.Iterations <= 0 ? CatcherScheme.KdfIterations : envelope.Kdf.Iterations);

        SecretPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<SecretPayload>(Encoding.UTF8.GetString(plaintext), Json);
        }
        catch (JsonException ex)
        {
            throw new CatcherAuthException("Wrong passphrase or the Catcher file is damaged.", ex);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Pfx) || string.IsNullOrWhiteSpace(payload.PfxPassword))
        {
            throw new CatcherAuthException("Catcher secrets are missing from the pack.");
        }

        var cert = CatcherCrypto.ImportPfx(CatcherCrypto.FromBase64(payload.Pfx), payload.PfxPassword);
        if (!cert.HasPrivateKey)
        {
            cert.Dispose();
            throw new CatcherTlsException("Catcher certificate is missing a private key.");
        }
        var fingerprint = CatcherCrypto.FingerprintSha256(cert);
        if (!CatcherCrypto.FingerprintsEqual(fingerprint, envelope.FingerprintSha256))
        {
            cert.Dispose();
            throw new CatcherTlsException("Unwrapped certificate does not match the fingerprint in the Catcher file.");
        }

        return new CatcherUnwrapped
        {
            Envelope = envelope,
            Certificate = cert,
            AuthToken = CatcherCrypto.DeriveAuthToken(passphrase, envelope.AuthSalt)
        };
    }

    public static void ExportFile(CatcherEnvelope envelope, string path)
    {
        ValidateEnvelope(envelope);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(envelope, Json));
        var readBack = File.ReadAllText(path);
        var loaded = Parse(readBack);
        if (!string.Equals(loaded.Id, envelope.Id, StringComparison.Ordinal)
            || !CatcherCrypto.FingerprintsEqual(loaded.FingerprintSha256, envelope.FingerprintSha256))
        {
            throw new IOException("Catcher file was written but could not be read back correctly.");
        }
    }

    public static CatcherEnvelope LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Catcher file not found.", path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static CatcherEnvelope Parse(string json)
    {
        CatcherEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<CatcherEnvelope>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Not a Mercury Catcher file.", ex);
        }

        if (envelope is null)
        {
            throw new InvalidDataException("Not a Mercury Catcher file.");
        }

        ValidateEnvelope(envelope);
        return envelope;
    }

    public static CatcherUnwrapped ImportFile(string path, string passphrase) =>
        Unwrap(LoadFile(path), passphrase);

    public static void ValidateEnvelope(CatcherEnvelope envelope)
    {
        if (!string.Equals(envelope.Format, CatcherScheme.Format, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported Catcher format: {envelope.Format}");
        }

        if (envelope.Version != CatcherScheme.Version)
        {
            throw new InvalidDataException($"Unsupported Catcher version: {envelope.Version}");
        }

        if (!string.Equals(envelope.Scheme, CatcherScheme.HttpsTls13, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported encryption scheme: {envelope.Scheme}. v1 is {CatcherScheme.HttpsTls13}.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Id) || string.IsNullOrWhiteSpace(envelope.Name))
        {
            throw new InvalidDataException("Catcher template is missing an id or name.");
        }

        if (string.IsNullOrWhiteSpace(envelope.FingerprintSha256) ||
            CatcherCrypto.NormalizeFingerprint(envelope.FingerprintSha256).Length != 64)
        {
            throw new InvalidDataException("Catcher file is missing a TLS certificate fingerprint.");
        }

        if (string.IsNullOrWhiteSpace(envelope.AuthSalt) ||
            string.IsNullOrWhiteSpace(envelope.Kdf.Salt) ||
            string.IsNullOrWhiteSpace(envelope.Secrets.Ciphertext))
        {
            throw new InvalidDataException("Catcher file is missing wrapped secrets.");
        }
    }

    public static bool ContainsPassphrasePlaintext(string json, string passphrase) =>
        !string.IsNullOrEmpty(passphrase) &&
        json.Contains(passphrase, StringComparison.Ordinal);

    private sealed class SecretPayload
    {
        public string Pfx { get; set; } = "";
        public string PfxPassword { get; set; } = "";
    }
}
