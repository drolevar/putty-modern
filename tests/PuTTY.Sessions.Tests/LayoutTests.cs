using System.Xml.Linq;
using PuTTY.Sessions;

namespace PuTTY.Sessions.Tests;

/// <summary>
/// The pure parts of arranging terminals: the shapes of the arrangements, the edits of a saved layout's document ids (for
/// named layouts), and the file of named layouts. The sample layout has the structure of the app's window: Sessions on the
/// left, the document area in the middle with the connection log below it, Session details on the right.
/// </summary>
[TestClass]
public sealed class LayoutTests
{
    // Two document groups side by side: doc.session.1 and .2 as tabs on the left (1 shown), .3 pinned on the right.
    private const string Sample = """
        <DockSiteLayout Version="3">
          <SplitContainer Orientation="Horizontal" RelativeSize="1">
            <ToolWindowContainer RelativeSize="0.2" SelectedId="sessions"><ToolWindow Id="sessions" State="Docked" /></ToolWindowContainer>
            <SplitContainer Orientation="Vertical" RelativeSize="0.6">
              <DocumentHost RelativeSize="0.75">
                <SplitContainer Orientation="Horizontal" RelativeSize="1">
                  <DocumentContainer RelativeSize="0.5" SelectedId="doc.session.1">
                    <Document Id="doc.session.1" State="Docked" />
                    <Document Id="doc.session.2" State="Docked" />
                  </DocumentContainer>
                  <DocumentContainer RelativeSize="0.5" SelectedId="doc.session.3">
                    <Document Id="doc.session.3" State="Docked" IsPinned="True" />
                  </DocumentContainer>
                </SplitContainer>
              </DocumentHost>
              <ToolWindowContainer RelativeSize="0.25" SelectedId="log"><ToolWindow Id="log" State="Docked" /></ToolWindowContainer>
            </SplitContainer>
            <ToolWindowContainer RelativeSize="0.2" SelectedId="details"><ToolWindow Id="details" State="Docked" /></ToolWindowContainer>
          </SplitContainer>
        </DockSiteLayout>
        """;

    private static XElement Host(string xml) => XDocument.Parse(xml).Descendants("DocumentHost").Single();

    private static string[] Ids(XElement element) => [.. element.Descendants("Document").Select(d => (string)d.Attribute("Id")!)];

    // ----- The shapes -------------------------------------------------------------------------

    [TestMethod]
    [DataRow(Arrangement.Tabs, 1)]
    [DataRow(Arrangement.SideBySide, 2)]
    [DataRow(Arrangement.Stacked, 2)]
    [DataRow(Arrangement.ThreeColumns, 3)]
    [DataRow(Arrangement.ThreeRows, 3)]
    [DataRow(Arrangement.Grid, 4)]
    [DataRow(Arrangement.MainAndTwo, 3)]
    public void An_arrangement_has_the_groups_its_name_says_and_every_split_shares_out_the_whole(Arrangement arrangement, int groups)
    {
        ArrangementShape shape = ArrangementShape.Of(arrangement);

        Assert.AreEqual(groups, shape.Leaves);
        Assert.AreEqual(groups, Arrangements.Cells(arrangement));
        AssertWeightsAddUp(shape);
    }

    private static void AssertWeightsAddUp(ArrangementShape shape)
    {
        if (shape.IsLeaf)
        {
            return;
        }

        Assert.AreEqual(1.0, shape.Children.Sum(c => c.Weight), 1e-9);
        foreach (ArrangementShape child in shape.Children)
        {
            AssertWeightsAddUp(child);
        }
    }

    [TestMethod]
    public void The_grid_is_two_rows_of_two_and_the_main_arrangement_is_a_large_group_beside_two_stacked()
    {
        ArrangementShape grid = ArrangementShape.Of(Arrangement.Grid);
        Assert.IsFalse(grid.Horizontal, "the rows are stacked");
        Assert.IsTrue(grid.Children.All(row => row.Horizontal && row.Children.Count == 2), "each row has two side by side");

        ArrangementShape main = ArrangementShape.Of(Arrangement.MainAndTwo);
        Assert.IsTrue(main.Horizontal);
        Assert.IsTrue(main.Children[0].IsLeaf);
        Assert.AreEqual(0.6, main.Children[0].Weight, 1e-9);
        Assert.IsFalse(main.Children[1].Horizontal, "the two small groups are stacked");
        Assert.AreEqual(2, main.Children[1].Leaves);
    }

    [TestMethod]
    public void Every_arrangement_has_a_title_for_the_menu()
    {
        foreach (Arrangement arrangement in Arrangements.All)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(Arrangements.Title(arrangement)), arrangement.ToString());
        }

        Assert.AreEqual(Enum.GetValues<Arrangement>().Length, Arrangements.All.Count);
    }

    // ----- Editing a saved layout's documents ------------------------------------------------------

    [TestMethod]
    public void The_documents_of_a_layout_are_listed_in_tree_order()
    {
        CollectionAssert.AreEqual(new[] { "doc.session.1", "doc.session.2", "doc.session.3" }, LayoutArranger.DocumentIds(Sample).ToArray());
    }

    [TestMethod]
    public void A_remapped_layout_has_the_new_ids_and_keeps_what_the_tool_windows_were()
    {
        string result = LayoutArranger.RemapDocumentIds(Sample, new Dictionary<string, string>
        {
            ["doc.session.1"] = "doc.session.7",
            ["doc.session.2"] = "doc.session.8",
            ["doc.session.3"] = "doc.session.9",
        });

        CollectionAssert.AreEqual(new[] { "doc.session.7", "doc.session.8", "doc.session.9" }, Ids(Host(result)));
        XElement firstGroup = Host(result).Descendants("DocumentContainer").First();
        Assert.AreEqual("doc.session.7", (string?)firstGroup.Attribute("SelectedId"), "the shown tab follows its document");
        Assert.AreEqual("True", (string?)Host(result).Descendants("Document").Last().Attribute("IsPinned"), "a document keeps its attributes");
        XDocument document = XDocument.Parse(result);
        CollectionAssert.AreEqual(new[] { "sessions", "log", "details" }, document.Descendants("ToolWindow").Select(t => (string)t.Attribute("Id")!).ToArray());
        Assert.AreEqual("0.6", (string?)document.Descendants("SplitContainer").Skip(1).First().Attribute("RelativeSize"));
    }

    [TestMethod]
    public void A_document_the_map_does_not_name_is_taken_out_with_the_group_it_leaves_empty()
    {
        string result = LayoutArranger.RemapDocumentIds(Sample, new Dictionary<string, string> { ["doc.session.3"] = "doc.session.5" });

        XElement host = Host(result);
        CollectionAssert.AreEqual(new[] { "doc.session.5" }, Ids(host));
        Assert.AreEqual("DocumentContainer", host.Elements().Single().Name.LocalName, "a split of one group is that group");
        Assert.AreEqual(1, host.Descendants("DocumentContainer").Count());
    }

    [TestMethod]
    public void Retaining_some_documents_drops_the_others_and_the_shown_tab_falls_back_to_the_first_left()
    {
        string result = LayoutArranger.Retain(Sample, ["doc.session.2", "doc.session.3"]);

        XElement host = Host(result);
        CollectionAssert.AreEqual(new[] { "doc.session.2", "doc.session.3" }, Ids(host));
        XElement left = host.Descendants("DocumentContainer").First();
        Assert.AreEqual("doc.session.2", (string?)left.Attribute("SelectedId"), "the tab that was shown is gone");
        Assert.AreEqual(2, host.Descendants("DocumentContainer").Count());
    }

    [TestMethod]
    public void Retaining_no_document_leaves_an_empty_document_area_and_the_panels_alone()
    {
        string result = LayoutArranger.Retain(Sample, []);

        Assert.IsFalse(Host(result).Descendants("Document").Any());
        Assert.AreEqual(0, Host(result).Elements().Count());
        Assert.AreEqual(3, XDocument.Parse(result).Descendants("ToolWindow").Count());
    }

    [TestMethod]
    public void A_split_of_tool_windows_is_never_collapsed_even_with_a_single_pane()
    {
        const string layout = """
            <DockSiteLayout Version="3">
              <SplitContainer Orientation="Horizontal" RelativeSize="1">
                <SplitContainer Orientation="Vertical" RelativeSize="0.3">
                  <ToolWindowContainer RelativeSize="1"><ToolWindow Id="sessions" State="Docked" /></ToolWindowContainer>
                </SplitContainer>
                <DocumentHost RelativeSize="0.7"><DocumentContainer RelativeSize="1" SelectedId="a"><Document Id="a" State="Docked" /></DocumentContainer></DocumentHost>
              </SplitContainer>
            </DockSiteLayout>
            """;

        string result = LayoutArranger.Retain(layout, ["a"]);

        Assert.AreEqual(2, XDocument.Parse(result).Descendants("SplitContainer").Count());
    }

    [TestMethod]
    public void Documents_a_layout_does_not_mention_become_tabs_of_its_last_group()
    {
        string result = LayoutArranger.EnsureDocuments(Sample, ["doc.session.1", "doc.session.4", "doc.session.5"]);

        XElement last = Host(result).Descendants("DocumentContainer").Last();
        CollectionAssert.AreEqual(new[] { "doc.session.3", "doc.session.4", "doc.session.5" }, Ids(last));
        Assert.AreEqual("Docked", (string?)last.Elements("Document").Last().Attribute("State"));
    }

    [TestMethod]
    public void A_layout_that_has_every_document_comes_back_the_same()
    {
        string result = LayoutArranger.EnsureDocuments(Sample, ["doc.session.1", "doc.session.2"]);

        CollectionAssert.AreEqual(LayoutArranger.DocumentIds(Sample).ToArray(), LayoutArranger.DocumentIds(result).ToArray());
        Assert.AreEqual(2, Host(result).Descendants("DocumentContainer").Count());
    }

    [TestMethod]
    public void A_layout_with_no_group_gets_one_for_the_documents_it_misses()
    {
        const string empty = """<DockSiteLayout Version="3"><DocumentHost RelativeSize="1" /></DockSiteLayout>""";

        string result = LayoutArranger.EnsureDocuments(empty, ["x", "y"]);

        CollectionAssert.AreEqual(new[] { "x", "y" }, Ids(Host(result)));
    }

    [TestMethod]
    public void Text_that_is_not_a_docking_layout_is_refused()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => LayoutArranger.DocumentIds("<Something />"));
        Assert.ThrowsExactly<InvalidOperationException>(() => LayoutArranger.EnsureDocuments("""<DockSiteLayout Version="3" />""", ["x"]));
    }

    // ----- The file of named layouts ------------------------------------------------------------

    private static NamedLayout Layout(string name) => new()
    {
        Name = name,
        Layout = Sample,
        Sessions = [new OpenSessionRecord { Id = "doc.session.1", Title = "web", SavedName = "web-prod-01" }],
    };

    private static string Scratch() => Path.Combine(Path.GetTempPath(), "puttymodern-layouts-" + Guid.NewGuid().ToString("N"), "layouts.json");

    [TestMethod]
    public void Named_layouts_come_back_as_they_were_saved()
    {
        string path = Scratch();
        try
        {
            var store = new NamedLayoutStore(path);
            var file = new NamedLayoutsFile();
            file.Upsert(Layout("Wide"));
            file.Upsert(Layout("Ops"));
            Assert.IsTrue(store.Save(file));

            NamedLayoutsFile loaded = new NamedLayoutStore(path).Load();

            CollectionAssert.AreEqual(new[] { "Ops", "Wide" }, loaded.Names.ToArray());
            NamedLayout wide = loaded.Find("wide")!;
            Assert.AreEqual(Sample, wide.Layout);
            Assert.AreEqual("web-prod-01", wide.Sessions.Single().SavedName);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void Saving_under_a_name_that_exists_replaces_it_and_removing_forgets_it()
    {
        var file = new NamedLayoutsFile();
        file.Upsert(Layout("Ops"));
        NamedLayout again = Layout("OPS");
        again.Sessions.Clear();
        file.Upsert(again);

        Assert.AreEqual(1, file.Layouts.Count);
        Assert.AreEqual(0, file.Find("ops")!.Sessions.Count);
        Assert.IsTrue(file.Remove("ops"));
        Assert.IsFalse(file.Remove("ops"));
        Assert.AreEqual(0, file.Names.Count);
    }

    [TestMethod]
    public void A_missing_or_damaged_file_gives_no_layouts_and_a_newer_one_is_not_written_over()
    {
        string path = Scratch();
        try
        {
            var store = new NamedLayoutStore(path);
            Assert.AreEqual(0, store.Load().Layouts.Count);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");
            Assert.AreEqual(0, store.Load().Layouts.Count);
            Assert.IsFalse(store.IsFromNewerVersion);

            const string newer = """{ "version": 99, "layouts": [ { "name": "x", "layout": "<a/>" } ] }""";
            File.WriteAllText(path, newer);
            Assert.AreEqual(0, store.Load().Layouts.Count);
            Assert.IsTrue(store.IsFromNewerVersion);
            Assert.IsFalse(store.Save(new NamedLayoutsFile()), "a file from a newer version is left as it is");
            Assert.AreEqual(newer, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    public void Entries_without_a_name_or_a_layout_are_dropped_when_reading()
    {
        string path = Scratch();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, """{ "version": 1, "layouts": [ { "name": "", "layout": "<a/>" }, { "name": "no layout" }, { "name": "good", "layout": "<a/>" } ] }""");

            NamedLayoutsFile loaded = new NamedLayoutStore(path).Load();

            CollectionAssert.AreEqual(new[] { "good" }, loaded.Names.ToArray());
            Assert.AreEqual(0, loaded.Find("good")!.Sessions.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("Default", false)]
    [DataRow(" default ", false)]
    [DataRow("Ops", true)]
    [DataRow("2 x 2 for the lab", true)]
    public void A_layout_name_needs_something_in_it_and_cannot_be_the_built_in_one(string name, bool fine)
    {
        Assert.AreEqual(fine, LayoutNames.Problem(name) is null, name);
    }

    [TestMethod]
    public void A_layout_name_has_a_limit_and_is_stored_without_the_spaces_around_it()
    {
        Assert.IsNotNull(LayoutNames.Problem(new string('x', LayoutNames.MaxLength + 1)));
        Assert.IsNull(LayoutNames.Problem(new string('x', LayoutNames.MaxLength)));
        Assert.AreEqual("Ops", LayoutNames.Normalise("  Ops "));
    }

    [TestMethod]
    public void The_workspace_remembers_which_layout_was_last_used()
    {
        string path = Scratch();
        try
        {
            var store = new WorkspaceStateStore(path);
            store.Save(new WorkspaceState { Layout = Sample, LayoutName = "Ops" });

            Assert.AreEqual("Ops", store.Load()!.LayoutName);

            store.Save(new WorkspaceState { Layout = Sample });
            Assert.IsNull(store.Load()!.LayoutName);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
