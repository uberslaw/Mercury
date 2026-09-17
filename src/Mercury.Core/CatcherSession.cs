using System.Collections.Concurrent;

namespace Mercury;

/// <summary>In-memory Catcher passphrases for the current process. Never written to job JSON or logs.</summary>
public static class CatcherSession
{
    private static readonly ConcurrentDictionary<string, string> Passphrases = new(StringComparer.Ordinal);

    public static void Remember(string templateId, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        CatcherCrypto.ValidatePassphrase(passphrase);
        Passphrases[templateId] = passphrase;
    }

    public static string Require(string templateId)
    {
        if (Passphrases.TryGetValue(templateId, out var passphrase) && !string.IsNullOrEmpty(passphrase))
        {
            return passphrase;
        }

        throw new CatcherAuthException("Catcher passphrase is required. Enter it on the Transfer tab; it is not stored in the job.");
    }

    public static void Forget(string templateId)
    {
        Passphrases.TryRemove(templateId, out _);
    }

    public static void Clear() => Passphrases.Clear();
}
