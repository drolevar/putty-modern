using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using static KeyboardIme.Native;

namespace KeyboardIme;

/// <summary>
/// Interactive by default: focus the terminal control or the proxy TextBox and type; the log shows
/// every event from every source. With <c>--selftest</c> it injects its own keystrokes through
/// all three designs and writes results.json and summary.md.
/// Options: --out DIR, --layouts 0409,0414,0419,0422, --passes A,B,C, --quick, --keep.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly string[] args = Environment.GetCommandLineArgs();
    private readonly nint hwnd;
    private readonly List<string> live = [];
    private UIElement? expectedFocus;
    private bool started;
    private readonly RunInfo info = new();

    // A plain UserControl with handlers attached: a C# subclass of a WinUI control crashed a NativeAOT build
    // at start-up (COMException 0x80040111), see docs/spike-keyboard-ime.md.
    private readonly UserControl Terminal = TerminalKeyControl.Create();
    private readonly TextBox ProxyBox = new() { PlaceholderText = "proxy TextBox (candidate for IME text)" };

    public MainWindow()
    {
        InitializeComponent();
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 760));

        if (Has("--probe-partial")) RootGrid.Children.Add(new ProbePartial());
        if (Has("--probe-plain")) RootGrid.Children.Add(new ProbePlain());

        Terminal.Height = 120;
        Grid.SetRow(Terminal, 1);
        RootGrid.Children.Add(Terminal);

        // The proxy TextBox is created in code so it can be left out (--no-textbox): a TextBox in the
        // tree was found to crash a NativeAOT build at start-up (see docs/spike-keyboard-ime.md).
        if (!Has("--no-textbox"))
        {
            Grid.SetRow(ProxyBox, 2);
            RootGrid.Children.Add(ProxyBox);
        }

        TerminalKeyControl.AttachProxyBox(ProxyBox);
        if (!Has("--no-hooks"))
        {
            info.HookInstalled = Hook.Install();
            info.SentHookInstalled = Hook.InstallSent();
        }


        Closed += (_, _) => Hook.Remove();

        FocusTerminalButton.Click += (_, _) => Terminal.Focus(FocusState.Programmatic);
        FocusProxyButton.Click += (_, _) => ProxyBox.Focus(FocusState.Programmatic);
        SwallowToggle.Click += (_, _) => Hook.Swallow = SwallowToggle.IsChecked == true;
        SaveButton.Click += (_, _) => Save();

        if (!Has("--selftest"))
        {
            Rec.Live = e =>
            {
                if (e.Kind == "PreviewKeyDown" && e.Src == "xaml") return;
                live.Add($"{e.Src,-6} {e.Kind,-18} vk={e.Vk:X2} sc={e.Scan:X2}{(e.Ext ? "e" : " ")} ch={(e.Ch >= 0 ? e.Ch.ToString("X4") : "    ")} {e.Mods} {e.Extra}");
                if (live.Count > 70) live.RemoveAt(0);
                LogText.Text = string.Join('\n', live);
            };
        }

        Activated += (_, _) =>
        {
            if (started) return;
            started = true;
            DispatcherQueue.TryEnqueue(async () =>
            {
                App.Trace("activated; starting after delay");
                await Task.Delay(800);      // the window content must be in a ContentIsland first

                if (Has("--smoke"))
                {
                    await Smoke();
                }
                else if (Has("--selftest"))
                {
                    await SelfTest();
                }
            });
        };
    }

    private bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    private string Opt(string name, string fallback)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private string OutDir => Opt("--out", Path.Combine(AppContext.BaseDirectory, "results"));

    private void Save()
    {
        info.Os = $"{RuntimeInformation.OSDescription}";
        Analysis.Write(OutDir, info);
        StatusText.Text = $"saved to {OutDir}";
    }

    private bool FocusIs(UIElement target) => ReferenceEquals(FocusManager.GetFocusedElement(Content.XamlRoot), target);

    private async Task<bool> Focus(Control target)
    {
        expectedFocus = target;
        target.Focus(FocusState.Programmatic);
        await Task.Delay(150);
        return FocusIs(target);
    }

    /// <summary>
    /// Types one key whose meaning differs between layouts (scan 0x1B) and checks that the virtual
    /// key that arrives is the one the active layout assigns to that scan code. If injecting a bare
    /// scan code gets it wrong, switch the injector to sending virtual keys it computes itself.
    /// </summary>
    private async Task<string> ProbeLayout(Injector injector, nint hkl, string layout)
    {
        uint expected = MapVirtualKeyExW(0x1B, 3 /* MAPVK_VSC_TO_VK_EX */, hkl);

        async Task<uint> Type(string note)
        {
            Rec.Step = $"{layout}|probe|{note}";
            int before = Rec.All.Count;
            await injector.Tap("rbracket");
            Ev? down = Rec.All.Skip(before).FirstOrDefault(e => e.Src == "raw" && e.Kind == "WM_KEYDOWN");
            await injector.Tap("space");      // finishes a dead-key sequence if that key started one
            if (ReferenceEquals(expectedFocus, ProxyBox))
            {
                TerminalKeyControl.Suppress = true;
                ProxyBox.Text = string.Empty;
                await Task.Delay(10);
                TerminalKeyControl.Suppress = false;
            }

            return down is null ? 0u : (uint)down.Vk;
        }

        injector.UseVk = false;
        uint got = await Type("000:scan");
        if (got == expected)
        {
            return $"scan injection is consistent (vk 0x{got:X2})";
        }

        injector.UseVk = true;
        uint viaVk = await Type("001:vk");
        string result = viaVk == expected ? "using VK injection" : $"VK injection also off (0x{viaVk:X2})";
        return $"scan injection gave vk 0x{got:X2} for scan 0x1B but layout says 0x{expected:X2}; {result}";
    }

    /// <summary>
    /// A check that is safe on any desktop: it posts a few key messages to this window only (no
    /// SendInput), once with the hook recording and once with the hook swallowing, and reports what
    /// each source saw. Used to confirm the plumbing works in a NativeAOT build.
    /// </summary>
    private async Task Smoke()
    {
        var lines = new List<string>();
        try
        {
            Activate();
            await Task.Delay(300);
            bool focused = await Focus(Terminal);
            nint target = GetFocus();
            lines.Add($"focus on terminal control: {focused}; focused HWND class: {(target == 0 ? "none" : ClassName(target))}");
            lines.Add($"hook installed: {info.HookInstalled}; sent-message hook: {info.SentHookInstalled}");

            foreach (bool swallow in new[] { false, true })
            {
                Hook.Swallow = swallow;
                Rec.Pass = swallow ? "smoke-hook" : "smoke-xaml";
                Rec.Step = "0409|smoke|000:a";
                TerminalKeyControl.HandleAll = false;
                const uint scan = 0x1E;
                nint down = (nint)(1 | (scan << 16));
                nint up = (nint)(1 | (scan << 16) | (1u << 30) | (1u << 31));
                PostMessageW(target, WM_KEYDOWN, 0x41, down);
                PostMessageW(target, WM_CHAR, (nuint)'a', down);
                PostMessageW(target, WM_KEYUP, 0x41, up);
                await Task.Delay(400);

                var ev = Rec.All.Where(e => e.Pass == Rec.Pass).ToList();
                string Count(string src, string kind) => ev.Count(e => e.Src == src && e.Kind == kind).ToString();
                lines.Add($"[{Rec.Pass}] raw WM_KEYDOWN={Count("raw", "WM_KEYDOWN")} WM_CHAR={Count("raw", "WM_CHAR")} | xaml KeyDown={Count("xaml", "KeyDown")} Char={Count("xaml", "Char")} | ported text={Count("ported", "text")} (ch={string.Join(',', ev.Where(e => e.Src == "ported" && e.Kind == "text").Select(e => e.Ch.ToString("X4")))})");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"exception: {ex}");
        }
        finally
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllLines(Path.Combine(OutDir, "smoke.txt"), lines);
            App.Trace("smoke done");
            if (!Has("--keep"))
            {
                Application.Current.Exit();
            }
        }
    }

    private async Task SelfTest()
    {
        App.Trace("SelfTest start");
        string[] layouts = Opt("--layouts", "0409,0414,0419,0422").Split(',');
        string[] passes = Opt("--passes", "A,A2,B,C").Split(',');
        bool quick = Has("--quick");
        info.Passes = [.. passes];

        nint originalLayout = GetKeyboardLayout(0);
        bool originalNum = (GetKeyState(VK_NUMLOCK) & 1) != 0;
        bool originalCaps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        var injector = new Injector(hwnd, () => expectedFocus is not null && FocusIs(expectedFocus),
            async () => expectedFocus is Control c && await Focus(c));

        try
        {
            // Refuse to start unless nothing is physically held and this window really is in front.
            foreach (int vk in new[] { VK_SHIFT, VK_CONTROL, VK_MENU, 0x5B, 0x5C })
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    info.AbortReason = $"a modifier key (0x{vk:X2}) was physically held at the start";
                    return;
                }
            }

            Activate();
            SetForegroundWindow(hwnd);
            await Task.Delay(400);
            if (GetForegroundWindow() != hwnd && Has("--force-foreground"))
            {
                ForceForeground(hwnd);
                await Task.Delay(400);
            }

            if (GetForegroundWindow() != hwnd)
            {
                info.AbortReason = "the spike window could not become the foreground window; no key was sent";
                return;
            }

            foreach (string pass in passes)
            {
                Control? target = pass == "C" ? ProxyBox : Terminal;
                Hook.Swallow = pass == "B";
                TerminalKeyControl.HandleAll = pass != "A2";
                foreach (string layout in layouts)
                {
                    nint hkl = LoadKeyboardLayoutW(layout.PadLeft(8, '0'), 0x80 /* KLF_NOTELLSHELL */);
                    if (hkl == 0)
                    {
                        info.LayoutNotes[layout] = "could not be loaded";
                        continue;
                    }

                    ActivateKeyboardLayout(hkl, 0);
                    await Task.Delay(200);
                    nint active = GetKeyboardLayout(0);
                    if ((active & 0xFFFF) != (hkl & 0xFFFF))
                    {
                        info.LayoutNotes[layout] = $"could not be activated (active 0x{active:X}, wanted 0x{hkl:X})";
                        continue;
                    }

                    string deadKeys = string.Join(' ', Scenarios.DeadKeys(hkl).Select(d => d.token).Distinct());
                    if (!info.Layouts.Contains(layout)) info.Layouts.Add(layout);

                    if (!await Focus(target))
                    {
                        info.AbortReason = $"could not give XAML focus to the {(pass == "C" ? "proxy TextBox" : "terminal control")}";
                        return;
                    }

                    Rec.Pass = pass;
                    injector.Hkl = hkl;
                    string probe = layout.StartsWith("E0", StringComparison.OrdinalIgnoreCase) ? "IME layout, no scan-code probe" : await ProbeLayout(injector, hkl, layout);
                    info.LayoutNotes[layout] = $"HKL 0x{hkl:X}; dead keys found: {deadKeys}; {probe}";
                    string[] only = Opt("--groups", "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (Scenario scenario in Scenarios.For(hkl, layout, quick))
                    {
                        if (only.Length > 0 && !only.Any(p => scenario.Group.StartsWith(p, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        info.TextBoxGroups[$"{layout}|{scenario.Group}"] = scenario.TextBox;
                        StatusText.Text = $"pass {pass} layout {layout} {scenario.Group}";
                        for (int i = 0; i < scenario.Taps.Length; i++)
                        {
                            Rec.Step = $"{layout}|{scenario.Group}|{i:D3}:{scenario.Taps[i]}";
                            await injector.Tap(scenario.Taps[i]);
                            if (pass == "C")
                            {
                                TerminalKeyControl.Suppress = true;
                                ProxyBox.Text = string.Empty;
                                await Task.Delay(10);
                                TerminalKeyControl.Suppress = false;
                            }

                            if (injector.Aborted)
                            {
                                info.AbortReason = injector.AbortReason;
                                return;
                            }
                        }

                        Rec.Step = string.Empty;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            info.AbortReason = $"exception: {ex}";
            App.Trace($"SelfTest exception: {ex}");
        }
        finally
        {
            injector.ReleaseAll();
            Rec.Step = string.Empty;
            try
            {
                await injector.ForceToggle("numlock", VK_NUMLOCK, originalNum);
                await injector.ForceToggle("capslock", VK_CAPITAL, originalCaps);
            }
            catch
            {
                // Restoring toggles is best effort.
            }

            ActivateKeyboardLayout(originalLayout, 0);
            info.KeysSent = injector.KeysSent;
            info.HookCalls = Hook.Calls;
            info.AbortReason ??= injector.AbortReason;
            App.Trace("SelfTest saving");
            Save();
            App.Trace("SelfTest saved");
            if (!Has("--keep"))
            {
                Application.Current.Exit();
            }
        }
    }
}
