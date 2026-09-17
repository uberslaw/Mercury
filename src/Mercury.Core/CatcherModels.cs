namespace Mercury;

/// <summary>v1: sender connects outbound to a Catcher that is already listening.</summary>
public static class CatcherScheme
{
    public const string Format = "mercury-catch";
    public const int Version = 1;
    public const string HttpsTls13 = "https-tls1.3";
    public const string FileExtension = ".mercury-catch";
    public const string AuthorizationScheme = "Mercury-PSK";
    public const string ReadyPath = "/v1/ready";
    public const string PushPath = "/v1/push";
    public const int DefaultPublicPort = 443;
    public const int DefaultInternalPort = 8443;
    public const int KdfIterations = 210_000;
    public const int MinPassphraseLength = 8;
}

public sealed class CatcherTarget
{
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; } = CatcherScheme.DefaultPublicPort;
    public string FingerprintSha256 { get; set; } = "";
    public string AuthSalt { get; set; } = "";
}

public sealed class CatcherKdf
{
    public string Alg { get; set; } = "PBKDF2-SHA256";
    public int Iterations { get; set; } = CatcherScheme.KdfIterations;
    public string Salt { get; set; } = "";
}

public sealed class CatcherSecretBlob
{
    public string Alg { get; set; } = "AES-256-GCM";
    public string Nonce { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public string Tag { get; set; } = "";
}

/// <summary>
/// Export/import file. Public routing fields are plaintext; the TLS private key is wrapped.
/// The user passphrase is never stored in this document.
/// </summary>
public sealed class CatcherEnvelope
{
    public string Format { get; set; } = CatcherScheme.Format;
    public int Version { get; set; } = CatcherScheme.Version;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Scheme { get; set; } = CatcherScheme.HttpsTls13;
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; } = CatcherScheme.DefaultPublicPort;
    public int InternalListenPort { get; set; } = CatcherScheme.DefaultInternalPort;
    public string BindAddress { get; set; } = "";
    public string FingerprintSha256 { get; set; } = "";
    public string AuthSalt { get; set; } = "";
    public CatcherKdf Kdf { get; set; } = new();
    public CatcherSecretBlob Secrets { get; set; } = new();

    public string Display =>
        string.IsNullOrWhiteSpace(Name)
            ? $"{PublicHost}:{PublicPort}"
            : $"{Name}  —  {PublicHost}:{PublicPort} → :{InternalListenPort}";

    public CatcherTarget ToTarget() =>
        new()
        {
            TemplateId = Id,
            TemplateName = Name,
            PublicHost = PublicHost,
            PublicPort = PublicPort,
            FingerprintSha256 = FingerprintSha256,
            AuthSalt = AuthSalt
        };

    public string PortForwardHint()
    {
        var wan = string.IsNullOrWhiteSpace(PublicHost) ? "public-ip-or-hostname" : PublicHost;
        var lan = string.IsNullOrWhiteSpace(BindAddress) ? "catcher-LAN-ip" : BindAddress;
        return $"Router / NAT: forward {wan}:{PublicPort} → {lan}:{InternalListenPort} (HTTPS). Catcher must be listening. Mercury does not punch through NAT by itself.";
    }
}

public sealed class CatcherCatalog
{
    public List<CatcherEnvelope> Templates { get; set; } = [];
}

public sealed class CatcherLocalSettings
{
    public string DestinationFolder { get; set; } = "";
    public string? LastTemplateId { get; set; }
}

public sealed class CatcherPushResult
{
    public bool Ok { get; init; }
    public bool AlreadyReceived { get; init; }
    public long ReceivedBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Error { get; init; }
}

public sealed class CatcherTransferInfo
{
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;
    public string FileName { get; init; } = "";
    public long Bytes { get; init; }
    public string Sha256 { get; init; } = "";
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
}

public sealed class CatcherTlsException : IOException
{
    public CatcherTlsException(string message) : base(message)
    {
    }

    public CatcherTlsException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class CatcherAuthException : IOException
{
    public CatcherAuthException(string message) : base(message)
    {
    }

    public CatcherAuthException(string message, Exception inner) : base(message, inner)
    {
    }
}

public enum CatcherOnlineStatus
{
    Unknown,
    Offline,
    FingerprintMismatch,
    Unauthorized,
    Listening,
    Ready
}

public readonly record struct CatcherProbeResult(CatcherOnlineStatus Status, string Message);
