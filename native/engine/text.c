/*
 * text.c - text in PuTTY's two encodings.
 *
 * PuTTY hands some strings over in the ANSI code page and some in UTF-8, and says which; the host side
 * is always UTF-8. Both functions return a new string the caller frees with sfree.
 */

#include "putty.h"
#include "tree234.h"
#include "putty_engine.h"
#include "engine_internal.h"

/* Text PuTTY gives us is UTF-8 only when it says so; otherwise it is in the ANSI code page. */
char *pe_text_to_utf8(const char *text, bool is_utf8)
{
    if (is_utf8 || !text)
        return dupstr(text ? text : "");
    int wide_len = MultiByteToWideChar(CP_ACP, 0, text, -1, NULL, 0);
    if (wide_len <= 0)
        return dupstr(text);
    wchar_t *wide = snewn(wide_len, wchar_t);
    MultiByteToWideChar(CP_ACP, 0, text, -1, wide, wide_len);
    int utf8_len = WideCharToMultiByte(CP_UTF8, 0, wide, -1, NULL, 0, NULL, NULL);
    char *out = snewn(utf8_len > 0 ? utf8_len : 1, char);
    if (utf8_len > 0)
        WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, utf8_len, NULL, NULL);
    else
        out[0] = '\0';
    sfree(wide);
    return out;
}

char *pe_text_from_utf8(const char *utf8, bool want_utf8)
{
    if (want_utf8 || !utf8)
        return dupstr(utf8 ? utf8 : "");
    int wide_len = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    if (wide_len <= 0)
        return dupstr(utf8);
    wchar_t *wide = snewn(wide_len, wchar_t);
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, wide, wide_len);
    int acp_len = WideCharToMultiByte(CP_ACP, 0, wide, -1, NULL, 0, NULL, NULL);
    char *out = snewn(acp_len > 0 ? acp_len : 1, char);
    if (acp_len > 0)
        WideCharToMultiByte(CP_ACP, 0, wide, -1, out, acp_len, NULL, NULL);
    else
        out[0] = '\0';
    sfree(wide);
    return out;
}

/* ----- Installed fonts (for the font chooser) ----------------------------------------------- */

struct font_enum_state {
    pe_font_fn callback;
    void *user;
    tree234 *seen;
    int count;
};

static int font_name_cmp(void *a, void *b)
{
    return strcmp((const char *)a, (const char *)b);
}

static int CALLBACK font_enum_proc(const LOGFONTW *lf, const TEXTMETRICW *tm, DWORD type, LPARAM param)
{
    (void)tm; (void)type;
    struct font_enum_state *st = (struct font_enum_state *)param;
    if (lf->lfFaceName[0] == L'@')              /* vertical writing variants */
        return 1;
    int len = WideCharToMultiByte(CP_UTF8, 0, lf->lfFaceName, -1, NULL, 0, NULL, NULL);
    if (len <= 1)
        return 1;
    char *name = snewn(len, char);
    WideCharToMultiByte(CP_UTF8, 0, lf->lfFaceName, -1, name, len, NULL, NULL);
    if (find234(st->seen, name, NULL)) {        /* the same family arrives once per script */
        sfree(name);
        return 1;
    }
    add234(st->seen, name);
    st->callback(st->user, name);
    st->count++;
    return 1;
}

PE_API int32_t pe_fonts_enumerate(pe_font_fn callback, void *user)
{
    int32_t rc = pe_check_engine_thread();
    if (rc != PE_OK)
        return rc;
    if (!callback)
        return PE_ERR_FAILED;

    struct font_enum_state st = { callback, user, newtree234(font_name_cmp), 0 };
    HDC dc = GetDC(NULL);
    LOGFONTW filter;
    memset(&filter, 0, sizeof(filter));
    filter.lfCharSet = DEFAULT_CHARSET;
    EnumFontFamiliesExW(dc, &filter, font_enum_proc, (LPARAM)&st, 0);
    ReleaseDC(NULL, dc);

    char *name;
    while ((name = (char *)index234(st.seen, 0)) != NULL) {
        delpos234(st.seen, 0);
        sfree(name);
    }
    freetree234(st.seen);
    return st.count;
}