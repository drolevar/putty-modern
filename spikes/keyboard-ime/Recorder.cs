using System.Text;

namespace KeyboardIme;

/// <summary>One observed input event from any source.</summary>
internal sealed class Ev
{
    public string Pass = "";
    public string Step = "";
    public int Seq;
    public string Src = "";     // raw (thread message hook), sent (WH_CALLWNDPROC), xaml (control), tbox (proxy TextBox), ported (ToUnicodeEx as PuTTY calls it)
    public string Kind = "";
    public int Vk = -1;
    public int Scan = -1;
    public bool Ext;
    public bool Alt;            // context code / IsMenuKeyDown
    public bool Was;            // previous key state / WasKeyDown
    public bool Up;
    public int Rep;
    public int Ch = -1;
    public string Mods = "";
    public string Extra = "";
}

internal static class Rec
{
    public static readonly List<Ev> All = [];
    public static string Pass = "";
    public static string Step = "";
    public static Action<Ev>? Live;
    private static int seq;

    public static Ev Add(string src, string kind, int vk = -1, int scan = -1, bool ext = false, bool alt = false,
        bool was = false, bool up = false, int rep = 0, int ch = -1, string extra = "")
    {
        var e = new Ev
        {
            Pass = Pass, Step = Step, Seq = ++seq, Src = src, Kind = kind, Vk = vk, Scan = scan, Ext = ext,
            Alt = alt, Was = was, Up = up, Rep = rep, Ch = ch, Mods = Mods(), Extra = extra,
        };
        All.Add(e);
        Live?.Invoke(e);
        return e;
    }

    /// <summary>Modifier keys that are down according to this thread's input state.</summary>
    public static string Mods()
    {
        var sb = new StringBuilder();
        void Add(int vk, string name)
        {
            if (Native.IsDown(vk)) { if (sb.Length > 0) sb.Append(' '); sb.Append(name); }
        }

        Add(Native.VK_LSHIFT, "LS"); Add(Native.VK_RSHIFT, "RS");
        Add(Native.VK_LCONTROL, "LC"); Add(Native.VK_RCONTROL, "RC");
        Add(Native.VK_LMENU, "LA"); Add(Native.VK_RMENU, "RA");
        return sb.ToString();
    }
}
