using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// The diagnostic file log: off by default, a level filter, the switches that turn it on, and the engine's own trace
/// arriving in the same file. <see cref="PuttyLog"/> is process-wide, so every test closes it again.
/// </summary>
[TestClass]
public sealed class LogTests
{
    private static string ScratchFile() => Path.Combine(Path.GetTempPath(), "puttymodern-logtest-" + Guid.NewGuid().ToString("N") + ".log");

    private static string Read(string path)
    {
        PuttyLog.Close();
        return File.ReadAllText(path);
    }

    [TestCleanup]
    public void Cleanup() => PuttyLog.Close();

    [TestMethod]
    public void The_log_is_off_until_it_is_configured()
    {
        PuttyLog.Close();
        Assert.AreEqual(PuttyLogLevel.Off, PuttyLog.Level);
        Assert.IsNull(PuttyLog.FilePath);
        Assert.IsFalse(PuttyLog.IsEnabled(PuttyLogLevel.Error));
        PuttyLog.Error("test", "goes nowhere");     // must not throw or create anything
    }

    [TestMethod]
    public void A_level_includes_the_ones_below_it_and_nothing_above()
    {
        string file = ScratchFile();
        try
        {
            Assert.IsTrue(PuttyLog.Configure(PuttyLogLevel.Information, file));
            PuttyLog.Error("test", "an-error");
            PuttyLog.Warning("test", "a-warning");
            PuttyLog.Info("test", "an-info");
            PuttyLog.Detail("test", "a-detail");
            PuttyLog.Trace("test", "a-trace");

            string text = Read(file);
            StringAssert.Contains(text, "ERR [");
            StringAssert.Contains(text, "test: an-error");
            StringAssert.Contains(text, "WRN [");
            StringAssert.Contains(text, "test: a-warning");
            StringAssert.Contains(text, "test: an-info");
            Assert.DoesNotContain("a-detail", text);
            Assert.DoesNotContain("a-trace", text);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void A_multi_line_message_is_indented_and_an_exception_is_included()
    {
        string file = ScratchFile();
        try
        {
            PuttyLog.Configure(PuttyLogLevel.Trace, file);
            PuttyLog.Info("test", "first\nsecond");
            PuttyLog.Error("test", "it failed", new InvalidOperationException("the reason"));

            string text = Read(file);
            StringAssert.Contains(text, "first" + Environment.NewLine + "    second");
            StringAssert.Contains(text, "it failed: System.InvalidOperationException: the reason");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void A_file_that_cannot_be_opened_leaves_the_log_off_instead_of_throwing()
    {
        string blocked = Path.Combine(Path.GetTempPath(), "puttymodern-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(blocked);
        try
        {
            Assert.IsFalse(PuttyLog.Configure(PuttyLogLevel.Debug, blocked));       // a directory, not a file
            Assert.AreEqual(PuttyLogLevel.Off, PuttyLog.Level);
            PuttyLog.Info("test", "still fine");
        }
        finally
        {
            Directory.Delete(blocked);
        }
    }

    [TestMethod]
    [DataRow("--log", PuttyLogLevel.Debug)]
    [DataRow("--log=trace", PuttyLogLevel.Trace)]
    [DataRow("--LOG=Warning", PuttyLogLevel.Warning)]
    [DataRow("--log=1", PuttyLogLevel.Error)]
    [DataRow("--log=nonsense", PuttyLogLevel.Debug)]        // the user wants a log; the name was wrong
    public void The_command_line_switches_the_log_on(string argument, PuttyLogLevel expected)
    {
        Assert.AreEqual(expected, PuttyLog.TryParseSwitch(["PuTTY.App.exe", argument, "--connect", "raw://x:1"], out string? file));
        Assert.IsNull(file);
    }

    [TestMethod]
    public void Nothing_on_the_command_line_means_off_and_a_file_alone_means_debug()
    {
        Assert.AreEqual(PuttyLogLevel.Off, PuttyLog.TryParseSwitch(["PuTTY.App.exe", "--connect", "raw://x:1"], out _));
        Assert.AreEqual(PuttyLogLevel.Debug, PuttyLog.TryParseSwitch(["a.exe", "--log-file=C:/x/y.log"], out string? file));
        Assert.AreEqual("C:/x/y.log", file);
        Assert.AreEqual(PuttyLogLevel.Information, PuttyLog.TryParseSwitch(["a.exe", "--log=info", "--log-file", "z.log"], out file));
        Assert.AreEqual("z.log", file);
    }

    [TestMethod]
    public void The_engines_own_trace_and_PuTTYs_event_log_reach_the_file()
    {
        string file = ScratchFile();
        PuttyLog.Configure(PuttyLogLevel.Debug, file);      // before the engine starts: Initialize registers the trace handler
        try
        {
            using var server = new LoopbackServer();
            using (var engine = new EngineThread())
            {
                PuttySession session = engine.Run(() =>
                {
                    var s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
                    s.Start();
                    s.AttachOffscreenView(640, 320, "Consolas", 22);
                    return s;
                });
                Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, TimeSpan.FromSeconds(10)));
                engine.Run(session.Dispose);
            }

            string text = Read(file);
            StringAssert.Contains(text, "engine: engine started");
            StringAssert.Contains(text, $"engine: session #");
            StringAssert.Contains(text, $"raw to 127.0.0.1 port {server.Port}");           // native, at Information
            StringAssert.Contains(text, "view attached, 640 x 320 px, font Consolas");      // native, at Information
            StringAssert.Contains(text, "terminal fitted to");                              // native, at Debug
            StringAssert.Contains(text, "palette: ");                                       // native, at Debug
            StringAssert.Contains(text, "PuTTY says: Connecting to 127.0.0.1");             // PuTTY's event log, at Debug
            StringAssert.Contains(text, "disposed");
            StringAssert.Contains(text, ": closed");                                        // native
            Assert.DoesNotContain("palette[", text);                                         // that is Trace
        }
        finally
        {
            File.Delete(file);
        }
    }

    [TestMethod]
    public void The_serial_lines_settings_are_logged_when_it_is_opened()
    {
        // No port is needed: a session on a line that does not exist still logs what it was asked to open, and why it failed.
        string file = ScratchFile();
        PuttyLog.Configure(PuttyLogLevel.Information, file);
        try
        {
            using var registry = new RegistrySandbox();
            registry.WriteSession("serial test", string.Empty, 0, "serial", null,
                ("SerialLine", "COM250"), ("SerialSpeed", 19200), ("SerialDataBits", 7), ("SerialStopHalfbits", 4),
                ("SerialParity", 2), ("SerialFlowControl", 2));
            using var engine = new EngineThread();
            Assert.ThrowsExactly<InvalidOperationException>(() => engine.Run(() => PuttySession.OpenSaved("serial test").Start()));

            string text = Read(file);
            StringAssert.Contains(text, "serial line 'COM250', 19200 baud, 7 data bits, 2 stop bits, parity e, flow control RTS/CTS");
            StringAssert.Contains(text, "COM250");
            StringAssert.Contains(text, "could not be opened");                 // the managed side, with PuTTY's reason
            StringAssert.Contains(text, "the backend would not start");         // the engine's own line
        }
        finally
        {
            File.Delete(file);
        }
    }
}
