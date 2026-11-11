using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>
/// Where to connect, from the command line: <c>--connect raw://host:port</c> (or <c>telnet://</c>,
/// <c>rlogin://</c>, <c>ssh://[user@]host[:port]</c>), and for SSH optionally <c>--key file.ppk</c>.
/// This is a way to start a session for tests and development, not the session UI.
/// </summary>
internal sealed record ConnectionTarget(SessionProtocol Protocol, string Host, int Port, string? UserName = null, string? KeyFile = null)
{
    private const int DefaultSshPort = 22;

    /// <summary>The target named by <c>--connect</c>, or null if the option is absent or malformed.</summary>
    public static ConnectionTarget? FromArguments(IReadOnlyList<string> args)
    {
        string? key = null;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i].Equals("--key", StringComparison.OrdinalIgnoreCase))
            {
                key = args[i + 1];
            }
        }

        for (int i = 0; i + 1 < args.Count; i++)
        {
            if (args[i].Equals("--connect", StringComparison.OrdinalIgnoreCase))
            {
                return Parse(args[i + 1]) is { } target ? target with { KeyFile = key } : null;
            }
        }

        return null;
    }

    public static ConnectionTarget? Parse(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        SessionProtocol? protocol = uri.Scheme.ToLowerInvariant() switch
        {
            "raw" => SessionProtocol.Raw,
            "telnet" => SessionProtocol.Telnet,
            "rlogin" => SessionProtocol.Rlogin,
            "ssh" => SessionProtocol.Ssh,
            _ => null,
        };
        if (protocol is not { } p)
        {
            return null;
        }

        // A URI with no port says -1 for a scheme it does not know; SSH has a default, the others need one.
        int port = uri.Port == -1 && p == SessionProtocol.Ssh ? DefaultSshPort : uri.Port;
        if (port < 1 || port > 65535)
        {
            return null;
        }

        string? user = uri.UserInfo.Length > 0 ? Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]) : null;
        return new ConnectionTarget(p, uri.Host, port, string.IsNullOrEmpty(user) ? null : user);
    }
}
