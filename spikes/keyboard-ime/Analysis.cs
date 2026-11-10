using System.Text;
using System.Text.Json;

namespace KeyboardIme;

internal sealed class RunInfo
{
    public string Os = "";
    public string Notes = "";
    public string? AbortReason;
    public int KeysSent;
    public int HookCalls;
    public bool HookInstalled;
    public bool SentHookInstalled;
    public List<string> Passes = [];
    public List<string> Layouts = [];
    public Dictionary<string, string> LayoutNotes = [];
    public Dictionary<string, bool> TextBoxGroups = [];   // "layout|group" -> the proxy TextBox is meant to supply its text
}

/// <summary>Turns the recorded events into results.json and a summary.md that answers the spike's questions.</summary>
internal static class Analysis
{
    private sealed record Step(string Pass, string Id, string Layout, string Group, string Token, List<Ev> Events);

    public static void Write(string dir, RunInfo info)
    {
        Directory.CreateDirectory(dir);
        WriteJson(Path.Combine(dir, "results.json"));
        File.WriteAllText(Path.Combine(dir, "summary.md"), Summary(info));
    }

    private static void WriteJson(string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream);
        w.WriteStartArray();
        foreach (Ev e in Rec.All)
        {
            w.WriteStartObject();
            w.WriteString("pass", e.Pass);
            w.WriteString("step", e.Step);
            w.WriteNumber("seq", e.Seq);
            w.WriteString("src", e.Src);
            w.WriteString("kind", e.Kind);
            if (e.Vk >= 0) w.WriteNumber("vk", e.Vk);
            if (e.Scan >= 0) w.WriteNumber("scan", e.Scan);
            if (e.Ext) w.WriteBoolean("ext", true);
            if (e.Alt) w.WriteBoolean("alt", true);
            if (e.Was) w.WriteBoolean("was", true);
            if (e.Up) w.WriteBoolean("up", true);
            if (e.Rep != 0) w.WriteNumber("rep", e.Rep);
            if (e.Ch >= 0) w.WriteNumber("ch", e.Ch);
            if (e.Mods.Length > 0) w.WriteString("mods", e.Mods);
            if (e.Extra.Length > 0) w.WriteString("extra", e.Extra);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static List<Step> Steps() =>
        Rec.All.Where(e => e.Step.Length > 0)
            .GroupBy(e => (e.Pass, e.Step))
            .Select(g =>
            {
                string[] p = g.Key.Step.Split('|');
                string token = p.Length > 2 ? p[2][(p[2].IndexOf(':') + 1)..] : "";
                return new Step(g.Key.Pass, g.Key.Step, p[0], p.Length > 1 ? p[1] : "", token, [.. g.OrderBy(e => e.Seq)]);
            })
            .ToList();

    private static string Hex(IEnumerable<int> chars) => string.Join(' ', chars.Select(c => c.ToString("X4")));

    private static List<int> Chars(Step s, string src, params string[] kinds) =>
        s.Events.Where(e => e.Src == src && kinds.Contains(e.Kind) && e.Ch >= 0).Select(e => e.Ch).ToList();

    private static List<string> KeySeq(Step s, string src, params string[] kinds) =>
        s.Events.Where(e => e.Src == src && kinds.Contains(e.Kind) && e.Vk >= 0)
            .Select(e => $"{e.Vk:X2}/{e.Scan:X2}{(e.Ext ? "e" : "")}").ToList();

    /// <summary>The text a proxy TextBox ended up holding after the step (last TextChanged), as hex code units.</summary>
    private static List<int> BoxText(Step s)
    {
        Ev? last = s.Events.LastOrDefault(e => e.Src == "tbox" && e.Kind == "TextChanged");
        if (last is null || last.Extra.Length == 0)
        {
            return [];
        }

        return last.Extra.Split(' ').Select(t => Convert.ToInt32(t[2..], 16)).ToList();
    }

    private static bool Printable(int c) => c >= 0x20 && c != 0x7F;

    private static string Summary(RunInfo info)
    {
        var sb = new StringBuilder();
        List<Step> steps = Steps();

        sb.AppendLine("# Keyboard / IME spike: results");
        sb.AppendLine();
        sb.AppendLine($"- OS: {info.Os}");
        sb.AppendLine($"- Passes: {string.Join(", ", info.Passes)}; layouts: {string.Join(", ", info.Layouts)}");
        sb.AppendLine($"- Keys sent: {info.KeysSent}; steps recorded: {steps.Count}; events: {Rec.All.Count}");
        sb.AppendLine($"- Thread message hook installed: {info.HookInstalled}; hook calls: {info.HookCalls}");
        sb.AppendLine($"- Run aborted: {info.AbortReason ?? "no"}");
        if (info.Notes.Length > 0) sb.AppendLine($"- Notes: {info.Notes}");
        foreach (var (k, v) in info.LayoutNotes) sb.AppendLine($"- Layout {k}: {v}");
        sb.AppendLine();

        Windows(sb);
        PassA(sb, steps, info, "A");
        PassA(sb, steps, info, "A2");
        PassB(sb, steps);
        PassC(sb, steps, info);
        DeadKeyTraces(sb, steps);
        ImeTraces(sb, steps);
        KeyIdentity(sb, steps);
        return sb.ToString();
    }

    private static void Windows(StringBuilder sb)
    {
        sb.AppendLine("## Where raw key messages land (window classes) and IME messages seen");
        sb.AppendLine();
        foreach (var g in Rec.All.Where(e => e.Src == "raw" && e.Kind is "WM_KEYDOWN" or "WM_SYSKEYDOWN").GroupBy(e => e.Extra).OrderByDescending(g => g.Count()))
            sb.AppendLine($"- key-down messages delivered to class `{g.Key}`: {g.Count()}");
        foreach (var g in Rec.All.Where(e => e.Src == "raw" && e.Kind.StartsWith("WM_IME") || e.Kind == "WM_INPUTLANGCHANGE").GroupBy(e => e.Kind))
            sb.AppendLine($"- {g.Key}: {g.Count()}");
        sb.AppendLine();
    }

    private static void Table(StringBuilder sb, string title, string[] columns, List<string[]> rows)
    {
        sb.AppendLine($"### {title}");
        sb.AppendLine();
        sb.AppendLine("| " + string.Join(" | ", columns) + " |");
        sb.AppendLine("|" + string.Join("|", columns.Select(_ => "---")) + "|");
        foreach (string[] r in rows) sb.AppendLine("| " + string.Join(" | ", r) + " |");
        sb.AppendLine();
    }

    private static string Ratio(int ok, int total) => total == 0 ? "-" : ok == total ? $"{ok}/{total}" : $"**{ok}/{total}**";

    private static void Examples(StringBuilder sb, string title, List<string> lines)
    {
        if (lines.Count == 0) return;
        sb.AppendLine($"#### {title} (examples, at most 3 per layout and group)");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (string l in lines.Take(80)) sb.AppendLine(l);
        if (lines.Count > 80) sb.AppendLine($"... and {lines.Count - 80} more");
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void PassA(StringBuilder sb, List<Step> all, RunInfo info, string pass)
    {
        var steps = all.Where(s => s.Pass == pass).ToList();
        if (steps.Count == 0) return;
        sb.AppendLine(pass == "A" ? "## Pass A: terminal control handles every KeyDown (Handled = true), XAML events" : "## Pass A2: terminal control handles only keys that are not text; text keys are left to XAML");
        sb.AppendLine();
        sb.AppendLine("Ground truth is the raw message stream from the thread hook. `keys` compares the sequence of key-downs (vk/scan[e=extended]); `text` compares WM_CHAR and WM_SYSCHAR characters with what the source delivered as characters.");
        sb.AppendLine();

        var rows = new List<string[]>();
        var keyEx = new List<string>();
        var textEx = new List<string>();
        foreach (var grp in steps.GroupBy(s => (s.Layout, s.Group)))
        {
            int n = 0, keysX = 0, textX = 0, previewOnly = 0;
            int shown = 0, shownT = 0;
            foreach (Step s in grp)
            {
                n++;
                var rawKeys = KeySeq(s, "raw", "WM_KEYDOWN", "WM_SYSKEYDOWN");
                var xKeys = KeySeq(s, "xaml", "KeyDown");

                if (rawKeys.SequenceEqual(xKeys)) keysX++;
                else if (shown++ < 3) keyEx.Add($"{s.Layout} {s.Group} [{s.Token}] raw={string.Join(',', rawKeys)} xaml={string.Join(',', xKeys)} preview={string.Join(',', KeySeq(s, "xaml", "PreviewKeyDown"))}");

                if (KeySeq(s, "xaml", "PreviewKeyDown").Count > xKeys.Count) previewOnly++;

                var rawText = Chars(s, "raw", "WM_CHAR", "WM_SYSCHAR");
                var xText = Chars(s, "xaml", "Char");

                if (rawText.SequenceEqual(xText)) textX++;
                else if (shownT++ < 3) textEx.Add($"{s.Layout} {s.Group} [{s.Token}] raw={Hex(rawText)} dead={Hex(Chars(s, "raw", "WM_DEADCHAR", "WM_SYSDEADCHAR"))} xaml={Hex(xText)}");

            }

            rows.Add([grp.Key.Layout, grp.Key.Group, n.ToString(), Ratio(keysX, n), Ratio(textX, n), previewOnly.ToString()]);
        }

        Table(sb, $"Pass {pass}", ["layout", "group", "steps", "keys xaml = raw", "text xaml = raw", "seen only in Preview"], rows);
        Examples(sb, "Key-down mismatches (XAML KeyDown vs raw)", keyEx);
        Examples(sb, "Character mismatches (XAML Char vs raw WM_CHAR)", textEx);
    }

    private static void PassB(StringBuilder sb, List<Step> all)
    {
        var steps = all.Where(s => s.Pass == "B").ToList();
        if (steps.Count == 0) return;
        var truth = all.Where(s => s.Pass is "A" or "A2" or "C").GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        sb.AppendLine("## Pass B: thread hook swallows key messages and runs PuTTY's ToUnicodeEx logic itself");
        sb.AppendLine();
        sb.AppendLine("`ported` text is what the faithful TranslateKey text path produced; it is compared with the raw WM_CHAR text recorded for the same step in pass A. `leaks` counts XAML or TextBox events that still arrived although the hook swallowed the message (key-downs that carry `VK_PROCESSKEY` or `VK_PACKET` are deliberately passed through, as PuTTY lets Windows translate those, so they show up here).");
        sb.AppendLine();

        var rows = new List<string[]>();
        var ex = new List<string>();
        foreach (var grp in steps.GroupBy(s => (s.Layout, s.Group)))
        {
            int n = 0, same = 0, leaks = 0, compared = 0, shown = 0;
            foreach (Step s in grp)
            {
                n++;
                leaks += s.Events.Count(e => e.Src is "xaml" or "tbox" && e.Kind is not "PreviewKeyDown");
                if (!truth.TryGetValue(s.Id, out Step? a)) continue;
                compared++;
                var rawText = Chars(a, "raw", "WM_CHAR", "WM_SYSCHAR");
                var ported = Chars(s, "ported", "text").Concat(Chars(s, "raw", "WM_CHAR", "WM_SYSCHAR")).ToList();   // PuTTY's window procedure handles WM_CHAR too
                if (rawText.SequenceEqual(ported)) same++;
                else if (shown++ < 3) ex.Add($"{s.Layout} {s.Group} [{s.Token}] rawA={Hex(rawText)} ported={Hex(ported)} {string.Join(' ', s.Events.Where(e => e.Src == "ported").Select(e => e.Kind + ":" + e.Extra))}");
            }

            rows.Add([grp.Key.Layout, grp.Key.Group, n.ToString(), Ratio(same, compared), leaks == 0 ? "0" : $"**{leaks}**"]);
        }

        Table(sb, "Pass B", ["layout", "group", "steps", "ported text = raw text", "leaks"], rows);
        Examples(sb, "Ported text differs from raw text", ex);
    }

    private static void PassC(StringBuilder sb, List<Step> all, RunInfo info)
    {
        var steps = all.Where(s => s.Pass == "C").ToList();
        if (steps.Count == 0) return;
        sb.AppendLine("## Pass C: proxy TextBox has focus; the terminal handles non-text keys in PreviewKeyDown");
        sb.AppendLine();
        sb.AppendLine("Only groups whose text the TextBox is meant to supply are compared: the TextBox's text after each step against the printable characters of the raw WM_CHAR stream. Ctrl and Alt combinations are handled by the terminal from key information, so they are not compared.");
        sb.AppendLine();

        var rows = new List<string[]>();
        var ex = new List<string>();
        int composition = Rec.All.Count(e => e.Pass == "C" && e.Src == "tbox" && e.Kind.StartsWith("Composition"));
        foreach (var grp in steps.GroupBy(s => (s.Layout, s.Group)))
        {
            if (!info.TextBoxGroups.GetValueOrDefault($"{grp.Key.Layout}|{grp.Key.Group}")) continue;
            int n = 0, same = 0, shown = 0;
            foreach (Step s in grp)
            {
                n++;
                var expected = Chars(s, "raw", "WM_CHAR", "WM_SYSCHAR").Where(Printable).ToList();
                var actual = BoxText(s).Where(Printable).ToList();
                if (expected.SequenceEqual(actual)) same++;
                else if (shown++ < 3) ex.Add($"{s.Layout} {s.Group} [{s.Token}] raw={Hex(expected)} dead={Hex(Chars(s, "raw", "WM_DEADCHAR", "WM_SYSDEADCHAR"))} textbox={Hex(actual)}");
            }

            rows.Add([grp.Key.Layout, grp.Key.Group, n.ToString(), Ratio(same, n)]);
        }

        Table(sb, "Pass C", ["layout", "group", "steps", "TextBox text = raw printable text"], rows);
        sb.AppendLine($"TextBox composition events seen in pass C: {composition} (non-zero needs a real IME).");
        sb.AppendLine();
        Examples(sb, "TextBox text differs from raw text", ex);
    }

    private static void DeadKeyTraces(StringBuilder sb, List<Step> all)
    {
        var dead = all.Where(s => s.Group.StartsWith("dead-")).OrderBy(s => s.Layout).ThenBy(s => s.Group).ThenBy(s => s.Id).ToList();
        if (dead.Count == 0) return;
        sb.AppendLine("## Dead-key traces (every step, every pass)");
        sb.AppendLine();
        sb.AppendLine("Text per step as hex code units. `dead` is the WM_DEADCHAR payload; `xaml`/`ported`/`tbox` are what each design delivered.");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (Step s in dead)
        {
            sb.AppendLine($"{s.Pass} {s.Layout} {s.Group,-14} {s.Token,-16} raw=[{Hex(Chars(s, "raw", "WM_CHAR", "WM_SYSCHAR"))}] dead=[{Hex(Chars(s, "raw", "WM_DEADCHAR", "WM_SYSDEADCHAR"))}] xaml=[{Hex(Chars(s, "xaml", "Char"))}] ported=[{Hex(Chars(s, "ported", "text"))}] tbox=[{Hex(BoxText(s))}]");
        }

        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void ImeTraces(StringBuilder sb, List<Step> all)
    {
        var ime = all.Where(s => s.Group.StartsWith("ime-")).OrderBy(s => s.Layout).ThenBy(s => s.Pass).ThenBy(s => s.Id).ToList();
        if (ime.Count == 0) return;
        sb.AppendLine("## IME traces (every step, every pass)");
        sb.AppendLine();
        sb.AppendLine("`keys` are the raw key-downs (vk hex; E5 = VK_PROCESSKEY, the IME ate the key). `msgs` are the IME window messages seen (posted or sent). `tbox` are the proxy TextBox events; `xaml` is CharacterReceived on the terminal control; `text` is the TextBox content after the step.");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (Step s in ime)
        {
            string keys = string.Join(',', KeySeq(s, "raw", "WM_KEYDOWN", "WM_SYSKEYDOWN").Select(k => k.Split('/')[0]));
            string msgs = string.Join(' ', s.Events.Where(e => e.Src is "raw" or "sent" && e.Kind.StartsWith("WM_IME")).Select(e => e.Kind.Replace("WM_IME_", "") + (e.Src == "sent" ? "*" : "")).GroupBy(x => x).Select(g => g.Count() > 1 ? $"{g.Key}x{g.Count()}" : g.Key));
            string tbox = string.Join(' ', s.Events.Where(e => e.Src == "tbox" && e.Kind is not ("PreviewKeyDown" or "PreviewKeyUp" or "KeyDown" or "BeforeTextChanging")).Select(e => e.Kind + (e.Extra.Length > 0 ? $"({e.Extra})" : "")));
            sb.AppendLine($"{s.Pass,-2} {s.Token,-8} keys=[{keys}] msgs=[{msgs}] tbox=[{tbox}] xaml=[{Hex(Chars(s, "xaml", "Char"))}] ported=[{Hex(Chars(s, "ported", "text"))}] rawchar=[{Hex(Chars(s, "raw", "WM_CHAR", "WM_SYSCHAR"))}] text=[{Hex(BoxText(s))}]");
        }

        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void KeyIdentity(StringBuilder sb, List<Step> all)
    {
        var steps = all.Where(s => s.Pass == "A2" && s.Group is "numpad-on" or "numpad-off" or "nav" && s.Layout == "0409").ToList();
        if (steps.Count == 0) return;
        sb.AppendLine("## Key identity for the keypad and navigation keys (US layout)");
        sb.AppendLine();
        sb.AppendLine("Pass A2. vk/scan, `e` marks the extended-key flag. This is what the ported TranslateKey needs to tell the numeric keypad from the cursor block.");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (Step s in steps)
        {
            sb.AppendLine($"{s.Group,-10} {s.Token,-12} raw={string.Join(',', KeySeq(s, "raw", "WM_KEYDOWN", "WM_SYSKEYDOWN")),-24} xaml={string.Join(',', KeySeq(s, "xaml", "KeyDown")),-24}");
        }

        sb.AppendLine("```");
        sb.AppendLine();
    }
}
