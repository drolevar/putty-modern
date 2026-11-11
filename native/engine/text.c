/*
 * text.c - text in PuTTY's two encodings.
 *
 * PuTTY hands some strings over in the ANSI code page and some in UTF-8, and says which; the host side
 * is always UTF-8. Both functions return a new string the caller frees with sfree.
 */

#include "putty.h"
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
