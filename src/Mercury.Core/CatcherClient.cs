using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Mercury;

public static class CatcherClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task ReadyAsync(
        CatcherTarget target,
        string passphrase,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(target.FingerprintSha256);
        using var request = new HttpRequestMessage(HttpMethod.Get, ReadyUri(target))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        Authorize(request, target, passphrase);
        using var response = await SendPinnedAsync(client, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CatcherAuthException("Catcher rejected the passphrase.");
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            throw new IOException("Catcher is listening but not Ready to receive. Tick Ready to receive on the Catcher.");
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reachability probe. Always sends the derived auth token; never logs the passphrase.
    /// </summary>
    public static async Task<CatcherProbeResult> ProbeAsync(
        CatcherTarget target,
        string passphrase,
        CancellationToken cancellationToken)
    {
        try
        {
            CatcherCrypto.ValidatePassphrase(passphrase);
        }
        catch (Exception ex)
        {
            return new CatcherProbeResult(CatcherOnlineStatus.Unauthorized, ex.Message);
        }

        try
        {
            using var client = CreateHttpClient(target.FingerprintSha256);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReadyUri(target))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            Authorize(request, target, passphrase);
            using var response = await SendPinnedAsync(client, request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new CatcherProbeResult(CatcherOnlineStatus.Unauthorized, "Catcher rejected the passphrase.");
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new CatcherProbeResult(CatcherOnlineStatus.Listening, "Catcher is listening. Waiting for Ready to receive.");
            }

            if (response.IsSuccessStatusCode)
            {
                return new CatcherProbeResult(CatcherOnlineStatus.Ready, "Catcher is ready to receive.");
            }

            return new CatcherProbeResult(CatcherOnlineStatus.Offline, $"Catcher returned {(int)response.StatusCode}.");
        }
        catch (CatcherTlsException)
        {
            return new CatcherProbeResult(CatcherOnlineStatus.FingerprintMismatch,
                "TLS fingerprint does not match this template. Refusing to connect.");
        }
        catch (CatcherAuthException)
        {
            return new CatcherProbeResult(CatcherOnlineStatus.Unauthorized, "Catcher rejected the passphrase.");
        }
        catch (Exception)
        {
            return new CatcherProbeResult(CatcherOnlineStatus.Offline, "Catcher is offline or unreachable.");
        }
    }

    public static async Task<CatcherPushResult> PushAsync(
        CatcherTarget target,
        string passphrase,
        string filePath,
        string fileName,
        string sha256,
        BandwidthBudget? budget,
        string? jobId,
        double? maxBytesPerSecond,
        Action<long>? onChunk,
        CancellationToken cancellationToken,
        bool unpack = false)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Nothing to send to Catcher.", filePath);
        }

        var length = new FileInfo(filePath).Length;
        using var client = CreateHttpClient(target.FingerprintSha256);
        using var content = new ProgressFileContent(filePath, budget, jobId, maxBytesPerSecond, onChunk, cancellationToken);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = length;

        using var request = new HttpRequestMessage(HttpMethod.Put, PushUri(target))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = content
        };
        Authorize(request, target, passphrase);
        request.Headers.ExpectContinue = false;
        request.Headers.TryAddWithoutValidation("X-Mercury-Filename", CatcherCrypto.SafeFileName(fileName));
        request.Headers.TryAddWithoutValidation("X-Mercury-Sha256", CatcherCrypto.NormalizeFingerprint(sha256));
        if (unpack)
        {
            request.Headers.TryAddWithoutValidation("X-Mercury-Unpack", "1");
        }

        using var response = await SendPinnedAsync(client, request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CatcherAuthException("Catcher rejected the passphrase.");
        }

        CatcherPushResult? result = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                result = JsonSerializer.Deserialize<CatcherPushResult>(body, Json);
            }
            catch (JsonException)
            {
                result = null;
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = result?.Error ?? $"Catcher returned {(int)response.StatusCode} {response.ReasonPhrase}.";
            throw new IOException(error);
        }

        result ??= new CatcherPushResult { Ok = true, ReceivedBytes = length, Sha256 = sha256 };
        if (!string.IsNullOrWhiteSpace(result.Sha256) &&
            !CatcherCrypto.FingerprintsEqual(result.Sha256, sha256))
        {
            throw new IOException("Catcher SHA-256 does not match the file that was sent.");
        }

        return result;
    }

    public static Uri ReadyUri(CatcherTarget target) =>
        new(CatcherCrypto.FormatBaseUrl(target.PublicHost, target.PublicPort) + CatcherScheme.ReadyPath);

    public static Uri PushUri(CatcherTarget target) =>
        new(CatcherCrypto.FormatBaseUrl(target.PublicHost, target.PublicPort) + CatcherScheme.PushPath);

    private static void Authorize(HttpRequestMessage request, CatcherTarget target, string passphrase)
    {
        var token = CatcherCrypto.DeriveAuthToken(passphrase, target.AuthSalt);
        request.Headers.Authorization = new AuthenticationHeaderValue(CatcherScheme.AuthorizationScheme, token);
    }

    private static HttpClient CreateHttpClient(string expectedFingerprint)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    PinCertificate(cert, expectedFingerprint)
            }
        };

        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        client.DefaultRequestHeaders.ExpectContinue = false;
        return client;
    }

    private static bool PinCertificate(X509Certificate? cert, string expectedFingerprint)
    {
        if (cert is null)
        {
            return false;
        }

        if (cert is X509Certificate2 existing)
        {
            return CatcherCrypto.FingerprintsEqual(CatcherCrypto.FingerprintSha256(existing), expectedFingerprint);
        }

        using var cert2 = new X509Certificate2(cert);
        return CatcherCrypto.FingerprintsEqual(CatcherCrypto.FingerprintSha256(cert2), expectedFingerprint);
    }

    private static async Task<HttpResponseMessage> SendPinnedAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            throw new CatcherTlsException(
                "TLS certificate fingerprint does not match the Catcher template. Refusing to connect (possible MITM).",
                ex);
        }
        catch (AuthenticationException ex)
        {
            throw new CatcherTlsException(
                "TLS handshake failed. Catcher must present the certificate from the imported template.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CatcherTlsException(
                "Could not complete HTTPS to Catcher. Is it listening, and does the public host/port reach that listener?",
                ex);
        }
    }

    private sealed class ProgressFileContent : HttpContent
    {
        private readonly string _path;
        private readonly BandwidthBudget? _budget;
        private readonly string? _jobId;
        private readonly double? _maxBytesPerSecond;
        private readonly Action<long>? _onChunk;
        private readonly CancellationToken _cancellationToken;

        public ProgressFileContent(
            string path,
            BandwidthBudget? budget,
            string? jobId,
            double? maxBytesPerSecond,
            Action<long>? onChunk,
            CancellationToken cancellationToken)
        {
            _path = path;
            _budget = budget;
            _jobId = jobId;
            _maxBytesPerSecond = maxBytesPerSecond;
            _onChunk = onChunk;
            _cancellationToken = cancellationToken;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var file = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                HashUtil.BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);
            var buffer = new byte[HashUtil.BufferSize];
            int read;
            while ((read = await file.ReadAsync(buffer.AsMemory(0, buffer.Length), _cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (_budget is not null && !string.IsNullOrEmpty(_jobId))
                {
                    await _budget.ConsumeAsync(_jobId, _maxBytesPerSecond, read, _cancellationToken).ConfigureAwait(false);
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), _cancellationToken).ConfigureAwait(false);
                _onChunk?.Invoke(read);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = new FileInfo(_path).Length;
            return true;
        }
    }
}
