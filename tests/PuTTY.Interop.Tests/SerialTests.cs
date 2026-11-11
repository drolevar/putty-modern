using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Serial connections (PuTTY's serial backend), as far as they can be checked without a serial port: this machine and the
/// Sandbox have none and a virtual pair needs a driver. What is checked is the failure path (a line that does not exist
/// must fail with PuTTY's own message, not hang or crash); a real device is for a machine that has one.
/// </summary>
[TestClass]
public sealed class SerialTests
{
    [TestMethod]
    public void A_saved_serial_session_on_a_line_that_does_not_exist_fails_to_start_with_PuTTYs_message()
    {
        using var registry = new RegistrySandbox();
        registry.WriteSession("no such line", string.Empty, 0, "serial", null,
            ("SerialLine", "COM98"), ("SerialSpeed", 9600), ("SerialDataBits", 8), ("SerialStopHalfbits", 2), ("SerialParity", 0), ("SerialFlowControl", 1));
        using var engine = new EngineThread();

        // The serial backend opens its device while it starts, so unlike a network connection the failure is not an event
        // later but the exception of Start(), which the app shows in a message.
        var failure = Assert.ThrowsExactly<InvalidOperationException>(() =>
            engine.Run(() => PuttySession.OpenSaved("no such line").Start()));

        StringAssert.Contains(failure.Message, "COM98");
        StringAssert.Contains(failure.Message, "cannot find the file");
    }

    [TestMethod]
    public void A_saved_serial_session_is_described_by_its_line_and_speed()
    {
        using var registry = new RegistrySandbox();
        registry.WriteSession("on com3", string.Empty, 0, "serial", null, ("SerialLine", "COM3"), ("SerialSpeed", 115200));
        using var engine = new EngineThread();

        SavedSessionSummary? summary = engine.Run(() => PuttySessions.Describe("on com3"));

        Assert.IsNotNull(summary);
        Assert.AreEqual(SavedSessionProtocol.Serial, summary.Protocol);
        StringAssert.Contains(summary.Host, "COM3");
    }
}
