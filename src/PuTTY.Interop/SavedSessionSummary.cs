namespace PuTTY.Interop;

/// <summary>The protocol a saved session was saved with; unlike <see cref="SessionProtocol"/> it includes those that are only opened from a saved session.</summary>
public enum SavedSessionProtocol
{
    Raw = 0,
    Telnet = 1,
    Rlogin = 2,
    Ssh = 3,
    Serial = 4,
    Supdup = 5,
    Other = 6,
}

/// <summary>Where an open session connects: see <see cref="PuttySession.GetEndpoint"/>.</summary>
/// <param name="Host">The host name or address; the serial line for a Serial session.</param>
/// <param name="Port">The port.</param>
/// <param name="Protocol">How it connects.</param>
/// <param name="UserName">The user name in its settings, or null.</param>
/// <param name="KeyFile">The private key file in its settings, or null.</param>
public sealed record SessionEndpoint(string Host, int Port, SavedSessionProtocol Protocol, string? UserName, string? KeyFile);

/// <summary>Where a saved session connects to: enough for a line in a list. See <see cref="PuttySessions.Describe"/>.</summary>
/// <param name="Host">The host name or address; the serial line for a Serial session; empty for a session saved without one.</param>
/// <param name="Port">The saved port (meaningless for Serial).</param>
/// <param name="Protocol">How it connects.</param>
public sealed record SavedSessionSummary(string Host, int Port, SavedSessionProtocol Protocol)
{
    /// <summary>The protocol as PuTTY names it in its own box.</summary>
    public string ProtocolName => Protocol switch
    {
        SavedSessionProtocol.Raw => "Raw",
        SavedSessionProtocol.Telnet => "Telnet",
        SavedSessionProtocol.Rlogin => "Rlogin",
        SavedSessionProtocol.Ssh => "SSH",
        SavedSessionProtocol.Serial => "Serial",
        SavedSessionProtocol.Supdup => "SUPDUP",
        _ => "Other",
    };

    /// <summary>"10.20.0.12 · SSH", or just the protocol if the session has no host.</summary>
    public string Description => Host.Length == 0 ? ProtocolName : $"{Host} · {ProtocolName}";
}
