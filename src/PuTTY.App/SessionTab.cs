using Digi21.WinUI.Docking;
using Microsoft.UI.Xaml.Controls;
using PuTTY.Interop;

namespace PuTTY.App;

/// <summary>
/// One open session in the document area: its docking document (the tab), the terminal view inside it, the session,
/// and the object that turns the engine's questions into dialogs. Opening a session makes one; closing the tab
/// disposes it, which ends the session.
/// </summary>
internal sealed class SessionTab : IDisposable
{
    private SessionInteraction interaction;
    private bool disposed;

    public SessionTab(DocumentWindow document, UserControl host, TerminalViewController terminal, PuttySession session,
        SessionInteraction interaction, string title, string? description)
    {
        Document = document;
        Host = host;
        Terminal = terminal;
        Session = session;
        this.interaction = interaction;
        Title = title;
        Description = description;
    }

    /// <summary>The tab in the document area.</summary>
    public DocumentWindow Document { get; }

    /// <summary>The control the terminal view is built in; the document's content.</summary>
    public UserControl Host { get; }

    public TerminalViewController Terminal { get; }

    /// <summary>The session in the tab; Reconnect puts a new one in its place (<see cref="Replace"/>).</summary>
    public PuttySession Session { get; private set; }

    /// <summary>What the tab is called: the saved session's name, or the host.</summary>
    public string Title { get; }

    /// <summary>Where it connects ("10.20.0.12 · SSH"), or null.</summary>
    public string? Description { get; }

    /// <summary>False once the connection has ended (the tab stays, with what the server last said).</summary>
    public bool Connected { get; set; } = true;

    /// <summary>The text for the window's title bar: what the server set, or PuTTY's own default for the host.</summary>
    public string WindowTitle { get; set; } = "PuTTY";

    /// <summary>
    /// Reconnect: a new session takes the old one's place in this tab. The old one ends, and its renderer lets go of the
    /// terminal panel, which the caller then gives to the new one with <c>Terminal.Show</c>.
    /// </summary>
    public void Replace(PuttySession fresh, SessionInteraction freshInteraction)
    {
        SessionInteraction oldInteraction = interaction;
        PuttySession old = Session;
        Session = fresh;
        interaction = freshInteraction;
        oldInteraction.Dispose();
        old.Dispose();
        Connected = true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // The question dialogs first, then the view (it lets go of the keyboard), then the session, whose renderer holds the panel's swap chain.
        interaction.Dispose();
        Terminal.Dispose();
        Session.Dispose();
    }
}
