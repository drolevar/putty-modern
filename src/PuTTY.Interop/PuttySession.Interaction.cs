using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>Optional settings for a session. Everything is optional.</summary>
public sealed class PuttySessionOptions
{
    /// <summary>The SSH user name. Without one the server asks, as a <see cref="PuttySession.PromptRequested"/>.</summary>
    public string? UserName { get; init; }

    /// <summary>A PuTTY private key (.ppk) to authenticate with.</summary>
    public string? KeyFile { get; init; }

    /// <summary>A remote command to run instead of a shell.</summary>
    public string? Command { get; init; }

    /// <summary>Whether to try Pageant; null keeps PuTTY's default (on).</summary>
    public bool? TryAgent { get; init; }

    /// <summary>
    /// Start a new session from the user's own "Default Settings" in PuTTY's storage, as PuTTY does,
    /// instead of the built-in defaults. Not for a saved session, which brings its own settings.
    /// </summary>
    public bool UseSavedDefaults { get; init; }
}

/// <summary>One question of a <see cref="SessionPromptEventArgs"/>: its text, and whether the typed answer may be shown.</summary>
public readonly record struct SessionPromptField(string Text, bool Echo);

/// <summary>
/// The server, or PuTTY itself (a key passphrase), wants answers: a user name, a password, a
/// passphrase, or the questions of keyboard-interactive authentication. Answer with
/// <see cref="Respond"/> or refuse with <see cref="Cancel"/>, from inside the handler or later:
/// a dialog takes time. A question nobody answers stalls the login, and with no handler at all it
/// is refused. Only one question is open at a time.
/// </summary>
public sealed class SessionPromptEventArgs : EventArgs
{
    private readonly PuttySession session;
    private readonly ulong id;

    internal SessionPromptEventArgs(PuttySession session, ulong id, string name, string instruction, bool fromServer,
        IReadOnlyList<SessionPromptField> fields)
    {
        this.session = session;
        this.id = id;
        Name = name;
        Instruction = instruction;
        FromServer = fromServer;
        Fields = fields;
    }

    /// <summary>A short title, possibly empty.</summary>
    public string Name { get; }

    /// <summary>A longer explanation, possibly empty and possibly several lines.</summary>
    public string Instruction { get; }

    /// <summary>True when the questions came from the server rather than from PuTTY, which a front end may want to show.</summary>
    public bool FromServer { get; }

    /// <summary>The questions. May be empty: the text is then only to be shown, and <see cref="Respond"/> with no answers is its "OK".</summary>
    public IReadOnlyList<SessionPromptField> Fields { get; }

    /// <summary>True once <see cref="Respond"/> or <see cref="Cancel"/> has delivered an answer.</summary>
    public bool IsAnswered { get; private set; }

    /// <summary>
    /// Answers, one string per field in order. Returns false if the question is no longer open (the
    /// session ended, or it was answered already). Throws if the number of answers is wrong.
    /// </summary>
    public bool Respond(params string[] answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Length != Fields.Count)
        {
            throw new ArgumentException($"The question has {Fields.Count} fields but {answers.Length} answers were given.", nameof(answers));
        }

        if (IsAnswered || !session.RespondToPrompt(id, answers))
        {
            return false;
        }

        IsAnswered = true;
        return true;
    }

    /// <summary>Refuses the question: the login is abandoned, as when a user presses Cancel. Returns false if it is no longer open.</summary>
    public bool Cancel()
    {
        if (IsAnswered || !session.CancelPrompt(id))
        {
            return false;
        }

        IsAnswered = true;
        return true;
    }
}

/// <summary>What a <see cref="SessionConfirmEventArgs"/> asks about.</summary>
public enum SessionConfirmKind
{
    /// <summary>The server's SSH host key is unknown, or has changed from the one on record.</summary>
    HostKey = 0,

    /// <summary>The connection would use a cipher or algorithm below the configured warning threshold.</summary>
    WeakAlgorithm = 1,

    /// <summary>The best host key on record is below the threshold although a stronger one is on offer.</summary>
    WeakCachedHostKey = 2,
}

/// <summary>The kind of a piece of a confirmation dialog's text.</summary>
public enum DialogTextKind
{
    Paragraph = 0,

    /// <summary>To be shown as it is, such as a fingerprint.</summary>
    Display = 1,

    /// <summary>A warning headline, as for a host key that has changed.</summary>
    ScaryHeading = 2,

    Title = 3,

    /// <summary>The question; a dialog with buttons does not need it.</summary>
    Prompt = 4,

    /// <summary>What a non-interactive run would say; not for a dialog.</summary>
    BatchAbort = 5,

    MoreInfoKey = 6,

    MoreInfoValueShort = 7,

    MoreInfoValueBlob = 8,
}

/// <summary>One piece of a confirmation dialog's text.</summary>
public readonly record struct DialogTextItem(DialogTextKind Kind, string Text);

/// <summary>
/// A question that needs a yes or no: an unknown or changed SSH host key, or a weak algorithm.
/// Answer with <see cref="Accept"/>, <see cref="ConnectOnce"/> or <see cref="Cancel"/>, from inside
/// the handler or later. With no handler the answer is no.
/// </summary>
public sealed class SessionConfirmEventArgs : EventArgs
{
    private readonly PuttySession session;
    private readonly ulong id;

    internal SessionConfirmEventArgs(PuttySession session, ulong id, SessionConfirmKind kind, string host, int port,
        string keyType, IReadOnlyList<DialogTextItem> items)
    {
        this.session = session;
        this.id = id;
        Kind = kind;
        Host = host;
        Port = port;
        KeyType = keyType;
        Items = items;
    }

    public SessionConfirmKind Kind { get; }

    /// <summary>The host of a host key question, otherwise empty.</summary>
    public string Host { get; }

    /// <summary>The port of a host key question, otherwise 0.</summary>
    public int Port { get; }

    /// <summary>The key type of a host key question (such as <c>ssh-ed25519</c>), otherwise empty.</summary>
    public string KeyType { get; }

    /// <summary>
    /// The dialog's text as PuTTY words it: for a host key, whether it is new or changed and the
    /// fingerprints, in a <see cref="DialogTextKind.Display"/> piece.
    /// </summary>
    public IReadOnlyList<DialogTextItem> Items { get; }

    /// <summary>True once an answer has been delivered.</summary>
    public bool IsAnswered { get; private set; }

    /// <summary>Yes. For a host key this also remembers it in PuTTY's host key cache. Returns false if the question is no longer open.</summary>
    public bool Accept() => Deliver(PuttySession.AnswerAccept);

    /// <summary>Yes, this time only (a host key is not remembered). Returns false if the question is no longer open.</summary>
    public bool ConnectOnce() => Deliver(PuttySession.AnswerConnectOnce);

    /// <summary>No: the connection is abandoned. Returns false if the question is no longer open.</summary>
    public bool Cancel() => Deliver(PuttySession.AnswerCancel);

    private bool Deliver(int answer)
    {
        if (IsAnswered || !session.RespondToConfirmation(id, answer))
        {
            return false;
        }

        IsAnswered = true;
        return true;
    }
}

public sealed unsafe partial class PuttySession
{
    internal const int AnswerCancel = 0;
    internal const int AnswerAccept = 1;
    internal const int AnswerConnectOnce = 2;

    /// <summary>
    /// A question for the user: a user name, a password, a passphrase, keyboard-interactive
    /// questions. Raised on the engine thread. See <see cref="SessionPromptEventArgs"/>.
    /// </summary>
    public event EventHandler<SessionPromptEventArgs>? PromptRequested;

    /// <summary>
    /// A yes or no question: an unknown or changed host key, or a weak algorithm. Raised on the
    /// engine thread. See <see cref="SessionConfirmEventArgs"/>.
    /// </summary>
    public event EventHandler<SessionConfirmEventArgs>? ConfirmationRequested;

    internal bool RespondToPrompt(ulong id, string[] answers)
    {
        if (disposed || handle == 0)
        {
            return false;
        }

        // The answers may be passwords: UTF-8 copies that are wiped as soon as the call returns.
        byte*[] copies = new byte*[answers.Length];
        try
        {
            for (int i = 0; i < answers.Length; i++)
            {
                copies[i] = (byte*)Marshal.StringToCoTaskMemUTF8(answers[i]);
            }

            fixed (byte** list = copies)
            {
                return Delivered(NativeMethods.SessionPromptRespond(handle, id, list, answers.Length), nameof(RespondToPrompt));
            }
        }
        finally
        {
            foreach (byte* copy in copies)
            {
                if (copy != null)
                {
                    Marshal.ZeroFreeCoTaskMemUTF8((nint)copy);
                }
            }
        }
    }

    internal bool CancelPrompt(ulong id) =>
        !disposed && handle != 0 && Delivered(NativeMethods.SessionPromptCancel(handle, id), nameof(CancelPrompt));

    internal bool RespondToConfirmation(ulong id, int answer) =>
        !disposed && handle != 0 && Delivered(NativeMethods.SessionConfirmRespond(handle, id, answer), nameof(RespondToConfirmation));

    /// <summary>True if the engine took the answer; false if the question was no longer open. Other failures are bugs.</summary>
    private static bool Delivered(int result, string operation)
    {
        if (result == NativeMethods.Ok)
        {
            return true;
        }

        if (result == NativeMethods.ErrState)
        {
            return false;
        }

        PuttyEngine.Check(result, operation);
        return false;
    }

    [UnmanagedCallersOnly]
    private static void OnPrompt(nint user, ulong id, byte* name, byte* instruction, int fromServer, int count, byte** prompts, int* echo)
    {
        SessionPromptEventArgs? args = null;
        try
        {
            if (From(user) is { } session)
            {
                var fields = new SessionPromptField[count];
                for (int i = 0; i < count; i++)
                {
                    fields[i] = new SessionPromptField(Marshal.PtrToStringUTF8((nint)prompts[i]) ?? string.Empty, echo[i] != 0);
                }

                args = new SessionPromptEventArgs(
                    session,
                    id,
                    Marshal.PtrToStringUTF8((nint)name) ?? string.Empty,
                    Marshal.PtrToStringUTF8((nint)instruction) ?? string.Empty,
                    fromServer != 0,
                    fields);
                if (session.PromptRequested is { } handler)
                {
                    handler(session, args);
                }
                else
                {
                    args.Cancel();      // nobody to ask: fail safe
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.PromptRequested handler threw: {ex}");
            try
            {
                args?.Cancel();
            }
            catch (Exception)
            {
                // nothing more can be done
            }
        }
    }

    [UnmanagedCallersOnly]
    private static void OnConfirm(nint user, ulong id, int kind, byte* host, int port, byte* keyType, int count, int* types, byte** texts)
    {
        SessionConfirmEventArgs? args = null;
        try
        {
            if (From(user) is { } session)
            {
                var items = new DialogTextItem[count];
                for (int i = 0; i < count; i++)
                {
                    items[i] = new DialogTextItem((DialogTextKind)types[i], Marshal.PtrToStringUTF8((nint)texts[i]) ?? string.Empty);
                }

                args = new SessionConfirmEventArgs(
                    session,
                    id,
                    (SessionConfirmKind)kind,
                    Marshal.PtrToStringUTF8((nint)host) ?? string.Empty,
                    port,
                    Marshal.PtrToStringUTF8((nint)keyType) ?? string.Empty,
                    items);
                if (session.ConfirmationRequested is { } handler)
                {
                    handler(session, args);
                }
                else
                {
                    args.Cancel();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"PuttySession.ConfirmationRequested handler threw: {ex}");
            try
            {
                args?.Cancel();
            }
            catch (Exception)
            {
                // nothing more can be done
            }
        }
    }
}
