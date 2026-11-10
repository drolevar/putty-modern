/*
 * keys.c - keyboard input: a port of PuTTY's key translation, fed from the raw message stream.
 *
 * Why it looks like this (the keyboard/IME spike, docs/spike-keyboard-ime.md):
 *
 *   - PuTTY's TranslateKey (windows/window.c) is driven by raw WM_KEYDOWN/WM_KEYUP messages and
 *     calls ToUnicodeEx itself, so it, not Windows, owns dead-key state. XAML key events cannot
 *     feed it faithfully (a handled KeyDown suppresses CharacterReceived, Alt+Space still opens
 *     the system menu, Tab has scan code 0, a Unicode key event reports virtual key 0xFF).
 *   - So a WH_GETMESSAGE hook on the engine thread sees each message before it is translated or
 *     dispatched. While a terminal has keyboard focus (the host says so with
 *     pe_set_key_target) key messages go through pe_translate_key() and become WM_NULL, which
 *     keeps TranslateMessage and XAML out of it. With no target the hook does nothing.
 *   - VK_PROCESSKEY and VK_PACKET key-downs are left alone, as in PuTTY: Windows translates them
 *     into WM_CHAR (a Unicode packet) or into IME messages.
 *
 * pe_translate_key() is PuTTY's TranslateKey (windows/window.c, MIT licence, the pinned revision
 * in external/putty) with these deliberate changes, and nothing else:
 *   - the state it keeps (compose key, Alt+keypad accumulator) lives in a pe_keyctx per session
 *     instead of in WinGuiSeat;
 *   - SendMessage(WM_VSCROLL, ...) became direct term_scroll() calls, with the mapping
 *     window.c's WM_VSCROLL handler uses;
 *   - Alt+Space no longer sends WM_SYSCOMMAND itself (it returns -1 and the message goes on to
 *     Windows, which opens the system menu), and Alt+Enter no longer flips full screen (the
 *     host decides; it still returns -1);
 *   - the Windows 9x branches, the commented-out debug output and show_mouseptr() are gone, and
 *     ToUnicodeEx is called directly.
 * Keep it that way, so a later PuTTY change can be diffed against it.
 *
 * Runs on the engine thread only.
 */

#include <string.h>

#include "putty.h"
#include "terminal/terminal.h"
#include "putty_engine.h"
#include "engine_internal.h"

static const int clips_system[] = { CLIP_SYSTEM };

/* The same mapping as window.c's WM_VSCROLL handler. */
static void scroll_terminal(pe_keyctx *ctx, int request)
{
    switch (request) {
      case SB_BOTTOM:
        term_scroll(ctx->term, -1, 0);
        break;
      case SB_TOP:
        term_scroll(ctx->term, +1, 0);
        break;
      case SB_LINEDOWN:
        term_scroll(ctx->term, 0, +1);
        break;
      case SB_LINEUP:
        term_scroll(ctx->term, 0, -1);
        break;
      case SB_PAGEDOWN:
        term_scroll(ctx->term, 0, +ctx->term->rows / 2);
        break;
      case SB_PAGEUP:
        term_scroll(ctx->term, 0, -ctx->term->rows / 2);
        break;
    }
}

/*
 * Translate a WM_(SYS)?KEY(UP|DOWN) message into a string of ASCII
 * codes. Returns number of bytes used, zero to drop the message,
 * -1 to forward the message to Windows, or another negative number
 * to indicate a NUL-terminated "special" string.
 */
int pe_translate_key(pe_keyctx *ctx, UINT message, WPARAM wParam,
                     LPARAM lParam, unsigned char *output)
{
    BYTE keystate[256];
    int scan, shift_state;
    bool left_alt = false, key_down;
    int r, i;
    unsigned char *p = output;
    int funky_type = conf_get_int(ctx->conf, CONF_funky_type);
    bool no_applic_k = conf_get_bool(ctx->conf, CONF_no_applic_k);
    bool ctrlaltkeys = conf_get_bool(ctx->conf, CONF_ctrlaltkeys);
    bool nethack_keypad = conf_get_bool(ctx->conf, CONF_nethack_keypad);
    char keypad_key = '\0';

    HKL kbd_layout = GetKeyboardLayout(0);

    r = GetKeyboardState(keystate);
    if (!r)
        memset(keystate, 0, sizeof(keystate));
    else {
        if (wParam == VK_MENU && (HIWORD(lParam) & KF_EXTENDED)) {
            keystate[VK_RMENU] = keystate[VK_MENU];
        }


        /* Nastiness with NUMLock - Shift-NUMLock is left alone though */
        if ((funky_type == FUNKY_VT400 ||
             (funky_type <= FUNKY_LINUX && ctx->term->app_keypad_keys &&
              !no_applic_k))
            && wParam == VK_NUMLOCK && !(keystate[VK_SHIFT] & 0x80)) {

            wParam = VK_EXECUTE;

            /* UnToggle NUMLock */
            if ((HIWORD(lParam) & (KF_UP | KF_REPEAT)) == 0)
                keystate[VK_NUMLOCK] ^= 1;
        }

        /* And write back the 'adjusted' state */
        SetKeyboardState(keystate);
    }

    /* Disable Auto repeat if required */
    if (ctx->term->repeat_off &&
        (HIWORD(lParam) & (KF_UP | KF_REPEAT)) == KF_REPEAT)
        return 0;

    if ((HIWORD(lParam) & KF_ALTDOWN) && (keystate[VK_RMENU] & 0x80) == 0)
        left_alt = true;

    key_down = ((HIWORD(lParam) & KF_UP) == 0);

    /* Make sure Ctrl-ALT is not the same as AltGr for ToAscii unless told. */
    if (left_alt && (keystate[VK_CONTROL] & 0x80)) {
        if (ctrlaltkeys)
            keystate[VK_MENU] = 0;
        else {
            keystate[VK_RMENU] = 0x80;
            left_alt = false;
        }
    }

    scan = (HIWORD(lParam) & (KF_UP | KF_EXTENDED | 0xFF));
    shift_state = ((keystate[VK_SHIFT] & 0x80) != 0)
        + ((keystate[VK_CONTROL] & 0x80) != 0) * 2;

    /* Note if AltGr was pressed and if it was used as a compose key */
    if (!ctx->compose_state) {
        ctx->compose_keycode = 0x100;
        if (conf_get_bool(ctx->conf, CONF_compose_key)) {
            if (wParam == VK_MENU && (HIWORD(lParam) & KF_EXTENDED))
                ctx->compose_keycode = wParam;
        }
        if (wParam == VK_APPS)
            ctx->compose_keycode = wParam;
    }

    if (wParam == ctx->compose_keycode) {
        if (ctx->compose_state == 0 &&
            (HIWORD(lParam) & (KF_UP | KF_REPEAT)) == 0)
            ctx->compose_state = 1;
        else if (ctx->compose_state == 1 && (HIWORD(lParam) & KF_UP))
            ctx->compose_state = 2;
        else
            ctx->compose_state = 0;
    } else if (ctx->compose_state == 1 && wParam != VK_CONTROL)
        ctx->compose_state = 0;

    if (ctx->compose_state > 1 && left_alt)
        ctx->compose_state = 0;

    /* Sanitize the number pad if not using a PC NumPad */
    if (left_alt || (ctx->term->app_keypad_keys && !no_applic_k
                     && funky_type != FUNKY_XTERM) ||
        funky_type == FUNKY_VT400 || nethack_keypad || ctx->compose_state) {
        if ((HIWORD(lParam) & KF_EXTENDED) == 0) {
            int nParam = 0;
            switch (wParam) {
              case VK_INSERT:
                nParam = VK_NUMPAD0;
                break;
              case VK_END:
                nParam = VK_NUMPAD1;
                break;
              case VK_DOWN:
                nParam = VK_NUMPAD2;
                break;
              case VK_NEXT:
                nParam = VK_NUMPAD3;
                break;
              case VK_LEFT:
                nParam = VK_NUMPAD4;
                break;
              case VK_CLEAR:
                nParam = VK_NUMPAD5;
                break;
              case VK_RIGHT:
                nParam = VK_NUMPAD6;
                break;
              case VK_HOME:
                nParam = VK_NUMPAD7;
                break;
              case VK_UP:
                nParam = VK_NUMPAD8;
                break;
              case VK_PRIOR:
                nParam = VK_NUMPAD9;
                break;
              case VK_DELETE:
                nParam = VK_DECIMAL;
                break;
            }
            if (nParam) {
                if (keystate[VK_NUMLOCK] & 1)
                    shift_state |= 1;
                wParam = nParam;
            }
        }
    }

    /* If a key is pressed and AltGr is not active */
    if (key_down && (keystate[VK_RMENU] & 0x80) == 0 && !ctx->compose_state) {
        /* Okay, prepare for most alts then ... */
        if (left_alt)
            *p++ = '\033';

        /* Lets see if it's a pattern we know all about ... */
        if (wParam == VK_PRIOR && shift_state == 1) {
            scroll_terminal(ctx, SB_PAGEUP);
            return 0;
        }
        if (wParam == VK_PRIOR && shift_state == 3) { /* ctrl-shift-pageup */
            scroll_terminal(ctx, SB_TOP);
            return 0;
        }
        if (wParam == VK_NEXT && shift_state == 3) { /* ctrl-shift-pagedown */
            scroll_terminal(ctx, SB_BOTTOM);
            return 0;
        }

        if (wParam == VK_PRIOR && shift_state == 2) {
            scroll_terminal(ctx, SB_LINEUP);
            return 0;
        }
        if (wParam == VK_NEXT && shift_state == 1) {
            scroll_terminal(ctx, SB_PAGEDOWN);
            return 0;
        }
        if (wParam == VK_NEXT && shift_state == 2) {
            scroll_terminal(ctx, SB_LINEDOWN);
            return 0;
        }
        if ((wParam == VK_PRIOR || wParam == VK_NEXT) && shift_state == 3) {
            term_scroll_to_selection(ctx->term, (wParam == VK_PRIOR ? 0 : 1));
            return 0;
        }
        if (wParam == VK_INSERT && shift_state == 2) {
            switch (conf_get_int(ctx->conf, CONF_ctrlshiftins)) {
              case CLIPUI_IMPLICIT:
                break;          /* no need to re-copy to CLIP_LOCAL */
              case CLIPUI_EXPLICIT:
                term_request_copy(ctx->term, clips_system,
                                  lenof(clips_system));
                break;
              default:
                break;
            }
            return 0;
        }
        if (wParam == VK_INSERT && shift_state == 1) {
            switch (conf_get_int(ctx->conf, CONF_ctrlshiftins)) {
              case CLIPUI_IMPLICIT:
                term_request_paste(ctx->term, CLIP_LOCAL);
                break;
              case CLIPUI_EXPLICIT:
                term_request_paste(ctx->term, CLIP_SYSTEM);
                break;
              default:
                break;
            }
            return 0;
        }
        if (wParam == 'C' && shift_state == 3) {
            switch (conf_get_int(ctx->conf, CONF_ctrlshiftcv)) {
              case CLIPUI_IMPLICIT:
                break;          /* no need to re-copy to CLIP_LOCAL */
              case CLIPUI_EXPLICIT:
                term_request_copy(ctx->term, clips_system,
                                  lenof(clips_system));
                break;
              default:
                break;
            }
            return 0;
        }
        if (wParam == 'V' && shift_state == 3) {
            switch (conf_get_int(ctx->conf, CONF_ctrlshiftcv)) {
              case CLIPUI_IMPLICIT:
                term_request_paste(ctx->term, CLIP_LOCAL);
                break;
              case CLIPUI_EXPLICIT:
                term_request_paste(ctx->term, CLIP_SYSTEM);
                break;
              default:
                break;
            }
            return 0;
        }
        if (left_alt && wParam == VK_F4 &&
            conf_get_bool(ctx->conf, CONF_alt_f4)) {
            return -1;
        }
        if (left_alt && wParam == VK_SPACE &&
            conf_get_bool(ctx->conf, CONF_alt_space)) {
            /* window.c sends WM_SYSCOMMAND/SC_KEYMENU here; letting the message go on does the same. */
            return -1;
        }
        if (left_alt && wParam == VK_RETURN &&
            conf_get_bool(ctx->conf, CONF_fullscreenonaltenter) &&
            (conf_get_int(ctx->conf, CONF_resize_action) != RESIZE_DISABLED)) {
            /* window.c flips full screen here; that is the host's business. */
            return -1;
        }
        /* Control-Numlock for app-keypad mode switch */
        if (wParam == VK_PAUSE && shift_state == 2) {
            ctx->term->app_keypad_keys = !ctx->term->app_keypad_keys;
            return 0;
        }

        if (wParam == VK_BACK && shift_state == 0) {    /* Backspace */
            *p++ = (conf_get_bool(ctx->conf, CONF_bksp_is_delete) ?
                    0x7F : 0x08);
            *p++ = 0;
            return -2;
        }
        if (wParam == VK_BACK && shift_state == 1) {    /* Shift Backspace */
            /* We do the opposite of what is configured */
            *p++ = (conf_get_bool(ctx->conf, CONF_bksp_is_delete) ?
                    0x08 : 0x7F);
            *p++ = 0;
            return -2;
        }
        if (wParam == VK_TAB && shift_state == 1) {     /* Shift tab */
            *p++ = 0x1B;
            *p++ = '[';
            *p++ = 'Z';
            return p - output;
        }
        if (wParam == VK_SPACE && shift_state == 2) {   /* Ctrl-Space */
            *p++ = 0;
            return p - output;
        }
        if (wParam == VK_SPACE && shift_state == 3) {   /* Ctrl-Shift-Space */
            *p++ = 160;
            return p - output;
        }
        if (wParam == VK_CANCEL && shift_state == 2) {  /* Ctrl-Break */
            if (ctx->backend)
                backend_special(ctx->backend, SS_BRK, 0);
            return 0;
        }
        if (wParam == VK_PAUSE) {      /* Break/Pause */
            *p++ = 26;
            *p++ = 0;
            return -2;
        }
        /* Control-2 to Control-8 are special */
        if (shift_state == 2 && wParam >= '2' && wParam <= '8') {
            *p++ = "\000\033\034\035\036\037\177"[wParam - '2'];
            return p - output;
        }
        if (shift_state == 2 && (wParam == 0xBD || wParam == 0xBF)) {
            *p++ = 0x1F;
            return p - output;
        }
        if (shift_state == 2 && (wParam == 0xDF || wParam == 0xDC)) {
            *p++ = 0x1C;
            return p - output;
        }
        if (shift_state == 3 && wParam == 0xDE) {
            *p++ = 0x1E;               /* Ctrl-~ == Ctrl-^ in xterm at least */
            return p - output;
        }

        switch (wParam) {
            bool consumed_alt;

          case VK_NUMPAD0: keypad_key = '0'; goto numeric_keypad;
          case VK_NUMPAD1: keypad_key = '1'; goto numeric_keypad;
          case VK_NUMPAD2: keypad_key = '2'; goto numeric_keypad;
          case VK_NUMPAD3: keypad_key = '3'; goto numeric_keypad;
          case VK_NUMPAD4: keypad_key = '4'; goto numeric_keypad;
          case VK_NUMPAD5: keypad_key = '5'; goto numeric_keypad;
          case VK_NUMPAD6: keypad_key = '6'; goto numeric_keypad;
          case VK_NUMPAD7: keypad_key = '7'; goto numeric_keypad;
          case VK_NUMPAD8: keypad_key = '8'; goto numeric_keypad;
          case VK_NUMPAD9: keypad_key = '9'; goto numeric_keypad;
          case VK_DECIMAL: keypad_key = '.'; goto numeric_keypad;
          case VK_ADD: keypad_key = '+'; goto numeric_keypad;
          case VK_SUBTRACT: keypad_key = '-'; goto numeric_keypad;
          case VK_MULTIPLY: keypad_key = '*'; goto numeric_keypad;
          case VK_DIVIDE: keypad_key = '/'; goto numeric_keypad;
          case VK_EXECUTE: keypad_key = 'G'; goto numeric_keypad;
            /* also the case for VK_RETURN below can sometimes come here */
          numeric_keypad:
            /* Left Alt overrides all numeric keypad usage to act as
             * numeric character code input */
            if (left_alt) {
                if (keypad_key >= '0' && keypad_key <= '9')
                    ctx->alt_numberpad_accumulator =
                        ctx->alt_numberpad_accumulator * 10 + keypad_key - '0';
                else
                    ctx->alt_numberpad_accumulator = 0;
                break;
            }

            {
                int nchars = format_numeric_keypad_key(
                    (char *)p, ctx->term, keypad_key,
                    shift_state & 1, shift_state & 2);
                if (!nchars) {
                    /*
                     * If we didn't get an escape sequence out of the
                     * numeric keypad key, then that must be because
                     * we're in Num Lock mode without application
                     * keypad enabled. In that situation we leave this
                     * keypress to the ToUnicode/ToAsciiEx handler
                     * below, which will translate it according to the
                     * appropriate keypad layout (e.g. so that what a
                     * Brit thinks of as keypad '.' can become ',' in
                     * the German layout).
                     *
                     * An exception is the keypad Return key: if we
                     * didn't get an escape sequence for that, we
                     * treat it like ordinary Return, taking into
                     * account Telnet special new line codes and
                     * config options.
                     */
                    if (keypad_key == '\r')
                        goto ordinary_return_key;
                    break;
                }

                p += nchars;
                return p - output;
            }

            int fkey_number;
          case VK_F1: fkey_number = 1; goto numbered_function_key;
          case VK_F2: fkey_number = 2; goto numbered_function_key;
          case VK_F3: fkey_number = 3; goto numbered_function_key;
          case VK_F4: fkey_number = 4; goto numbered_function_key;
          case VK_F5: fkey_number = 5; goto numbered_function_key;
          case VK_F6: fkey_number = 6; goto numbered_function_key;
          case VK_F7: fkey_number = 7; goto numbered_function_key;
          case VK_F8: fkey_number = 8; goto numbered_function_key;
          case VK_F9: fkey_number = 9; goto numbered_function_key;
          case VK_F10: fkey_number = 10; goto numbered_function_key;
          case VK_F11: fkey_number = 11; goto numbered_function_key;
          case VK_F12: fkey_number = 12; goto numbered_function_key;
          case VK_F13: fkey_number = 13; goto numbered_function_key;
          case VK_F14: fkey_number = 14; goto numbered_function_key;
          case VK_F15: fkey_number = 15; goto numbered_function_key;
          case VK_F16: fkey_number = 16; goto numbered_function_key;
          case VK_F17: fkey_number = 17; goto numbered_function_key;
          case VK_F18: fkey_number = 18; goto numbered_function_key;
          case VK_F19: fkey_number = 19; goto numbered_function_key;
          case VK_F20: fkey_number = 20; goto numbered_function_key;
          numbered_function_key:
            consumed_alt = false;
            p += format_function_key((char *)p, ctx->term, fkey_number,
                                     shift_state & 1, shift_state & 2,
                                     left_alt, &consumed_alt);
            if (consumed_alt) {
                /* supersedes the usual prefixing of Esc */
                p -= 1;
                memmove(output, output + 1, p - output);
            }
            return p - output;

            SmallKeypadKey sk_key;
          case VK_HOME: sk_key = SKK_HOME; goto small_keypad_key;
          case VK_END: sk_key = SKK_END; goto small_keypad_key;
          case VK_INSERT: sk_key = SKK_INSERT; goto small_keypad_key;
          case VK_DELETE: sk_key = SKK_DELETE; goto small_keypad_key;
          case VK_PRIOR: sk_key = SKK_PGUP; goto small_keypad_key;
          case VK_NEXT: sk_key = SKK_PGDN; goto small_keypad_key;
          small_keypad_key:
            /* These keys don't generate terminal input with Ctrl */
            if (shift_state & 2)
                break;

            consumed_alt = false;
            p += format_small_keypad_key((char *)p, ctx->term, sk_key,
                                         shift_state & 1, shift_state & 2,
                                         left_alt, &consumed_alt);
            if (consumed_alt) {
                /* supersedes the usual prefixing of Esc */
                p -= 1;
                memmove(output, output + 1, p - output);
            }
            return p - output;

            char xkey;
          case VK_UP: xkey = 'A'; goto arrow_key;
          case VK_DOWN: xkey = 'B'; goto arrow_key;
          case VK_RIGHT: xkey = 'C'; goto arrow_key;
          case VK_LEFT: xkey = 'D'; goto arrow_key;
          case VK_CLEAR: xkey = 'G'; goto arrow_key; /* close enough */
          arrow_key:
            consumed_alt = false;
            p += format_arrow_key((char *)p, ctx->term, xkey, shift_state & 1,
                                  shift_state & 2, left_alt, &consumed_alt);
            if (consumed_alt) {
                /* supersedes the usual prefixing of Esc */
                p -= 1;
                memmove(output, output + 1, p - output);
            }
            return p - output;

          case VK_RETURN:
            if (HIWORD(lParam) & KF_EXTENDED) {
                keypad_key = '\r';
                goto numeric_keypad;
            }
          ordinary_return_key:
            if (shift_state == 0 && ctx->term->cr_lf_return) {
                *p++ = '\r';
                *p++ = '\n';
                return p - output;
            } else {
                *p++ = 0x0D;
                *p++ = 0;
                return -2;
            }
        }
    }

    /* Okay we've done everything interesting; let windows deal with
     * the boring stuff */
    {
        bool capsOn = false;

        /* helg: clear CAPS LOCK state if caps lock switches to cyrillic */
        if (keystate[VK_CAPITAL] != 0 &&
            conf_get_bool(ctx->conf, CONF_xlat_capslockcyr)) {
            capsOn = !left_alt;
            keystate[VK_CAPITAL] = 0;
        }

        wchar_t keys_unicode[3];

        /* XXX how do we know what the max size of the keys array should
         * be is? There's indication on MS' website of an Inquire/InquireEx
         * functioning returning a KBINFO structure which tells us. */
        r = ToUnicodeEx(wParam, scan, keystate, keys_unicode,
                        lenof(keys_unicode), 0, kbd_layout);
        if (r > 0) {
            WCHAR keybuf;

            p = output;
            for (i = 0; i < r; i++) {
                wchar_t wch = keys_unicode[i];

                if (ctx->compose_state == 2 && wch >= ' ' && wch < 0x80) {
                    ctx->compose_char = wch;
                    ctx->compose_state++;
                    continue;
                }
                if (ctx->compose_state == 3 && wch >= ' ' && wch < 0x80) {
                    int nc;
                    ctx->compose_state = 0;

                    if ((nc = check_compose(ctx->compose_char, wch)) == -1) {
                        MessageBeep(MB_ICONHAND);
                        return 0;
                    }
                    keybuf = nc;
                    term_keyinputw(ctx->term, &keybuf, 1);
                    continue;
                }

                ctx->compose_state = 0;

                if (!key_down) {
                    if (ctx->alt_numberpad_accumulator) {
                        if (in_utf(ctx->term) ||
                            ctx->term->ucsdata->dbcs_screenfont) {
                            keybuf = ctx->alt_numberpad_accumulator;
                            term_keyinputw(ctx->term, &keybuf, 1);
                        } else {
                            char ch = (char) ctx->alt_numberpad_accumulator;
                            /*
                             * We need not bother about stdin
                             * backlogs here, because in GUI PuTTY
                             * we can't do anything about it
                             * anyway; there's no means of asking
                             * Windows to hold off on KEYDOWN
                             * messages. We _have_ to buffer
                             * everything we're sent.
                             */
                            term_keyinput(ctx->term, -1, &ch, 1);
                        }
                        ctx->alt_numberpad_accumulator = 0;
                    } else {
                        term_keyinputw(ctx->term, &wch, 1);
                    }
                } else {
                    if (capsOn && wch < 0x80) {
                        WCHAR cbuf[2];
                        cbuf[0] = 27;
                        cbuf[1] = xlat_uskbd2cyrllic(wch);
                        term_keyinputw(
                            ctx->term, cbuf+!left_alt, 1+!!left_alt);
                    } else {
                        WCHAR cbuf[2];
                        cbuf[0] = '\033';
                        cbuf[1] = wch;
                        term_keyinputw(
                            ctx->term, cbuf +!left_alt, 1+!!left_alt);
                    }
                }
            }

            return p - output;
        }
    }

    /*
     * ALT alone may or may not want to bring up the System menu.
     * If it's not meant to, we return 0 on presses or releases of
     * ALT, to show that we've swallowed the keystroke. Otherwise
     * we return -1, which means Windows will give the keystroke
     * its default handling (i.e. bring up the System menu).
     */
    if (wParam == VK_MENU && !conf_get_bool(ctx->conf, CONF_alt_only))
        return 0;

    return -1;
}

/* ----- The hook --------------------------------------------------------------------------- */

static HHOOK message_hook;
static pe_keyctx *key_target;       /* the terminal that has keyboard focus, or NULL */

/*
 * What window.c's window procedure does with the keyboard messages, applied to a message the
 * hook has just seen. Returns true if the message was consumed (and turned into WM_NULL).
 */
static void handle_message(pe_keyctx *ctx, MSG *m)
{
    switch (m->message) {
      case WM_KEYDOWN:
      case WM_SYSKEYDOWN:
      case WM_KEYUP:
      case WM_SYSKEYUP:
        /* The scan code and keypress timing are a good source of random noise. */
        noise_ultralight(NOISE_SOURCE_KEY, m->lParam);

        /*
         * PuTTY lets Windows translate a Unicode packet and the IME's PROCESS key itself
         * (TranslateMessage gives WM_CHAR, or the IME messages), so leave those alone.
         */
        if (m->wParam == VK_PROCESSKEY || m->wParam == VK_PACKET)
            return;

        {
            unsigned char buf[20];
            int len = pe_translate_key(ctx, m->message, m->wParam, m->lParam, buf);
            if (len == -1)
                return;         /* forward the message to Windows */
            if (len != 0)
                term_keyinput(ctx->term, -1, buf, len);
            m->message = WM_NULL;
        }
        return;

      case WM_CHAR:
      case WM_SYSCHAR:
        /* Posted by someone else, or produced for a VK_PACKET: window.c takes them as text. */
        {
            wchar_t c = (wchar_t)m->wParam;

            if (IS_HIGH_SURROGATE(c)) {
                ctx->pending_surrogate = c;
            } else if (IS_SURROGATE_PAIR(ctx->pending_surrogate, c)) {
                wchar_t pair[2];
                pair[0] = ctx->pending_surrogate;
                pair[1] = c;
                term_keyinputw(ctx->term, pair, 2);
            } else if (!IS_SURROGATE(c)) {
                term_keyinputw(ctx->term, &c, 1);
            }
        }
        m->message = WM_NULL;
        return;
    }
}

static LRESULT CALLBACK message_hook_proc(int code, WPARAM wParam, LPARAM lParam)
{
    /* PM_REMOVE: the message is about to be returned; a peek (PM_NOREMOVE) must not be handled twice. */
    if (code == HC_ACTION && wParam == PM_REMOVE && key_target)
        handle_message(key_target, (MSG *)lParam);
    return CallNextHookEx(message_hook, code, wParam, lParam);
}

PE_API int32_t pe_set_key_target(pe_session *session)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;

    if (!session) {
        key_target = NULL;
        return PE_OK;
    }

    pe_keyctx *ctx = pe_session_keyctx(session);
    if (!ctx)
        return PE_ERR_STATE;    /* not a live session, or it has no terminal */

    if (!message_hook) {
        message_hook = SetWindowsHookExW(WH_GETMESSAGE, message_hook_proc, NULL,
                                         GetCurrentThreadId());
        if (!message_hook)
            return PE_ERR_FAILED;
    }
    key_target = ctx;
    return PE_OK;
}

void pe_keys_session_closing(pe_keyctx *ctx)
{
    if (key_target == ctx)
        key_target = NULL;
}

void pe_keys_reset(void)
{
    key_target = NULL;
    if (message_hook) {
        UnhookWindowsHookEx(message_hook);
        message_hook = NULL;
    }
}
