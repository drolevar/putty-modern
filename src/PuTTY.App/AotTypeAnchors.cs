using Microsoft.UI.Xaml;

namespace PuTTY.App;

/// <summary>
/// Keeps WinUI classes that the app only ever <i>receives</i> from native code (a resource looked up by key, control
/// templates, <c>FindName</c>, <c>XamlReader.Load</c>) known to the WinRT projection under NativeAOT.
/// </summary>
/// <remarks>
/// <para>
/// The projection turns a native object into a managed one by looking up its runtime class name.
/// When the trimmer finds no use of that class in managed code, the lookup quietly falls back to the
/// nearest known base class: a <c>ContentPresenter</c> comes back as a <c>FrameworkElement</c>, a
/// <c>Button</c> as a <c>ContentControl</c>. Then <c>x as ContentPresenter</c> is null, no error is
/// raised, and whatever depended on the cast silently does nothing. Constructing an instance somewhere
/// reachable is enough to keep the class known.
/// </para>
/// <para>
/// Until the docking library fixed it (<c>TemplatePart&lt;T&gt;</c> and <c>As&lt;T&gt;</c>, library 1.6.1-alpha.0.6 and
/// later), this also listed the five classes the library casts its template parts to (<c>ContentPresenter</c>,
/// <c>Rectangle</c>, <c>Border</c>, <c>Button</c>, <c>FontIcon</c>), without which the docked windows' content was
/// missing and their Close buttons did nothing. They are gone: the owner ran the published app without them and the
/// content, the glyphs and the Close buttons were right.
/// </para>
/// <para>
/// What is left is the app's own cast of a resource it looks up (<c>AccentButtonStyle</c>) to <c>Style</c>, and the cast of what
/// <c>XamlReader.Load</c> returns for the Connection log's row template to <c>DataTemplate</c>. List here
/// every WinUI class that the app's own code casts to but never constructs, and check a new one the way CLAUDE.md
/// (section NativeAOT) describes: load it with <c>XamlReader</c> and look at <c>GetType()</c>.
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
        _ = new Style();
        _ = new DataTemplate();
    }
}
