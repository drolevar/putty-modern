using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// PuTTY's configuration box driven without any drawing: the controls PuTTY describes, the values it puts
/// in them, what its handlers do when the host reports a click or a keystroke, and what ends up in PuTTY's
/// storage. The registry is the process's scratch copy (<see cref="RegistrySandbox"/>).
/// </summary>
[TestClass]
public sealed class SettingsDialogTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    /// <summary>One dialog on its own engine, with a record of what it told the host.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly EngineThread Engine = new();
        public readonly SettingsDialog Dialog;
        public readonly List<string> Messages = [];
        public readonly List<int> Changed = [];
        public readonly List<bool> Ends = [];
        public readonly List<(int Id, int R, int G, int B)> ColourRequests = [];
        public int Beeps;

        public Rig(bool useSavedDefaults = false)
        {
            Dialog = Engine.Run(() =>
            {
                var dialog = SettingsDialog.Open(useSavedDefaults);
                dialog.MessageRaised += (text, _) => Messages.Add(text);
                dialog.ControlChanged += id => Changed.Add(id);
                dialog.Ended += go => Ends.Add(go);
                dialog.Beeped += () => Beeps++;
                dialog.ColourRequested += (id, r, g, b) => ColourRequests.Add((id, r, g, b));
                return dialog;
            });
        }

        public T Run<T>(Func<T> action) => Engine.Run(action);

        public void Run(Action action) => Engine.Run(action);

        /// <summary>The control on page <paramref name="path"/> whose label is exactly <paramref name="label"/> (or, with a null label, the only one of that type with a given height).</summary>
        public SettingsControl Find(string path, string label, SettingsControlType? type = null) =>
            Dialog.Groups.Where(g => g.Path == path).SelectMany(g => g.Controls)
                .Single(c => c.Label == label && (type is null || c.Type == type));

        public SettingsControl HostBox => Find("Session", "Host Name (or IP address)", SettingsControlType.EditBox);

        public SettingsControl PortBox => Find("Session", "Port", SettingsControlType.EditBox);

        public SettingsControl ProtocolRadio =>
            Dialog.Groups.Where(g => g.Path == "Session").SelectMany(g => g.Controls).Single(c => c.Type == SettingsControlType.Radio && c.Buttons.Contains("SSH"));

        public SettingsControl SavedName => Find("Session", "Saved Sessions", SettingsControlType.EditBox);

        public SettingsControl SavedList => Dialog.Groups.Where(g => g.Path == "Session").SelectMany(g => g.Controls)
            .Single(c => c.Type == SettingsControlType.ListBox && c.ListHeight == 7);

        public SettingsControl OpenButton => Find(string.Empty, "Open", SettingsControlType.Button);

        /// <summary>The drop-down list behind the "Other:" radio button, with the protocols that have no button of their own.</summary>
        public SettingsControl ProtocolList => Dialog.Groups.Where(g => g.Path == "Session").SelectMany(g => g.Controls)
            .Single(c => c.IsDropDown && c.Type == SettingsControlType.ListBox);

        public int ProtocolIndex(string label) => Array.IndexOf([.. ProtocolRadio.Buttons], label);

        /// <summary>
        /// Chooses a protocol as the user does: SSH and Serial have radio buttons; the others (Telnet, Rlogin, Raw,
        /// SUPDUP) are picked from the list that the "Other:" button goes with.
        /// </summary>
        public void ChooseProtocol(string name)
        {
            int button = ProtocolIndex(name);
            if (button >= 0)
            {
                Dialog.SetRadio(ProtocolRadio.Id, button);
                return;
            }

            int entry = Dialog.GetList(ProtocolList.Id).ToList().FindIndex(e => e.Text == name);
            Assert.IsTrue(entry >= 0, $"no protocol called {name}: " + string.Join(", ", Dialog.GetList(ProtocolList.Id).Select(e => e.Text)));
            Dialog.Select(ProtocolList.Id, entry);
        }

        public void Dispose()
        {
            Engine.Run(Dialog.Dispose);
            Engine.Dispose();
        }
    }

    // ----- The description ----------------------------------------------------------------------

    [TestMethod]
    public void PuTTY_describes_its_pages_and_controls()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        string[] pages = [.. rig.Dialog.Groups.Select(g => g.Path).Distinct()];

        foreach (string expected in new[] { "Session", "Session/Logging", "Terminal", "Terminal/Keyboard", "Window", "Window/Colours", "Connection", "Connection/SSH", "Connection/SSH/Auth" })
        {
            CollectionAssert.Contains(pages, expected);
        }

        Assert.IsTrue(rig.Dialog.Controls.Count > 150, $"PuTTY has about 200 controls; got {rig.Dialog.Controls.Count}");
        Assert.IsTrue(rig.Dialog.Groups.Count > 40);
        // Open and Cancel are in a group of their own, on no page.
        Assert.IsNotNull(rig.OpenButton);
        Assert.IsTrue(rig.OpenButton.Flags.HasFlag(SettingsControlFlags.Default));
        Assert.IsTrue(rig.Find(string.Empty, "Cancel", SettingsControlType.Button).Flags.HasFlag(SettingsControlFlags.Cancel));
        // Every control belongs to the group that lists it, and ids are unique.
        Assert.AreEqual(rig.Dialog.Controls.Count, rig.Dialog.Controls.Select(c => c.Id).Distinct().Count());
        foreach (SettingsGroup group in rig.Dialog.Groups)
        {
            Assert.IsTrue(group.Controls.All(c => c.Group == group.Index));
        }
    }

    [TestMethod]
    public void Controls_carry_what_a_host_needs_to_draw_them()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        SettingsControl protocols = rig.ProtocolRadio;
        CollectionAssert.IsSubsetOf(new[] { "SSH", "Serial", "Other:" }, protocols.Buttons.ToArray());
        string[] others = [.. rig.Run(() => rig.Dialog.GetList(rig.ProtocolList.Id)).Select(e => e.Text)];
        CollectionAssert.IsSubsetOf(new[] { "Telnet", "Rlogin", "Raw" }, others);
        Assert.AreEqual(protocols.Buttons.Count, protocols.Shortcuts.Length, "one shortcut slot per button");

        Assert.AreEqual('n', rig.HostBox.Shortcut);
        Assert.AreEqual(100, rig.HostBox.PercentWidth);
        Assert.AreEqual(0, rig.HostBox.ColumnStart);
        Assert.AreEqual(1, rig.PortBox.ColumnStart, "host and port share a row of two columns");

        SettingsControl columns = rig.Dialog.Groups.Where(g => g.Path == "Session").SelectMany(g => g.Controls)
            .First(c => c.Type == SettingsControlType.Columns);
        Assert.AreEqual(columns.Columns, columns.Percentages.Count);
        Assert.AreEqual(100, columns.Percentages.Sum());

        Assert.AreEqual(SettingsFileFilter.KeyFiles,
            rig.Dialog.Controls.First(c => c.Type == SettingsControlType.FileSelect && c.Label!.Contains("Private key")).Filter);
        Assert.IsTrue(rig.Dialog.Controls.Any(c => c.Type == SettingsControlType.FontSelect));
        Assert.IsTrue(rig.Dialog.Controls.Any(c => c.Type == SettingsControlType.ListBox && c.IsDropDown));
    }

    // ----- Values -------------------------------------------------------------------------------

    [TestMethod]
    public void Opening_fills_the_controls_from_the_defaults()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        Assert.AreEqual(string.Empty, rig.Run(() => rig.Dialog.GetText(rig.HostBox.Id)));
        Assert.AreEqual("22", rig.Run(() => rig.Dialog.GetText(rig.PortBox.Id)));
        Assert.AreEqual(rig.ProtocolIndex("SSH"), rig.Run(() => rig.Dialog.GetRadio(rig.ProtocolRadio.Id)));
        Assert.IsTrue(rig.Run(() => rig.Dialog.GetChecked(rig.Find("Session/Logging", "Flush log file frequently", SettingsControlType.CheckBox).Id)));
    }

    [TestMethod]
    public void Choosing_a_protocol_changes_the_port_as_PuTTY_does()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        rig.Run(() => rig.ChooseProtocol("Telnet"));

        Assert.AreEqual("23", rig.Run(() => rig.Dialog.GetText(rig.PortBox.Id)), "PuTTY switches 22 to Telnet's 23");
        Assert.IsTrue(rig.Changed.Contains(rig.PortBox.Id));

        rig.Run(() => rig.Dialog.SetText(rig.PortBox.Id, "2323"));
        rig.Run(() => rig.ChooseProtocol("SSH"));
        Assert.AreEqual("2323", rig.Run(() => rig.Dialog.GetText(rig.PortBox.Id)), "a port the user chose is left alone");
    }

    [TestMethod]
    public void A_check_box_and_a_radio_group_change_the_settings_and_read_back()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();
        SettingsControl flush = rig.Find("Session/Logging", "Flush log file frequently", SettingsControlType.CheckBox);

        rig.Run(() => rig.Dialog.SetChecked(flush.Id, false));
        Assert.IsFalse(rig.Run(() => rig.Dialog.GetChecked(flush.Id)));

        SettingsControl closeOnExit = rig.Find("Session", "Close window on exit:", SettingsControlType.Radio);
        rig.Run(() => rig.Dialog.SetRadio(closeOnExit.Id, 1));
        Assert.AreEqual(1, rig.Run(() => rig.Dialog.GetRadio(closeOnExit.Id)));

        // Refreshing from the settings shows what the settings hold, which are what the user set.
        rig.Run(() => rig.Dialog.SetRadio(closeOnExit.Id, 0));
        Assert.AreEqual(0, rig.Run(() => rig.Dialog.GetRadio(closeOnExit.Id)));
    }

    // ----- Going ahead ----------------------------------------------------------------------------

    [TestMethod]
    public void Open_without_a_host_beeps_and_does_not_end_the_dialog()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        rig.Run(() => rig.Dialog.Act(rig.OpenButton.Id));

        Assert.AreEqual(0, rig.Ends.Count);
        Assert.AreEqual(1, rig.Beeps);
    }

    [TestMethod]
    public void Open_with_a_host_ends_the_dialog_and_the_session_uses_the_edited_settings()
    {
        using var registry = new RegistrySandbox();
        using var server = new LoopbackServer(echo: true);
        using var rig = new Rig();
        rig.Run(() =>
        {
            rig.ChooseProtocol("Raw");
            rig.Dialog.SetText(rig.HostBox.Id, "127.0.0.1");
            rig.Dialog.SetText(rig.PortBox.Id, server.Port.ToString());
            rig.Dialog.Act(rig.OpenButton.Id);
        });

        CollectionAssert.AreEqual(new[] { true }, rig.Ends);
        PuttySession session = rig.Run(() =>
        {
            PuttySession s = rig.Dialog.CreateSession();
            s.Start();
            return s;
        });

        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "the session did not connect to the edited host and port");
        rig.Run(session.Dispose);
    }

    [TestMethod]
    public void Cancel_ends_the_dialog_with_no()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        rig.Run(() => rig.Dialog.Act(rig.Find(string.Empty, "Cancel", SettingsControlType.Button).Id));

        CollectionAssert.AreEqual(new[] { false }, rig.Ends);
    }

    [TestMethod]
    public void The_host_may_close_the_dialog_from_inside_the_Ended_event()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();
        rig.Run(() =>
        {
            rig.Dialog.Ended += _ => rig.Dialog.Dispose();     // PuTTY's handler is still on the stack here
            rig.Dialog.SetText(rig.HostBox.Id, "127.0.0.1");
            rig.Dialog.Act(rig.OpenButton.Id);
        });

        // The engine tears the dialog down once the handler has returned; nothing crashes and the next call is refused.
        Thread.Sleep(200);
        Assert.ThrowsExactly<ObjectDisposedException>(() => rig.Run(() => rig.Dialog.GetText(rig.HostBox.Id)));
    }

    // ----- Saved sessions: edit a setting, save a session, reopen it ---------------------------------------

    [TestMethod]
    public void A_session_saved_from_the_dialog_is_in_PuTTYs_storage_and_loads_back_into_a_fresh_dialog()
    {
        using var registry = new RegistrySandbox();
        using (var rig = new Rig())
        {
            rig.Run(() =>
            {
                rig.ChooseProtocol("Telnet");
                rig.Dialog.SetText(rig.HostBox.Id, "box.example.org");
                rig.Dialog.SetText(rig.PortBox.Id, "2323");
                rig.Dialog.SetText(rig.SavedName.Id, "my telnet box");
                rig.Dialog.SetChecked(rig.Find("Session/Logging", "Flush log file frequently", SettingsControlType.CheckBox).Id, false);
                rig.Dialog.Act(rig.Find("Session", "Save", SettingsControlType.Button).Id);
            });

            Assert.AreEqual(0, rig.Messages.Count, string.Join(" | ", rig.Messages));
            CollectionAssert.Contains(rig.Run(() => PuttySessions.List()).ToArray(), "my telnet box");
            string stored = registry.Snapshot();
            StringAssert.Contains(stored, "HostName(String)=box.example.org");
            StringAssert.Contains(stored, "PortNumber(DWord)=2323");
            StringAssert.Contains(stored, "Protocol(String)=telnet");
            StringAssert.Contains(stored, "LogFlush(DWord)=0");
            Assert.IsTrue(rig.Run(() => rig.Dialog.GetList(rig.SavedList.Id)).Any(e => e.Text == "my telnet box"), "the list of saved sessions shows it");
        }

        // A new dialog: the list has it; load it; every page shows what was saved.
        using var again = new Rig();
        IReadOnlyList<SettingsListEntry> entries = again.Run(() => again.Dialog.GetList(again.SavedList.Id));
        int at = entries.ToList().FindIndex(e => e.Text == "my telnet box");
        Assert.IsTrue(at >= 0, string.Join(", ", entries.Select(e => e.Text)));

        again.Run(() =>
        {
            again.Dialog.Select(again.SavedList.Id, at);
            again.Dialog.Act(again.Find("Session", "Load", SettingsControlType.Button).Id);
        });

        Assert.AreEqual("box.example.org", again.Run(() => again.Dialog.GetText(again.HostBox.Id)));
        Assert.AreEqual("2323", again.Run(() => again.Dialog.GetText(again.PortBox.Id)));
        Assert.AreEqual(again.ProtocolIndex("Other:"), again.Run(() => again.Dialog.GetRadio(again.ProtocolRadio.Id)), "Telnet is under Other:");
        Assert.AreEqual("Telnet", again.Run(() => again.Dialog.GetList(again.ProtocolList.Id)).Single(e => e.Selected).Text);
        Assert.IsFalse(again.Run(() => again.Dialog.GetChecked(again.Find("Session/Logging", "Flush log file frequently", SettingsControlType.CheckBox).Id)));
        Assert.AreEqual("my telnet box", again.Run(() => again.Dialog.GetText(again.SavedName.Id)));
    }

    [TestMethod]
    public void Deleting_a_saved_session_removes_it_from_the_storage_and_the_list()
    {
        using var registry = new RegistrySandbox();
        registry.WriteSession("doomed", "10.0.0.1", 22, "ssh");
        registry.WriteSession("kept", "10.0.0.2", 22, "ssh");
        using var rig = new Rig();
        int at = rig.Run(() => rig.Dialog.GetList(rig.SavedList.Id)).ToList().FindIndex(e => e.Text == "doomed");
        Assert.IsTrue(at >= 0);

        rig.Run(() =>
        {
            rig.Dialog.Select(rig.SavedList.Id, at);
            rig.Dialog.Act(rig.Find("Session", "Delete", SettingsControlType.Button).Id);
        });

        string[] left = [.. rig.Run(() => PuttySessions.List())];
        CollectionAssert.AreEquivalent(new[] { "kept" }, left);
        CollectionAssert.AreEquivalent(new[] { "Default Settings", "kept" }, rig.Run(() => rig.Dialog.GetList(rig.SavedList.Id)).Select(e => e.Text).ToArray(),
            "the dialog always lists Default Settings first, saved or not");
    }

    [TestMethod]
    public void Double_clicking_a_saved_session_loads_it_and_ends_the_dialog()
    {
        using var registry = new RegistrySandbox();
        registry.WriteSession("go now", "10.0.0.9", 22, "ssh");
        using var rig = new Rig();
        int at = rig.Run(() => rig.Dialog.GetList(rig.SavedList.Id)).ToList().FindIndex(e => e.Text == "go now");

        rig.Run(() =>
        {
            rig.Dialog.Select(rig.SavedList.Id, at);
            rig.Dialog.Act(rig.SavedList.Id);       // a double click on the list
        });

        CollectionAssert.AreEqual(new[] { true }, rig.Ends);
        Assert.AreEqual("10.0.0.9", rig.Run(() => rig.Dialog.GetText(rig.HostBox.Id)));
    }

    // ----- Refusals, colours, fonts, files ---------------------------------------------------------------

    [TestMethod]
    public void A_forwarding_without_a_source_port_is_refused_with_a_message()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();
        SettingsControl add = rig.Dialog.Groups.Where(g => g.Path == "Connection/SSH/Tunnels").SelectMany(g => g.Controls)
            .Single(c => c.Type == SettingsControlType.Button && c.Label == "Add");

        rig.Run(() => rig.Dialog.Act(add.Id));

        Assert.IsTrue(rig.Messages.Any(m => m.Contains("source port")), string.Join(" | ", rig.Messages));
    }

    [TestMethod]
    public void The_colour_chooser_round_trips_through_the_host()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();
        SettingsControl modify = rig.Find("Window/Colours", "Modify", SettingsControlType.Button);
        SettingsControl list = rig.Dialog.Groups.Where(g => g.Path == "Window/Colours").SelectMany(g => g.Controls)
            .Single(c => c.Type == SettingsControlType.ListBox);
        int foreground = rig.Run(() => rig.Dialog.GetList(list.Id)).ToList().FindIndex(e => e.Text == "Default Foreground");
        Assert.IsTrue(foreground >= 0);

        rig.Run(() =>
        {
            rig.Dialog.Select(list.Id, foreground);
            rig.Dialog.Act(modify.Id);
        });

        Assert.AreEqual(1, rig.ColourRequests.Count, "PuTTY asks the host for a colour chooser");
        Assert.AreEqual((187, 187, 187), (rig.ColourRequests[0].R, rig.ColourRequests[0].G, rig.ColourRequests[0].B), "starting at the current colour");

        rig.Run(() => rig.Dialog.ColourChosen(modify.Id, (10, 20, 30)));

        SettingsControl[] texts = [.. rig.Dialog.Groups.Where(g => g.Path == "Window/Colours").SelectMany(g => g.Controls)
            .Where(c => c.Type == SettingsControlType.EditBox)];
        string[] values = [.. texts.Select(c => rig.Run(() => rig.Dialog.GetText(c.Id)))];
        CollectionAssert.AreEqual(new[] { "10", "20", "30" }, values, "the red, green and blue boxes show the colour chosen");
    }

    [TestMethod]
    public void A_font_and_a_file_are_taken_and_given_back()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();
        SettingsControl font = rig.Dialog.Controls.First(c => c.Type == SettingsControlType.FontSelect);
        SettingsControl keyFile = rig.Dialog.Controls.First(c => c.Type == SettingsControlType.FileSelect && c.Label!.Contains("Private key"));

        rig.Run(() =>
        {
            rig.Dialog.SetFont(font.Id, new SettingsFont("Cascadia Mono", 13, true, 0));
            rig.Dialog.SetFile(keyFile.Id, @"C:\keys\work.ppk");
        });

        Assert.AreEqual(new SettingsFont("Cascadia Mono", 13, true, 0), rig.Run(() => rig.Dialog.GetFont(font.Id)));
        Assert.AreEqual(@"C:\keys\work.ppk", rig.Run(() => rig.Dialog.GetFile(keyFile.Id)));
    }

    [TestMethod]
    public void Text_with_non_ASCII_characters_survives_the_trip()
    {
        using var registry = new RegistrySandbox();
        using var rig = new Rig();

        rig.Run(() => rig.Dialog.SetText(rig.SavedName.Id, "Сервер ünïcode 日本"));

        Assert.AreEqual("Сервер ünïcode 日本", rig.Run(() => rig.Dialog.GetText(rig.SavedName.Id)));
    }
}
