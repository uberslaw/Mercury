using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Mercury;

public sealed class CatcherHttpsServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly string _authToken;
    private readonly string _destinationFolder;
    private readonly Action<string>? _log;
    private readonly Action<CatcherTransferInfo>? _onTransfer;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _acceptLoop;
    private int _busy;
    private volatile int _readyToReceive;

    public CatcherHttpsServer(
        IPAddress bindAddress,
        int port,
        X509Certificate2 certificate,
        string authToken,
        string destinationFolder,
        Action<string>? log = null,
        Action<CatcherTransferInfo>? onTransfer = null)
    {
        _certificate = certificate;
        _authToken = authToken;
        _destinationFolder = destinationFolder;
        _log = log;
        _onTransfer = onTransfer;
        _listener = new TcpListener(bindAddress, port);
        BindAddress = bindAddress;
        Port = port;
    }

    public IPAddress BindAddress { get; }
    public int Port { get; }
    public bool IsListening { get; private set; }

    /// <summary>Health GET /v1/ready returns 200 only when this is on. Auth is still required.</summary>
    public bool ReadyToReceive
    {
        get => _readyToReceive != 0;
        set => Interlocked.Exchange(ref _readyToReceive, value ? 1 : 0);
    }

    public CatcherTransferInfo? LastTransfer { get; private set; }
    public string? LastError { get; private set; }

    public string EndpointDisplay => $"{BindAddress}:{Port}";

    public void Start()
    {
        Directory.CreateDirectory(_destinationFolder);
        _listener.Start();
        IsListening = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
        _log?.Invoke($"Listening HTTPS on {EndpointDisplay} (TLS 1.3 preferred).");
    }

    public async ValueTask DisposeAsync()
    {
        IsListening = false;
        _lifetime.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // already stopped
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 }
                }, cancellationToken).ConfigureAwait(false);

                var head = await ReadHeadersAsync(ssl, cancellationToken).ConfigureAwait(false);
                if (head is null)
                {
                    return;
                }

                if (!Authorize(head.Authorization))
                {
                    _log?.Invoke("Rejected a connection that did not present the matching passphrase.");
                    await WriteJsonAsync(ssl, 401, "Unauthorized", new CatcherPushResult
                    {
                        Ok = false,
                        Error = "unauthorized"
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (head.Method == "GET" && head.Path.StartsWith(CatcherScheme.ReadyPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ReadyToReceive)
                    {
                        await WriteJsonAsync(ssl, 503, "Service Unavailable", new
                        {
                            ok = false,
                            listening = true,
                            ready = false,
                            error = "not ready to receive"
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await WriteJsonAsync(ssl, 200, "OK", new
                    {
                        ok = true,
                        listening = true,
                        ready = true,
                        fingerprintSha256 = CatcherCrypto.FingerprintSha256(_certificate)
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (head.Method == "PUT" && head.Path.StartsWith(CatcherScheme.PushPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ReadyToReceive)
                    {
                        await WriteJsonAsync(ssl, 503, "Service Unavailable", new CatcherPushResult
                        {
                            Ok = false,
                            Error = "not ready to receive"
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    if (head.ExpectContinue)
                    {
                        await WriteContinueAsync(ssl, cancellationToken).ConfigureAwait(false);
                    }

                    await HandlePushAsync(ssl, head, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(ssl, 404, "Not Found", new CatcherPushResult
                {
                    Ok = false,
                    Error = "not found"
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                LastError = Sanitize(ex.Message);
                _log?.Invoke($"Catcher connection failed: {LastError}");
            }
        }
    }

    private async Task HandlePushAsync(SslStream ssl, HttpHead head, CancellationToken cancellationToken)
    {
        if (head.ContentLength < 0)
        {
            await WriteJsonAsync(ssl, 411, "Length Required", new CatcherPushResult
            {
                Ok = false,
                Error = "Content-Length is required."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            await WriteJsonAsync(ssl, 503, "Service Unavailable", new CatcherPushResult
            {
                Ok = false,
                Error = "Catcher is already receiving a transfer."
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var fileName = CatcherCrypto.SafeFileName(head.FileName);
            var destPath = Path.Combine(_destinationFolder, fileName);
            Directory.CreateDirectory(_destinationFolder);
            var temp = destPath + ".mercury.tmp";
            try
            {
                string sha;
                await using (var dest = new FileStream(
                                 temp,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 HashUtil.BufferSize,
                                 FileOptions.SequentialScan | FileOptions.Asynchronous))
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    var remaining = head.ContentLength;
                    if (head.Leftover.Length > 0)
                    {
                        var take = (int)Math.Min(head.Leftover.Length, remaining);
                        await dest.WriteAsync(head.Leftover.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                        hasher.AppendData(head.Leftover.AsSpan(0, take));
                        remaining -= take;
                    }

                    var buffer = new byte[HashUtil.BufferSize];
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = await ssl.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new IOException("Connection closed before the whole file arrived.");
                        }

                        await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hasher.AppendData(buffer.AsSpan(0, read));
                        remaining -= read;
                    }

                    await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
                    sha = Convert.ToHexString(hasher.GetHashAndReset());
                }

                if (!string.IsNullOrWhiteSpace(head.Sha256) &&
                    !CatcherCrypto.FingerprintsEqual(sha, head.Sha256))
                {
                    TryDelete(temp);
                    await WriteJsonAsync(ssl, 409, "Conflict", new CatcherPushResult
                    {
                        Ok = false,
                        Error = "SHA-256 mismatch."
                    }, cancellationToken).ConfigureAwait(false);
                    Report(new CatcherTransferInfo
                    {
                        FileName = fileName,
                        Bytes = head.ContentLength,
                        Sha256 = sha,
                        Ok = false,
                        Message = "SHA-256 mismatch."
                    });
                    return;
                }

                var already = File.Exists(destPath) && CatcherCrypto.FingerprintsEqual(CatcherCrypto.Sha256File(destPath), sha);
                if (already)
                {
                    TryDelete(temp);
                    var reusedPath = destPath;
                    if (head.Unpack)
                    {
                        reusedPath = await UnpackTransportAsync(destPath, cancellationToken).ConfigureAwait(false);
                    }

                    var reused = new CatcherPushResult
                    {
                        Ok = true,
                        AlreadyReceived = true,
                        ReceivedBytes = head.ContentLength,
                        Sha256 = sha,
                        Path = reusedPath
                    };
                    Report(new CatcherTransferInfo
                    {
                        FileName = Path.GetFileName(reusedPath),
                        Bytes = reused.ReceivedBytes,
                        Sha256 = sha,
                        Ok = true,
                        Message = head.Unpack
                            ? "Already received; unpacked to a folder tree."
                            : "Already received (same SHA-256)."
                    });
                    await WriteJsonAsync(ssl, 200, "OK", reused, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (File.Exists(destPath))
                {
                    destPath = UniquePath(destPath);
                }

                File.Move(temp, destPath);
                var storedPath = destPath;
                if (head.Unpack)
                {
                    storedPath = await UnpackTransportAsync(destPath, cancellationToken).ConfigureAwait(false);
                }

                var result = new CatcherPushResult
                {
                    Ok = true,
                    ReceivedBytes = head.ContentLength,
                    Sha256 = sha,
                    Path = storedPath
                };
                Report(new CatcherTransferInfo
                {
                    FileName = Path.GetFileName(storedPath),
                    Bytes = result.ReceivedBytes,
                    Sha256 = sha,
                    Ok = true,
                    Message = head.Unpack
                        ? $"Unpacked {ByteFormatter.ToString(result.ReceivedBytes)} to a folder tree."
                        : $"Received {ByteFormatter.ToString(result.ReceivedBytes)}."
                });
                _log?.Invoke(head.Unpack
                    ? $"Unpacked into {storedPath}."
                    : $"Received {fileName} ({ByteFormatter.ToString(result.ReceivedBytes)}).");
                await WriteJsonAsync(ssl, 200, "OK", result, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(temp);
                LastError = Sanitize(ex.Message);
                _log?.Invoke($"Receive failed: {LastError}");
                await WriteJsonAsync(ssl, 500, "Internal Server Error", new CatcherPushResult
                {
                    Ok = false,
                    Error = LastError
                }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<string> UnpackTransportAsync(string zipPath, CancellationToken cancellationToken)
    {
        var destRoot = ZipPack.ExtractRootFromZip(zipPath);
        if (!File.Exists(zipPath))
        {
            return Directory.Exists(destRoot) ? destRoot : zipPath;
        }

        await ZipPack.ExtractDirectAsync(zipPath, destRoot, _log, cancellationToken).ConfigureAwait(false);
        ZipPack.DeleteTransport(zipPath);
        _log?.Invoke($"Unpacked into {destRoot}; removed transport zip.");
        return destRoot;
    }

    private void Report(CatcherTransferInfo info)
    {
        LastTransfer = info;
        _onTransfer?.Invoke(info);
    }

    private bool Authorize(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        const string prefix = CatcherScheme.AuthorizationScheme + " ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header[prefix.Length..].Trim();
        return CatcherCrypto.TokensEqual(token, _authToken);
    }

    private static async Task<HttpHead?> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var acc = new MemoryStream();
        var buffer = new byte[4096];
        while (acc.Length < 65_536)
        {
            var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            acc.Write(buffer, 0, n);
            var bytes = acc.ToArray();
            var end = IndexOfHeaderEnd(bytes);
            if (end < 0)
            {
                continue;
            }

            var headerText = Encoding.ASCII.GetString(bytes, 0, end);
            var leftover = bytes[(end + 4)..];
            return HttpHead.Parse(headerText, leftover);
        }

        throw new InvalidDataException("HTTP headers are too large.");
    }

    private static int IndexOfHeaderEnd(byte[] bytes)
    {
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteContinueAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        Stream stream,
        int status,
        string reason,
        object body,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(body, Json);
        var payload = Encoding.UTF8.GetBytes(json);
        var header =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string UniquePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{name}-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static string Sanitize(string message)
    {
        if (message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Mercury-PSK", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            return "authentication error";
        }

        return message;
    }

    private sealed class HttpHead
    {
        public string Method { get; init; } = "";
        public string Path { get; init; } = "";
        public long ContentLength { get; init; } = -1;
        public string? Authorization { get; init; }
        public string? FileName { get; init; }
        public string? Sha256 { get; init; }
        public bool ExpectContinue { get; init; }
        public bool Unpack { get; init; }
        public byte[] Leftover { get; init; } = [];

        public static HttpHead Parse(string headerText, byte[] leftover)
        {
            var lines = headerText.Split(["\r\n", "\n"], StringSplitOptions.None);
            if (lines.Length == 0)
            {
                throw new InvalidDataException("Empty HTTP request.");
            }

            var request = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (request.Length < 2)
            {
                throw new InvalidDataException("Malformed HTTP request line.");
            }

            string? authorization = null;
            string? fileName = null;
            string? sha = null;
            var expectContinue = false;
            var unpack = false;
            long contentLength = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var name = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(value, out var n))
                {
                    contentLength = n;
                }
                else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = value;
                }
                else if (name.Equals("X-Mercury-Filename", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = value;
                }
                else if (name.Equals("X-Mercury-Sha256", StringComparison.OrdinalIgnoreCase))
                {
                    sha = value;
                }
                else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase) &&
                         value.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    expectContinue = true;
                }
                else if (name.Equals("X-Mercury-Unpack", StringComparison.OrdinalIgnoreCase) &&
                         (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)))
                {
                    unpack = true;
                }
            }

            return new HttpHead
            {
                Method = request[0].ToUpperInvariant(),
                Path = request[1],
                ContentLength = contentLength,
                Authorization = authorization,
                FileName = fileName,
                Sha256 = sha,
                ExpectContinue = expectContinue,
                Unpack = unpack,
                Leftover = leftover
            };
        }
    }
}
