using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Sessions saved in PuTTY's own storage: listed by name and opened with their own settings. The
/// registry is the process's scratch copy (<see cref="RegistrySandbox"/>), filled in the way PuTTY
/// itself writes it, so these tests read what PuTTY classic would have left behind.
/// </summary>
[TestClass]
public sealed class SavedSessionTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    [TestMethod]
    public void No_saved_sessions_is_an_empty_list_not_an_error()
    {
        using var registry = new RegistrySandbox();
        using var engine = new EngineThread();

        Assert.AreEqual(0, engine.Run(() => PuttySessions.List()).Count);
    }

    [TestMethod]
    public void Saved_sessions_are_listed_under_the_names_the_user_gave_them()
    {
        using var registry = new RegistrySandbox();
        registry.WriteSession("web server", "10.0.0.1", 22, "ssh");
        registry.WriteSession("Dev/box: ünïcode & more", "10.0.0.2", 22, "ssh");
        registry.WriteSession(".dotted", "10.0.0.3", 23, "telnet");
        registry.WriteSession(PuttySessions.DefaultSettingsName, "", 22, "ssh");
        using var engine = new EngineThread();

        string[] names = [.. engine.Run(() => PuttySessions.List()).Order(StringComparer.Ordinal)];

        CollectionAssert.AreEquivalent(
            new[] { ".dotted", "Default Settings", "Dev/box: ünïcode & more", "web server" }, names);
    }

    [TestMethod]
    public void A_saved_session_opens_with_its_own_host_port_and_protocol()
    {
        using var registry = new RegistrySandbox();
        using var server = new LoopbackServer(echo: true);
        registry.WriteSession("loopback raw", "127.0.0.1", server.Port, "raw");
        using var engine = new EngineThread();
        var output = new List<byte>();

        PuttySession session = engine.Run(() =>
        {
            var s = PuttySession.OpenSaved("loopback raw");
            s.Output += (_, e) => { lock (output) { output.AddRange(e.Data); } };
            s.Start();
            return s;
        });

        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "the saved session did not connect");
        engine.Run(() => session.Send(Encoding.ASCII.GetBytes("ping")));
        Assert.IsTrue(EngineThread.WaitUntil(() => { lock (output) { return Encoding.ASCII.GetString([.. output]).Contains("ping"); } }, Generous));
        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void A_session_with_a_name_that_is_not_saved_fails_to_start_with_a_clear_message()
    {
        using var registry = new RegistrySandbox();
        registry.WriteSession("there", "127.0.0.1", 1, "raw");
        using var engine = new EngineThread();

        var failure = Assert.ThrowsExactly<InvalidOperationException>(() => engine.Run(() => PuttySession.OpenSaved("not there").Start()));

        StringAssert.Contains(failure.Message, "not there");
    }

    [TestMethod]
    public void A_saved_user_name_is_used_so_the_server_is_not_asked_for_one()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        registry.WriteSession("test box", "127.0.0.1", server.Port, "ssh", SshServerFixture.User, ("TryAgent", 0));
        using var engine = new EngineThread();
        var prompts = new List<string>();
        var output = new StringBuilder();

        PuttySession session = engine.Run(() =>
        {
            var s = PuttySession.OpenSaved("test box");
            s.Output += (_, e) => { lock (output) { output.Append(Encoding.UTF8.GetString(e.Data)); } };
            s.ConfirmationRequested += (_, e) => e.Accept();
            s.PromptRequested += (_, e) =>
            {
                lock (prompts) { prompts.Add(e.Fields.Count > 0 ? e.Fields[0].Text : string.Empty); }
                e.Respond(SshServerFixture.Password);
            };
            s.Start();
            return s;
        });

        Assert.IsTrue(EngineThread.WaitUntil(() => { lock (output) { return output.ToString().Contains("Welcome"); } }, Generous));
        lock (prompts)
        {
            Assert.AreEqual(1, prompts.Count, "only the password: " + string.Join(" | ", prompts));
            StringAssert.Contains(prompts[0].ToLowerInvariant(), "password");
        }

        Assert.AreEqual($"ssh-ed25519@{server.Port}:127.0.0.1", registry.CachedHostKeys().Single(), "accepting the host key stores it where PuTTY looks");
        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void Overrides_replace_what_the_saved_session_says()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        registry.WriteSession("test box", "127.0.0.1", server.Port, "ssh", "somebody else", ("TryAgent", 0));
        using var engine = new EngineThread();
        var output = new StringBuilder();

        PuttySession session = engine.Run(() =>
        {
            var s = PuttySession.OpenSaved("test box", new PuttySessionOptions { UserName = SshServerFixture.User });
            s.Output += (_, e) => { lock (output) { output.Append(Encoding.UTF8.GetString(e.Data)); } };
            s.ConfirmationRequested += (_, e) => e.ConnectOnce();
            s.PromptRequested += (_, e) => e.Respond(SshServerFixture.Password);
            s.Start();
            return s;
        });

        Assert.IsTrue(EngineThread.WaitUntil(() => { lock (output) { return output.ToString().Contains("Welcome"); } }, Generous),
            "the override user name was not used");
        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void Reading_saved_sessions_never_writes_to_the_storage()
    {
        using var registry = new RegistrySandbox();
        using var server = new LoopbackServer(echo: true);
        registry.WriteSession("loopback raw", "127.0.0.1", server.Port, "raw");
        string before = registry.Snapshot();
        using var engine = new EngineThread();

        engine.Run(() => PuttySessions.List());
        PuttySession session = engine.Run(() =>
        {
            var s = PuttySession.OpenSaved("loopback raw");
            s.Start();
            return s;
        });
        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous));
        engine.Run(session.Dispose);

        Assert.AreEqual(before, registry.Snapshot(), "opening a saved session must leave PuTTY's storage as it was");
    }
}
