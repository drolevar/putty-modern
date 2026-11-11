using System.Runtime.InteropServices;

namespace PuTTY.Interop;

/// <summary>
/// Raw declarations for the flat C ABI in native/engine/include/putty_engine.h.
/// Keep these in step with that header; every parameter and return type must be blittable.
/// </summary>
internal static partial class NativeMethods
{
    internal const string Library = "puttyengine";

    // Result codes from the header.
    internal const int Ok = 0;
    internal const int ErrState = -1;
    internal const int ErrThread = -2;
    internal const int ErrFailed = -3;

    [LibraryImport(Library, EntryPoint = "pe_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "pe_probe")]
    internal static partial int Probe();

    [LibraryImport(Library, EntryPoint = "pe_init")]
    internal static partial int Init();

    [LibraryImport(Library, EntryPoint = "pe_shutdown")]
    internal static partial int Shutdown();

    [LibraryImport(Library, EntryPoint = "pe_pump")]
    internal static partial int Pump(uint timeoutMilliseconds);

    [LibraryImport(Library, EntryPoint = "pe_session_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SessionOpen(
        string host, int port, int protocol, NativeSessionEvents* events, nint user, out nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_open_ex", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SessionOpenEx(
        string host, int port, int protocol, NativeSessionOptions* options, NativeSessionEvents* events,
        nint user, out nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_open_saved", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SessionOpenSaved(
        string name, NativeSessionOptions* overrides, NativeSessionEvents* events, nint user, out nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_open_like")]
    internal static unsafe partial int SessionOpenLike(nint like, NativeSessionEvents* events, nint user, out nint session);

    [LibraryImport(Library, EntryPoint = "pe_sessions_enumerate")]
    internal static unsafe partial int SessionsEnumerate(delegate* unmanaged<nint, byte*, void> callback, nint user);

    [LibraryImport(Library, EntryPoint = "pe_session_get_endpoint")]
    internal static unsafe partial int SessionGetEndpoint(nint session, NativeSessionEndpoint* endpoint);

    [LibraryImport(Library, EntryPoint = "pe_session_summary", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int SessionSummary(string name, byte* host, int hostSize, out int protocol, out int port);

    // ----- The settings dialog -----

    [LibraryImport(Library, EntryPoint = "pe_config_open")]
    internal static unsafe partial int ConfigOpen(uint flags, NativeConfigEvents* events, nint user, out nint config);

    [LibraryImport(Library, EntryPoint = "pe_config_open_for_session")]
    internal static unsafe partial int ConfigOpenForSession(nint session, NativeConfigEvents* events, nint user, out nint config);

    [LibraryImport(Library, EntryPoint = "pe_config_apply")]
    internal static partial int ConfigApply(nint config);

    [LibraryImport(Library, EntryPoint = "pe_config_describe")]
    internal static partial int ConfigDescribe(nint config);

    [LibraryImport(Library, EntryPoint = "pe_config_refresh")]
    internal static partial int ConfigRefresh(nint config);

    [LibraryImport(Library, EntryPoint = "pe_config_set_page", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ConfigSetPage(nint config, string? path);

    [LibraryImport(Library, EntryPoint = "pe_config_user_focus")]
    internal static partial int ConfigUserFocus(nint config, int id);

    [LibraryImport(Library, EntryPoint = "pe_config_user_set_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ConfigUserSetText(nint config, int id, string text);

    [LibraryImport(Library, EntryPoint = "pe_config_user_set_checked")]
    internal static partial int ConfigUserSetChecked(nint config, int id, int isChecked);

    [LibraryImport(Library, EntryPoint = "pe_config_user_set_radio")]
    internal static partial int ConfigUserSetRadio(nint config, int id, int button);

    [LibraryImport(Library, EntryPoint = "pe_config_user_select")]
    internal static partial int ConfigUserSelect(nint config, int id, int index, int selected);

    [LibraryImport(Library, EntryPoint = "pe_config_user_action")]
    internal static partial int ConfigUserAction(nint config, int id);

    [LibraryImport(Library, EntryPoint = "pe_config_user_move")]
    internal static partial int ConfigUserMove(nint config, int id, int from, int to);

    [LibraryImport(Library, EntryPoint = "pe_fonts_enumerate")]
    internal static unsafe partial int FontsEnumerate(delegate* unmanaged<nint, byte*, void> callback, nint user);

    [LibraryImport(Library, EntryPoint = "pe_config_user_set_file", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ConfigUserSetFile(nint config, int id, string path);

    [LibraryImport(Library, EntryPoint = "pe_config_user_set_font", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int ConfigUserSetFont(nint config, int id, string name, int height, int bold, int charset);

    [LibraryImport(Library, EntryPoint = "pe_config_colour_result")]
    internal static partial int ConfigColourResult(nint config, int id, int ok, int r, int g, int b);

    [LibraryImport(Library, EntryPoint = "pe_config_get_text")]
    internal static unsafe partial int ConfigGetText(nint config, int id, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "pe_config_get_checked")]
    internal static partial int ConfigGetChecked(nint config, int id);

    [LibraryImport(Library, EntryPoint = "pe_config_get_radio")]
    internal static partial int ConfigGetRadio(nint config, int id);

    [LibraryImport(Library, EntryPoint = "pe_config_list_count")]
    internal static partial int ConfigListCount(nint config, int id);

    [LibraryImport(Library, EntryPoint = "pe_config_list_item")]
    internal static unsafe partial int ConfigListItem(nint config, int id, int index, byte* buffer, int capacity, int* itemId, int* selected);

    [LibraryImport(Library, EntryPoint = "pe_config_get_file")]
    internal static unsafe partial int ConfigGetFile(nint config, int id, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "pe_config_get_font")]
    internal static unsafe partial int ConfigGetFont(nint config, int id, byte* name, int capacity, int* height, int* bold, int* charset);

    [LibraryImport(Library, EntryPoint = "pe_config_start_session")]
    internal static unsafe partial int ConfigStartSession(nint config, NativeSessionEvents* events, nint user, out nint session);

    [LibraryImport(Library, EntryPoint = "pe_config_close")]
    internal static partial int ConfigClose(nint config);

    [LibraryImport(Library, EntryPoint = "pe_session_prompt_respond")]
    internal static unsafe partial int SessionPromptRespond(nint session, ulong id, byte** answers, int count);

    [LibraryImport(Library, EntryPoint = "pe_session_prompt_cancel")]
    internal static partial int SessionPromptCancel(nint session, ulong id);

    [LibraryImport(Library, EntryPoint = "pe_session_confirm_respond")]
    internal static partial int SessionConfirmRespond(nint session, ulong id, int answer);

    [LibraryImport(Library, EntryPoint = "pe_session_send")]
    internal static unsafe partial int SessionSend(nint session, byte* data, uint length);

    [LibraryImport(Library, EntryPoint = "pe_session_is_connected")]
    internal static partial int SessionIsConnected(nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_close")]
    internal static partial int SessionClose(nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_enable_terminal")]
    internal static partial int SessionEnableTerminal(nint session, int columns, int rows);

    [LibraryImport(Library, EntryPoint = "pe_session_set_terminal_events")]
    internal static unsafe partial int SessionSetTerminalEvents(nint session, NativeTerminalEvents* events);

    [LibraryImport(Library, EntryPoint = "pe_session_attach_offscreen_view", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SessionAttachOffscreenView(nint session, int widthPx, int heightPx, string fontFamily, float sizePx);

    [LibraryImport(Library, EntryPoint = "pe_session_attach_panel_view", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SessionAttachPanelView(nint session, nint panel, int widthPx, int heightPx, string fontFamily, float sizePx);

    [LibraryImport(Library, EntryPoint = "pe_session_resize_view")]
    internal static partial int SessionResizeView(nint session, int widthPx, int heightPx);

    [LibraryImport(Library, EntryPoint = "pe_session_set_font", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SessionSetFont(nint session, string fontFamily, float sizePx);

    [LibraryImport(Library, EntryPoint = "pe_session_disconnect")]
    internal static partial int SessionDisconnect(nint session);

    [LibraryImport(Library, EntryPoint = "pe_session_get_font")]
    internal static unsafe partial int SessionGetFont(nint session, byte* name, int capacity, int* height, int* bold);

    [LibraryImport(Library, EntryPoint = "pe_session_terminal_geometry")]
    internal static partial int SessionTerminalGeometry(nint session, out NativeTerminalGeometry geometry);

    [LibraryImport(Library, EntryPoint = "pe_diag_session_read_pixel")]
    internal static partial int DiagSessionReadPixel(nint session, int x, int y, out uint rgb);

    [LibraryImport(Library, EntryPoint = "pe_session_mouse")]
    internal static partial int SessionMouse(nint session, int kind, int button, int xPx, int yPx, int modifiers, uint timeMs);

    [LibraryImport(Library, EntryPoint = "pe_session_wheel")]
    internal static partial int SessionWheel(nint session, int delta, int horizontal, int xPx, int yPx, int modifiers);

    [LibraryImport(Library, EntryPoint = "pe_session_scroll")]
    internal static partial int SessionScroll(nint session, int mode, int amount);

    [LibraryImport(Library, EntryPoint = "pe_session_type_text")]
    internal static unsafe partial int SessionTypeText(nint session, char* text, int units);

    [LibraryImport(Library, EntryPoint = "pe_session_paste")]
    internal static unsafe partial int SessionPaste(nint session, char* text, int units);

    [LibraryImport(Library, EntryPoint = "pe_session_set_local_echo_edit")]
    internal static partial int SessionSetLocalEchoEdit(nint session, int echo, int edit);

    [LibraryImport(Library, EntryPoint = "pe_set_key_target")]
    internal static partial int SetKeyTarget(nint session);

    [LibraryImport(Library, EntryPoint = "pe_set_warning_handler")]
    internal static unsafe partial int SetWarningHandler(delegate* unmanaged<nint, int, byte*, void> handler, nint user);

    [LibraryImport(Library, EntryPoint = "pe_diag_raise_warning")]
    internal static partial int DiagRaiseWarning(int kind);

    [LibraryImport(Library, EntryPoint = "pe_diag_generate_ed25519_key", StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial int DiagGenerateEd25519Key(string path, string? passphrase, byte* publicLine, int capacity);

    [LibraryImport(Library, EntryPoint = "pe_diag_render_text", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DiagRenderText(
        string fontFamily, float sizePx, string text, uint fgRgb, uint bgRgb, int bold, out NativeDiagRenderResult result);

    [LibraryImport(Library, EntryPoint = "pe_diag_queue_callback")]
    internal static partial int DiagQueueCallback();

    [LibraryImport(Library, EntryPoint = "pe_diag_schedule_timer")]
    internal static partial int DiagScheduleTimer(uint delayMilliseconds);

    [LibraryImport(Library, EntryPoint = "pe_diag_get_counters")]
    internal static partial int DiagGetCounters(out NativeDiagCounters counters);
}

/// <summary>
/// Mirrors <c>pe_session_events</c>: function pointers the engine calls on its thread. Each
/// first argument is the <c>user</c> pointer given to <c>pe_session_open</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSessionEvents
{
    public delegate* unmanaged<nint, void*, uint, int, void> Output;   // user, data, length, isStderr
    public delegate* unmanaged<nint, byte*, void> Log;                 // user, utf8 text
    public delegate* unmanaged<nint, byte*, int, void> Error;          // user, utf8 message, isFatal
    public delegate* unmanaged<nint, void> Closed;                     // user
    public delegate* unmanaged<nint, ulong, byte*, byte*, int, int, byte**, int*, void> Prompt;   // user, id, name, instruction, fromServer, count, prompts, echo
    public delegate* unmanaged<nint, ulong, int, byte*, int, byte*, int, int*, byte**, void> Confirm;   // user, id, kind, host, port, keyType, count, types, texts
}

/// <summary>Mirrors <c>pe_ctrl_info</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeCtrlInfo
{
    public int Id;
    public int Type;
    public int Flags;
    public int Group;
    public int ColumnStart;
    public int ColumnSpan;
    public int PercentWidth;
    public int ListHeight;
    public int MultiSel;
    public int NColumns;
    public int* Percentages;
    public int NButtons;
    public byte** Buttons;
    public byte* Shortcuts;
    public int Shortcut;
    public byte* Label;
    public byte* Title;
    public int Filter;
    public int AlignNextTo;
}

/// <summary>Mirrors <c>pe_config_events</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeConfigEvents
{
    public delegate* unmanaged<nint, int, byte*, byte*, byte*, void> Group;     // user, index, path, name, title
    public delegate* unmanaged<nint, NativeCtrlInfo*, void> Control;
    public delegate* unmanaged<nint, int, void> Changed;                         // user, id
    public delegate* unmanaged<nint, int, byte*, void> LabelChanged;             // user, id, text
    public delegate* unmanaged<nint, int, void> Update;                          // user, begin
    public delegate* unmanaged<nint, byte*, int, void> Message;                  // user, text, isError
    public delegate* unmanaged<nint, void> Beep;
    public delegate* unmanaged<nint, int, void> Focus;
    public delegate* unmanaged<nint, int, int, int, int, void> ColourRequest;    // user, id, r, g, b
    public delegate* unmanaged<nint, int, void> End;                             // user, value
}

/// <summary>Mirrors <c>pe_session_options</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSessionOptions
{
    public uint Size;
    public byte* UserName;
    public byte* KeyFile;
    public byte* Command;
    public int TryAgent;                                               // -1 default, 0 off, 1 on
    public uint Flags;                                                 // 1 = start from the saved Default Settings
}

/// <summary>Mirrors <c>pe_session_endpoint</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeSessionEndpoint
{
    public int Size;
    public int Protocol;
    public int Port;
    public fixed byte Host[256];
    public fixed byte UserName[128];
    public fixed byte KeyFile[512];
}

/// <summary>Mirrors <c>pe_terminal_events</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTerminalEvents
{
    public delegate* unmanaged<nint, byte*, void> Title;               // user, utf8 title
    public delegate* unmanaged<nint, int, void> Bell;                  // user, bell indicator setting
    public delegate* unmanaged<nint, int, int, int, void> Scrollbar;   // user, total, start, page
    public delegate* unmanaged<nint, char*, int, void> ClipboardWrite; // user, utf16 text, units
    public delegate* unmanaged<nint, void> ClipboardRequestPaste;      // user
    public delegate* unmanaged<nint, int, int, int, int, void> Cursor; // user, x, y, width, height
}

/// <summary>Mirrors <c>pe_terminal_geometry</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeTerminalGeometry
{
    public int Columns;
    public int Rows;
    public int CellWidth;
    public int CellHeight;
}

/// <summary>Mirrors <c>pe_diag_render_result</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDiagRenderResult
{
    public int CellWidth;
    public int CellHeight;
    public int Cells;
    public fixed int Ink[64];
    public uint CornerRgb;
}

/// <summary>Mirrors <c>pe_diag_counters</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDiagCounters
{
    public uint CallbacksRun;
    public uint TimersFired;
}
