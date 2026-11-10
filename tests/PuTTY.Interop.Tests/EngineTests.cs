using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>Loads the real native engine and checks the managed/native contract.</summary>
[TestClass]
public sealed class EngineTests
{
    [TestMethod]
    public void NativeAbiVersion_matches_what_the_managed_side_expects()
    {
        Assert.AreEqual(PuttyEngine.ExpectedAbiVersion, PuttyEngine.NativeAbiVersion);
    }

    [TestMethod]
    public void EnsureCompatible_accepts_the_built_engine()
    {
        PuttyEngine.EnsureCompatible();
    }

    [TestMethod]
    public void Probe_runs_PuTTY_Conf_inside_the_engine()
    {
        Assert.IsTrue(PuttyEngine.Probe());
    }
}
