/*
 * termwin.c - the TermWin the emulator draws through, onto the renderer (render.h).
 *
 * PuTTY's terminal emulator draws and reports through a vtable of 24 methods (TermWin). The Win32
 * implementation is in windows/window.c (do_text_internal, wintw_draw_cursor and friends); this
 * is the same logic with GDI replaced by the renderer's cell calls. The attribute handling (which
 * colour index a cell uses, reverse video, bold as a brighter colour, the cursor colours 260 and
 * 261, wide characters, combining characters) is copied from there, so the colours come out as
 * PuTTY's do.
 *
 * With no renderer attached the TermWin still answers everything and draws nothing
 * (setup_draw_ctx returns false, so the emulator skips painting): that is a headless session.
 *
 * Not done yet: double-width and double-height lines are drawn at normal size over twice the
 * cells, clipboard and mouse mode are accepted and ignored (slice 2d), and the system bell,
 * minimise/maximise/move/z-order requests are for the host, which is told only about the title,
 * the bell and the scrollbar.
 */

#include <string.h>

#include "putty.h"
#include "terminal/terminal.h"
#include "putty_engine.h"
#include "engine_internal.h"
#include "render.h"

#define TW(p) container_of(p, pe_termwin, tw)

static uint32_t pack_rgb(const rgb *c)
{
    return ((uint32_t)c->r << 16) | ((uint32_t)c->g << 8) | (uint32_t)c->b;
}

static uint32_t pack_optional(const optionalrgb *c)
{
    return ((uint32_t)c->r << 16) | ((uint32_t)c->g << 8) | (uint32_t)c->b;
}

/* ----- Drawing --------------------------------------------------------------------------- */

static bool tw_setup_draw_ctx(TermWin *tw)
{
    pe_termwin *w = TW(tw);
    if (!w->renderer || !pe_render_begin(w->renderer))
        return false;
    w->drawing = true;
    return true;
}

static void tw_free_draw_ctx(TermWin *tw)
{
    pe_termwin *w = TW(tw);
    if (w->renderer && w->drawing)
        pe_render_end(w->renderer);
    w->drawing = false;
}

/*
 * The emulator hands over characters in the form window.c's GDI drawing wants: where the screen
 * font's code page has the character, init_ucs() replaced it with a "direct to font" code
 * (CSET_ACP or CSET_OEMCP plus the font's index). We draw Unicode, so turn those back into the
 * character they stand for.
 */
static wchar_t resolve_direct_font(pe_termwin *w, wchar_t c)
{
    if (!DIRECT_FONT(c))
        return c;
    struct unicode_data *u = w->term->ucsdata;
    bool oem = (c & CSET_MASK) == CSET_OEMCP;
    unsigned char index = (unsigned char)(c & 0xFF);
    wchar_t resolved = oem ? u->unitab_oemcp[index] : u->unitab_font[index];
    if (DIRECT_FONT(resolved)) {
        /* The table itself says "the screen font's own code": the ANSI or OEM code page maps it. */
        wchar_t wide = 0;
        char byte = (char)index;
        if (MultiByteToWideChar(oem ? CP_OEMCP : CP_ACP, 0, &byte, 1, &wide, 1) == 1)
            return wide;
        return 0xFFFD;
    }
    return resolved;
}

/* A character that is part of a variation selector sequence: drawn with its base, no cell of its own. */
static bool is_variation_selector(wchar_t c)
{
    return c >= 0xFE00 && c <= 0xFE0F;
}

static void draw_text_run(pe_termwin *w, int x, int y, wchar_t *text, int len,
                          unsigned long attr, int lattr, truecolour tc)
{
    pe_renderer *r = w->renderer;
    if (!r || !w->drawing || len < 1)
        return;

    lattr &= LATTR_MODE;
    int cells_per_char = (attr & ATTR_WIDE) ? 2 : 1;
    if (lattr != LATTR_NORM) {
        /* Only the left half of a double-width line is drawn; its glyphs get twice the cells. */
        if (x * 2 >= w->term->cols)
            return;
        x *= 2;
        cells_per_char *= 2;
    }

    bool is_cursor = false;
    if ((attr & ATTR_ACTCURS) && (w->cursor_type == CURSOR_BLOCK || w->term->big_cursor)) {
        tc.fg = tc.bg = optionalrgb_none;
        attr &= ~(ATTR_REVERSE | ATTR_BLINK | ATTR_COLOURS | ATTR_DIM);
        /* the cursor's own foreground and background */
        attr |= (260 << ATTR_FGSHIFT) | (261 << ATTR_BGSHIFT);
        is_cursor = true;
    }

    /* Anything left as an original character set is unprintable. */
    if (DIRECT_CHAR(text[0]) && (len < 2 || !IS_SURROGATE_PAIR(text[0], text[1]))) {
        for (int i = 0; i < len; i++)
            text[i] = 0xFFFD;
    }

    for (int i = 0; i < len; i++)
        text[i] = resolve_direct_font(w, text[i]);

    int nfg = (int)((attr & ATTR_FGMASK) >> ATTR_FGSHIFT);
    int nbg = (int)((attr & ATTR_BGMASK) >> ATTR_BGSHIFT);
    if (attr & ATTR_REVERSE) {
        int t = nfg;
        nfg = nbg;
        nbg = t;
        optionalrgb trgb = tc.fg;
        tc.fg = tc.bg;
        tc.bg = trgb;
    }
    if (w->bold_colours && (attr & ATTR_BOLD) && !is_cursor) {
        if (nfg < 16)
            nfg |= 8;
        else if (nfg >= 256)
            nfg |= 1;
    }
    if (w->bold_colours && (attr & ATTR_BLINK)) {
        if (nbg < 16)
            nbg |= 8;
        else if (nbg >= 256)
            nbg |= 1;
    }

    uint32_t fg = tc.fg.enabled ? pack_optional(&tc.fg) : w->colours[nfg];
    uint32_t bg = tc.bg.enabled ? pack_optional(&tc.bg) : w->colours[nbg];
    if (attr & ATTR_DIM) {
        uint32_t fr = ((fg >> 16) & 0xFF) * 2 / 3;
        uint32_t fgc = ((fg >> 8) & 0xFF) * 2 / 3;
        uint32_t fb = (fg & 0xFF) * 2 / 3;
        fg = (fr << 16) | (fgc << 8) | fb;
    }

    /* How many characters, hence cells: a surrogate pair is one, a variation selector none. */
    int nchars = 0;
    for (int i = 0; i < len; i++) {
        if (text[i] == UCSWIDE || is_variation_selector(text[i]))
            continue;
        if (i + 1 < len && IS_SURROGATE_PAIR(text[i], text[i + 1]))
            i++;
        nchars++;
    }
    int ncells = nchars * cells_per_char;

    /* A combining character is drawn over what is already there, so it must not erase it. */
    if (!(attr & TATTR_COMBINING))
        pe_render_fill(r, x, y, ncells, bg);

    uint32_t style = (w->bold_font && (attr & ATTR_BOLD)) ? PE_STYLE_BOLD : 0;
    int col = x;
    for (int i = 0; i < len; i++) {
        if (text[i] == UCSWIDE || is_variation_selector(text[i]))
            continue;
        int units = 1;
        if (i + 1 < len && IS_SURROGATE_PAIR(text[i], text[i + 1]))
            units = 2;
        if (text[i] != L' ')
            pe_render_glyph(r, col, y, &text[i], units, cells_per_char, fg, style);
        col += cells_per_char;
        i += units - 1;
    }

    if (attr & ATTR_UNDER)
        pe_render_underline(r, x, y, ncells, fg);
    if (attr & ATTR_STRIKE) {
        int32_t cw = 0, ch = 0;
        pe_render_cell_size(r, &cw, &ch);
        pe_render_rect(r, x * cw, y * ch + ch / 2, ncells * cw, 1, fg);
    }
}

static void tw_draw_text(TermWin *tw, int x, int y, wchar_t *text, int len,
                         unsigned long attr, int lattr, truecolour tc)
{
    pe_termwin *w = TW(tw);

    if (attr & TATTR_COMBINING) {
        /* A run of combining characters: the first (with any variation selector) in place, the
         * rest overlaid on it, one at a time. */
        unsigned long a = 0;
        int len0 = 1;
        if (len >= 2 && IS_SURROGATE_PAIR(text[0], text[1]))
            len0 = 2;
        if (len - len0 >= 1 && is_variation_selector(text[len0])) {
            attr &= ~TATTR_COMBINING;
            draw_text_run(w, x, y, text, len0 + 1, attr, lattr, tc);
            text += len0 + 1;
            len -= len0 + 1;
            a = TATTR_COMBINING;
        } else {
            attr &= ~TATTR_COMBINING;
        }

        while (len > 0) {
            int n = (len >= 2 && IS_SURROGATE_PAIR(text[0], text[1])) ? 2 : 1;
            draw_text_run(w, x, y, text, n, attr | a, lattr, tc);
            text += n;
            len -= n;
            a = TATTR_COMBINING;
        }
    } else {
        draw_text_run(w, x, y, text, len, attr, lattr, tc);
    }
}

static void tw_draw_cursor(TermWin *tw, int x, int y, wchar_t *text, int len,
                           unsigned long attr, int lattr, truecolour tc)
{
    pe_termwin *w = TW(tw);
    pe_renderer *r = w->renderer;
    int ctype = w->cursor_type;

    lattr &= LATTR_MODE;

    if ((attr & ATTR_ACTCURS) && (ctype == CURSOR_BLOCK || w->term->big_cursor)) {
        if (*text != UCSWIDE) {
            tw_draw_text(tw, x, y, text, len, attr, lattr, tc);
            return;
        }
        ctype = CURSOR_VERTICAL_LINE;
        attr |= ATTR_RIGHTCURS;
    }
    if (!r || !w->drawing)
        return;

    int32_t cw = 0, ch = 0;
    pe_render_cell_size(r, &cw, &ch);
    int fnt_width = cw * (1 + (lattr != LATTR_NORM));
    int char_width = fnt_width * ((attr & ATTR_WIDE) ? 2 : 1);
    int px = x * fnt_width;
    int py = y * ch;
    uint32_t colour = w->colours[261];

    if ((attr & ATTR_PASCURS) && (ctype == CURSOR_BLOCK || w->term->big_cursor)) {
        /* the box outline of the cursor in a window that is not focused */
        pe_render_rect(r, px, py, char_width, 1, colour);
        pe_render_rect(r, px, py + ch - 1, char_width, 1, colour);
        pe_render_rect(r, px, py, 1, ch, colour);
        pe_render_rect(r, px + char_width - 1, py, 1, ch, colour);
    } else if ((attr & (ATTR_ACTCURS | ATTR_PASCURS)) && ctype != CURSOR_BLOCK) {
        int startx, starty, length;
        bool vertical = ctype != CURSOR_UNDERLINE;
        if (!vertical) {
            startx = px;
            starty = py + pe_render_baseline(r) + 1;
            if (starty >= py + ch)
                starty = py + ch - 1;
            length = char_width;
        } else {
            startx = px + ((attr & ATTR_RIGHTCURS) ? char_width - 1 : 0);
            starty = py;
            length = ch;
        }
        if (attr & ATTR_ACTCURS) {
            if (vertical)
                pe_render_rect(r, startx, starty, 1, length, colour);
            else
                pe_render_rect(r, startx, starty, length, 1, colour);
        } else {
            /* the same shape dotted, in a window that is not focused */
            for (int i = 0; i < length; i += 2) {
                if (vertical)
                    pe_render_rect(r, startx, starty + i, 1, 1, colour);
                else
                    pe_render_rect(r, startx + i, starty, 1, 1, colour);
            }
        }
    }
}

static void tw_draw_trust_sigil(TermWin *tw, int x, int y)
{
    /* PuTTY draws an icon; the host shows trust some other way. A solid block marks the cells. */
    pe_termwin *w = TW(tw);
    if (!w->renderer || !w->drawing)
        return;
    int32_t cw = 0, ch = 0;
    pe_render_cell_size(w->renderer, &cw, &ch);
    pe_render_rect(w->renderer, x * cw, y * ch, cw * 2, ch, w->colours[256]);
}

/*
 * Width in cells of a character, which the emulator asks about characters it cannot be sure of.
 * East Asian wide and fullwidth characters and the emoji blocks take two cells.
 */
static int tw_char_width(TermWin *tw, int uc)
{
    (void)tw;
    if (uc < 0x1100)
        return 1;
    if ((uc >= 0x1100 && uc <= 0x115F) || (uc >= 0x2E80 && uc <= 0xA4CF && uc != 0x303F) ||
        (uc >= 0xAC00 && uc <= 0xD7A3) || (uc >= 0xF900 && uc <= 0xFAFF) ||
        (uc >= 0xFE30 && uc <= 0xFE6F) || (uc >= 0xFF00 && uc <= 0xFF60) ||
        (uc >= 0xFFE0 && uc <= 0xFFE6) || (uc >= 0x1F300 && uc <= 0x1F64F) ||
        (uc >= 0x1F900 && uc <= 0x1F9FF) || (uc >= 0x20000 && uc <= 0x3FFFD))
        return 2;
    return 1;
}

/* ----- Reports to the host and bookkeeping ------------------------------------------------ */

static void tw_set_cursor_pos(TermWin *tw, int x, int y)
{
    pe_termwin *w = TW(tw);
    w->cursor_x = x;
    w->cursor_y = y;
}

static void tw_set_raw_mouse_mode(TermWin *tw, bool enable)
{
    TW(tw)->raw_mouse_mode = enable;
}

static void tw_set_raw_mouse_mode_pointer(TermWin *tw, bool enable)
{
    (void)tw;
    (void)enable;
}

static void tw_set_scrollbar(TermWin *tw, int total, int start, int page)
{
    pe_termwin *w = TW(tw);
    w->sb_total = total;
    w->sb_start = start;
    w->sb_page = page;
    pe_session_notify_scrollbar(w->owner, total, start, page);
}

static void tw_bell(TermWin *tw, int mode)
{
    pe_termwin *w = TW(tw);
    if (mode != BELL_DISABLED)
        pe_session_notify_bell(w->owner);
}

/*
 * Only the system clipboard is the host's business (CLIP_LOCAL is the emulator's own selection
 * buffer). window.c also puts the text on the clipboard in other formats and tracks ownership; the
 * host here gets the plain text and decides.
 */
static void tw_clip_write(TermWin *tw, int clipboard, wchar_t *text, int *attrs,
                          truecolour *colours, int len, bool must_deselect)
{
    (void)attrs; (void)colours; (void)must_deselect;
    /* The emulator includes the terminating NUL that the Windows clipboard format wants; the host gets just the text. */
    while (len > 0 && text[len - 1] == L'\0')
        len--;
    if (clipboard == CLIP_SYSTEM && len > 0)
        pe_session_notify_clipboard_write(TW(tw)->owner, text, len);
}

static void tw_clip_request_paste(TermWin *tw, int clipboard)
{
    if (clipboard == CLIP_SYSTEM)
        pe_session_notify_paste_request(TW(tw)->owner);
}

static void tw_refresh(TermWin *tw)
{
    pe_termwin *w = TW(tw);
    pe_session_schedule_repaint(w->owner);
}

static void tw_request_resize(TermWin *tw, int width, int height)
{
    (void)width; (void)height;
    pe_termwin *w = TW(tw);
    /* The host owns the size of the view; decline, which the protocol allows. */
    if (w->term)
        term_resize_request_completed(w->term);
}

static void tw_set_title(TermWin *tw, const char *title, int codepage)
{
    pe_termwin *w = TW(tw);
    if (codepage == CP_UTF8) {
        pe_session_notify_title(w->owner, title);
        return;
    }
    /* Convert from the title's code page to UTF-8 for the host. */
    int wide_len = MultiByteToWideChar(codepage, 0, title, -1, NULL, 0);
    if (wide_len <= 0) {
        pe_session_notify_title(w->owner, title);
        return;
    }
    wchar_t *wide = snewn(wide_len, wchar_t);
    MultiByteToWideChar(codepage, 0, title, -1, wide, wide_len);
    int utf8_len = WideCharToMultiByte(CP_UTF8, 0, wide, -1, NULL, 0, NULL, NULL);
    if (utf8_len > 0) {
        char *utf8 = snewn(utf8_len, char);
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, utf8, utf8_len, NULL, NULL);
        pe_session_notify_title(w->owner, utf8);
        sfree(utf8);
    }
    sfree(wide);
}

static void tw_set_icon_title(TermWin *tw, const char *icontitle, int codepage)
{
    (void)tw; (void)icontitle; (void)codepage;
}

static void tw_set_minimised(TermWin *tw, bool minimised) { (void)tw; (void)minimised; }
static void tw_set_maximised(TermWin *tw, bool maximised) { (void)tw; (void)maximised; }
static void tw_move(TermWin *tw, int x, int y) { (void)tw; (void)x; (void)y; }
static void tw_set_zorder(TermWin *tw, bool top) { (void)tw; (void)top; }

static void tw_palette_set(TermWin *tw, unsigned start, unsigned ncolours, const rgb *colours)
{
    pe_termwin *w = TW(tw);
    for (unsigned i = 0; i < ncolours && start + i < OSC4_NCOLOURS; i++)
        w->colours[start + i] = pack_rgb(&colours[i]);
}

static void tw_palette_get_overrides(TermWin *tw, Terminal *term)
{
    /* PuTTY's Windows front end offers the system colours here; we have no overrides. */
    (void)tw; (void)term;
}

static void tw_unthrottle(TermWin *tw, size_t bufsize)
{
    pe_termwin *w = TW(tw);
    pe_session_unthrottle(w->owner, bufsize);
}

static const TermWinVtable engine_termwin_vt = {
    .setup_draw_ctx = tw_setup_draw_ctx,
    .draw_text = tw_draw_text,
    .draw_cursor = tw_draw_cursor,
    .draw_trust_sigil = tw_draw_trust_sigil,
    .char_width = tw_char_width,
    .free_draw_ctx = tw_free_draw_ctx,
    .set_cursor_pos = tw_set_cursor_pos,
    .set_raw_mouse_mode = tw_set_raw_mouse_mode,
    .set_raw_mouse_mode_pointer = tw_set_raw_mouse_mode_pointer,
    .set_scrollbar = tw_set_scrollbar,
    .bell = tw_bell,
    .clip_write = tw_clip_write,
    .clip_request_paste = tw_clip_request_paste,
    .refresh = tw_refresh,
    .request_resize = tw_request_resize,
    .set_title = tw_set_title,
    .set_icon_title = tw_set_icon_title,
    .set_minimised = tw_set_minimised,
    .set_maximised = tw_set_maximised,
    .move = tw_move,
    .set_zorder = tw_set_zorder,
    .palette_set = tw_palette_set,
    .palette_get_overrides = tw_palette_get_overrides,
    .unthrottle = tw_unthrottle,
};

/* ----- Life cycle ------------------------------------------------------------------------ */

void pe_termwin_init(pe_termwin *w, void *owner)
{
    memset(w, 0, sizeof(*w));
    w->tw.vt = &engine_termwin_vt;
    w->owner = owner;
    w->cursor_type = CURSOR_BLOCK;
    /* Until the emulator sets the palette, plain white on black. */
    for (int i = 0; i < OSC4_NCOLOURS; i++)
        w->colours[i] = 0xBBBBBB;
    w->colours[258] = 0x000000;
}

void pe_termwin_use_settings(pe_termwin *w, Terminal *term, Conf *conf)
{
    w->term = term;
    int bold_style = conf_get_int(conf, CONF_bold_style);
    w->bold_font = (bold_style & BOLD_STYLE_FONT) != 0;
    w->bold_colours = (bold_style & BOLD_STYLE_COLOUR) != 0;
    w->cursor_type = conf_get_int(conf, CONF_cursor_type);

    /* Which clipboards a selection goes to and which one a mouse paste reads (window.c: setup_clipboards). */
    term->n_mouse_select_clipboards = 1;
    if (conf_get_bool(conf, CONF_mouseautocopy))
        term->mouse_select_clipboards[term->n_mouse_select_clipboards++] = CLIP_SYSTEM;
    switch (conf_get_int(conf, CONF_mousepaste)) {
      case CLIPUI_IMPLICIT:
        term->mouse_paste_clipboard = CLIP_LOCAL;
        break;
      case CLIPUI_EXPLICIT:
        term->mouse_paste_clipboard = CLIP_SYSTEM;
        break;
      default:
        term->mouse_paste_clipboard = CLIP_NULL;
        break;
    }
}

void pe_termwin_set_renderer(pe_termwin *w, pe_renderer *renderer)
{
    if (w->renderer)
        pe_render_destroy(w->renderer);
    w->renderer = renderer;
    w->drawing = false;
}

void pe_termwin_destroy(pe_termwin *w)
{
    pe_termwin_set_renderer(w, NULL);
    w->term = NULL;
}
