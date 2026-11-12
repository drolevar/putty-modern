/*
 * render.h - the terminal renderer's C interface (engine-internal).
 *
 * The renderer draws a grid of character cells with Direct2D and DirectWrite. It is C++
 * (render.cpp) and knows nothing about PuTTY; the TermWin that does (session.c, plain C) drives it
 * through these functions. PuTTY's headers cannot be included from C++ (ssh.h uses `new` as a
 * member name), which is why the line is drawn here.
 *
 * Two targets, one drawing code path:
 *   - offscreen: a WIC bitmap, software rendered. For tests; needs neither a window nor a GPU.
 *   - panel: a DXGI composition swap chain plugged into a XAML SwapChainPanel. Drawing goes to a
 *     persistent canvas bitmap and is copied to the swap chain on pe_render_end, because the
 *     contents of a flip-model back buffer do not survive Present and the terminal repaints only
 *     what changed.
 *
 * All sizes are physical pixels and all positions are cells. Colours are 0x00RRGGBB. Everything
 * runs on the thread that created the renderer.
 */

#ifndef PUTTY_ENGINE_RENDER_H
#define PUTTY_ENGINE_RENDER_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct pe_renderer pe_renderer;

#define PE_STYLE_BOLD 0x01u

/* Cursor outline kinds for pe_render_cursor. */
#define PE_CURSOR_BLOCK     0
#define PE_CURSOR_UNDERLINE 1
#define PE_CURSOR_BAR       2

/* Creates a renderer that draws to a software bitmap of the given size. NULL on failure. */
pe_renderer *pe_render_create_offscreen(int32_t width_px, int32_t height_px);

/*
 * Creates a renderer that draws to a swap chain it plugs into the SwapChainPanel whose
 * ISwapChainPanelNative (Microsoft.UI.Xaml.Media.DXInterop) is behind `panel_unknown`. NULL on
 * failure.
 */
pe_renderer *pe_render_create_for_panel(void *panel_unknown, int32_t width_px, int32_t height_px);

void pe_render_destroy(pe_renderer *r);

/* Chooses the font and works out the cell size. False if the family is not installed. */
bool pe_render_set_font(pe_renderer *r, const wchar_t *family, float size_px);

void pe_render_cell_size(const pe_renderer *r, int32_t *width_px, int32_t *height_px);

/* Distance from the top of a cell to the text baseline, in pixels. */
int32_t pe_render_baseline(const pe_renderer *r);

/* Distance from the top of a cell to the text baseline, in pixels. */
int32_t pe_render_baseline(const pe_renderer *r);

/* Resizes the drawing surface; its contents are lost and the caller repaints everything. */
bool pe_render_resize(pe_renderer *r, int32_t width_px, int32_t height_px);

/* A frame is pe_render_begin, any number of draw calls, then pe_render_end, which presents. */
bool pe_render_begin(pe_renderer *r);

void pe_render_fill(pe_renderer *r, int32_t col, int32_t row, int32_t cells, uint32_t rgb);

/* One glyph (one or two UTF-16 units) spread over `cells` cells (2 for a wide character). */
void pe_render_glyph(pe_renderer *r, int32_t col, int32_t row, const wchar_t *text, int32_t units,
                     int32_t cells, uint32_t fg, uint32_t style);

void pe_render_underline(pe_renderer *r, int32_t col, int32_t row, int32_t cells, uint32_t rgb);

/* A solid rectangle in pixels, for cursor shapes, strike-through and selection. */
void pe_render_rect(pe_renderer *r, int32_t x, int32_t y, int32_t width, int32_t height, uint32_t rgb);

/* A shield with a tick in the box (the terminal's trust sigil); the box is cleared to `background` first. Vector shapes, so sharp at any size. */
void pe_render_shield(pe_renderer *r, int32_t x, int32_t y, int32_t width, int32_t height,
                      uint32_t background, uint32_t body, uint32_t mark);

/* A solid rectangle in pixels, for cursor shapes, strike-through and selection. */
void pe_render_rect(pe_renderer *r, int32_t x, int32_t y, int32_t width, int32_t height, uint32_t rgb);

/* A solid block, underline or bar for an active cursor (kind PE_CURSOR_*), or its hollow outline. */
void pe_render_cursor(pe_renderer *r, int32_t col, int32_t row, int32_t cells, int32_t kind,
                      bool hollow, uint32_t rgb);

bool pe_render_end(pe_renderer *r);

/* Offscreen renderers only: the colour of one pixel of the last finished frame. */
bool pe_render_read_pixel(pe_renderer *r, int32_t x, int32_t y, uint32_t *rgb);

#ifdef __cplusplus
}
#endif

#endif /* PUTTY_ENGINE_RENDER_H */
