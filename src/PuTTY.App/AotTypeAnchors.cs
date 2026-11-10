using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;

namespace PuTTY.App;

/// <summary>
/// Keeps WinUI classes that the app only ever <i>receives</i> from native code (control templates,
/// <c>GetTemplateChild</c>, <c>FindName</c>, <c>XamlReader.Load</c>) known to the WinRT projection
/// under NativeAOT.
/// </summary>
/// <remarks>
/// <para>
/// The projection turns a native object into a managed one by looking up its runtime class name.
/// When the trimmer finds no use of that class in managed code, the lookup quietly falls back to the
/// nearest known base class: a <c>ContentPresenter</c> comes back as a <c>FrameworkElement</c>, a
/// <c>Button</c> as a <c>ContentControl</c>. Then <c>x as ContentPresenter</c> is null, no error is
/// raised, and whatever depended on the cast silently does nothing. That is exactly why the docked
/// windows' content was missing (the docking library plugs the windows into a
/// <c>ContentPresenter</c> it gets from its template) and why the windows' Close buttons did nothing
/// (<c>as Button</c>). Constructing an instance somewhere reachable is enough to keep the class known.
/// </para>
/// <para>
/// List here every WinUI class that managed code casts to but never constructs, and check a new one
/// the way CLAUDE.md (section NativeAOT) describes: load it with
/// <c>XamlReader</c> and look at <c>GetType()</c>. The classes the docking library casts its template
/// parts to are: <c>ContentPresenter</c>, <c>Rectangle</c>, <c>Border</c>, <c>Button</c> and
/// <c>FontIcon</c>. <c>TextBlock</c>, <c>Grid</c> and <c>StackPanel</c> are already constructed by the
/// app or the library.
/// </para>
/// </remarks>
internal static class AotTypeAnchors
{
    private static bool touched;

    /// <summary>Call once at start-up, on the UI thread.</summary>
    public static void Touch()
    {
        if (touched)
        {
            return;
        }

        touched = true;
        _ = new ContentPresenter();
        _ = new Border();
        _ = new Button();
        _ = new FontIcon();
        _ = new Rectangle();
    }
}
