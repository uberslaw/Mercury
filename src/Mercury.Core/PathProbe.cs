namespace Mercury;

public static class PathProbe
{
    public static async Task<bool> ExistsAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await Task.Run(() => Directory.Exists(path) || File.Exists(path), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {timeout.TotalSeconds:0.#}s waiting for '{path}'. If this is a network path, check VPN, share permissions, and that the host is online.");
        }
    }

    public static async Task<bool> DirectoryExistsAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await Task.Run(() => Directory.Exists(path), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Timed out after {timeout.TotalSeconds:0.#}s waiting for '{path}'. If this is a network path, check VPN, share permissions, and that the host is online.");
        }
    }

    public static TimeSpan TimeoutFor(string path) =>
        PathNormalizer.IsNetwork(path) ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4);
}
