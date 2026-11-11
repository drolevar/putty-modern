using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Connecting through a proxy (PuTTY's Connection > Proxy page): HTTP CONNECT, SOCKS4 and SOCKS5, with and without a
/// login, against proxies written for the test (<see cref="ProxyServer"/>). The session is a saved raw one whose
/// settings name the proxy; PuTTY's proxy code does the talking, behind our event loop and Seat. PuTTY does not proxy
/// connections to the local machine unless told to, and the target here is on loopback, so every session says to.
/// </summary>
[TestClass]
public sealed class ProxyTests
{
    private const int MethodSocks4 = 1, MethodSocks5 = 2, MethodHttp = 3;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    private static (string Name, object Value)[] ProxySettings(int method, ProxyServer proxy, string? user = null, string? password = null)
    {
        var settings = new List<(string, object)>
        {
            ("ProxyMethod", method),
            ("ProxyHost", "127.0.0.1"),
            ("ProxyPort", proxy.Port),
            ("ProxyLocalhost", 1),
        };
        if (user is not null)
        {
            settings.Add(("ProxyUsername", user));
            settings.Add(("ProxyPassword", password ?? string.Empty));
        }

        return [.. settings];
    }

    private static PuttySession Open(EngineThread engine, StringBuilder output, Action<PuttySession>? configure = null) =>
        engine.Run(() =>
        {
            var session = PuttySession.OpenSaved("proxied");
            session.Output += (_, e) => { lock (output) { output.Append(Encoding.UTF8.GetString(e.Data)); } };
            configure?.Invoke(session);
            session.Start();
            return session;
        });

    private static bool Contains(StringBuilder output, string text)
    {
        lock (output)
        {
            return output.ToString().Contains(text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The target speaks first, as an SSH server does. **PuTTY's HTTP CONNECT negotiation needs it to:** its header reader
    /// takes the blank line that ends the headers as final only when it sees the byte after it (it checks for a
    /// continuation line, and the "\r\n" of a blank line is two characters where its exception expects one), so with
    /// a target that waits for the client the connection never finishes opening. Real SSH, Telnet and mail servers
    /// speak first; a raw connection to a server that waits for the client does not work through an HTTP proxy.
    /// </summary>
    private static void SpeakFirst(LoopbackServer target, ProxyKind kind, StringBuilder output)
    {
        target.Send("READY"u8);
        Assert.IsTrue(EngineThread.WaitUntil(() => Contains(output, "READY"), Generous), $"the target's first words never arrived through the {kind} proxy");
    }

    /// <summary>Opens the saved session through the proxy and checks that data moves both ways and the proxy was asked for the target.</summary>
    private static void ConnectsThrough(ProxyKind kind, int method, string? user, string? password, string expectedRequest,
        Action<PuttySession>? configure = null)
    {
        using var registry = new RegistrySandbox();
        using var target = new LoopbackServer(echo: true);
        using var proxy = new ProxyServer(kind, user, password);
        registry.WriteSession("proxied", "127.0.0.1", target.Port, "raw", null, ProxySettings(method, proxy, user, password));
        using var engine = new EngineThread();
        var output = new StringBuilder();

        PuttySession session = Open(engine, output, configure);

        Assert.IsTrue(EngineThread.WaitUntil(() => target.ClientCount == 1, Generous), "the target never saw a connection: " + string.Join(" | ", proxy.Requests));
        CollectionAssert.Contains(proxy.Requests, expectedRequest.Replace("{port}", target.Port.ToString()));
        SpeakFirst(target, kind, output);
        engine.Run(() => session.Send(Encoding.UTF8.GetBytes("through the proxy")));
        Assert.IsTrue(EngineThread.WaitUntil(() => Contains(output, "through the proxy"), Generous),
            "the echo of what was sent never came back through the proxy");
        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void An_HTTP_CONNECT_proxy_carries_the_connection()
    {
        ConnectsThrough(ProxyKind.HttpConnect, MethodHttp, null, null, "CONNECT 127.0.0.1:{port}");
    }

    [TestMethod]
    public void An_HTTP_proxy_that_wants_a_login_gets_the_saved_one()
    {
        ConnectsThrough(ProxyKind.HttpConnect, MethodHttp, "bob", "s3cret", "CONNECT 127.0.0.1:{port}");
    }

    [TestMethod]
    public void A_SOCKS5_proxy_carries_the_connection()
    {
        ConnectsThrough(ProxyKind.Socks5, MethodSocks5, null, null, "SOCKS5 127.0.0.1:{port}");
    }

    [TestMethod]
    public void A_SOCKS5_proxy_that_wants_a_login_gets_the_saved_one()
    {
        ConnectsThrough(ProxyKind.Socks5, MethodSocks5, "bob", "s3cret", "SOCKS5 127.0.0.1:{port}");
    }

    [TestMethod]
    public void A_SOCKS4_proxy_carries_the_connection()
    {
        ConnectsThrough(ProxyKind.Socks4, MethodSocks4, null, null, "SOCKS4 127.0.0.1:{port}");
    }

    [TestMethod]
    public void Closing_a_session_while_the_proxy_is_still_negotiating_is_safe()
    {
        // The target is silent, so PuTTY's HTTP negotiation never finishes (see SpeakFirst). Closing the session then
        // used to be a double free: raw_free frees the Seat the proxy had borrowed, then closes the socket, whose
        // cleanup hands the seat back through the pointer just freed. The engine returns the seat first.
        using var registry = new RegistrySandbox();
        using var target = new LoopbackServer(echo: true);
        using var proxy = new ProxyServer(ProxyKind.HttpConnect);
        registry.WriteSession("proxied", "127.0.0.1", target.Port, "raw", null, ProxySettings(MethodHttp, proxy));
        using var engine = new EngineThread();
        PuttySession session = Open(engine, new StringBuilder());
        Assert.IsTrue(EngineThread.WaitUntil(() => target.ClientCount == 1, Generous), "the proxy never reached the target");

        engine.Run(session.Dispose);

        Thread.Sleep(200);      // let the engine thread run its callbacks; a crash would show here or when the engine stops
    }

    [TestMethod]
    public void A_local_proxy_command_carries_the_connection()
    {
        // "Local" proxy (Connection > Proxy > Proxy command): PuTTY starts a program and talks to the target through
        // its standard input and output. The program here relays them to the target (what plink -nc does), and
        // like plink it ends when its input does: PuTTY closes its end of the pipes when the session is closed, and
        // CloseHandle on the read end waits for the reading thread's ReadFile, i.e. for the program to end. A proxy
        // command that does not end when its input is closed makes closing the session wait for it for ever. It also
        // says nothing on its standard error: PuTTY's sk_handle_close never frees the handle that reads it, so text
        // arriving after the session is closed is handed to the freed socket (an upstream bug, see CLAUDE.md).
        using var registry = new RegistrySandbox();
        using var target = new LoopbackServer(echo: true);
        string directory = Path.Combine(Path.GetTempPath(), "puttymodern-relay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string relay = Path.Combine(directory, "relay.ps1");
            File.WriteAllText(relay, """
                param([string]$h, [int]$p)
                $ErrorActionPreference = 'SilentlyContinue'
                $c = New-Object System.Net.Sockets.TcpClient($h, $p)
                $s = $c.GetStream()
                $in = [Console]::OpenStandardInput()
                $out = [Console]::OpenStandardOutput()
                $ps = [powershell]::Create()
                [void]$ps.AddScript({ param($in, $s, $c) $b = New-Object byte[] 4096; while (($n = $in.Read($b, 0, 4096)) -gt 0) { $s.Write($b, 0, $n); $s.Flush() }; $c.Close() }).AddArgument($in).AddArgument($s).AddArgument($c)
                [void]$ps.BeginInvoke()
                $b = New-Object byte[] 4096
                try { while (($n = $s.Read($b, 0, 4096)) -gt 0) { $out.Write($b, 0, $n); $out.Flush() } } catch { }
                """);
            registry.WriteSession("proxied", "127.0.0.1", target.Port, "raw", null,
                ("ProxyMethod", 5),
                // the command is stored with backslash escapes (\n is a newline, \ a backslash), so a path needs them doubled
                ("ProxyTelnetCommand", $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{relay.Replace("\\", "\\\\")}\" %host %port"),
                ("ProxyLocalhost", 1));
            using var engine = new EngineThread();
            var output = new StringBuilder();

            var lines = new List<string>();
            PuttySession session = Open(engine, output, s =>
            {
                s.Log += (_, e) => { lock (lines) { lines.Add(e.Text); } };
                s.Error += (_, e) => { lock (lines) { lines.Add("ERROR " + e.Message); } };
            });

            Assert.IsTrue(EngineThread.WaitUntil(() => target.ClientCount == 1, TimeSpan.FromSeconds(30)),
                "the relay never reached the target; the session said: " + string.Join(" | ", lines));
            engine.Run(() => session.Send(Encoding.UTF8.GetBytes("through the command")));
            Assert.IsTrue(EngineThread.WaitUntil(() => Contains(output, "through the command"), Generous),
                "the echo of what was sent never came back through the proxy command");
            engine.Run(session.Dispose);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // the relay may still be exiting; a scratch folder in %TEMP%
            }
        }
    }
    [TestMethod]
    public void A_proxy_login_that_is_wrong_ends_the_session_with_an_error()
    {
        using var registry = new RegistrySandbox();
        using var target = new LoopbackServer(echo: true);
        using var proxy = new ProxyServer(ProxyKind.Socks5, "bob", "s3cret");
        registry.WriteSession("proxied", "127.0.0.1", target.Port, "raw", null, ProxySettings(MethodSocks5, proxy, "bob", "WRONG"));
        using var engine = new EngineThread();
        var errors = new List<string>();
        bool closed = false;

        engine.Run(() =>
        {
            var session = PuttySession.OpenSaved("proxied");
            session.Error += (_, e) => { lock (errors) { errors.Add(e.Message); } };
            session.Closed += (_, _) => closed = true;
            session.Start();
            return session;
        });

        Assert.IsTrue(EngineThread.WaitUntil(() => Volatile.Read(ref closed), Generous), "the session should end when the proxy refuses the login");
        Assert.AreEqual(0, target.ClientCount, "the proxy refused, so the target must not have been reached");
        Assert.IsTrue(proxy.Requests.Any(r => r.StartsWith("refused", StringComparison.Ordinal)), string.Join(" | ", proxy.Requests));
    }

    [TestMethod]
    public void An_HTTP_proxy_that_wants_a_login_nobody_saved_asks_for_it_as_a_prompt()
    {
        using var registry = new RegistrySandbox();
        using var target = new LoopbackServer(echo: true);
        using var proxy = new ProxyServer(ProxyKind.HttpConnect, "bob", "s3cret");
        // the proxy settings name no user: PuTTY asks, through the Seat, when the proxy says 407
        registry.WriteSession("proxied", "127.0.0.1", target.Port, "raw", null, ProxySettings(MethodHttp, proxy));
        using var engine = new EngineThread();
        var output = new StringBuilder();
        var asked = new List<string>();

        PuttySession session = Open(engine, output, s => s.PromptRequested += (_, e) =>
        {
            lock (asked) { asked.Add(string.Join("|", e.Fields.Select(f => f.Text))); }
            e.Respond(e.Fields.Select(f => f.Text.Contains("sername", StringComparison.OrdinalIgnoreCase) ? "bob" : "s3cret").ToArray());
        });

        Assert.IsTrue(EngineThread.WaitUntil(() => target.ClientCount == 1, Generous),
            "the target never saw a connection; asked: " + string.Join(" ; ", asked) + "; proxy: " + string.Join(" | ", proxy.Requests));
        SpeakFirst(target, ProxyKind.HttpConnect, output);
        lock (asked)
        {
            Assert.IsTrue(asked.Count > 0, "the host should have been asked for the proxy login");
        }

        engine.Run(session.Dispose);
    }
}
