/*
 * boxdraw.h - the terminal's line-drawing and block characters, drawn by the renderer itself.
 *
 * A font draws U+2500..U+259F as glyphs that span its own ascent and descent, while a terminal cell is a whole number of
 * pixels (render.cpp rounds the font's line height up), so with some fonts and sizes a column of vertical bars has a
 * one-pixel gap at each row boundary and a row of horizontal bars has one at each column boundary. Terminals that draw
 * these characters themselves (Windows Terminal, kitty, WezTerm) do not have it. Here every character of the two blocks is
 * built from rectangles that run to the cell's edges, so neighbouring cells always meet, whatever the font.
 *
 * Box drawing (U+2500..U+257F): each character is up to four segments (left, right, up, down), each none, light, heavy or
 * double; the table below was generated from the Unicode character names. Dashed lines are drawn solid, arcs as square
 * corners (they are rare in terminal programs). Block elements (U+2580..U+259F) are fractions of the cell; the three shades
 * are the foreground colour at 25, 50 and 75 percent over the background already drawn.
 */

#ifndef PUTTY_ENGINE_BOXDRAW_H
#define PUTTY_ENGINE_BOXDRAW_H

#include <d2d1.h>

#include <algorithm>
#include <cmath>
#include <cstdint>

namespace boxdraw {

/* Segments of U+2500 + index: left | right << 2 | up << 4 | down << 6, each 0 none, 1 light, 2 heavy, 3 double. 0 for the diagonals. */
static const uint8_t kSegments[128] = {
    0x05,  /* U+2500 Light Horizontal */
    0x0A,  /* U+2501 Heavy Horizontal */
    0x50,  /* U+2502 Light Vertical */
    0xA0,  /* U+2503 Heavy Vertical */
    0x05,  /* U+2504 Light Triple Dash Horizontal */
    0x0A,  /* U+2505 Heavy Triple Dash Horizontal */
    0x50,  /* U+2506 Light Triple Dash Vertical */
    0xA0,  /* U+2507 Heavy Triple Dash Vertical */
    0x05,  /* U+2508 Light Quadruple Dash Horizontal */
    0x0A,  /* U+2509 Heavy Quadruple Dash Horizontal */
    0x50,  /* U+250A Light Quadruple Dash Vertical */
    0xA0,  /* U+250B Heavy Quadruple Dash Vertical */
    0x44,  /* U+250C Light Down And Right */
    0x48,  /* U+250D Down Light And Right Heavy */
    0x84,  /* U+250E Down Heavy And Right Light */
    0x88,  /* U+250F Heavy Down And Right */
    0x41,  /* U+2510 Light Down And Left */
    0x42,  /* U+2511 Down Light And Left Heavy */
    0x81,  /* U+2512 Down Heavy And Left Light */
    0x82,  /* U+2513 Heavy Down And Left */
    0x14,  /* U+2514 Light Up And Right */
    0x18,  /* U+2515 Up Light And Right Heavy */
    0x24,  /* U+2516 Up Heavy And Right Light */
    0x28,  /* U+2517 Heavy Up And Right */
    0x11,  /* U+2518 Light Up And Left */
    0x12,  /* U+2519 Up Light And Left Heavy */
    0x21,  /* U+251A Up Heavy And Left Light */
    0x22,  /* U+251B Heavy Up And Left */
    0x54,  /* U+251C Light Vertical And Right */
    0x58,  /* U+251D Vertical Light And Right Heavy */
    0x64,  /* U+251E Up Heavy And Right Down Light */
    0x94,  /* U+251F Down Heavy And Right Up Light */
    0xA4,  /* U+2520 Vertical Heavy And Right Light */
    0x68,  /* U+2521 Down Light And Right Up Heavy */
    0x98,  /* U+2522 Up Light And Right Down Heavy */
    0xA8,  /* U+2523 Heavy Vertical And Right */
    0x51,  /* U+2524 Light Vertical And Left */
    0x52,  /* U+2525 Vertical Light And Left Heavy */
    0x61,  /* U+2526 Up Heavy And Left Down Light */
    0x91,  /* U+2527 Down Heavy And Left Up Light */
    0xA1,  /* U+2528 Vertical Heavy And Left Light */
    0x62,  /* U+2529 Down Light And Left Up Heavy */
    0x92,  /* U+252A Up Light And Left Down Heavy */
    0xA2,  /* U+252B Heavy Vertical And Left */
    0x45,  /* U+252C Light Down And Horizontal */
    0x46,  /* U+252D Left Heavy And Right Down Light */
    0x49,  /* U+252E Right Heavy And Left Down Light */
    0x4A,  /* U+252F Down Light And Horizontal Heavy */
    0x85,  /* U+2530 Down Heavy And Horizontal Light */
    0x86,  /* U+2531 Right Light And Left Down Heavy */
    0x89,  /* U+2532 Left Light And Right Down Heavy */
    0x8A,  /* U+2533 Heavy Down And Horizontal */
    0x15,  /* U+2534 Light Up And Horizontal */
    0x16,  /* U+2535 Left Heavy And Right Up Light */
    0x19,  /* U+2536 Right Heavy And Left Up Light */
    0x1A,  /* U+2537 Up Light And Horizontal Heavy */
    0x25,  /* U+2538 Up Heavy And Horizontal Light */
    0x26,  /* U+2539 Right Light And Left Up Heavy */
    0x29,  /* U+253A Left Light And Right Up Heavy */
    0x2A,  /* U+253B Heavy Up And Horizontal */
    0x55,  /* U+253C Light Vertical And Horizontal */
    0x56,  /* U+253D Left Heavy And Right Vertical Light */
    0x59,  /* U+253E Right Heavy And Left Vertical Light */
    0x5A,  /* U+253F Vertical Light And Horizontal Heavy */
    0x65,  /* U+2540 Up Heavy And Down Horizontal Light */
    0x95,  /* U+2541 Down Heavy And Up Horizontal Light */
    0xA5,  /* U+2542 Vertical Heavy And Horizontal Light */
    0x66,  /* U+2543 Left Up Heavy And Right Down Light */
    0x69,  /* U+2544 Right Up Heavy And Left Down Light */
    0x96,  /* U+2545 Left Down Heavy And Right Up Light */
    0x99,  /* U+2546 Right Down Heavy And Left Up Light */
    0x6A,  /* U+2547 Down Light And Up Horizontal Heavy */
    0x9A,  /* U+2548 Up Light And Down Horizontal Heavy */
    0xA6,  /* U+2549 Right Light And Left Vertical Heavy */
    0xA9,  /* U+254A Left Light And Right Vertical Heavy */
    0xAA,  /* U+254B Heavy Vertical And Horizontal */
    0x05,  /* U+254C Light Double Dash Horizontal */
    0x0A,  /* U+254D Heavy Double Dash Horizontal */
    0x50,  /* U+254E Light Double Dash Vertical */
    0xA0,  /* U+254F Heavy Double Dash Vertical */
    0x0F,  /* U+2550 Double Horizontal */
    0xF0,  /* U+2551 Double Vertical */
    0x4C,  /* U+2552 Down Single And Right Double */
    0xC4,  /* U+2553 Down Double And Right Single */
    0xCC,  /* U+2554 Double Down And Right */
    0x43,  /* U+2555 Down Single And Left Double */
    0xC1,  /* U+2556 Down Double And Left Single */
    0xC3,  /* U+2557 Double Down And Left */
    0x1C,  /* U+2558 Up Single And Right Double */
    0x34,  /* U+2559 Up Double And Right Single */
    0x3C,  /* U+255A Double Up And Right */
    0x13,  /* U+255B Up Single And Left Double */
    0x31,  /* U+255C Up Double And Left Single */
    0x33,  /* U+255D Double Up And Left */
    0x5C,  /* U+255E Vertical Single And Right Double */
    0xF4,  /* U+255F Vertical Double And Right Single */
    0xFC,  /* U+2560 Double Vertical And Right */
    0x53,  /* U+2561 Vertical Single And Left Double */
    0xF1,  /* U+2562 Vertical Double And Left Single */
    0xF3,  /* U+2563 Double Vertical And Left */
    0x4F,  /* U+2564 Down Single And Horizontal Double */
    0xC5,  /* U+2565 Down Double And Horizontal Single */
    0xCF,  /* U+2566 Double Down And Horizontal */
    0x1F,  /* U+2567 Up Single And Horizontal Double */
    0x35,  /* U+2568 Up Double And Horizontal Single */
    0x3F,  /* U+2569 Double Up And Horizontal */
    0x5F,  /* U+256A Vertical Single And Horizontal Double */
    0xF5,  /* U+256B Vertical Double And Horizontal Single */
    0xFF,  /* U+256C Double Vertical And Horizontal */
    0x44,  /* U+256D Light Arc Down And Right */
    0x41,  /* U+256E Light Arc Down And Left */
    0x11,  /* U+256F Light Arc Up And Left */
    0x14,  /* U+2570 Light Arc Up And Right */
    0x00,  /* U+2571 Light Diagonal Upper Right To Lower Left */
    0x00,  /* U+2572 Light Diagonal Upper Left To Lower Right */
    0x00,  /* U+2573 Light Diagonal Cross */
    0x01,  /* U+2574 Light Left */
    0x10,  /* U+2575 Light Up */
    0x04,  /* U+2576 Light Right */
    0x40,  /* U+2577 Light Down */
    0x02,  /* U+2578 Heavy Left */
    0x20,  /* U+2579 Heavy Up */
    0x08,  /* U+257A Heavy Right */
    0x80,  /* U+257B Heavy Down */
    0x09,  /* U+257C Light Left And Heavy Right */
    0x90,  /* U+257D Light Up And Heavy Down */
    0x06,  /* U+257E Heavy Left And Light Right */
    0x60,  /* U+257F Heavy Up And Light Down */
};

struct Canvas {
    ID2D1RenderTarget *target;
    ID2D1SolidColorBrush *brush;
    uint32_t rgb;

    void SetColor(float alpha) const
    {
        brush->SetColor(D2D1::ColorF(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f, alpha));
    }

    void Fill(int x0, int y0, int x1, int y1, float alpha = 1.0f) const
    {
        if (x1 <= x0 || y1 <= y0)
            return;
        SetColor(alpha);
        target->FillRectangle(D2D1::RectF(static_cast<float>(x0), static_cast<float>(y0), static_cast<float>(x1),
                                          static_cast<float>(y1)), brush);
    }
};

inline bool IsHandled(wchar_t c)
{
    return (c >= 0x2500 && c <= 0x259F) || (c >= 0xE0B0 && c <= 0xE0BF);
}

/*
 * The Powerline separators U+E0B0..U+E0BF (private use, so a font either has them, drawn the way its designer liked and
 * with the same one-pixel gaps, or has nothing and the cell shows a missing-glyph box). They are shapes that fill the cell
 * and meet their neighbour's background, so they are drawn here, the way Windows Terminal does:
 *   E0B0 / E0B2  solid triangle pointing right / left          E0B1 / E0B3  the same as a chevron (outline)
 *   E0B4 / E0B6  solid half circle bulging right / left        E0B5 / E0B7  the same as an arc (outline)
 *   E0B8 / E0BA / E0BC / E0BE  solid triangles in the lower left / lower right / upper left / upper right corner
 *   E0B9 / E0BF  backslash                                      E0BB / E0BD  slash
 * The other Powerline and Nerd Font symbols (the branch, the line number, the lock) are left to the font.
 */
inline void DrawPowerline(const Canvas &g, int x, int y, int cw, int ch, wchar_t c)
{
    ID2D1Factory *factory = nullptr;
    g.target->GetFactory(&factory);
    if (!factory)
        return;
    struct Release { ID2D1Factory *f; ~Release() { f->Release(); } } releaseFactory{ factory };   // GetFactory adds a reference
    const float l = static_cast<float>(x), t = static_cast<float>(y);
    const float r = static_cast<float>(x + cw), b = static_cast<float>(y + ch);
    const float mid = t + ch / 2.0f;
    const float stroke = std::fmax(1.0f, std::round(cw / 8.0f));
    g.SetColor(1.0f);

    auto line = [&](float x0, float y0, float x1, float y1) {
        g.target->DrawLine(D2D1::Point2F(x0, y0), D2D1::Point2F(x1, y1), g.brush, stroke);
    };
    auto polygon = [&](const D2D1_POINT_2F *points, int n, bool filled) {
        ID2D1PathGeometry *path = nullptr;
        ID2D1GeometrySink *sink = nullptr;
        if (FAILED(factory->CreatePathGeometry(&path)))
            return;
        if (SUCCEEDED(path->Open(&sink))) {
            sink->BeginFigure(points[0], filled ? D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN_HOLLOW);
            for (int i = 1; i < n; i++)
                sink->AddLine(points[i]);
            sink->EndFigure(filled ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            sink->Close();
            sink->Release();
            if (filled)
                g.target->FillGeometry(path, g.brush);
            else
                g.target->DrawGeometry(path, g.brush, stroke);
        }
        path->Release();
    };
    // A half ellipse as wide as the cell and as high: from the top to the bottom through the right (or the left) edge.
    auto half = [&](bool right, bool filled) {
        ID2D1PathGeometry *path = nullptr;
        ID2D1GeometrySink *sink = nullptr;
        if (FAILED(factory->CreatePathGeometry(&path)))
            return;
        if (SUCCEEDED(path->Open(&sink))) {
            const float edge = right ? l : r;
            sink->BeginFigure(D2D1::Point2F(edge, t), filled ? D2D1_FIGURE_BEGIN_FILLED : D2D1_FIGURE_BEGIN_HOLLOW);
            sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(edge, b), D2D1::SizeF(static_cast<float>(cw), ch / 2.0f), 0.0f,
                                          right ? D2D1_SWEEP_DIRECTION_CLOCKWISE : D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE,
                                          D2D1_ARC_SIZE_SMALL));
            sink->EndFigure(filled ? D2D1_FIGURE_END_CLOSED : D2D1_FIGURE_END_OPEN);
            sink->Close();
            sink->Release();
            if (filled)
                g.target->FillGeometry(path, g.brush);
            else
                g.target->DrawGeometry(path, g.brush, stroke);
        }
        path->Release();
    };

    switch (c) {
    case 0xE0B0: { const D2D1_POINT_2F p[] = { {l, t}, {r, mid}, {l, b} }; polygon(p, 3, true); break; }
    case 0xE0B1: { const D2D1_POINT_2F p[] = { {l, t}, {r, mid}, {l, b} }; polygon(p, 3, false); break; }
    case 0xE0B2: { const D2D1_POINT_2F p[] = { {r, t}, {l, mid}, {r, b} }; polygon(p, 3, true); break; }
    case 0xE0B3: { const D2D1_POINT_2F p[] = { {r, t}, {l, mid}, {r, b} }; polygon(p, 3, false); break; }
    case 0xE0B4: half(true, true); break;
    case 0xE0B5: half(true, false); break;
    case 0xE0B6: half(false, true); break;
    case 0xE0B7: half(false, false); break;
    case 0xE0B8: { const D2D1_POINT_2F p[] = { {l, t}, {l, b}, {r, b} }; polygon(p, 3, true); break; }
    case 0xE0BA: { const D2D1_POINT_2F p[] = { {r, t}, {r, b}, {l, b} }; polygon(p, 3, true); break; }
    case 0xE0BC: { const D2D1_POINT_2F p[] = { {l, t}, {r, t}, {l, b} }; polygon(p, 3, true); break; }
    case 0xE0BE: { const D2D1_POINT_2F p[] = { {l, t}, {r, t}, {r, b} }; polygon(p, 3, true); break; }
    case 0xE0B9: case 0xE0BF: line(l, t, r, b); break;
    case 0xE0BB: case 0xE0BD: line(r, t, l, b); break;
    default: break;
    }
}

/* The block elements, U+2580..U+259F. x, y is the cell's top left corner. */
inline void DrawBlock(const Canvas &g, int x, int y, int cw, int ch, wchar_t c)
{
    auto sx = [&](int eighths) { return x + (cw * eighths + 4) / 8; };
    auto sy = [&](int eighths) { return y + (ch * eighths + 4) / 8; };
    const int x1 = x + cw, y1 = y + ch;
    const int midx = x + (cw + 1) / 2, midy = y + (ch + 1) / 2;

    if (c == 0x2580) { g.Fill(x, y, x1, midy); return; }                                    // upper half
    if (c >= 0x2581 && c <= 0x2588) { g.Fill(x, sy(8 - (c - 0x2580)), x1, y1); return; }    // lower 1/8 .. full block
    if (c >= 0x2589 && c <= 0x258F) { g.Fill(x, y, sx(8 - (c - 0x2588)), y1); return; }     // left 7/8 .. 1/8
    if (c == 0x2590) { g.Fill(midx, y, x1, y1); return; }                                   // right half
    if (c == 0x2591) { g.Fill(x, y, x1, y1, 0.25f); return; }
    if (c == 0x2592) { g.Fill(x, y, x1, y1, 0.50f); return; }
    if (c == 0x2593) { g.Fill(x, y, x1, y1, 0.75f); return; }
    if (c == 0x2594) { g.Fill(x, y, x1, sy(1)); return; }                                   // upper 1/8
    if (c == 0x2595) { g.Fill(sx(7), y, x1, y1); return; }                                  // right 1/8

    // Quadrants U+2596..U+259F: bit 0 upper left, 1 upper right, 2 lower left, 3 lower right.
    static const uint8_t kQuadrants[10] = { 0x4, 0x8, 0x1, 0x7, 0x9, 0x3, 0xB, 0x2, 0x6, 0xE };
    const uint8_t q = kQuadrants[c - 0x2596];
    if (q & 1) g.Fill(x, y, midx, midy);
    if (q & 2) g.Fill(midx, y, x1, midy);
    if (q & 4) g.Fill(x, midy, midx, y1);
    if (q & 8) g.Fill(midx, midy, x1, y1);
}

/* The diagonals U+2571..U+2573. */
inline void DrawDiagonals(const Canvas &g, int x, int y, int cw, int ch, wchar_t c, float thickness)
{
    g.SetColor(1.0f);
    const D2D1_POINT_2F tl = D2D1::Point2F(static_cast<float>(x), static_cast<float>(y));
    const D2D1_POINT_2F tr = D2D1::Point2F(static_cast<float>(x + cw), static_cast<float>(y));
    const D2D1_POINT_2F bl = D2D1::Point2F(static_cast<float>(x), static_cast<float>(y + ch));
    const D2D1_POINT_2F br = D2D1::Point2F(static_cast<float>(x + cw), static_cast<float>(y + ch));
    if (c == 0x2571 || c == 0x2573)
        g.target->DrawLine(bl, tr, g.brush, thickness);       // lower left to upper right
    if (c == 0x2572 || c == 0x2573)
        g.target->DrawLine(tl, br, g.brush, thickness);       // upper left to lower right
}

/* The box drawing characters, U+2500..U+257F. */
inline void DrawBox(const Canvas &g, int x, int y, int cw, int ch, wchar_t c)
{
    const uint8_t seg = kSegments[c - 0x2500];
    if (seg == 0) {
        DrawDiagonals(g, x, y, cw, ch, c, std::fmax(1.0f, std::round(cw / 8.0f)));
        return;
    }
    const int L = seg & 3, R = (seg >> 2) & 3, U = (seg >> 4) & 3, D = (seg >> 6) & 3;

    const int t = (std::max)(1, static_cast<int>(std::lround(cw / 8.0)));     // a light line, and one line of a double
    auto thick = [&](int s) { return s == 2 ? (std::max)(2, 2 * t) : t; };    // one line of a light or heavy style
    auto span = [&](int s) { return s == 0 ? 0 : s == 3 ? 3 * t : thick(s); };

    const int x1 = x + cw, y1 = y + ch;
    const int vspan = (std::max)(span(U), span(D));       // the width the vertical lines take at the centre
    const int hspan = (std::max)(span(L), span(R));       // the height the horizontal lines take at the centre
    const int vx0 = x + (cw - vspan) / 2, vx1 = vx0 + vspan;
    const int hy0 = y + (ch - hspan) / 2, hy1 = hy0 + hspan;
    const int dbx = x + (cw - 3 * t) / 2;               // the two lines of a double vertical: [dbx, dbx+t) and [dbx+2t, dbx+3t)
    const int dby = y + (ch - 3 * t) / 2;               // and of a double horizontal
    const bool verticalPassesThroughDouble = U == 3 && D == 3;
    const bool horizontalIsDouble = L == 3 || R == 3;

    // Horizontal segments: the left one runs from the cell's edge to the centre, the right one from the centre to the edge.
    for (int side = 0; side < 2; side++) {
        const int s = side == 0 ? L : R;
        if (s == 0)
            continue;
        auto from = [&](int inner) { return side == 0 ? x : inner; };
        auto to = [&](int inner) { return side == 0 ? inner : x1; };
        const int outer = side == 0 ? vx1 : vx0;                   // past the vertical lines' far edge
        const int nearLine = side == 0 ? vx0 + t : vx1 - t;        // against the vertical line on this side
        if (s == 3) {
            // Two lines. The upper one meets the Up vertical, the lower one the Down vertical: against a double one it stops
            // at its nearer line (an inner corner), otherwise it goes on to the far side (an outer corner, or a plain pass).
            const int endUpper = U == 3 ? nearLine : outer;
            const int endLower = D == 3 ? nearLine : outer;
            g.Fill(from(endUpper), dby, to(endUpper), dby + t);
            g.Fill(from(endLower), dby + 2 * t, to(endLower), dby + 3 * t);
        } else {
            const int end = verticalPassesThroughDouble ? nearLine : outer;
            const int ya = y + (ch - span(s)) / 2;
            g.Fill(from(end), ya, to(end), ya + thick(s));
        }
    }

    // Vertical segments: the upper one runs from the cell's edge to the centre, the lower one from the centre to the edge.
    for (int side = 0; side < 2; side++) {
        const int s = side == 0 ? U : D;
        if (s == 0)
            continue;
        const int opposite = side == 0 ? D : U;
        auto from = [&](int inner) { return side == 0 ? y : inner; };
        auto to = [&](int inner) { return side == 0 ? inner : y1; };
        const int outer = side == 0 ? hy1 : hy0;
        if (s == 3) {
            // Two lines. The left one meets the Left horizontal, the right one the Right horizontal: against a double one it
            // stops at its nearer line, otherwise it goes on to the far side.
            const int nearLine = side == 0 ? dby + t : dby + 2 * t;
            const int endLeft = L == 3 ? nearLine : outer;
            const int endRight = R == 3 ? nearLine : outer;
            g.Fill(dbx, from(endLeft), dbx + t, to(endLeft));
            g.Fill(dbx + 2 * t, from(endRight), dbx + 3 * t, to(endRight));
        } else {
            // A single line that ends at double horizontal ones stops between them; one that goes through them does not.
            const int end = horizontalIsDouble && opposite == 0 ? (hy0 + hy1) / 2 : outer;
            const int xa = x + (cw - thick(s)) / 2;
            g.Fill(xa, from(end), xa + thick(s), to(end));
        }
    }
}

/* Draws the character in the cell whose top left corner is x, y with the given colour. Returns false for any other character. */
inline bool Draw(ID2D1RenderTarget *target, ID2D1SolidColorBrush *brush, int x, int y, int cw, int ch, wchar_t c, uint32_t rgb)
{
    if (!IsHandled(c) || cw < 2 || ch < 2)
        return false;
    const Canvas g{ target, brush, rgb };
    if (c >= 0xE0B0)
        DrawPowerline(g, x, y, cw, ch, c);
    else if (c < 0x2580)
        DrawBox(g, x, y, cw, ch, c);
    else
        DrawBlock(g, x, y, cw, ch, c);
    return true;
}

} // namespace boxdraw

#endif
