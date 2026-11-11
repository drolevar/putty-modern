using System.Runtime.InteropServices;
using PuTTY.Interop;

namespace PuTTY.Interop.Tests;

/// <summary>
/// Keyboard input: PuTTY's key translation fed from a message hook on the engine thread. The
/// tests post the same messages Windows would (key-down, key-up, character) to the engine's
/// thread, set the thread's keyboard state to describe held modifiers, and check the bytes that
/// reach a real TCP server through the terminal and line discipline. Nothing is injected into the
/// desktop, so this is safe to run anywhere.
/// </summary>
[TestClass]
public sealed partial class KeyboardTests
{
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CHAR = 0x0102, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_NUMLOCK = 0x90, VK_LMENU = 0xA4, VK_RMENU = 0xA5, VK_LCONTROL = 0xA2;
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(5);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetKeyboardState(byte* state);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadKeyboardLayoutW(string klid, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint ActivateKeyboardLayout(nint hkl, uint flags);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint hkl);

    /// <summary>An engine, a server and a raw session with a terminal that has keyboard focus.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly EngineThread Engine = new();
        public readonly LoopbackServer Server = new();
        public readonly PuttySession Session;
        public nint Layout;

        public Rig(string layoutId = "00000409")
        {
            Session = Engine.Run(() =>
            {
                var session = new PuttySession("127.0.0.1", Server.Port, SessionProtocol.Raw);
                session.Start();
                return session;
            });
            Assert.IsTrue(EngineThread.WaitUntil(() => Server.ClientCount == 1, Generous), "never connected");

            Layout = Engine.Run(() =>
            {
                nint hkl = LoadKeyboardLayoutW(layoutId, 0x80 /* KLF_NOTELLSHELL */);
                if (hkl != 0)
                {
                    ActivateKeyboardLayout(hkl, 0);
                }

                Session.EnableTerminal(80, 24);
                // Raw sessions edit the line locally by default and hold typing until Enter; these tests
                // look at what each key sends, so turn that off.
                Session.SetLocalEchoAndEditing(LocalMode.Off, LocalMode.Off);
                PuttySession.SetKeyboardTarget(Session);
                return hkl;
            });
        }

        public byte[] Received => Server.Received;

        /// <summary>Describes the modifiers held, in the thread's keyboard state, on the engine thread.</summary>
        public void Hold(bool shift = false, bool ctrl = false, bool alt = false, bool altGr = false, bool numLock = false) =>
            Engine.Run(() =>
            {
                unsafe
                {
                    byte* state = stackalloc byte[256];
                    new Span<byte>(state, 256).Clear();
                    if (shift) { state[VK_SHIFT] = 0x80; }
                    if (ctrl || altGr) { state[VK_CONTROL] = 0x80; state[VK_LCONTROL] = 0x80; }
                    if (alt) { state[VK_MENU] = 0x80; state[VK_LMENU] = 0x80; }
                    if (altGr) { state[VK_MENU] = 0x80; state[VK_RMENU] = 0x80; }
                    if (numLock) { state[VK_NUMLOCK] = 0x01; }
                    Assert.IsTrue(SetKeyboardState(state));
                }
            });

        /// <summary>Posts a key press (down, then up) to the engine thread as Windows would.</summary>
        public void Press(byte vk, uint scan, bool extended = false, bool altContext = false) =>
            Engine.Run(() =>
            {
                uint thread = GetCurrentThreadId();
                nint down = (nint)(1u | (scan << 16) | (extended ? 1u << 24 : 0u) | (altContext ? 1u << 29 : 0u));
                nint up = (nint)((uint)down | (1u << 30) | (1u << 31));
                Assert.IsTrue(PostThreadMessageW(thread, altContext ? WM_SYSKEYDOWN : WM_KEYDOWN, vk, down));
                Assert.IsTrue(PostThreadMessageW(thread, altContext ? WM_SYSKEYUP : WM_KEYUP, vk, up));
            });

        public void PostChar(char c) =>
            Engine.Run(() => Assert.IsTrue(PostThreadMessageW(GetCurrentThreadId(), WM_CHAR, c, 1)));

        public bool WaitForBytes(int count) => EngineThread.WaitUntil(() => Received.Length >= count, Generous);

        public void Dispose()
        {
            Engine.Run(Session.Dispose);
            Engine.Dispose();
            Server.Dispose();
        }
    }

    private static string Text(byte[] bytes) => System.Text.Encoding.Latin1.GetString(bytes);

    [TestMethod]
    public void Letters_reach_the_server_as_typed_and_shift_makes_capitals()
    {
        using var rig = new Rig();

        rig.Hold();
        rig.Press(0x41, 0x1E);                  // A
        Assert.IsTrue(rig.WaitForBytes(1));
        rig.Hold(shift: true);
        rig.Press(0x41, 0x1E);                  // Shift+A
        Assert.IsTrue(rig.WaitForBytes(2));

        Assert.AreEqual("aA", Text(rig.Received));
    }

    [TestMethod]
    public void Cursor_and_function_keys_use_PuTTYs_default_sequences()
    {
        using var rig = new Rig();
        rig.Hold();

        rig.Press(0x26, 0x48, extended: true);  // Up
        rig.Press(0x70, 0x3B);                  // F1
        rig.Press(0x24, 0x47, extended: true);  // Home
        rig.Press(0x2E, 0x53, extended: true);  // Delete
        rig.Press(0x21, 0x49, extended: true);  // Page Up

        string expected = "\x1b[A" + "\x1b[11~" + "\x1b[1~" + "\x1b[3~" + "\x1b[5~";
        Assert.IsTrue(rig.WaitForBytes(expected.Length), $"got {Convert.ToHexString(rig.Received)}");
        Assert.AreEqual(expected, Text(rig.Received));
    }

    [TestMethod]
    public void Enter_sends_a_carriage_return_and_Backspace_sends_delete()
    {
        using var rig = new Rig();
        rig.Hold();

        rig.Press(0x0D, 0x1C);                  // Enter
        rig.Press(0x08, 0x0E);                  // Backspace

        Assert.IsTrue(rig.WaitForBytes(2), $"got {Convert.ToHexString(rig.Received)}");
        Assert.AreEqual((byte)0x0D, rig.Received[0]);
        Assert.AreEqual((byte)0x7F, rig.Received[^1]);   // PuTTY's default: Backspace is Delete
    }

    [TestMethod]
    public void Control_and_Alt_combinations_become_control_codes_and_escape_prefixes()
    {
        using var rig = new Rig();

        rig.Hold(ctrl: true);
        rig.Press(0x41, 0x1E);                  // Ctrl+A
        Assert.IsTrue(rig.WaitForBytes(1));
        rig.Hold(alt: true);
        rig.Press(0x41, 0x1E, altContext: true);   // Alt+A
        Assert.IsTrue(rig.WaitForBytes(3), $"got {Convert.ToHexString(rig.Received)}");

        Assert.AreEqual("\x01\x1b" + "a", Text(rig.Received));
    }

    [TestMethod]
    public void The_keypad_types_digits_when_NumLock_is_on()
    {
        using var rig = new Rig();

        rig.Hold(numLock: true);
        rig.Press(0x61, 0x4F);                  // keypad 1 (VK_NUMPAD1)
        rig.Press(0x6B, 0x4E);                  // keypad +

        Assert.IsTrue(rig.WaitForBytes(2), $"got {Convert.ToHexString(rig.Received)}");
        Assert.AreEqual("1+", Text(rig.Received));
    }

    [TestMethod]
    public void Text_typed_through_TypeText_reaches_the_server_as_UTF8()
    {
        // What the IME proxy hands over: committed composition text, which never arrives as key messages.
        using var rig = new Rig();

        rig.Engine.Run(() => rig.Session.TypeText("\u4F60\u597D"));              // two Chinese characters
        rig.Engine.Run(() => rig.Session.TypeText("a\U0001F600b"));              // an emoji, as a surrogate pair inside one call

        byte[] expected = [0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD, 0x61, 0xF0, 0x9F, 0x98, 0x80, 0x62];
        Assert.IsTrue(rig.WaitForBytes(expected.Length), "got only " + BitConverter.ToString(rig.Received));
        CollectionAssert.AreEqual(expected, rig.Received[..expected.Length]);
    }

    [TestMethod]
    public void Text_posted_as_characters_is_sent_as_UTF8_including_surrogate_pairs()
    {
        using var rig = new Rig();

        rig.PostChar('é');                 // e acute
        rig.PostChar('\uD83D');                 // an emoji, as the two halves Windows posts
        rig.PostChar('\uDE00');

        byte[] expected = [0xC3, 0xA9, 0xF0, 0x9F, 0x98, 0x80];
        Assert.IsTrue(rig.WaitForBytes(expected.Length), $"got {Convert.ToHexString(rig.Received)}");
        CollectionAssert.AreEqual(expected, rig.Received);
    }

    [TestMethod]
    public void A_unicode_packet_key_and_the_input_method_key_are_left_for_windows()
    {
        using var rig = new Rig();
        rig.Hold();

        rig.Press(0xE7, 0);                     // VK_PACKET: Windows turns it into a WM_CHAR
        rig.Press(0xE5, 0);                     // VK_PROCESSKEY: the input method owns it
        rig.PostChar('x');                      // what the packet would have become
        rig.Press(0x59, 0x15);                  // Y

        Assert.IsTrue(rig.WaitForBytes(2), $"got {Convert.ToHexString(rig.Received)}");
        Assert.AreEqual("xy", Text(rig.Received));
    }

    [TestMethod]
    public void Nothing_is_taken_while_no_terminal_has_keyboard_focus()
    {
        using var rig = new Rig();
        rig.Hold();

        rig.Engine.Run(() => PuttySession.SetKeyboardTarget(null));
        rig.Press(0x58, 0x2D);                  // X, ignored
        rig.PostChar('x');                      // ignored
        rig.Engine.Run(() => PuttySession.SetKeyboardTarget(rig.Session));
        rig.Press(0x59, 0x15);                  // Y, delivered

        Assert.IsTrue(rig.WaitForBytes(1));
        Assert.AreEqual("y", Text(rig.Received));
    }

    [TestMethod]
    public void Closing_the_session_clears_the_key_target_and_the_engine_carries_on()
    {
        using var rig = new Rig();
        rig.Hold();
        rig.Engine.Run(rig.Session.Dispose);

        rig.PostChar('x');                      // no target now: must not touch the freed session
        rig.Press(0x58, 0x2D);

        rig.Engine.Run(PuttyEngine.Diagnostics.QueueCallback);
        Assert.IsTrue(
            EngineThread.WaitUntil(() => rig.Engine.Run(() => PuttyEngine.Diagnostics.GetCounters().CallbacksRun) == 1, Generous),
            "the engine stopped after the key target's session closed");
        Assert.AreEqual(0, rig.Received.Length);
    }

    [TestMethod]
    public void Setting_the_key_target_needs_a_session_with_a_terminal()
    {
        using var engine = new EngineThread();
        using var server = new LoopbackServer();
        PuttySession session = engine.Run(() =>
        {
            var s = new PuttySession("127.0.0.1", server.Port, SessionProtocol.Raw);
            s.Start();
            return s;
        });

        engine.Run(() => Assert.Throws<InvalidOperationException>(() => PuttySession.SetKeyboardTarget(session)));
        engine.Run(session.Dispose);
    }

    [TestMethod]
    public void A_Norwegian_dead_key_composes_with_the_next_letter_and_AltGr_types_a_brace()
    {
        using var rig = new Rig("00000414");
        if (rig.Layout == 0)
        {
            Assert.Inconclusive("the Norwegian keyboard layout is not available on this machine");
        }

        // The key right of P is a dead diaeresis on this layout; the virtual key comes from the layout.
        byte deadKey = (byte)MapVirtualKeyExW(0x1B, 3 /* MAPVK_VSC_TO_VK_EX */, rig.Layout);
        rig.Hold();
        rig.Press(deadKey, 0x1B);
        rig.Press(0x41, 0x1E);                  // A: the diaeresis and the A become a-umlaut
        Assert.IsTrue(rig.WaitForBytes(2), $"got {Convert.ToHexString(rig.Received)}");
        CollectionAssert.AreEqual(new byte[] { 0xC3, 0xA4 }, rig.Received);

        // AltGr+7 is an opening brace.
        byte seven = (byte)MapVirtualKeyExW(0x08, 3, rig.Layout);
        rig.Hold(altGr: true);
        rig.Press(seven, 0x08);
        Assert.IsTrue(rig.WaitForBytes(3), $"got {Convert.ToHexString(rig.Received)}");
        Assert.AreEqual((byte)'{', rig.Received[^1]);
    }
}
