using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>
/// Where to connect, from the command line: <c>--connect raw://host:port</c> (or <c>telnet://</c>,
/// <c>rlogin://</c>). SSH waits for the prompt work of slice 3, since logging in needs password and
/// host-key dialogs. This is a way to start a session for tests and development, not the session UI.
/// </summary>
internal sealed record ConnectionTarget(SessionProtocol Protocol, string Host, int Port)
{
    /// <summary>The target named by <c>--connect</c>, or null if the option is absent or malformed.</summary>
    public static ConnectionTarget? FromArguments(IReadOnlyList<string> args)
    {
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i].Equals("--connect", StringComparison.OrdinalIgnoreCase))
            {
                return Parse(args[i + 1]);
            }
        }

        return null;
    }

    public static ConnectionTarget? Parse(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Port < 1 || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        SessionProtocol? protocol = uri.Scheme.ToLowerInvariant() switch
        {
            "raw" => SessionProtocol.Raw,
            "telnet" => SessionProtocol.Telnet,
            "rlogin" => SessionProtocol.Rlogin,
            _ => null,
        };

        return protocol is { } p ? new ConnectionTarget(p, uri.Host, uri.Port) : null;
    }
}
