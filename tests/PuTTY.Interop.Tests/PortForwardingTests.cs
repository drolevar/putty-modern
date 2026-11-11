using System.Net;
using System.Net.Sockets;
using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// SSH port forwarding (PuTTY's Connection > SSH > Tunnels page): local, remote and dynamic forwards through the test
/// SSH server, which relays what the client asks for. The session is a saved SSH one whose "PortForwardings" setting
/// lists the forwards; PuTTY's own code opens the listeners and the channels.
/// </summary>
[TestClass]
public sealed class PortForwardingTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(15);
    private const int UnusedPort = 9;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Rig : IDisposable
    {
        private readonly List<string> log = [];
        public readonly RegistrySandbox Registry = new();
        public readonly SshServerFixture Server;
        public readonly LoopbackServer Target = new(echo: true);
        public readonly EngineThread Engine = new();
        public PuttySession Session = null!;

        public Rig(string forwardings, params string[] serverArguments)
        {
            Server = SshServerFixture.Start(serverArguments);
            Registry.WriteSession("forwarded", "127.0.0.1", Server.Port, "ssh", SshServerFixture.User,
                ("PortForwardings", forwardings));
            Session = Engine.Run(() =>
            {
                PuttySession session = PuttySession.OpenSaved("forwarded");
                session.Log += (_, e) => { lock (log) { log.Add(e.Text); } };
                session.ConfirmationRequested += (_, e) => e.ConnectOnce();
                session.PromptRequested += (_, e) => e.Respond(SshServerFixture.Password);
                session.Start();
                return session;
            });
        }

        public string[] Log { get { lock (log) { return [.. log]; } } }

        /// <summary>Connects to a listener that PuTTY opens once the login is done, trying for a while.</summary>
        public TcpClient ConnectTo(int port)
        {
            var deadline = DateTime.UtcNow + Generous;
            while (true)
            {
                try
                {
                    var client = new TcpClient();
                    client.Connect(IPAddress.Loopback, port);
                    return client;
                }
                catch (SocketException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(150);
                }
            }
        }

        public void Dispose()
        {
            Engine.Run(() => Session?.Dispose());
            Engine.Dispose();
            Target.Dispose();
            Server.Dispose();
            Registry.Dispose();
        }
    }

    /// <summary>Sends text and waits for the same text to come back (the target echoes).</summary>
    private static void AssertEchoes(TcpClient client, string text)
    {
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = (int)Generous.TotalMilliseconds;
        stream.Write(Encoding.UTF8.GetBytes(text));
        byte[] buffer = new byte[text.Length];
        int got = 0;
        while (got < buffer.Length)
        {
            int n = stream.Read(buffer, got, buffer.Length - got);
            Assert.IsTrue(n > 0, "the connection closed before the echo came back");
            got += n;
        }

        Assert.AreEqual(text, Encoding.UTF8.GetString(buffer));
    }

    [TestMethod]
    public void A_local_forward_carries_a_connection_to_where_the_server_connects_it()
    {
        int listen = FreePort();
        using var target = new LoopbackServer(echo: true);
        using var rig = new Rig($"L{listen}=127.0.0.1:{target.Port}");

        using TcpClient client = rig.ConnectTo(listen);
        AssertEchoes(client, "through the local forward");

        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Server.Events.Any(e => e.Contains($"\"dest\":\"127.0.0.1:{target.Port}\"") && e.Contains("\"ok\":true")), Generous),
            "the server never reported the channel: " + string.Join(" ", rig.Server.Events));
    }

    [TestMethod]
    public void A_remote_forward_makes_the_server_listen_and_PuTTY_connect_back()
    {
        int remote = FreePort();
        using var target = new LoopbackServer(echo: true);
        using var rig = new Rig($"R{remote}=127.0.0.1:{target.Port}");

        // The server listens on its side once PuTTY has asked; a connection to it comes out of PuTTY at the target.
        using TcpClient client = rig.ConnectTo(remote);
        AssertEchoes(client, "through the remote forward");

        Assert.IsTrue(rig.Server.Events.Any(e => e.Contains("tcpip-forward")), "the server never saw the request");
    }

    [TestMethod]
    public void A_dynamic_forward_is_a_SOCKS_server_whose_connections_go_through_the_tunnel()
    {
        int listen = FreePort();
        using var target = new LoopbackServer(echo: true);
        using var rig = new Rig($"D{listen}");

        using TcpClient client = rig.ConnectTo(listen);
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = (int)Generous.TotalMilliseconds;
        stream.Write([5, 1, 0]);                                    // SOCKS5, one method: none
        byte[] method = new byte[2];
        stream.ReadExactly(method);
        CollectionAssert.AreEqual(new byte[] { 5, 0 }, method);
        stream.Write([5, 1, 0, 1, 127, 0, 0, 1, (byte)(target.Port >> 8), (byte)(target.Port & 0xFF)]);   // CONNECT 127.0.0.1:port
        byte[] reply = new byte[10];
        stream.ReadExactly(reply);
        Assert.AreEqual(0, reply[1], "the SOCKS reply should say succeeded");

        AssertEchoes(client, "through the dynamic forward");
        Assert.IsTrue(rig.Server.Events.Any(e => e.Contains($"\"dest\":\"127.0.0.1:{target.Port}\"")), string.Join(" ", rig.Server.Events));
    }

    [TestMethod]
    public void A_server_that_refuses_forwarding_closes_the_connection_and_the_log_says_why()
    {
        int listen = FreePort();
        using var rig = new Rig($"L{listen}=127.0.0.1:{UnusedPort}", "--no-forwarding");

        using TcpClient client = rig.ConnectTo(listen);
        NetworkStream stream = client.GetStream();
        stream.ReadTimeout = (int)Generous.TotalMilliseconds;
        stream.Write("hello"u8);
        int n;
        try
        {
            n = stream.Read(new byte[10]);
        }
        catch (IOException)
        {
            n = 0;
        }

        Assert.AreEqual(0, n, "the connection should be closed, there is nowhere for it to go");
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Log.Any(l => l.Contains("refused", StringComparison.OrdinalIgnoreCase)), Generous),
            "the event log should say that the server refused: " + string.Join(" | ", rig.Log));
    }

}
