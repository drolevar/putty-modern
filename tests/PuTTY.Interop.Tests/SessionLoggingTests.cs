using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Session logging (PuTTY's Session > Logging page): the settings of a saved session turn the log on, and when the file
/// exists already and the settings say "ask", the host is asked (overwrite, append, or no logging). The sessions are
/// raw connections to a loopback server whose bytes are what gets logged (log type 2: all session output).
/// </summary>
[TestClass]
public sealed class SessionLoggingTests
{
    private const int LogAllOutput = 2;
    private const int ClashAsk = -1, ClashAppend = 0, ClashOverwrite = 1;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private sealed class Rig : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "puttymodern-log-" + Guid.NewGuid().ToString("N"));
        private readonly RegistrySandbox registry = new();
        private readonly List<SessionConfirmEventArgs> questions = [];
        private readonly List<string> errors = [];
        public readonly EngineThread Engine = new();
        public readonly LoopbackServer Server = new();
        public PuttySession? Session;

        public Rig(string? existing, int clash, string? answer)
        {
            Directory.CreateDirectory(directory);
            LogFile = Path.Combine(directory, "session.log");
            if (existing is not null)
            {
                File.WriteAllText(LogFile, existing);
            }

            registry.WriteSession("logged", "127.0.0.1", Server.Port, "raw", null,
                ("LogType", LogAllOutput), ("LogFileName", LogFile), ("LogFileClash", clash), ("LogHeader", 0));
            Session = Engine.Run(() =>
            {
                PuttySession session = PuttySession.OpenSaved("logged");
                session.Error += (_, e) => { lock (errors) { errors.Add(e.Message); } };
                session.ConfirmationRequested += (_, e) =>
                {
                    lock (questions) { questions.Add(e); }
                    switch (answer)
                    {
                        case "overwrite": e.Overwrite(); break;
                        case "append": e.Append(); break;
                        case "cancel": e.Cancel(); break;
                        // null: the test answers later
                    }
                };
                session.Start();
                session.EnableTerminal(80, 24);     // the emulator is what logs session output
                return session;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => Server.ClientCount == 1, Generous), "never connected");
        }

        public string LogFile { get; }

        public IReadOnlyList<SessionConfirmEventArgs> Questions
        {
            get { lock (questions) { return [.. questions]; } }
        }

        public IReadOnlyList<string> Errors
        {
            get { lock (errors) { return [.. errors]; } }
        }

        public void Send(string text) => Server.Send(Encoding.UTF8.GetBytes(text));

        public string ReadLog()
        {
            if (!File.Exists(LogFile))
            {
                return string.Empty;
            }

            // PuTTY keeps the file open for writing: read it without asking for exclusive access
            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public bool WaitForLog(string text) => EngineThread.WaitUntil(() => ReadLog().Contains(text, StringComparison.Ordinal), Generous);

        public void Dispose()
        {
            Engine.Run(() => Session?.Dispose());
            Engine.Dispose();
            Server.Dispose();
            registry.Dispose();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // a scratch folder in %TEMP%
            }
        }
    }

    [TestMethod]
    public void A_session_whose_settings_turn_logging_on_writes_what_the_server_sent()
    {
        using var rig = new Rig(existing: null, ClashAsk, answer: null);

        rig.Send("hello from the server");

        Assert.IsTrue(rig.WaitForLog("hello from the server"), "the session log never got the text: " + rig.ReadLog());
        Assert.AreEqual(0, rig.Questions.Count, "a new file is not a question");
    }

    [TestMethod]
    public void A_file_that_exists_is_asked_about_and_append_adds_to_it()
    {
        using var rig = new Rig("OLD CONTENT\r\n", ClashAsk, answer: "append");

        rig.Send("new words");

        Assert.IsTrue(rig.WaitForLog("new words"), "nothing was appended: " + rig.ReadLog());
        string log = rig.ReadLog();
        StringAssert.StartsWith(log, "OLD CONTENT");
        Assert.AreEqual(1, rig.Questions.Count);
        Assert.AreEqual(SessionConfirmKind.LogFileExists, rig.Questions[0].Kind);
        Assert.AreEqual(rig.LogFile, rig.Questions[0].Host, "the question names the file");
    }

    [TestMethod]
    public void Overwrite_replaces_the_file()
    {
        using var rig = new Rig("OLD CONTENT\r\n", ClashAsk, answer: "overwrite");

        rig.Send("new words");

        Assert.IsTrue(rig.WaitForLog("new words"));
        Assert.IsFalse(rig.ReadLog().Contains("OLD CONTENT", StringComparison.Ordinal), "the old content should be gone");
    }

    [TestMethod]
    public void Cancel_turns_logging_off_and_leaves_the_file_alone_while_the_session_goes_on()
    {
        using var rig = new Rig("OLD CONTENT\r\n", ClashAsk, answer: "cancel");
        rig.Engine.Run(() => rig.Session!.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off));

        rig.Send("still here");
        rig.Engine.Run(() => rig.Session!.TypeText("ping"));

        Assert.IsTrue(EngineThread.WaitUntil(() => Encoding.UTF8.GetString(rig.Server.Received).Contains("ping"), Generous),
            "the session should carry on after logging was refused");
        Assert.AreEqual("OLD CONTENT\r\n", rig.ReadLog());
    }

    [TestMethod]
    public void With_nobody_to_ask_the_file_is_left_alone()
    {
        using var registry = new RegistrySandbox();
        using var server = new LoopbackServer();
        string directory = Path.Combine(Path.GetTempPath(), "puttymodern-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "session.log");
        File.WriteAllText(file, "OLD CONTENT\r\n");
        try
        {
            registry.WriteSession("logged", "127.0.0.1", server.Port, "raw", null,
                ("LogType", LogAllOutput), ("LogFileName", file), ("LogFileClash", ClashAsk));
            using var engine = new EngineThread();
            PuttySession session = engine.Run(() =>
            {
                PuttySession s = PuttySession.OpenSaved("logged");
                s.Start();      // no ConfirmationRequested handler
                s.EnableTerminal(80, 24);
                return s;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous));

            engine.Run(() => session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off));
            server.Send("data"u8);
            engine.Run(() => session.TypeText("ping"));

            Assert.IsTrue(EngineThread.WaitUntil(() => Encoding.UTF8.GetString(server.Received).Contains("ping"), Generous),
                "the session should carry on");
            Assert.AreEqual("OLD CONTENT\r\n", File.ReadAllText(file), "a question nobody can answer must not overwrite anything");
            engine.Run(session.Dispose);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void The_answer_may_come_later_and_what_was_logged_meanwhile_is_kept()
    {
        using var rig = new Rig("OLD CONTENT\r\n", ClashAsk, answer: null);

        rig.Send("sent while the question was open");
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.Questions.Count == 1, Generous), "the question never came");
        Assert.AreEqual("OLD CONTENT\r\n", rig.ReadLog(), "nothing is written until the question is answered");

        SessionConfirmEventArgs question = rig.Questions[0];
        Assert.IsTrue(rig.Engine.Run(question.Append));

        Assert.IsTrue(rig.WaitForLog("sent while the question was open"), "the queued text was lost: " + rig.ReadLog());
        Assert.IsFalse(rig.Engine.Run(question.Append), "a question can be answered once");
    }

    [TestMethod]
    public void The_settings_can_say_overwrite_or_append_always_and_then_nobody_is_asked()
    {
        foreach ((int clash, bool keepsOld) in new[] { (ClashOverwrite, false), (ClashAppend, true) })
        {
            using var rig = new Rig("OLD CONTENT\r\n", clash, answer: "cancel");

            rig.Send("fresh");

            Assert.IsTrue(rig.WaitForLog("fresh"), $"clash {clash}: " + rig.ReadLog());
            Assert.AreEqual(keepsOld, rig.ReadLog().Contains("OLD CONTENT", StringComparison.Ordinal), $"clash {clash}");
            Assert.AreEqual(0, rig.Questions.Count, $"clash {clash}: the settings decide, so there is no question");
        }
    }
}
