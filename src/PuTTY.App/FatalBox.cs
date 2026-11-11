using System.Runtime.InteropServices;

namespace PuTTY.App;

/// <summary>
/// The message box shown when the engine is about to end the process (App.OnEngineFatal). A native box, not a XAML dialog:
/// the process is going down inside native code on the UI thread, and a box of the system needs nothing of XAML.
/// </summary>
internal static partial class FatalBox
{
    private const uint MbOk = 0x0, MbIconError = 0x10, MbTaskModal = 0x2000, MbTopmost = 0x40000;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint window, string text, string caption, uint type);

    internal static void Show(string reason)
    {
        try
        {
            MessageBox(0,
                "PuTTY Modern has to close because of an internal error:\n\n" + reason +
                "\n\nYour open sessions were noted and come back when you start it again. The details are in " + CrashLog.FilePath + ".",
                "PuTTY Modern",
                MbOk | MbIconError | MbTaskModal | MbTopmost);
        }
        catch (Exception)
        {
            // the process is ending anyway
        }
    }
}
