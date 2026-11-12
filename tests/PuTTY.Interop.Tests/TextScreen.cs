using System.Text;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// A raw session on a loopback server with an offscreen terminal: send it text, wait for it to be drawn, read pixels.
/// With a registry sandbox the session is a saved one named "rig" with the given settings (PuTTY's own value names).
/// </summary>
internal sealed class TextScreen : IDisposable
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);
    private readonly LoopbackServer server = new();
    private readonly EngineThread engine = new();
    private readonly PuttySession? session;
    public readonly int CellWidth;
    public readonly int CellHeight;

    /// <param name="waitFor">Text that appears on the screen once everything sent has been drawn (escape sequences are not text).</param>
    public TextScreen(string font, float size, string text, string waitFor, RegistrySandbox? registry = null,
        params (string Name, object Value)[] settings)
    {
        try
        {
        session = engine.Run(() =>
        {
            PuttySession s;
            if (registry is null)
            {
                s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
            }
            else
            {
                registry.WriteSession("rig", "127.0.0.1", server.Port, "raw", null, settings);
                s = PuttySession.OpenSaved("rig");
            }

            s.Start();
            return s;
        });
        Assert.IsTrue(EngineThread.WaitUntil(() => server.ClientCount == 1, Generous), "never connected");
        engine.Run(() =>
        {
            session!.AttachOffscreenView(320, 240, font, size);
            session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
        });
        TerminalGeometry g = engine.Run(() => session!.Geometry);
        CellWidth = g.CellWidth;
        CellHeight = g.CellHeight;
        server.Send(Encoding.UTF8.GetBytes(text));
        Assert.IsTrue(EngineThread.WaitUntil(() => engine.Run(() => session!.GetScreenText()).Contains(waitFor), Generous),
            "the text never reached the screen: " + engine.Run(() => session!.GetScreenText()));
        Thread.Sleep(200);                                      // let the last repaint land
        }
        catch
        {
            Dispose();                                          // a failed setup must not leave the engine running for the next test
            throw;
        }
    }


    /// <summary>The pixels of the top left corner, row by row (index y * width + x), read in one call.</summary>
    public uint[] Snapshot(int width, int height) => engine.Run(() =>
    {
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = session!.ReadPixel(x, y);
            }
        }

        return pixels;
    });

    public uint Pixel(int x, int y) => engine.Run(() => session!.ReadPixel(x, y));

    public void Dispose()
    {
        if (session is not null)
        {
            engine.Run(session.Dispose);
        }

        engine.Dispose();
        server.Dispose();
    }
}
