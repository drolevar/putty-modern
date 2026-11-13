using CommunityToolkit.Mvvm.ComponentModel;
using PuTTY.Interop;

namespace PuTTY.App.ViewModels;

/// <summary>
/// What the main window knows about the engine: whether it loaded and answers. When it does, the welcome tab is an empty
/// state ("No session open"); when it does not, the tab says why. The engine's version and the result of its self-check go to
/// the diagnostic log, which is where someone looking for them goes.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        EngineProblem = DescribeProblem();
    }

    /// <summary>Why the engine cannot be used, or an empty string when it can.</summary>
    public string EngineProblem { get; }

    public bool EngineAvailable => EngineProblem.Length == 0;

    private static string DescribeProblem()
    {
        try
        {
            PuttyEngine.EnsureCompatible();
            if (!PuttyEngine.Probe())
            {
                PuttyLog.Warning("app", $"PuTTY engine ABI {PuttyEngine.NativeAbiVersion}: the self-check of its settings failed");
                return "The PuTTY engine started, but its self-check failed, so settings may not load or save. See the diagnostic log (--log).";
            }

            PuttyLog.Info("app", $"PuTTY engine ABI {PuttyEngine.NativeAbiVersion}, self-check ok");
            return string.Empty;
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
        {
            return $"PuTTY engine unavailable: {ex.Message}";
        }
    }
}
