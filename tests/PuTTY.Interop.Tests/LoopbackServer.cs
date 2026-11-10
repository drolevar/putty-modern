using System.Net;
using System.Net.Sockets;

namespace PuTTY.Interop.Tests;

/// <summary>
/// A real TCP server on the loopback interface, so session tests exercise PuTTY's actual
/// sockets rather than a stand-in. It records everything it receives, can echo it back,
/// can send bytes of its own, and can drop its clients.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> clients = [];
    private readonly List<byte> received = [];
    private readonly Lock gate = new();
    private readonly bool echo;

    public LoopbackServer(bool echo = false)
    {
        this.echo = echo;
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    /// <summary>Everything received from clients so far.</summary>
    public byte[] Received
    {
        get
        {
            lock (gate)
            {
                return [.. received];
            }
        }
    }

    public int ClientCount
    {
        get
        {
            lock (gate)
            {
                return clients.Count;
            }
        }
    }

    /// <summary>Sends bytes to the most recent client.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        TcpClient client;
        lock (gate)
        {
            client = clients[^1];
        }

        client.GetStream().Write(data);
    }

    /// <summary>Closes every client connection, as a server that hangs up would.</summary>
    public void DropClients()
    {
        lock (gate)
        {
            foreach (TcpClient client in clients)
            {
                client.Close();
            }

            clients.Clear();
        }
    }

    public void Dispose()
    {
        listener.Stop();
        DropClients();
    }

    /// <summary>A port that nothing is listening on.</summary>
    public static int UnusedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                lock (gate)
                {
                    clients.Add(client);
                }

                _ = Task.Run(() => ReadLoop(client));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // The listener was stopped: the test is over.
        }
    }

    private async Task ReadLoop(TcpClient client)
    {
        byte[] buffer = new byte[4096];
        try
        {
            NetworkStream stream = client.GetStream();
            int count;
            while ((count = await stream.ReadAsync(buffer)) > 0)
            {
                lock (gate)
                {
                    received.AddRange(buffer.AsSpan(0, count));
                }

                if (echo)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, count));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // The connection was closed.
        }
    }
}
