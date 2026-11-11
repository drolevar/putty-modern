using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// PuTTY's Change Settings on a running session: the settings box opened on a copy of the session's settings,
/// and Apply passing the edits to the terminal, line discipline, log and backend. The test server sends text, the
/// dialog changes a colour through PuTTY's own Colours page, and the offscreen view's pixels show the result.
/// </summary>
[TestClass]
public sealed class ReconfigureTests
{
    private const uint DefaultForeground = 0xBBBBBB;
    private const uint NewForeground = (10u << 16) | (200u << 8) | 30u;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    private sealed class Rig : IDisposable
    {
        public readonly EngineThread Engine = new();
        public readonly LoopbackServer Server = new();
        public readonly PuttySession Session;
        public readonly int CellWidth;
        public readonly int CellHeight;

        public Rig()
        {
            Session = Engine.Run(() =>
            {
                var session = new PuttySession("127.0.0.1", Server.Port, SessionProtocol.Raw);
                session.Start();
                return session;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => Server.ClientCount == 1, Generous), "never connected");
            Engine.Run(() =>
            {
                Session.AttachOffscreenView(640, 320, "Consolas", 22);
                Session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
            });
            TerminalGeometry g = Engine.Run(() => Session.Geometry);
            CellWidth = g.CellWidth;
            CellHeight = g.CellHeight;
        }

        public int CountInCell(int column, int row, uint colour) =>
            Engine.Run(() =>
            {
                int count = 0;
                for (int y = row * CellHeight; y < (row + 1) * CellHeight; y++)
                {
                    for (int x = column * CellWidth; x < (column + 1) * CellWidth; x++)
                    {
                        if (Session.ReadPixel(x, y) == colour)
                        {
                            count++;
                        }
                    }
                }

                return count;
            });

        public void Dispose()
        {
            Engine.Run(Session.Dispose);
            Engine.Dispose();
            Server.Dispose();
        }
    }

    private static SettingsControl Find(SettingsDialog dialog, string path, string label, SettingsControlType type) =>
        dialog.Groups.Where(g => g.Path == path).SelectMany(g => g.Controls).Single(c => c.Label == label && c.Type == type);

    /// <summary>Chooses a new Default Foreground the way the colour page does: select it, press Modify, answer the colour request.</summary>
    private static void ChooseForeground(EngineThread engine, SettingsDialog dialog, (int R, int G, int B) colour)
    {
        SettingsControl modify = Find(dialog, "Window/Colours", "Modify", SettingsControlType.Button);
        SettingsControl list = dialog.Groups.Where(g => g.Path == "Window/Colours").SelectMany(g => g.Controls)
            .Single(c => c.Type == SettingsControlType.ListBox);
        int foreground = engine.Run(() => dialog.GetList(list.Id)).ToList().FindIndex(e => e.Text == "Default Foreground");
        Assert.IsTrue(foreground >= 0);
        engine.Run(() =>
        {
            dialog.Select(list.Id, foreground);
            dialog.Act(modify.Id);
            dialog.ColourChosen(modify.Id, colour);
        });
    }

    [TestMethod]
    public void A_colour_changed_in_Change_Settings_recolours_the_running_terminal()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();
        rig.Server.Send(Encoding.UTF8.GetBytes("Hi"));
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.CountInCell(0, 0, DefaultForeground) > 0, Generous), "the text was never drawn");

        SettingsDialog dialog = rig.Engine.Run(() => SettingsDialog.OpenForSession(rig.Session));
        var ends = new List<bool>();
        rig.Engine.Run(() => dialog.Ended += go => ends.Add(go));
        Assert.IsTrue(dialog.IsMidSession);
        ChooseForeground(rig.Engine, dialog, (10, 200, 30));
        Assert.AreEqual(0, rig.CountInCell(0, 0, NewForeground), "editing the dialog changes nothing until Apply");

        SettingsControl apply = dialog.Groups.Where(g => g.Path.Length == 0).SelectMany(g => g.Controls)
            .Single(c => c.Type == SettingsControlType.Button && c.Flags.HasFlag(SettingsControlFlags.Default));
        Assert.AreEqual("Apply", apply.Label, "PuTTY calls its mid-session default button Apply");
        rig.Engine.Run(() => dialog.Act(apply.Id));
        CollectionAssert.AreEqual(new[] { true }, ends, "PuTTY ends the dialog when Apply is pressed");
        rig.Engine.Run(dialog.Apply);

        Assert.IsTrue(EngineThread.WaitUntil(() => rig.CountInCell(0, 0, NewForeground) > 0, Generous), "the text did not take the new colour");
        Assert.AreEqual(0, rig.CountInCell(0, 0, DefaultForeground), "the old colour is gone from the text that was already on screen");
        rig.Engine.Run(dialog.Dispose);
    }

    [TestMethod]
    public void Cancelling_Change_Settings_leaves_the_session_as_it_was()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();
        rig.Server.Send(Encoding.UTF8.GetBytes("Hi"));
        Assert.IsTrue(EngineThread.WaitUntil(() => rig.CountInCell(0, 0, DefaultForeground) > 0, Generous));

        SettingsDialog dialog = rig.Engine.Run(() => SettingsDialog.OpenForSession(rig.Session));
        ChooseForeground(rig.Engine, dialog, (10, 200, 30));
        rig.Engine.Run(dialog.Dispose);                     // closing the window is Cancel: nothing is applied

        Thread.Sleep(300);
        Assert.IsTrue(rig.CountInCell(0, 0, DefaultForeground) > 0);
        Assert.AreEqual(0, rig.CountInCell(0, 0, NewForeground));
    }

    [TestMethod]
    public void The_mid_session_box_leaves_out_what_cannot_change_while_connected()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        SettingsDialog dialog = rig.Engine.Run(() => SettingsDialog.OpenForSession(rig.Session));

        bool hasHostBox = dialog.Controls.Any(c => c.Label == "Host Name (or IP address)");
        Assert.IsFalse(hasHostBox, "PuTTY does not offer to change the host of a running session");
        Assert.IsTrue(dialog.Controls.Any(c => c.Label == "Apply"));
        Assert.IsFalse(dialog.Controls.Any(c => c.Label == "Open"));
        bool refused = false;
        try
        {
            rig.Engine.Run(() =>
            {
                using PuttySession another = dialog.CreateSession();       // a session is made unstarted; Start asks the engine
                another.Start();
            });
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Assert.IsTrue(refused, "a box on a running session changes that session; it does not start another");
        rig.Engine.Run(dialog.Dispose);
    }

    [TestMethod]
    public void A_dialog_whose_session_has_gone_cannot_apply_and_does_no_harm()
    {
        using var registry = new RegistrySandbox();
        using var engine = new EngineThread();
        using var server = new LoopbackServer();
        PuttySession session = engine.Run(() =>
        {
            var s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
            s.Start();
            return s;
        });
        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous));
        SettingsDialog dialog = engine.Run(() => SettingsDialog.OpenForSession(session));

        engine.Run(session.Dispose);

        bool refused = false;
        try
        {
            engine.Run(dialog.Apply);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        Assert.IsTrue(refused, "Apply on a closed session is refused");
        bool openRefused = false;
        try
        {
            engine.Run(() => SettingsDialog.OpenForSession(session));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            openRefused = true;
        }

        Assert.IsTrue(openRefused, "a closed session cannot be reconfigured");
        engine.Run(dialog.Dispose);
    }
}
