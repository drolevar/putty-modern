using Microsoft.UI.Xaml.Controls;

namespace KeyboardIme;

// Experiment for the NativeAOT crash of a C# subclass of a WinUI control (Beads sqm): the same
// class, once marked partial (what the CsWinRT source generator expects) and once not.
public sealed partial class ProbePartial : UserControl
{
    public ProbePartial() { Content = new TextBlock { Text = "partial subclass" }; }
}

public sealed class ProbePlain : UserControl
{
    public ProbePlain() { Content = new TextBlock { Text = "plain (not partial) subclass" }; }
}