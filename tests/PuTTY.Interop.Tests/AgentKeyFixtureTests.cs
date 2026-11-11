using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Writes the key that the agent check of the real app (<c>tools/app-agent-smoke.ps1</c>, which runs in Windows
/// Sandbox) loads into Pageant and lets the OpenSSH server accept: <c>artifacts/agent-keys/agent.ppk</c> (a PuTTY key
/// with no passphrase) and <c>agent.pub</c> (its public half as one line for <c>authorized_keys</c>). There is no other
/// way to make a .ppk here: the Windows puttygen.exe has no command line. <c>tools/run-in-sandbox.ps1 -Smoke agent</c>
/// runs this test first when the files are missing.
/// </summary>
[TestClass]
public sealed class AgentKeyFixtureTests
{
    [TestMethod]
    public void Writes_the_key_the_agent_smoke_check_uses()
    {
        string directory = Path.Combine(AgentKeyRoot(), "artifacts", "agent-keys");
        Directory.CreateDirectory(directory);
        string key = Path.Combine(directory, "agent.ppk");
        string publicFile = Path.Combine(directory, "agent.pub");
        File.Delete(key);

        using var registry = new RegistrySandbox();       // PuTTY's random seed file goes to a scratch folder, not the user's
        using var engine = new EngineThread();
        string line = engine.Run(() => PuttyEngine.Diagnostics.GenerateEd25519Key(key, null));

        File.WriteAllText(publicFile, line + " puttymodern-agent-test\n");
        Assert.IsTrue(File.Exists(key));
        StringAssert.StartsWith(line, "ssh-ed25519 ");
    }

    private static string AgentKeyRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PuTTY.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("PuTTY.slnx was not found above " + AppContext.BaseDirectory);
    }
}
