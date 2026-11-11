namespace PuTTY.Interop;

/// <summary>
/// What an SSH connection negotiated, learned from the lines PuTTY writes to its event log as it goes: the key exchange,
/// the cipher and MAC, and the server's host key fingerprint. PuTTY's core has no accessor for these (and it is not
/// modified here), but it says them in words that do not change, so the session details panel reads them from the log
/// lines that every <see cref="PuttySession"/> already raises. Feed each line to <see cref="Update"/>.
/// </summary>
public sealed class ConnectionFacts
{
    private const string FingerprintIntro = "Host key fingerprint is:";
    private bool nextLineIsFingerprint;

    /// <summary>What the key exchange was, as PuTTY words it: "ECDH key exchange with curve Curve25519, using hash SHA-256".</summary>
    public string? KeyExchange { get; private set; }

    /// <summary>The cipher: "AES-256 SDCTR (AES-NI accelerated)".</summary>
    public string? Cipher { get; private set; }

    /// <summary>The message authentication: "HMAC-SHA-256 (SHA-NI accelerated)".</summary>
    public string? Mac { get; private set; }

    /// <summary>The server's host key and its fingerprint, as PuTTY shows it: "ssh-ed25519 255 SHA256:...".</summary>
    public string? HostKey { get; private set; }

    /// <summary>The fingerprint alone ("SHA256:..." or "MD5:..."), or null.</summary>
    public string? Fingerprint
    {
        get
        {
            if (HostKey is null)
            {
                return null;
            }

            int space = HostKey.LastIndexOf(' ');
            return space >= 0 ? HostKey[(space + 1)..] : HostKey;
        }
    }

    /// <summary>True once anything is known.</summary>
    public bool Any => KeyExchange is not null || Cipher is not null || HostKey is not null;

    /// <summary>Takes one line of the event log; true if it told something.</summary>
    public bool Update(string line)
    {
        if (nextLineIsFingerprint)
        {
            nextLineIsFingerprint = false;
            if (line.Length > 0)
            {
                HostKey = line.Trim();
                return true;
            }
        }

        if (line == FingerprintIntro)
        {
            nextLineIsFingerprint = true;       // the fingerprint is the next line
            return false;
        }

        // "Doing ECDH key exchange with curve Curve25519, using hash SHA-256" (kex2-client.c); every key exchange begins "Doing ".
        if (line.StartsWith("Doing ", StringComparison.Ordinal))
        {
            KeyExchange = line["Doing ".Length..];
            return true;
        }

        // "Initialised AES-256 SDCTR outbound encryption" and "Initialised HMAC-SHA-256 outbound MAC algorithm" (bpp2.c).
        const string Initialised = "Initialised ";
        if (line.StartsWith(Initialised, StringComparison.Ordinal))
        {
            string rest = line[Initialised.Length..];
            int encryption = rest.IndexOf(" outbound encryption", StringComparison.Ordinal);
            if (encryption > 0)
            {
                Cipher = rest[..encryption];
                return true;
            }

            int mac = rest.IndexOf(" outbound MAC algorithm", StringComparison.Ordinal);
            if (mac > 0)
            {
                Mac = rest[..mac];
                return true;
            }
        }

        return false;
    }
}
