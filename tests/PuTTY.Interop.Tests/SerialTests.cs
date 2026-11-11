using System.Runtime.InteropServices;
using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Serial connections (PuTTY's serial backend). The failure path (a line that does not exist must fail with PuTTY's own
/// message, not hang or crash) needs nothing. The data path needs a serial port pair: a com0com pair named CNCA0 and CNCB0
/// (a driver, installed on the host by hand, not in the Sandbox), whose two ends are wired to each other. The session opens
/// CNCA0 as PuTTY would, the test plays the device on CNCB0; without the pair the test is inconclusive, not failed.
/// </summary>
[TestClass]
public sealed partial class SerialTests
{
    private const string SessionEnd = "CNCA0";
    private const string DeviceEnd = "\\\\.\\CNCB0";
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(nint file, byte* buffer, int count, out int read, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteFile(nint file, byte* buffer, int count, out int written, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCommTimeouts(nint file, ref CommTimeouts timeouts);

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout, ReadTotalTimeoutMultiplier, ReadTotalTimeoutConstant, WriteTotalTimeoutMultiplier, WriteTotalTimeoutConstant;
    }

    /// <summary>The device end of the pair, with reads that return at once when nothing is there; null when there is no pair.</summary>
    private static nint OpenDevice()
    {
        nint handle = CreateFile(DeviceEnd, 0xC0000000 /* read and write */, 0, 0, 3 /* OPEN_EXISTING */, 0, 0);
        if (handle == -1)
        {
            return 0;
        }

        var timeouts = new CommTimeouts { ReadIntervalTimeout = uint.MaxValue };
        SetCommTimeouts(handle, ref timeouts);
        return handle;
    }

    private static unsafe string ReadFromDevice(nint device)
    {
        var text = new StringBuilder();
        byte[] buffer = new byte[256];
        fixed (byte* p = buffer)
        {
            while (ReadFile(device, p, buffer.Length, out int read, 0) && read > 0)
            {
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }

        return text.ToString();
    }

    private static unsafe void WriteToDevice(nint device, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        fixed (byte* p = bytes)
        {
            Assert.IsTrue(WriteFile(device, p, bytes.Length, out int written, 0) && written == bytes.Length);
        }
    }

    [TestMethod]
    public void Data_flows_both_ways_over_a_serial_line()
    {
        nint device = OpenDevice();
        if (device == 0)
        {
            Assert.Inconclusive("No com0com pair named CNCA0 and CNCB0 on this machine (the data path of the serial backend needs one).");
        }

        try
        {
            using var registry = new RegistrySandbox();
            // No flow control: with RTS/CTS PuTTY waits for a CTS that a bare pair may not raise.
            registry.WriteSession("device", string.Empty, 0, "serial", null,
                ("SerialLine", SessionEnd), ("SerialSpeed", 9600), ("SerialDataBits", 8), ("SerialStopHalfbits", 2), ("SerialParity", 0), ("SerialFlowControl", 0));
            using var engine = new EngineThread();
            var output = new StringBuilder();
            bool closed = false;
            PuttySession session = engine.Run(() =>
            {
                PuttySession s = PuttySession.OpenSaved("device");
                s.Output += (_, e) => { lock (output) { output.Append(Encoding.ASCII.GetString(e.Data)); } };
                s.Closed += (_, _) => closed = true;
                s.Start();
                return s;
            });

            WriteToDevice(device, "hello from the device");
            Assert.IsTrue(EngineThread.WaitUntil(() => { lock (output) { return output.ToString().Contains("hello from the device", StringComparison.Ordinal); } }, Generous),
                "what the device sent never reached the session");

            engine.Run(() => session.Send("to the device"u8));
            string received = string.Empty;
            Assert.IsTrue(EngineThread.WaitUntil(() => (received += ReadFromDevice(device)).Contains("to the device", StringComparison.Ordinal), Generous),
                "what the session sent never reached the device; got [" + received + "]");

            Assert.IsFalse(Volatile.Read(ref closed));
            engine.Run(session.Dispose);
        }
        finally
        {
            CloseHandle(device);
        }
    }

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
