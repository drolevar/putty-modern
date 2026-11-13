using System.Runtime.InteropServices;
using Digi21.WinUI.Docking;
using Digi21.WinUI.Docking.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PuTTY.Interop;
using PuTTY.Sessions;

namespace PuTTY.App;

// How the terminals are arranged (tabs, columns, rows, a grid) and the layouts the user saves by name: the command bar's
// Split pane, Save layout and Layout buttons.
public sealed partial class MainWindow
{
    private readonly NamedLayoutStore layoutStore = new(
        TestRegistry.WorkspaceFileOverride is { } scratch
            ? Path.Combine(Path.GetDirectoryName(scratch)!, "layouts.json")      // a test run keeps it with its other scratch files
            : NamedLayoutStore.DefaultPath);

    private NamedLayoutsFile layouts = new();
    private string currentLayout = LayoutNames.Default;
    private string? defaultLayout;      // the docking layout as the markup has it, taken before a saved one is loaded

    /// <summary>Reads the saved layouts and shows them in the command bar.</summary>
    private void StartLayouts()
    {
        layouts = layoutStore.Load();
        commands.SetLayouts(layouts.Names, currentLayout);
    }

    /// <summary>
    /// The built-in layout starts with Session details and the Connection log collapsed to tabs (the log's under the terminals,
    /// the details' at the right edge), so the terminal has the room and the panels are one click away. It is done before
    /// the Default layout is taken, so choosing Default in the command bar gives the same arrangement as a fresh start, and a
    /// saved workspace replaces it as it replaces everything else.
    /// </summary>
    /// <remarks>
    /// The end-to-end checks read both panels (the buttons of Session details, the connection log's rows), which a collapsed
    /// panel does not show until it is opened, so a run started with <c>--connect</c> or with the test registry keeps them
    /// docked unless it asks for the real default with <see cref="TestRegistry.CollapsedPanelsVariable"/>.
    /// </remarks>
    private void CollapseDefaultPanels()
    {
        bool collapse = Environment.GetEnvironmentVariable(TestRegistry.CollapsedPanelsVariable) == "1"
            || (!startedWithTarget && !TestRegistry.IsActive);
        if (!collapse)
        {
            return;
        }

        // The dock site only knows the windows it has been asked about, and a window it does not know yet would
        // put the collapse off until it loads, after the layout has been taken.
        _ = DockSite.ToolWindows;
        foreach (ToolWindow panel in new[] { DetailsPanel, LogPanel })
        {
            panel.AutoHide();
            if (panel.State != DockingWindowState.AutoHide)
            {
                PuttyLog.Warning("app", $"\"{panel.Title}\" could not be collapsed at start (state {panel.State})");
            }
        }
    }

    /// <summary>Keeps the layout the markup describes (the built-in Default) before anything changes it.</summary>
    private void CaptureDefaultLayout()
    {
        try
        {
            defaultLayout ??= new DockSiteLayoutSerializer().SaveToString(DockSite);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            PuttyLog.Warning("app", "The built-in layout could not be read", ex);
        }
    }

    // ----- Arranging the terminals --------------------------------------------------------------------

    /// <summary>
    /// Arranges the open terminals as asked: the docking library's <c>DocumentHost.Arrange</c> with the tabs dealt into the
    /// groups of the arrangement in the order they are in now, the last group taking the rest as tabs. With fewer tabs than
    /// groups the groups that would be empty are not made (three tabs in a grid: two on top, one across the bottom).
    /// </summary>
    private void ApplyArrangement(Arrangement arrangement)
    {
        if (DockSite.DocumentHost is not { } host)
        {
            return;
        }

        DocumentWindow[] documents = [.. host.Documents.Where(tabOf.ContainsKey)];
        if (documents.Length == 0)
        {
            return;
        }

        DocumentWindow? shown = DockSite.ActiveDocument;
        ArrangementShape shape = ArrangementShape.Of(arrangement);
        int leaves = shape.Leaves;
        int next = 0;

        DocumentLayout Build(ArrangementShape node)
        {
            if (node.IsLeaf)
            {
                int index = next++;
                DocumentWindow[] group = index >= documents.Length ? [] : index == leaves - 1 ? documents[index..] : [documents[index]];
                DocumentWindow? selected = shown is not null && group.Contains(shown) ? shown : null;
                return DocumentLayout.Group(group, selected).WithRelativeSize(node.Weight);
            }

            return DocumentLayout.Split(node.Horizontal ? Orientation.Horizontal : Orientation.Vertical, [.. node.Children.Select(Build)])
                .WithRelativeSize(node.Weight);
        }

        try
        {
            host.Arrange(Build(shape));
            PuttyLog.Info("app", $"terminals arranged: {Arrangements.Title(arrangement)} ({documents.Length} tabs)");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            PuttyLog.Warning("app", "The terminals could not be arranged", ex);
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not arrange the terminals", ex.Message);
            return;
        }

        active?.Terminal.Focus();
    }

    // ----- Named layouts ------------------------------------------------------------------------------

    /// <summary>
    /// Asks for a name and saves the layout under it: where the panels are and how big, how the terminals are split into
    /// groups, and which sessions are open in which tab, so that applying it later can open the ones that are not open.
    /// </summary>
    private async Task SaveLayoutAsync()
    {
        string? name = await AskLayoutNameAsync();
        if (name is null)
        {
            return;
        }

        try
        {
            var layout = new NamedLayout { Name = name, Layout = new DockSiteLayoutSerializer().SaveToString(DockSite) };
            layout.Sessions.AddRange(OpenSessionRecords());
            layouts.Upsert(layout);
            if (!layoutStore.Save(layouts))
            {
                await SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not save the layout",
                    "The saved layouts are from a newer version of this app and were left as they are.");
                return;
            }

            currentLayout = layout.Name;
            commands.SetLayouts(layouts.Names, currentLayout);
            PuttyLog.Info("app", $"layout saved as \"{name}\" with {layout.Sessions.Count} sessions");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            PuttyLog.Warning("app", "The layout could not be saved", ex);
            await SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not save the layout", ex.Message);
        }
    }

    /// <summary>The Save layout dialog: a name, checked as it is typed. Null when cancelled.</summary>
    private async Task<string?> AskLayoutNameAsync()
    {
        var box = new TextBox
        {
            PlaceholderText = "Layout name",
            Text = string.Equals(currentLayout, LayoutNames.Default, StringComparison.OrdinalIgnoreCase) ? string.Empty : currentLayout,
        };
        AutomationProperties.SetAutomationId(box, "LayoutNameBox");
        AutomationProperties.SetName(box, "Layout name");
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(box);
        panel.Children.Add(note);

        var dialog = new ContentDialog
        {
            Title = "Save layout",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = DockSite.XamlRoot,
        };

        void Check()
        {
            string? problem = LayoutNames.Problem(box.Text);
            bool replaces = problem is null && layouts.Find(LayoutNames.Normalise(box.Text)) is not null;
            note.Text = problem
                ?? (replaces ? "A saved layout of that name will be replaced."
                             : "Saves where the panels are, how the terminals are split, and which sessions are open.");
            dialog.IsPrimaryButtonEnabled = problem is null;
        }

        box.TextChanged += (_, _) => Check();
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        Check();

        try
        {
            ContentDialogResult result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary && LayoutNames.Problem(box.Text) is null ? LayoutNames.Normalise(box.Text) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            PuttyLog.Warning("app", "The Save layout dialog could not be shown", ex);      // another dialog is open
            return null;
        }
    }

    /// <summary>
    /// Applies a layout: the built-in Default, or a saved one. A saved layout puts the panels and the terminal groups as they
    /// were, uses the tabs that are open already for the sessions it records (the same saved session, or the same place
    /// connected to) and opens the ones that are not; tabs it does not record stay open, as tabs of its last group. Nothing
    /// that is open is closed.
    /// </summary>
    private void ApplyLayout(string name)
    {
        if (!App.EngineRunning)
        {
            return;
        }

        var created = new List<SessionTab>();
        try
        {
            if (string.Equals(name, LayoutNames.Default, StringComparison.OrdinalIgnoreCase))
            {
                ApplyDefaultLayout();
            }
            else if (layouts.Find(name) is { } saved)
            {
                ApplyNamedLayout(saved, created);
            }
            else
            {
                _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not apply the layout", $"There is no saved layout called \"{name}\".");
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Xml.XmlException or NotSupportedException)
        {
            foreach (SessionTab tab in created)
            {
                // Tabs this made for the layout and could not place: they end with it.
                tabs.Remove(tab);
                tabOf.Remove(tab.Document);
                tab.Dispose();
            }

            PuttyLog.Warning("app", $"The layout \"{name}\" could not be applied", ex);
            _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not apply the layout", ex.Message);
        }
    }

    /// <summary>The panels as the markup has them and every open terminal in tabs of one group.</summary>
    private void ApplyDefaultLayout()
    {
        string xml = defaultLayout ?? throw new InvalidOperationException("The built-in layout is not available yet.");
        string[] ids = [.. tabs.Select(t => t.Document.SerializationId).OfType<string>()];
        if (ids.Length > 0)
        {
            xml = LayoutArranger.EnsureDocuments(LayoutArranger.Retain(xml, Array.Empty<string>()), ids);      // the welcome document goes, the tabs take its place
        }
        else
        {
            welcomeClosed = false;      // with no session open the welcome document is part of the layout again
        }

        LoadLayout(xml, []);
        currentLayout = LayoutNames.Default;
        commands.SetLayouts(layouts.Names, currentLayout);
    }

    private void ApplyNamedLayout(NamedLayout saved, List<SessionTab> created)
    {
        var claimed = new HashSet<SessionTab>();
        var map = new Dictionary<string, string>();
        foreach (OpenSessionRecord record in saved.Sessions)
        {
            if (tabs.FirstOrDefault(t => !claimed.Contains(t) && Matches(t, record)) is { } open)
            {
                claimed.Add(open);
                map[record.Id] = open.Document.SerializationId ?? string.Empty;
                continue;
            }

            // Not open: the layout brings it back, in the document the layout has for it.
            string id = $"doc.session.{++tabCounter}";
            if (TabFor(WithId(record, id)) is { } fresh)
            {
                created.Add(fresh);
                claimed.Add(fresh);
                map[record.Id] = id;
            }
        }

        string xml = LayoutArranger.RemapDocumentIds(saved.Layout, map);
        string[] openIds = [.. tabs.Select(t => t.Document.SerializationId).OfType<string>()];
        xml = LayoutArranger.EnsureDocuments(xml, openIds);
        LoadLayout(xml, created);
        currentLayout = saved.Name;
        commands.SetLayouts(layouts.Names, currentLayout);
        PuttyLog.Info("app", $"layout \"{saved.Name}\" applied: {saved.Sessions.Count} sessions in it, {created.Count} opened");
    }

    /// <summary>Loads a docking layout; the tabs made for it are the ones the loader asks for by id.</summary>
    private void LoadLayout(string xml, List<SessionTab> created)
    {
        var serializer = new DockSiteLayoutSerializer();
        serializer.DocumentResolving += (_, e) =>
        {
            if (created.FirstOrDefault(t => t.Document.SerializationId == e.Id) is { } tab)
            {
                e.Document = tab.Document;
            }
        };
        serializer.LoadFromString(DockSite, xml);

        foreach (SessionTab tab in created)
        {
            tab.Terminal.Show(tab.Session);
        }

        if (created.Count > 0)
        {
            welcomeClosed = true;
        }

        if (DockSite.ActiveDocument is { } shown && tabOf.TryGetValue(shown, out SessionTab? current))
        {
            Activate(current);
        }
        else
        {
            active?.Terminal.Focus();
        }
    }

    /// <summary>Forgets a saved layout; the current one falls back to the built-in Default (the arrangement stays as it is).</summary>
    private void DeleteLayout(string name)
    {
        if (string.Equals(name, LayoutNames.Default, StringComparison.OrdinalIgnoreCase) || !layouts.Remove(name))
        {
            return;
        }

        try
        {
            if (!layoutStore.Save(layouts))
            {
                _ = SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "Could not delete the layout",
                    "The saved layouts are from a newer version of this app and were left as they are.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PuttyLog.Warning("app", "The layouts could not be saved after a delete", ex);
        }

        if (string.Equals(currentLayout, name, StringComparison.OrdinalIgnoreCase))
        {
            currentLayout = LayoutNames.Default;
        }

        commands.SetLayouts(layouts.Names, currentLayout);
    }

    /// <summary>Whether an open tab is the session a layout recorded: the same saved session, or the same place connected to.</summary>
    private static bool Matches(SessionTab tab, OpenSessionRecord record)
    {
        if (record.SavedName is { } name)
        {
            return string.Equals(tab.SavedName, name, StringComparison.OrdinalIgnoreCase);
        }

        if (tab.SavedName is not null || record.Host is null)
        {
            return false;
        }

        try
        {
            SessionEndpoint endpoint = tab.Session.GetEndpoint();
            return string.Equals(endpoint.Host, record.Host, StringComparison.OrdinalIgnoreCase)
                && endpoint.Port == record.Port
                && ProtocolText(endpoint.Protocol) == record.Protocol
                && string.Equals(endpoint.UserName ?? string.Empty, record.UserName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static OpenSessionRecord WithId(OpenSessionRecord record, string id) => new()
    {
        Id = id,
        Title = record.Title,
        SavedName = record.SavedName,
        Host = record.Host,
        Port = record.Port,
        Protocol = record.Protocol,
        UserName = record.UserName,
        KeyFile = record.KeyFile,
        Description = record.Description,
    };

    private async Task ShowAboutAsync()
    {
        await SessionInteraction.ShowMessageAsync(DockSite.XamlRoot, "About PuTTY",
            "PuTTY Modern: a WinUI 3 front end for PuTTY.\n\nThe connections, the terminal emulation and the settings are PuTTY's own code, unmodified.");
    }
}
