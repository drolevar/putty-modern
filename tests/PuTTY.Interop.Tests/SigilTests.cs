using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// The trust sigil: the two cells the emulator marks at the start of each line that PuTTY itself wrote (the login's status
/// lines: "Using username ...") so that a server cannot fake them. It is a shield with a tick, drawn as vector shapes,
/// not a solid block.
/// </summary>
[TestClass]
public sealed class SigilTests
{
    private const uint ShieldGreen = 0x2EA043;
    private const uint TickWhite = 0xFFFFFF;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(15);

    [TestMethod]
    public void A_line_PuTTY_wrote_during_login_starts_with_a_shield_and_not_a_solid_block()
    {
        using var registry = new RegistrySandbox();
        using var server = SshServerFixture.Start();
        using var engine = new EngineThread();

        PuttySession session = engine.Run(() =>
        {
            var s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Ssh,
                new PuttySessionOptions { UserName = SshServerFixture.User, TryAgent = false });
            s.ConfirmationRequested += (_, e) => e.Accept();
            s.PromptRequested += (_, e) => e.Respond(SshServerFixture.Password);
            s.Start();
            s.AttachOffscreenView(800, 400, "Consolas", 20);
            return s;
        });

        try
        {
            string screen = string.Empty;
            Assert.IsTrue(EngineThread.WaitUntil(() =>
            {
                screen = engine.Run(() => session.GetScreenText());
                return screen.Contains("Welcome to the PuTTY modern test server.");
            }, Generous), "the login never finished; the screen shows: " + screen);

            string[] rows = screen.Replace("\r", string.Empty).Split('\n');
            int row = Array.FindIndex(rows, r => r.Contains("Using username"));
            Assert.IsGreaterThanOrEqualTo(0, row, "the login's status line is not on the screen: " + screen);
            TerminalGeometry g = engine.Run(() => session.Geometry);

            int green = 0, white = 0, inked = 0;
            int width = 2 * g.CellWidth;
            engine.Run(() =>
            {
                for (int y = row * g.CellHeight; y < (row + 1) * g.CellHeight; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        uint pixel = session.ReadPixel(x, y);
                        green += pixel == ShieldGreen ? 1 : 0;
                        white += pixel == TickWhite ? 1 : 0;
                        inked += pixel != 0 ? 1 : 0;
                    }
                }

                return 0;
            });

            int area = width * g.CellHeight;
            Assert.IsGreaterThanOrEqualTo(area / 5, green, $"no shield in the first two cells of row {row} ({green} green pixels of {area})");
            Assert.IsGreaterThanOrEqualTo(3, white, "no tick in the shield");
            Assert.IsLessThan(area * 8 / 10, inked, "the two cells are one solid block of colour, not a shield");

            // The server's own text (the welcome line) is not marked.
            int welcome = Array.FindIndex(rows, r => r.Contains("Welcome to the PuTTY modern test server."));
            int markedInWelcome = engine.Run(() =>
            {
                int count = 0;
                for (int y = welcome * g.CellHeight; y < (welcome + 1) * g.CellHeight; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        count += session.ReadPixel(x, y) == ShieldGreen ? 1 : 0;
                    }
                }

                return count;
            });
            Assert.AreEqual(0, markedInWelcome, "a line the server wrote must not carry the sigil");
        }
        finally
        {
            engine.Run(session.Dispose);
        }
    }
}
