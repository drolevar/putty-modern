using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PuTTY.Interop.Tests;

/// <summary>The kinds of proxy <see cref="ProxyServer"/> speaks.</summary>
internal enum ProxyKind
{
    HttpConnect,
    Socks4,
    Socks5,
}

/// <summary>
/// A proxy on the loopback interface for the proxy tests: an HTTP CONNECT proxy, a SOCKS4 one or a SOCKS5 one, each
/// optionally demanding a user name and password. It connects to whatever the client asks for, relays both ways and
/// records what it was asked (<see cref="Requests"/>). A protocol-level test double: it does what the specifications
/// say and nothing more, so a pass says PuTTY's own proxy code works behind our event loop and Seat.
/// </summary>
internal sealed class ProxyServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly ProxyKind kind;
    private readonly string? user;
    private readonly string? password;
    private readonly List<string> requests = [];
    private readonly Lock gate = new();
    private readonly CancellationTokenSource stop = new();

    public ProxyServer(ProxyKind kind, string? user = null, string? password = null)
    {
        this.kind = kind;
        this.user = user;
        this.password = password;
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>What was asked of the proxy: "CONNECT host:port", "SOCKS5 host:port", "SOCKS4 host:port", and "refused: ..." for a login it turned down.</summary>
    public string[] Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    private void Record(string text)
    {
        lock (gate)
        {
            requests.Add(text);
        }
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(() => Serve(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopping
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                TcpClient? target = kind switch
                {
                    ProxyKind.HttpConnect => await Http(stream),
                    ProxyKind.Socks4 => await Socks4(stream),
                    _ => await Socks5(stream),
                };
                if (target is null)
                {
                    return;
                }

                using (target)
                {
                    NetworkStream onward = target.GetStream();
                    Task up = stream.CopyToAsync(onward, stop.Token);
                    Task down = onward.CopyToAsync(stream, stop.Token);
                    await Task.WhenAny(up, down);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // a client that went away
            }
        }
    }

    private static async Task<byte[]> ReadExactly(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static async Task<string> ReadHeaders(NetworkStream stream)
    {
        var text = new StringBuilder();
        byte[] one = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0)
            {
                throw new IOException("closed");
            }

            text.Append((char)one[0]);
        }

        return text.ToString();
    }

    private async Task<TcpClient?> Http(NetworkStream stream)
    {
        while (true)
        {
            string head = await ReadHeaders(stream);
            string[] lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string[] first = lines[0].Split(' ');
            string destination = first[1];
            if (user is not null)
            {
                string expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                if (!lines.Any(l => string.Equals(l, expected, StringComparison.OrdinalIgnoreCase)))
                {
                    Record("refused: CONNECT " + destination);
                    // With a body, as real proxies send one: PuTTY's header reader wants to see the byte after the blank
                    // line before it takes the headers as finished (see SpeakFirst in ProxyTests).
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 6\r\n\r\ndenied"));
                    continue;           // the client may try again on this connection, or on a new one
                }
            }

            Record("CONNECT " + destination);
            int colon = destination.LastIndexOf(':');
            var target = new TcpClient();
            await target.ConnectAsync(destination[..colon], int.Parse(destination[(colon + 1)..]));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
            return target;
        }
    }

    private async Task<TcpClient?> Socks4(NetworkStream stream)
    {
        byte[] request = await ReadExactly(stream, 8);       // version, command, port, address
        while ((await ReadExactly(stream, 1))[0] != 0)
        {
            // the user id, ended by a zero
        }

        int port = (request[2] << 8) | request[3];
        var address = new IPAddress(request.AsSpan(4, 4));
        Record($"SOCKS4 {address}:{port}");
        var target = new TcpClient();
        await target.ConnectAsync(address, port);
        await stream.WriteAsync(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 });
        return target;
    }

    private async Task<TcpClient?> Socks5(NetworkStream stream)
    {
        byte[] greeting = await ReadExactly(stream, 2);
        byte[] methods = await ReadExactly(stream, greeting[1]);
        if (user is not null)
        {
            if (!methods.Contains((byte)2))
            {
                await stream.WriteAsync(new byte[] { 5, 0xFF });
                Record("refused: no password method offered");
                return null;
            }

            await stream.WriteAsync(new byte[] { 5, 2 });
            byte[] authVersion = await ReadExactly(stream, 2);
            string name = Encoding.UTF8.GetString(await ReadExactly(stream, authVersion[1]));
            byte[] passwordLength = await ReadExactly(stream, 1);
            string pass = Encoding.UTF8.GetString(await ReadExactly(stream, passwordLength[0]));
            if (name != user || pass != password)
            {
                await stream.WriteAsync(new byte[] { 1, 1 });
                Record("refused: SOCKS5 login " + name);
                return null;
            }

            await stream.WriteAsync(new byte[] { 1, 0 });
        }
        else
        {
            await stream.WriteAsync(new byte[] { 5, 0 });
        }

        byte[] head = await ReadExactly(stream, 4);          // version, command, reserved, address type
        string host;
        switch (head[3])
        {
            case 1:
                host = new IPAddress(await ReadExactly(stream, 4)).ToString();
                break;
            case 3:
                byte[] length = await ReadExactly(stream, 1);
                host = Encoding.ASCII.GetString(await ReadExactly(stream, length[0]));
                break;
            default:
                host = new IPAddress(await ReadExactly(stream, 16)).ToString();
                break;
        }

        byte[] portBytes = await ReadExactly(stream, 2);
        int port = (portBytes[0] << 8) | portBytes[1];
        Record($"SOCKS5 {host}:{port}");
        var target = new TcpClient();
        await target.ConnectAsync(host, port);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        return target;
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
