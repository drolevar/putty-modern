using CommunityToolkit.Mvvm.ComponentModel;
using PuTTY.Interop;

namespace PuTTY.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    // Partial property (not a field): the AOT-correct form for WinUI under NativeAOT.
    [ObservableProperty]
    public partial string EngineStatus { get; set; }

    public MainViewModel()
    {
        EngineStatus = DescribeEngine();
    }

    private static string DescribeEngine()
    {
        try
        {
            PuttyEngine.EnsureCompatible();
            string probe = PuttyEngine.Probe() ? "ok" : "FAILED";
            return $"PuTTY engine ABI {PuttyEngine.NativeAbiVersion}, Conf probe {probe}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
        {
            return $"PuTTY engine unavailable: {ex.Message}";
        }
    }
}
