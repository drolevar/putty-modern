using PuTTY.Sessions;

namespace PuTTY.Sessions.Tests;

[TestClass]
public sealed class MetadataTests
{
    private static string[] Names(IEnumerable<string> names) => [.. names];

    // ----- The model --------------------------------------------------------------------------

    [TestMethod]
    public void A_session_nobody_filed_is_at_the_top_and_a_vanished_one_does_not_show()
    {
        var metadata = new SessionMetadata();
        FolderEntry prod = metadata.CreateFolder("Production");
        metadata.MoveSession("web", prod.Id);
        metadata.MoveSession("gone", prod.Id);          // no longer in PuTTY's storage

        SessionTreeNode tree = metadata.BuildTree(["web", "new one", "db"]);

        CollectionAssert.AreEqual(new[] { "db", "new one" }, Names(tree.Sessions));
        SessionTreeNode folder = tree.Folders.Single();
        Assert.AreEqual("Production", folder.Name);
        CollectionAssert.AreEqual(new[] { "web" }, Names(folder.Sessions));
        Assert.IsNotNull(metadata.FindSession("gone"), "a vanished session keeps its entry in case it comes back");
    }

    [TestMethod]
    public void A_session_in_a_folder_that_does_not_exist_falls_back_to_the_top()
    {
        var metadata = new SessionMetadata();
        metadata.Sessions["web"] = new SessionEntry { FolderId = "deleted-by-hand" };

        SessionTreeNode tree = metadata.BuildTree(["web"]);

        CollectionAssert.AreEqual(new[] { "web" }, Names(tree.Sessions));
    }

    [TestMethod]
    public void Folders_nest_and_sort_by_name_ignoring_case()
    {
        var metadata = new SessionMetadata();
        FolderEntry zeta = metadata.CreateFolder("zeta");
        FolderEntry alpha = metadata.CreateFolder("Alpha");
        FolderEntry inner = metadata.CreateFolder("inner", alpha.Id);
        metadata.MoveSession("b", inner.Id);
        metadata.MoveSession("A", inner.Id);

        SessionTreeNode tree = metadata.BuildTree(["b", "A"]);

        CollectionAssert.AreEqual(new[] { "Alpha", "zeta" }, tree.Folders.Select(f => f.Name).ToArray());
        SessionTreeNode innerNode = tree.Folders[0].Folders.Single();
        CollectionAssert.AreEqual(new[] { "A", "b" }, Names(innerNode.Sessions));
        Assert.IsNotNull(zeta);
    }

    [TestMethod]
    public void Sibling_folders_cannot_share_a_name_but_different_parents_can()
    {
        var metadata = new SessionMetadata();
        FolderEntry a = metadata.CreateFolder("A");
        metadata.CreateFolder("Servers", a.Id);

        Assert.ThrowsExactly<InvalidOperationException>(() => metadata.CreateFolder("SERVERS", a.Id));
        metadata.CreateFolder("Servers");                // under the top, not under A: fine
        Assert.ThrowsExactly<ArgumentException>(() => metadata.CreateFolder("   "));
        Assert.ThrowsExactly<ArgumentException>(() => metadata.CreateFolder("x", "no such parent"));
    }

    [TestMethod]
    public void A_folder_cannot_be_moved_into_itself_or_its_own_descendant()
    {
        var metadata = new SessionMetadata();
        FolderEntry outer = metadata.CreateFolder("outer");
        FolderEntry middle = metadata.CreateFolder("middle", outer.Id);
        FolderEntry deep = metadata.CreateFolder("deep", middle.Id);

        Assert.ThrowsExactly<InvalidOperationException>(() => metadata.MoveFolder(outer.Id, outer.Id));
        Assert.ThrowsExactly<InvalidOperationException>(() => metadata.MoveFolder(outer.Id, deep.Id));
        metadata.MoveFolder(deep.Id, null);              // out to the top is fine
        Assert.IsNull(metadata.FindFolder(deep.Id)!.ParentId);
    }

    [TestMethod]
    public void Deleting_a_folder_moves_what_is_in_it_up_and_loses_nothing()
    {
        var metadata = new SessionMetadata();
        FolderEntry outer = metadata.CreateFolder("outer");
        FolderEntry inner = metadata.CreateFolder("inner", outer.Id);
        metadata.MoveSession("in inner", inner.Id);
        metadata.MoveSession("in outer", outer.Id);

        metadata.DeleteFolder(inner.Id);
        SessionTreeNode afterInner = metadata.BuildTree(["in inner", "in outer"]);
        CollectionAssert.AreEquivalent(new[] { "in inner", "in outer" }, Names(afterInner.Folders.Single().Sessions));

        metadata.DeleteFolder(outer.Id);
        SessionTreeNode afterOuter = metadata.BuildTree(["in inner", "in outer"]);
        Assert.AreEqual(0, afterOuter.Folders.Count);
        CollectionAssert.AreEquivalent(new[] { "in inner", "in outer" }, Names(afterOuter.Sessions));
    }

    [TestMethod]
    public void Folders_that_form_a_loop_in_a_damaged_file_end_up_at_the_top_instead_of_hanging()
    {
        var metadata = new SessionMetadata
        {
            Folders =
            [
                new FolderEntry { Id = "a", Name = "a", ParentId = "b" },
                new FolderEntry { Id = "b", Name = "b", ParentId = "a" },
            ],
        };

        SessionTreeNode tree = metadata.BuildTree([]);

        CollectionAssert.AreEquivalent(new[] { "a", "b" }, tree.Folders.Select(f => f.Name).ToArray());
    }

    [TestMethod]
    public void Recents_are_the_newest_opened_sessions_that_still_exist()
    {
        var metadata = new SessionMetadata();
        var start = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        metadata.RecordOpened("old", start);
        metadata.RecordOpened("newest", start.AddHours(2));
        metadata.RecordOpened("middle", start.AddHours(1));
        metadata.RecordOpened("deleted in PuTTY", start.AddHours(3));
        metadata.RecordOpened("old", start.AddMinutes(30));      // opened again: still the oldest here, but counted twice
        metadata.MoveSession("never opened", null);

        string[] recents = [.. metadata.Recents(["old", "newest", "middle", "never opened"], max: 3)];

        CollectionAssert.AreEqual(new[] { "newest", "middle", "old" }, recents);
        Assert.AreEqual(2, metadata.FindSession("old")!.OpenCount);
        CollectionAssert.AreEqual(new[] { "newest" }, metadata.Recents(["newest", "middle"], max: 1).ToArray());
    }

    [TestMethod]
    public void Prune_forgets_only_what_PuTTY_no_longer_has()
    {
        var metadata = new SessionMetadata();
        metadata.RecordOpened("kept", DateTimeOffset.UnixEpoch);
        metadata.RecordOpened("gone", DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, metadata.Prune(["kept"]));

        CollectionAssert.AreEqual(new[] { "kept" }, metadata.Sessions.Keys.ToArray());
    }

    // ----- The file ---------------------------------------------------------------------------

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "puttymodern-metadata-tests-" + Guid.NewGuid().ToString("N"), "workspace.json");

    private static void Clean(string file)
    {
        string? directory = Path.GetDirectoryName(file);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Saving_and_loading_gives_back_the_same_folders_sessions_and_history()
    {
        string file = TempFile();
        try
        {
            var store = new MetadataStore(file);
            var metadata = new SessionMetadata();
            FolderEntry prod = metadata.CreateFolder("Production \"main\" ünï");
            metadata.MoveSession("web server", prod.Id);
            var opened = new DateTimeOffset(2026, 11, 11, 12, 30, 0, TimeSpan.FromHours(1));
            metadata.RecordOpened("web server", opened);

            store.Save(metadata);
            SessionMetadata loaded = new MetadataStore(file).Load();

            Assert.AreEqual("Production \"main\" ünï", loaded.Folders.Single().Name);
            Assert.AreEqual(prod.Id, loaded.FindSession("web server")!.FolderId);
            Assert.AreEqual(opened, loaded.FindSession("web server")!.LastOpened);
            Assert.AreEqual(1, loaded.FindSession("web server")!.OpenCount);
            Assert.IsNull(store.LoadProblem);
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(file)!, "*.tmp").Length, "no temporary file may be left behind");
        }
        finally
        {
            Clean(file);
        }
    }

    [TestMethod]
    public void A_missing_file_is_an_empty_set_without_complaint()
    {
        string file = TempFile();
        var store = new MetadataStore(file);

        SessionMetadata loaded = store.Load();

        Assert.AreEqual(0, loaded.Folders.Count);
        Assert.AreEqual(0, loaded.Sessions.Count);
        Assert.IsNull(store.LoadProblem);
        Assert.IsFalse(File.Exists(file), "loading must not create the file");
    }

    [TestMethod]
    public void A_damaged_file_gives_an_empty_set_and_is_kept_aside_not_destroyed()
    {
        string file = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ this is not json");
            var store = new MetadataStore(file);

            SessionMetadata loaded = store.Load();

            Assert.AreEqual(0, loaded.Folders.Count);
            Assert.IsNotNull(store.LoadProblem);
            string[] kept = Directory.GetFiles(Path.GetDirectoryName(file)!, "workspace.json.damaged-*");
            Assert.AreEqual(1, kept.Length, "the damaged file is moved aside");
            Assert.AreEqual("{ this is not json", File.ReadAllText(kept[0]));
            Assert.IsFalse(File.Exists(file));
        }
        finally
        {
            Clean(file);
        }
    }

    [TestMethod]
    public void A_file_from_a_newer_version_is_read_but_never_written_over()
    {
        string file = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            const string newer = "{\"version\": 99, \"folders\": [{\"id\":\"f\",\"name\":\"Kept\",\"extra\":1}], \"sessions\": {}, \"somethingNew\": true}";
            File.WriteAllText(file, newer);
            var store = new MetadataStore(file);

            SessionMetadata loaded = store.Load();

            Assert.AreEqual("Kept", loaded.Folders.Single().Name, "what is understood is shown");
            Assert.IsTrue(loaded.IsReadOnly);
            Assert.IsNotNull(store.LoadProblem);
            Assert.ThrowsExactly<InvalidOperationException>(() => store.Save(loaded));
            Assert.AreEqual(newer, File.ReadAllText(file), "the newer file is untouched");
        }
        finally
        {
            Clean(file);
        }
    }

    [TestMethod]
    public void Properties_this_version_does_not_know_are_ignored()
    {
        string file = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{\"version\":1,\"folders\":[],\"sessions\":{\"a\":{\"folderId\":null,\"colour\":\"red\"}},\"theme\":\"x\"}");

            SessionMetadata loaded = new MetadataStore(file).Load();

            Assert.IsNotNull(loaded.FindSession("a"));
            Assert.IsFalse(loaded.IsReadOnly);
        }
        finally
        {
            Clean(file);
        }
    }

    [TestMethod]
    public void Saving_over_an_existing_file_replaces_it_whole()
    {
        string file = TempFile();
        try
        {
            var store = new MetadataStore(file);
            var first = new SessionMetadata();
            first.CreateFolder("one");
            first.CreateFolder("two");
            store.Save(first);

            var second = new SessionMetadata();
            second.CreateFolder("only");
            store.Save(second);

            Assert.AreEqual("only", store.Load().Folders.Single().Name);
        }
        finally
        {
            Clean(file);
        }
    }

    [TestMethod]
    public void The_default_file_is_in_the_app_data_folder_and_not_in_anything_PuTTY_owns()
    {
        string path = MetadataStore.DefaultPath;

        StringAssert.StartsWith(path, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Assert.IsFalse(path.Contains("SimonTatham", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(path.Contains("\\PuTTY\\", StringComparison.Ordinal), "PuTTY classic's own folder is %APPDATA%\\PuTTY: stay out of it");
    }
}
