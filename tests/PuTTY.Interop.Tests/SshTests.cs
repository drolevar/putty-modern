using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// SSH sessions against the test SSH server (see <see cref="SshServerFixture"/>): the questions the
/// engine hands to the host (password, user name, keyboard-interactive, host key) and what happens
/// when they are answered, refused, ignored or answered late. PuTTY's host key cache is redirected
/// to a scratch registry key for each test, so nothing touches the user's real known hosts.
/// </summary>
[TestClass]
public sealed class SshTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

    private sealed class Rig : IDisposable
    {
        private readonly Lock gate = new();
        private readonly StringBuilder output = new();
        private readonly List<string> log = [];
        private readonly List<string> errors = [];
        private readonly List<SessionPromptEventArgs> prompts = [];
        private readonly List<SessionConfirmEventArgs> confirmations = [];
        private int closedCount;

        public readonly EngineThread Engine = new();
        public PuttySession Session { get; private set; } = null!;

        /// <summary>What to do with a question; the default refuses it. Runs on the engine thread, may answer later by not answering.</summary>
        public Action<SessionPromptEventArgs> OnPrompt { get; set; } = e => e.Cancel();

        public Action<SessionConfirmEventArgs> OnConfirm { get; set; } = e => e.Cancel();

        public string OutputText { get { lock (gate) { return output.ToString(); } } }

        public string[] Log { get { lock (gate) { return [.. log]; } } }

        public string[] Errors { get { lock (gate) { return [.. errors]; } } }

        public SessionPromptEventArgs[] Prompts { get { lock (gate) { return [.. prompts]; } } }

        public SessionConfirmEventArgs[] Confirmations { get { lock (gate) { return [.. confirmations]; } } }

        public int ClosedCount { get { lock (gate) { return closedCount; } } }

        public void Start(int port, PuttySessionOptions? options, bool subscribe = true)
        {
            Session = Engine.Run(() =>
            {
                var session = new PuttySession("127.0.0.1", port, SessionProtocol.Ssh, options);
                session.Output += (_, e) => { lock (gate) { output.Append(Encoding.UTF8.GetString(e.Data)); } };
                session.Log += (_, e) => { lock (gate) { log.Add(e.Text); } };
                session.Error += (_, e) => { lock (gate) { errors.Add(e.Message); } };
                session.Closed += (_, _) => { lock (gate) { closedCount++; } };
                if (subscribe)
                {
                    session.PromptRequested += (_, e) => { lock (gate) { prompts.Add(e); } OnPrompt(e); };
                    session.ConfirmationRequested += (_, e) => { lock (gate) { confirmations.Add(e); } OnConfirm(e); };
                }

                session.Start();
                return session;
            });
        }

        public void Type(string text) => Engine.Run(() => Session.Send(Encoding.UTF8.GetBytes(text)));

        public bool WaitForOutput(string text) => EngineThread.WaitUntil(() => OutputText.Contains(text), Generous);

        public bool WaitForClosed() => EngineThread.WaitUntil(() => ClosedCount > 0, Generous);

        public string Diagnostics() =>
            $"output [{OutputText}] errors [{string.Join(" | ", Errors)}] log [{string.Join(" | ", Log.TakeLast(6))}]";

        public void Dispose()
        {
            Engine.Run(Session.Dispose);
            Engine.Dispose();
        }
    }

    private static PuttySessionOptions Test => new() { UserName = SshServerFixture.User, TryAgent = false };

    private static void AnswerPassword(SessionPromptEventArgs e) => e.Respond(SshServerFixture.Password);

    [TestMethod]
    public void A_password_login_runs_a_command()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var rig = new Rig { OnPrompt = AnswerPassword, OnConfirm = e => e.Accept() };

        rig.Start(server.Port, Test);

        Assert.IsTrue(rig.WaitForOutput("Welcome to the PuTTY modern test server."), rig.Diagnostics());
        rig.Type("echo hello from ssh\r");
        Assert.IsTrue(rig.WaitForOutput("hello from ssh\r\n$ "), rig.Diagnostics());
        Assert.IsTrue(EngineThread.WaitUntil(() => server.Events.Contains("{\"event\":\"command\",\"text\":\"echo hello from ssh\"}"), Generous),
            "the server never saw the command: " + string.Join(" ", server.Events));
        // PuTTY tries keyboard-interactive before plain password, and the server offers both.
        Assert.IsTrue(server.Events.Any(e => e.Contains("\"event\":\"auth\"") && e.Contains("\"user\":\"test\"") && e.Contains("\"ok\":true")), string.Join(" ", server.Events));
        Assert.AreEqual(0, rig.ClosedCount);

        Assert.AreEqual(1, rig.Confirmations.Length);
        var hostKey = rig.Confirmations[0];
        Assert.AreEqual(SessionConfirmKind.HostKey, hostKey.Kind);
        Assert.AreEqual("127.0.0.1", hostKey.Host);
        Assert.AreEqual(server.Port, hostKey.Port);
        Assert.AreEqual("ssh-ed25519", hostKey.KeyType);
        string text = string.Join("\n", hostKey.Items.Select(i => i.Text));
        Assert.IsTrue(text.Contains(server.Sha256), $"the dialog does not show the server's fingerprint {server.Sha256}: {text}");

        Assert.AreEqual(1, rig.Prompts.Length);
        var password = rig.Prompts[0];
        Assert.AreEqual(1, password.Fields.Count);
        Assert.IsFalse(password.Fields[0].Echo, "a password must not be echoed");
        StringAssert.Contains(password.Fields[0].Text.ToLowerInvariant(), "password");
        Assert.IsTrue(password.FromServer, "PuTTY marks an SSH-2 password prompt as the server's");
    }

    [TestMethod]
    public void Accepting_a_host_key_remembers_it_and_a_second_connection_does_not_ask()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();

        using (var first = new Rig { OnPrompt = AnswerPassword, OnConfirm = e => e.Accept() })
        {
            first.Start(server.Port, Test);
            Assert.IsTrue(first.WaitForOutput("Welcome"), first.Diagnostics());
        }

        CollectionAssert.AreEqual(new[] { $"ssh-ed25519@{server.Port}:127.0.0.1" }, registry.CachedHostKeys());

        using var second = new Rig { OnPrompt = AnswerPassword, OnConfirm = e => e.Cancel() };
        second.Start(server.Port, Test);
        Assert.IsTrue(second.WaitForOutput("Welcome"), second.Diagnostics());
        Assert.AreEqual(0, second.Confirmations.Length, "a cached host key must not be asked about again");
    }

    [TestMethod]
    public void Connecting_once_does_not_remember_the_host_key()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var rig = new Rig { OnPrompt = AnswerPassword, OnConfirm = e => e.ConnectOnce() };

        rig.Start(server.Port, Test);

        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        Assert.AreEqual(0, registry.CachedHostKeys().Length);
    }

    [TestMethod]
    public void Cancelling_the_host_key_question_ends_the_session_before_any_password_is_asked()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var rig = new Rig { OnPrompt = AnswerPassword };      // OnConfirm refuses

        rig.Start(server.Port, Test);

        Assert.IsTrue(rig.WaitForClosed(), rig.Diagnostics());
        Assert.AreEqual(1, rig.Confirmations.Length);
        Assert.AreEqual(0, rig.Prompts.Length, "no password may be offered to a server whose key was refused");
        Assert.AreEqual(0, registry.CachedHostKeys().Length);
        // A refusal is not an error: PuTTY just says in the event log why the connection ended.
        Assert.IsTrue(rig.Log.Any(l => l.Contains("host key verification", StringComparison.OrdinalIgnoreCase)), rig.Diagnostics());
    }

    [TestMethod]
    public void A_wrong_password_is_asked_again_and_cancelling_ends_the_session()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var rig = new Rig { OnConfirm = e => e.Accept() };
        rig.OnPrompt = e =>
        {
            if (rig.Prompts.Length == 1)
            {
                e.Respond("not the password");
            }
            else
            {
                e.Cancel();
            }
        };

        rig.Start(server.Port, Test);

        Assert.IsTrue(rig.WaitForClosed(), rig.Diagnostics());
        Assert.AreEqual(2, rig.Prompts.Length, rig.Diagnostics());
        Assert.IsFalse(rig.OutputText.Contains("Welcome"));
    }

    [TestMethod]
    public void Without_a_user_name_the_server_gets_asked_for_one_first()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var rig = new Rig { OnConfirm = e => e.Accept() };
        rig.OnPrompt = e =>
        {
            if (e.Fields[0].Text.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                Assert.IsTrue(e.Fields[0].Echo, "a user name may be shown as it is typed");
                e.Respond(SshServerFixture.User);
            }
            else
            {
                e.Respond(SshServerFixture.Password);
            }
        };

        rig.Start(server.Port, new PuttySessionOptions { TryAgent = false });

        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        Assert.AreEqual(2, rig.Prompts.Length);
    }

    [TestMethod]
    public void Keyboard_interactive_questions_come_from_the_server_with_their_own_text()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start("--methods", "keyboard-interactive");
        using var rig = new Rig { OnConfirm = e => e.Accept(), OnPrompt = AnswerPassword };

        rig.Start(server.Port, Test);

        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        var question = rig.Prompts.Single();
        Assert.IsTrue(question.FromServer, "keyboard-interactive questions are the server's");
        Assert.AreEqual("Password: ", question.Fields[0].Text);
        Assert.IsFalse(question.Fields[0].Echo);
        Assert.AreEqual("Test server", question.Name);
        StringAssert.Contains(question.Instruction, "Type the password.");
    }

    [TestMethod]
    public void A_question_can_be_answered_later_not_only_from_inside_the_event()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        SessionConfirmEventArgs? waitingConfirm = null;
        SessionPromptEventArgs? waitingPrompt = null;
        using var rig = new Rig
        {
            OnConfirm = e => waitingConfirm = e,        // not answered here
            OnPrompt = e => waitingPrompt = e,
        };

        rig.Start(server.Port, Test);

        Assert.IsTrue(EngineThread.WaitUntil(() => Volatile.Read(ref waitingConfirm) is not null, Generous), rig.Diagnostics());
        Thread.Sleep(300);
        Assert.AreEqual(0, rig.Prompts.Length, "the password question must wait for the host key answer");
        Assert.AreEqual(0, rig.ClosedCount);
        Assert.IsTrue(rig.Engine.Run(() => waitingConfirm!.Accept()));

        Assert.IsTrue(EngineThread.WaitUntil(() => Volatile.Read(ref waitingPrompt) is not null, Generous), rig.Diagnostics());
        Assert.IsTrue(rig.Engine.Run(() => waitingPrompt!.Respond(SshServerFixture.Password)));
        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());

        Assert.IsFalse(rig.Engine.Run(() => waitingPrompt!.Respond("again")), "a question can be answered once");
    }

    [TestMethod]
    public void With_nobody_listening_the_questions_are_refused_and_the_session_ends()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var rig = new Rig();

        rig.Start(server.Port, Test, subscribe: false);

        Assert.IsTrue(rig.WaitForClosed(), rig.Diagnostics());
        Assert.IsFalse(rig.OutputText.Contains("Welcome"));
        Assert.AreEqual(0, registry.CachedHostKeys().Length, "an unanswered host key must not be trusted");
    }

    [TestMethod]
    public void A_changed_host_key_is_called_out_and_can_be_refused()
    {
        using var registry = new RegistrySandbox();
        int port;
        using (var original = SshServerFixture.Start())
        {
            port = original.Port;
            using var rig = new Rig { OnPrompt = AnswerPassword, OnConfirm = e => e.Accept() };
            rig.Start(port, Test);
            Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        }

        // A different server (a different key) turns up at the same address.
        using var impostor = SshServerFixture.Start("--port", port.ToString());
        using var second = new Rig { OnPrompt = AnswerPassword };           // refuses the key
        second.Start(port, Test);

        Assert.IsTrue(second.WaitForClosed(), second.Diagnostics());
        var question = second.Confirmations.Single();
        Assert.AreEqual(SessionConfirmKind.HostKey, question.Kind);
        Assert.IsTrue(question.Items.Any(i => i.Kind == DialogTextKind.ScaryHeading),
            "a changed key must be flagged with a warning headline: " + string.Join(" / ", question.Items.Select(i => $"{i.Kind}: {i.Text}")));
        Assert.AreEqual(0, second.Prompts.Length);
    }

    /// <summary>A throwaway key pair made by the engine: the .ppk file and the public line for the server's authorized keys.</summary>
    private sealed class TestKey : IDisposable
    {
        public string File { get; }

        public string PublicLine { get; }

        public TestKey(Rig rig, string? passphrase)
        {
            File = Path.Combine(Path.GetTempPath(), $"puttymodern-test-{Guid.NewGuid():N}.ppk");
            PublicLine = rig.Engine.Run(() => PuttyEngine.Diagnostics.GenerateEd25519Key(File, passphrase));
        }

        public void Dispose()
        {
            if (System.IO.File.Exists(File))
            {
                System.IO.File.Delete(File);
            }
        }
    }

    [TestMethod]
    public void A_key_file_logs_in_without_asking_for_a_password()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig { OnConfirm = e => e.Accept() };       // any question but the host key refuses: a password prompt would fail the login
        using var key = new TestKey(rig, passphrase: null);
        using var server = SshServerFixture.Start("--methods", "publickey", "--authorized-key", key.PublicLine);

        rig.Start(server.Port, new PuttySessionOptions { UserName = SshServerFixture.User, KeyFile = key.File, TryAgent = false });

        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        Assert.AreEqual(0, rig.Prompts.Length);
    }

    [TestMethod]
    public void An_encrypted_key_file_asks_for_its_passphrase_locally()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig { OnConfirm = e => e.Accept(), OnPrompt = e => e.Respond("hunter2") };
        using var key = new TestKey(rig, passphrase: "hunter2");
        using var server = SshServerFixture.Start("--methods", "publickey", "--authorized-key", key.PublicLine);

        rig.Start(server.Port, new PuttySessionOptions { UserName = SshServerFixture.User, KeyFile = key.File, TryAgent = false });

        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        var question = rig.Prompts.Single();
        StringAssert.Contains(question.Fields[0].Text.ToLowerInvariant(), "passphrase");
        Assert.IsFalse(question.Fields[0].Echo);
        Assert.IsFalse(question.FromServer, "a key passphrase is asked by PuTTY, never by the server");
    }

    [TestMethod]
    public void A_wrong_passphrase_is_asked_again()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig { OnConfirm = e => e.Accept() };
        rig.OnPrompt = e => e.Respond(rig.Prompts.Length == 1 ? "not it" : "hunter2");
        using var key = new TestKey(rig, passphrase: "hunter2");
        using var server = SshServerFixture.Start("--methods", "publickey", "--authorized-key", key.PublicLine);

        rig.Start(server.Port, new PuttySessionOptions { UserName = SshServerFixture.User, KeyFile = key.File, TryAgent = false });

        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        Assert.AreEqual(2, rig.Prompts.Length);
    }

    [TestMethod]
    public void A_key_the_server_does_not_know_falls_back_to_the_password()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig { OnConfirm = e => e.Accept(), OnPrompt = AnswerPassword };
        using var key = new TestKey(rig, passphrase: null);
        using var other = new TestKey(rig, passphrase: null);
        using var server = SshServerFixture.Start("--authorized-key", other.PublicLine);   // not our key

        rig.Start(server.Port, new PuttySessionOptions { UserName = SshServerFixture.User, KeyFile = key.File, TryAgent = false });

        Assert.IsTrue(rig.WaitForOutput("Welcome"), rig.Diagnostics());
        StringAssert.Contains(rig.Prompts.Single().Fields[0].Text.ToLowerInvariant(), "password");
    }

    [TestMethod]
    public void A_remote_command_runs_instead_of_a_shell()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var rig = new Rig { OnConfirm = e => e.Accept(), OnPrompt = AnswerPassword };

        rig.Start(server.Port, new PuttySessionOptions { UserName = SshServerFixture.User, TryAgent = false, Command = "echo ran remotely" });

        Assert.IsTrue(rig.WaitForOutput("ran remotely"), rig.Diagnostics());
        Assert.IsTrue(rig.WaitForClosed(), "the session ends with the command: " + rig.Diagnostics());
        Assert.IsFalse(rig.OutputText.Contains("Welcome"));
    }
    [TestMethod]
    public void A_session_closed_while_a_question_is_open_ends_cleanly()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        SessionConfirmEventArgs? waiting = null;
        using var rig = new Rig { OnConfirm = e => waiting = e };

        rig.Start(server.Port, Test);
        Assert.IsTrue(EngineThread.WaitUntil(() => Volatile.Read(ref waiting) is not null, Generous), rig.Diagnostics());

        rig.Engine.Run(rig.Session.Dispose);                                // the user closed the tab
        Assert.IsFalse(rig.Engine.Run(() => waiting!.Accept()), "answering a question of a closed session does nothing");
    }
}
