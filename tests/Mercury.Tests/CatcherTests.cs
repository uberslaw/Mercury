using System.Net;
using System.Net.Sockets;

namespace Mercury.Tests;

public class CatcherPackTests
{
    [Fact]
    public void ExportRoundtripsAndOmitsPassphrase()
    {
        const string passphrase = "test-passphrase-9f3a-not-in-file";
        var envelope = CatcherPack.Create(
            "Warehouse",
            "203.0.113.10",
            443,
            8443,
            "",
            passphrase);

        var path = Path.Combine(Path.GetTempPath(), "mercury-catch-" + Guid.NewGuid().ToString("N") + CatcherScheme.FileExtension);
        try
        {
            CatcherPack.ExportFile(envelope, path);
            var json = File.ReadAllText(path);
            Assert.False(CatcherPack.ContainsPassphrasePlaintext(json, passphrase));
            Assert.DoesNotContain("test-passphrase", json, StringComparison.Ordinal);
            Assert.Contains("\"format\": \"mercury-catch\"", json);
            Assert.Contains("https-tls1.3", json);
            Assert.Equal(64, CatcherCrypto.NormalizeFingerprint(envelope.FingerprintSha256).Length);

            using var opened = CatcherPack.ImportFile(path, passphrase);
            Assert.Equal(envelope.Id, opened.Envelope.Id);
            Assert.True(CatcherCrypto.FingerprintsEqual(
                CatcherCrypto.FingerprintSha256(opened.Certificate),
                envelope.FingerprintSha256));
            Assert.Equal(CatcherCrypto.DeriveAuthToken(passphrase, envelope.AuthSalt), opened.AuthToken);
        }
        finally
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
                // temp leftover is OK
            }
        }
    }

    [Fact]
    public void WrongPassphraseFails()
    {
        var envelope = CatcherPack.Create("Lab", "127.0.0.1", 8443, 8443, "127.0.0.1", "correct-horse-battery");
        Assert.Throws<CatcherAuthException>(() => CatcherPack.Unwrap(envelope, "wrong-horse-battery"));
    }

    [Fact]
    public void DefaultBindIsAllInterfacesNotLoopback()
    {
        Assert.Equal(IPAddress.Any, CatcherCrypto.ParseBindAddress(""));
        Assert.False(CatcherCrypto.IsLoopbackOnly(""));
        Assert.True(CatcherCrypto.IsLoopbackOnly("127.0.0.1"));
    }
}

public class CatcherHttpsTests
{
    [Fact]
    public async Task PushLandsInCatcherFolder()
    {
        const string passphrase = "catcher-e2e-passphrase";
        var envelope = CatcherPack.Create("Loopback", "127.0.0.1", 1, 1, "127.0.0.1", passphrase);
        var port = FreePort();
        envelope.PublicPort = port;
        envelope.InternalListenPort = port;

        var root = Path.Combine(Path.GetTempPath(), "mercury-catcher-" + Guid.NewGuid().ToString("N"));
        var receive = Path.Combine(root, "received");
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        Directory.CreateDirectory(receive);
        File.WriteAllText(Path.Combine(src, "note.txt"), "hello catcher");
        File.WriteAllText(Path.Combine(src, "sub", "b.bin"), "payload");

        using var opened = CatcherPack.Unwrap(envelope, passphrase);
        await using var server = new CatcherHttpsServer(
            IPAddress.Loopback,
            port,
            opened.Certificate,
            opened.AuthToken,
            receive);
        server.Start();
        server.ReadyToReceive = true;

        CatcherSession.Remember(envelope.Id, passphrase);
        var data = Path.Combine(root, "app");
        Directory.CreateDirectory(data);
        var paths = new AppPaths(data);
        var job = new Job
        {
            Name = "catcher-e2e",
            SourcePath = src,
            DestinationPath = CatcherCrypto.FormatDestination("127.0.0.1", port, envelope.Name),
            Catcher = envelope.ToTarget(),
            Options = new JobOptions { RetryCount = 1, RetryWaitSeconds = 1, PackAsZip = true }
        };

        try
        {
            using var scheduler = new JobScheduler(paths);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await scheduler.StartAsync(job, resumeJournal: false, cts.Token);

            Assert.True(job.Status == JobStatus.Completed, $"status={job.Status} msg={job.ResultMessage} server={server.LastError}");
            Assert.Empty(Directory.GetFiles(receive, "*.zip"));
            Assert.Empty(Directory.GetFiles(receive, "*.mercury.tmp"));
            var tree = Path.Combine(receive, Path.GetFileName(src));
            Assert.True(File.Exists(Path.Combine(tree, "note.txt")), $"missing {tree}\\note.txt");
            Assert.True(File.Exists(Path.Combine(tree, "sub", "b.bin")));
            Assert.Equal("hello catcher", File.ReadAllText(Path.Combine(tree, "note.txt")));
        }
        finally
        {
            CatcherSession.Clear();
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task WrongFingerprintIsRejected()
    {
        const string passphrase = "catcher-pin-passphrase";
        var good = CatcherPack.Create("A", "127.0.0.1", 1, 1, "127.0.0.1", passphrase);
        var other = CatcherPack.Create("B", "127.0.0.1", 1, 1, "127.0.0.1", passphrase);
        var port = FreePort();
        using var opened = CatcherPack.Unwrap(good, passphrase);
        var receive = Path.Combine(Path.GetTempPath(), "mercury-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(receive);
        await using var server = new CatcherHttpsServer(
            IPAddress.Loopback, port, opened.Certificate, opened.AuthToken, receive);
        server.Start();
        server.ReadyToReceive = true;

        var target = good.ToTarget();
        target.PublicPort = port;
        target.FingerprintSha256 = other.FingerprintSha256;

        await Assert.ThrowsAsync<CatcherTlsException>(() =>
            CatcherClient.ReadyAsync(target, passphrase, CancellationToken.None));

        TryDeleteDir(receive);
    }

    [Fact]
    public async Task WrongPassphraseIsUnauthorized()
    {
        const string passphrase = "catcher-auth-passphrase";
        var envelope = CatcherPack.Create("Auth", "127.0.0.1", 1, 1, "127.0.0.1", passphrase);
        var port = FreePort();
        using var opened = CatcherPack.Unwrap(envelope, passphrase);
        var receive = Path.Combine(Path.GetTempPath(), "mercury-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(receive);
        await using var server = new CatcherHttpsServer(
            IPAddress.Loopback, port, opened.Certificate, opened.AuthToken, receive);
        server.Start();
        server.ReadyToReceive = true;

        var target = envelope.ToTarget();
        target.PublicPort = port;
        await Assert.ThrowsAsync<CatcherAuthException>(() =>
            CatcherClient.ReadyAsync(target, "incorrect-passphrase", CancellationToken.None));

        TryDeleteDir(receive);
    }

    [Fact]
    public async Task ReadyRequiresMatchingPassphraseAndReadyTick()
    {
        const string passphrase = "catcher-ready-passphrase";
        var envelope = CatcherPack.Create("Ready", "127.0.0.1", 1, 1, "127.0.0.1", passphrase);
        var port = FreePort();
        using var opened = CatcherPack.Unwrap(envelope, passphrase);
        var receive = Path.Combine(Path.GetTempPath(), "mercury-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(receive);
        await using var server = new CatcherHttpsServer(
            IPAddress.Loopback, port, opened.Certificate, opened.AuthToken, receive);
        server.Start();
        var target = envelope.ToTarget();
        target.PublicPort = port;

        var listening = await CatcherClient.ProbeAsync(target, passphrase, CancellationToken.None);
        Assert.Equal(CatcherOnlineStatus.Listening, listening.Status);

        server.ReadyToReceive = true;
        var ready = await CatcherClient.ProbeAsync(target, passphrase, CancellationToken.None);
        Assert.Equal(CatcherOnlineStatus.Ready, ready.Status);

        var denied = await CatcherClient.ProbeAsync(target, "wrong-pass-xx", CancellationToken.None);
        Assert.Equal(CatcherOnlineStatus.Unauthorized, denied.Status);

        var empty = await CatcherClient.ProbeAsync(target, "", CancellationToken.None);
        Assert.Equal(CatcherOnlineStatus.Unauthorized, empty.Status);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var anonymous = new HttpClient(handler);
        using var bare = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}{CatcherScheme.ReadyPath}")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var unauth = await anonymous.SendAsync(bare);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        TryDeleteDir(receive);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
            // temp leftover is OK
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // temp leftover is OK
        }
    }
}
