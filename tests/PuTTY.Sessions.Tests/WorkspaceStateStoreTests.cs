using PuTTY.Sessions;

namespace PuTTY.Sessions.Tests;

[TestClass]
public sealed class WorkspaceStateStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "PuttyModernSessionsTests", Guid.NewGuid().ToString("N"), "layout.json");

    [TestMethod]
    public void A_state_comes_back_as_it_was_saved()
    {
        var store = new WorkspaceStateStore(TempFile());
        var state = new WorkspaceState
        {
            Layout = "<DockLayout version=\"1\"><Split/></DockLayout>",
            Sessions =
            [
                new OpenSessionRecord { Id = "doc.session.1", Title = "web-prod-01", SavedName = "web-prod-01", Description = "10.20.0.12 . SSH" },
                new OpenSessionRecord { Id = "doc.session.2", Title = "10.0.0.5", Host = "10.0.0.5", Port = 2222, Protocol = "ssh", UserName = "admin", KeyFile = @"C:\keys\id.ppk" },
            ],
        };

        store.Save(state);
        WorkspaceState? loaded = store.Load();

        Assert.IsNotNull(loaded);
        Assert.AreEqual(state.Layout, loaded.Layout);
        Assert.AreEqual(2, loaded.Sessions.Count);
        Assert.AreEqual("web-prod-01", loaded.Sessions[0].SavedName);
        Assert.IsNull(loaded.Sessions[0].Host);
        Assert.AreEqual(2222, loaded.Sessions[1].Port);
        Assert.AreEqual("admin", loaded.Sessions[1].UserName);
        Assert.AreEqual(@"C:\keys\id.ppk", loaded.Sessions[1].KeyFile);
    }

    [TestMethod]
    public void A_missing_file_gives_nothing_not_an_error()
    {
        Assert.IsNull(new WorkspaceStateStore(TempFile()).Load());
    }

    [TestMethod]
    public void A_damaged_file_gives_nothing_and_a_later_save_replaces_it()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");
        var store = new WorkspaceStateStore(path);

        Assert.IsNull(store.Load());
        store.Save(new WorkspaceState { Layout = "ok" });

        Assert.AreEqual("ok", store.Load()?.Layout);
    }

    [TestMethod]
    public void A_file_from_a_newer_version_is_not_used()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"version\": 99, \"layout\": \"from the future\", \"sessions\": [] }");

        Assert.IsNull(new WorkspaceStateStore(path).Load());
    }

    [TestMethod]
    public void Unknown_properties_are_ignored()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"version\": 1, \"layout\": \"x\", \"sessions\": [ { \"id\": \"d\", \"title\": \"t\", \"somethingNew\": 5 } ], \"alsoNew\": true }");

        WorkspaceState? loaded = new WorkspaceStateStore(path).Load();

        Assert.IsNotNull(loaded);
        Assert.AreEqual("d", loaded.Sessions.Single().Id);
    }

    [TestMethod]
    public void Saving_leaves_no_temporary_file_behind()
    {
        string path = TempFile();
        var store = new WorkspaceStateStore(path);

        store.Save(new WorkspaceState());
        store.Save(new WorkspaceState { Layout = "second" });

        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length, "only layout.json");
        Assert.AreEqual("second", store.Load()?.Layout);
    }
}
