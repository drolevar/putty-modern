/*
 * render.cpp - the terminal renderer. See render.h for the contract.
 *
 * Text is drawn one glyph per cell, each at its own cell position, so the grid stays aligned even
 * when a character comes from a fallback font whose advance differs. Line spacing is made uniform
 * for the same reason: every glyph sits on the baseline measured from the primary font.
 */

#include <windows.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <dwrite.h>
#include <dxgi1_2.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <cmath>
#include <cstring>
#include <cwchar>
#include <initializer_list>
#include <new>

#include "putty_engine.h"
#include "render.h"

using Microsoft::WRL::ComPtr;

namespace {

// Microsoft.UI.Xaml.Media.DXInterop.ISwapChainPanelNative (not the UWP interface of the same name).
struct __declspec(uuid("63aad0b8-7c24-40ff-85a8-640d944cc325")) ISwapChainPanelNative : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain *swapChain) = 0;
};

D2D1_COLOR_F ColorOf(uint32_t rgb)
{
    return D2D1::ColorF(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f,
                        (rgb & 0xFF) / 255.0f, 1.0f);
}

void EnsureCom()
{
    // The caller's thread may or may not have COM yet (WinUI's has; a test thread may not).
    // RPC_E_CHANGED_MODE just means it is already initialised the other way, which is fine.
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
}

} // namespace

struct pe_renderer {
    bool offscreen = false;
    int width = 0;
    int height = 0;

    ComPtr<ID2D1Factory1> d2d;
    ComPtr<IDWriteFactory> dwrite;

    // Offscreen: a WIC bitmap and a render target that draws straight onto it.
    ComPtr<IWICImagingFactory> wic;
    ComPtr<IWICBitmap> wicBitmap;
    ComPtr<ID2D1RenderTarget> wicTarget;

    // Panel: a device context drawing to a persistent canvas, copied to the swap chain on present.
    ComPtr<ID3D11Device> d3d;
    ComPtr<ID2D1Device> d2dDevice;
    ComPtr<ID2D1DeviceContext> dc;
    ComPtr<IDXGISwapChain1> swapChain;
    ComPtr<ID2D1Bitmap1> canvas;
    ComPtr<ISwapChainPanelNative> panel;

    ComPtr<ID2D1SolidColorBrush> brush;
    ComPtr<IDWriteTextFormat> regular;
    ComPtr<IDWriteTextFormat> bold;
    float cellW = 8;
    float cellH = 16;
    float baseline = 12;

    ID2D1RenderTarget *Target() const
    {
        return offscreen ? static_cast<ID2D1RenderTarget *>(wicTarget.Get())
                         : static_cast<ID2D1RenderTarget *>(dc.Get());
    }

    bool CreateFactories()
    {
        EnsureCom();
        D2D1_FACTORY_OPTIONS options = {};
        return SUCCEEDED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1),
                                           &options, reinterpret_cast<void **>(d2d.GetAddressOf())))
            && SUCCEEDED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                                             reinterpret_cast<IUnknown **>(dwrite.GetAddressOf())));
    }

    bool CreateOffscreenSurface()
    {
        wicTarget.Reset();
        wicBitmap.Reset();
        if (FAILED(wic->CreateBitmap(width, height, GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad,
                                     &wicBitmap)))
            return false;
        D2D1_RENDER_TARGET_PROPERTIES props = D2D1::RenderTargetProperties(
            D2D1_RENDER_TARGET_TYPE_SOFTWARE,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);
        if (FAILED(d2d->CreateWicBitmapRenderTarget(wicBitmap.Get(), props, &wicTarget)))
            return false;
        wicTarget->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        brush.Reset();
        return SUCCEEDED(wicTarget->CreateSolidColorBrush(D2D1::ColorF(0), &brush));
    }

    bool CreateCanvas()
    {
        canvas.Reset();
        D2D1_BITMAP_PROPERTIES1 props = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);
        if (FAILED(dc->CreateBitmap(D2D1::SizeU(width, height), nullptr, 0, props, &canvas)))
            return false;
        dc->SetTarget(canvas.Get());
        dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        return true;
    }

    bool CreatePanelSurface(IUnknown *panelUnknown)
    {
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                       D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
                                       &d3d, nullptr, nullptr);
        if (FAILED(hr))
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr);
        if (FAILED(hr))
            return false;

        ComPtr<IDXGIDevice> dxgiDevice;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory2> factory;
        if (FAILED(d3d.As(&dxgiDevice)) || FAILED(dxgiDevice->GetAdapter(&adapter)) ||
            FAILED(adapter->GetParent(IID_PPV_ARGS(&factory))))
            return false;
        if (FAILED(d2d->CreateDevice(dxgiDevice.Get(), &d2dDevice)) ||
            FAILED(d2dDevice->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc)))
            return false;

        DXGI_SWAP_CHAIN_DESC1 desc = {};
        desc.Width = static_cast<UINT>(width);
        desc.Height = static_cast<UINT>(height);
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        if (FAILED(factory->CreateSwapChainForComposition(d3d.Get(), &desc, nullptr, &swapChain)))
            return false;

        if (FAILED(panelUnknown->QueryInterface(__uuidof(ISwapChainPanelNative),
                                                reinterpret_cast<void **>(panel.GetAddressOf()))) ||
            FAILED(panel->SetSwapChain(swapChain.Get())))
            return false;

        if (!CreateCanvas())
            return false;
        return SUCCEEDED(dc->CreateSolidColorBrush(D2D1::ColorF(0), &brush));
    }

    bool CopyCanvasToSwapChain()
    {
        ComPtr<IDXGISurface> surface;
        if (FAILED(swapChain->GetBuffer(0, IID_PPV_ARGS(&surface))))
            return false;
        D2D1_BITMAP_PROPERTIES1 props = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96.0f, 96.0f);
        ComPtr<ID2D1Bitmap1> backBuffer;
        if (FAILED(dc->CreateBitmapFromDxgiSurface(surface.Get(), props, &backBuffer)))
            return false;

        dc->SetTarget(backBuffer.Get());
        dc->BeginDraw();
        dc->DrawBitmap(canvas.Get());
        HRESULT hr = dc->EndDraw();
        dc->SetTarget(canvas.Get());
        if (FAILED(hr))
            return false;
        return SUCCEEDED(swapChain->Present(1, 0));
    }
};

extern "C" {

pe_renderer *pe_render_create_offscreen(int32_t width_px, int32_t height_px)
{
    if (width_px < 1 || height_px < 1)
        return nullptr;
    auto *r = new (std::nothrow) pe_renderer();
    if (!r)
        return nullptr;
    r->offscreen = true;
    r->width = width_px;
    r->height = height_px;
    if (!r->CreateFactories() ||
        FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&r->wic))) ||
        !r->CreateOffscreenSurface()) {
        delete r;
        return nullptr;
    }
    return r;
}

pe_renderer *pe_render_create_for_panel(void *panel_unknown, int32_t width_px, int32_t height_px)
{
    if (!panel_unknown || width_px < 1 || height_px < 1)
        return nullptr;
    auto *r = new (std::nothrow) pe_renderer();
    if (!r)
        return nullptr;
    r->offscreen = false;
    r->width = width_px;
    r->height = height_px;
    if (!r->CreateFactories() || !r->CreatePanelSurface(static_cast<IUnknown *>(panel_unknown))) {
        delete r;
        return nullptr;
    }
    return r;
}

void pe_render_destroy(pe_renderer *r)
{
    delete r;
}

bool pe_render_set_font(pe_renderer *r, const wchar_t *family, float size_px)
{
    if (!r || !family || size_px < 1.0f)
        return false;

    ComPtr<IDWriteFontCollection> fonts;
    UINT32 index = 0;
    BOOL exists = FALSE;
    if (FAILED(r->dwrite->GetSystemFontCollection(&fonts)) ||
        FAILED(fonts->FindFamilyName(family, &index, &exists)) || !exists)
        return false;

    ComPtr<IDWriteTextFormat> regular;
    ComPtr<IDWriteTextFormat> bold;
    if (FAILED(r->dwrite->CreateTextFormat(family, nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                                           DWRITE_FONT_STRETCH_NORMAL, size_px, L"", &regular)) ||
        FAILED(r->dwrite->CreateTextFormat(family, nullptr, DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_STYLE_NORMAL,
                                           DWRITE_FONT_STRETCH_NORMAL, size_px, L"", &bold)))
        return false;

    // Measure a reference glyph with the regular face to get the cell.
    ComPtr<IDWriteTextLayout> layout;
    if (FAILED(r->dwrite->CreateTextLayout(L"M", 1, regular.Get(), 1000.0f, 1000.0f, &layout)))
        return false;
    DWRITE_TEXT_METRICS metrics = {};
    DWRITE_LINE_METRICS line = {};
    UINT32 lines = 0;
    if (FAILED(layout->GetMetrics(&metrics)) || FAILED(layout->GetLineMetrics(&line, 1, &lines)))
        return false;

    r->cellW = std::fmax(1.0f, std::round(metrics.widthIncludingTrailingWhitespace));
    r->cellH = std::fmax(1.0f, std::ceil(line.height));
    r->baseline = std::fmin(std::fmax(1.0f, std::round(line.baseline)), r->cellH - 1.0f);

    for (IDWriteTextFormat *format : { regular.Get(), bold.Get() }) {
        format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
        format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
        format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
        format->SetLineSpacing(DWRITE_LINE_SPACING_METHOD_UNIFORM, r->cellH, line.baseline);
    }
    r->regular = regular;
    r->bold = bold;
    return true;
}

int32_t pe_render_baseline(const pe_renderer *r)
{
    return r ? static_cast<int32_t>(r->baseline) : 0;
}

void pe_render_rect(pe_renderer *r, int32_t x, int32_t y, int32_t width, int32_t height, uint32_t rgb)
{
    if (!r || width < 1 || height < 1)
        return;
    r->brush->SetColor(ColorOf(rgb));
    r->Target()->FillRectangle(D2D1::RectF(static_cast<float>(x), static_cast<float>(y),
                                           static_cast<float>(x + width), static_cast<float>(y + height)),
                               r->brush.Get());
}

/*
 * A shield with a tick inside the box (x, y, width, height), for the terminal's trust sigil: the mark the emulator puts at
 * the start of a line that PuTTY itself wrote (the login's status lines) and not the server. Drawn as vector shapes, so it
 * is as sharp at any font size or display scale as the text next to it. The box is cleared to `background` first.
 */
void pe_render_shield(pe_renderer *r, int32_t x, int32_t y, int32_t width, int32_t height,
                      uint32_t background, uint32_t body, uint32_t mark)
{
    if (!r || width < 4 || height < 4)
        return;
    pe_render_rect(r, x, y, width, height, background);

    const float pad = std::fmax(1.0f, std::floor(height / 8.0f));
    const float top = y + pad;
    const float bottom = y + height - pad;
    const float sh = bottom - top;
    const float sw = std::fmin(width - 2 * pad, sh * 0.82f);
    const float cx = x + width / 2.0f;
    const float half = sw / 2.0f;
    if (sh < 3 || sw < 3)
        return;

    ComPtr<ID2D1PathGeometry> path;
    ComPtr<ID2D1GeometrySink> sink;
    if (FAILED(r->d2d->CreatePathGeometry(&path)) || FAILED(path->Open(&sink)))
        return;
    sink->BeginFigure(D2D1::Point2F(cx, top), D2D1_FIGURE_BEGIN_FILLED);
    sink->AddLine(D2D1::Point2F(cx + half, top + sh * 0.14f));
    sink->AddLine(D2D1::Point2F(cx + half, top + sh * 0.52f));
    sink->AddBezier(D2D1::BezierSegment(D2D1::Point2F(cx + half, top + sh * 0.80f),
                                        D2D1::Point2F(cx + sw * 0.20f, bottom - sh * 0.10f),
                                        D2D1::Point2F(cx, bottom)));
    sink->AddBezier(D2D1::BezierSegment(D2D1::Point2F(cx - sw * 0.20f, bottom - sh * 0.10f),
                                        D2D1::Point2F(cx - half, top + sh * 0.80f),
                                        D2D1::Point2F(cx - half, top + sh * 0.52f)));
    sink->AddLine(D2D1::Point2F(cx - half, top + sh * 0.14f));
    sink->EndFigure(D2D1_FIGURE_END_CLOSED);
    if (FAILED(sink->Close()))
        return;

    r->brush->SetColor(ColorOf(body));
    r->Target()->FillGeometry(path.Get(), r->brush.Get());

    // The tick.
    const float stroke = std::fmax(1.2f, height / 11.0f);
    r->brush->SetColor(ColorOf(mark));
    const D2D1_POINT_2F a = D2D1::Point2F(cx - sw * 0.24f, top + sh * 0.44f);
    const D2D1_POINT_2F b = D2D1::Point2F(cx - sw * 0.04f, top + sh * 0.62f);
    const D2D1_POINT_2F c = D2D1::Point2F(cx + sw * 0.26f, top + sh * 0.28f);
    r->Target()->DrawLine(a, b, r->brush.Get(), stroke);
    r->Target()->DrawLine(b, c, r->brush.Get(), stroke);
}

void pe_render_cell_size(const pe_renderer *r, int32_t *width_px, int32_t *height_px)
{
    if (width_px)
        *width_px = r ? static_cast<int32_t>(r->cellW) : 0;
    if (height_px)
        *height_px = r ? static_cast<int32_t>(r->cellH) : 0;
}

bool pe_render_resize(pe_renderer *r, int32_t width_px, int32_t height_px)
{
    if (!r || width_px < 1 || height_px < 1)
        return false;
    r->width = width_px;
    r->height = height_px;
    if (r->offscreen)
        return r->CreateOffscreenSurface();

    r->dc->SetTarget(nullptr);
    r->canvas.Reset();
    if (FAILED(r->swapChain->ResizeBuffers(0, static_cast<UINT>(width_px), static_cast<UINT>(height_px),
                                           DXGI_FORMAT_UNKNOWN, 0)))
        return false;
    return r->CreateCanvas();
}

bool pe_render_begin(pe_renderer *r)
{
    if (!r || !r->Target())
        return false;
    r->Target()->BeginDraw();
    return true;
}

void pe_render_fill(pe_renderer *r, int32_t col, int32_t row, int32_t cells, uint32_t rgb)
{
    if (!r || cells < 1)
        return;
    r->brush->SetColor(ColorOf(rgb));
    r->Target()->FillRectangle(D2D1::RectF(col * r->cellW, row * r->cellH, (col + cells) * r->cellW,
                                           (row + 1) * r->cellH),
                               r->brush.Get());
}

void pe_render_glyph(pe_renderer *r, int32_t col, int32_t row, const wchar_t *text, int32_t units,
                     int32_t cells, uint32_t fg, uint32_t style)
{
    if (!r || !text || units < 1 || cells < 1 || !r->regular)
        return;
    r->brush->SetColor(ColorOf(fg));
    D2D1_RECT_F rect = D2D1::RectF(col * r->cellW, row * r->cellH, (col + cells) * r->cellW, (row + 1) * r->cellH);
    r->Target()->DrawText(text, static_cast<UINT32>(units), (style & PE_STYLE_BOLD) ? r->bold.Get() : r->regular.Get(),
                          rect, r->brush.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP, DWRITE_MEASURING_MODE_NATURAL);
}

void pe_render_underline(pe_renderer *r, int32_t col, int32_t row, int32_t cells, uint32_t rgb)
{
    if (!r || cells < 1)
        return;
    r->brush->SetColor(ColorOf(rgb));
    // One pixel below the baseline, as PuTTY draws it, kept inside the cell.
    float y = row * r->cellH + std::fmin(r->baseline + 1.0f, r->cellH - 1.0f);
    r->Target()->FillRectangle(D2D1::RectF(col * r->cellW, y, (col + cells) * r->cellW, y + 1.0f), r->brush.Get());
}

void pe_render_cursor(pe_renderer *r, int32_t col, int32_t row, int32_t cells, int32_t kind, bool hollow,
                      uint32_t rgb)
{
    if (!r || cells < 1)
        return;
    r->brush->SetColor(ColorOf(rgb));
    float x0 = col * r->cellW;
    float y0 = row * r->cellH;
    float x1 = (col + cells) * r->cellW;
    float y1 = (row + 1) * r->cellH;
    if (hollow) {
        r->Target()->DrawRectangle(D2D1::RectF(x0 + 0.5f, y0 + 0.5f, x1 - 0.5f, y1 - 0.5f), r->brush.Get(), 1.0f);
        return;
    }
    switch (kind) {
    case PE_CURSOR_UNDERLINE:
        r->Target()->FillRectangle(D2D1::RectF(x0, y1 - 2.0f, x1, y1), r->brush.Get());
        break;
    case PE_CURSOR_BAR:
        r->Target()->FillRectangle(D2D1::RectF(x0, y0, x0 + 2.0f, y1), r->brush.Get());
        break;
    default:
        r->Target()->FillRectangle(D2D1::RectF(x0, y0, x1, y1), r->brush.Get());
        break;
    }
}

bool pe_render_end(pe_renderer *r)
{
    if (!r || !r->Target())
        return false;
    HRESULT hr = r->Target()->EndDraw();
    if (FAILED(hr))
        return false;
    return r->offscreen ? true : r->CopyCanvasToSwapChain();
}

bool pe_render_read_pixel(pe_renderer *r, int32_t x, int32_t y, uint32_t *rgb)
{
    if (!r || !r->offscreen || !rgb || x < 0 || y < 0 || x >= r->width || y >= r->height)
        return false;
    WICRect area = { x, y, 1, 1 };
    ComPtr<IWICBitmapLock> lock;
    if (FAILED(r->wicBitmap->Lock(&area, WICBitmapLockRead, &lock)))
        return false;
    UINT size = 0;
    BYTE *data = nullptr;
    if (FAILED(lock->GetDataPointer(&size, &data)) || size < 4)
        return false;
    *rgb = (static_cast<uint32_t>(data[2]) << 16) | (static_cast<uint32_t>(data[1]) << 8) | data[0];
    return true;
}

/* ----- Diagnostics: draws text offscreen and reports what ended up where ------------------- */

PE_API int32_t pe_diag_render_text(const wchar_t *font_family, float size_px, const wchar_t *text,
                                   uint32_t fg_rgb, uint32_t bg_rgb, int32_t bold,
                                   pe_diag_render_result *out)
{
    if (!font_family || !text || !out)
        return PE_ERR_FAILED;
    size_t length = wcslen(text);
    if (length < 1 || length > 64)
        return PE_ERR_FAILED;

    // Size the surface after the cell, which needs a font, which needs a renderer: make a throwaway
    // one to measure, then the real one.
    pe_renderer *probe = pe_render_create_offscreen(16, 16);
    if (!probe)
        return PE_ERR_FAILED;
    bool measured = pe_render_set_font(probe, font_family, size_px);
    int32_t cw = 0, ch = 0;
    pe_render_cell_size(probe, &cw, &ch);
    pe_render_destroy(probe);
    if (!measured)
        return PE_ERR_FAILED;

    pe_renderer *r = pe_render_create_offscreen(cw * static_cast<int32_t>(length), ch);
    if (!r)
        return PE_ERR_FAILED;
    int32_t result = PE_ERR_FAILED;
    if (pe_render_set_font(r, font_family, size_px) && pe_render_begin(r)) {
        pe_render_fill(r, 0, 0, static_cast<int32_t>(length), bg_rgb);
        for (size_t i = 0; i < length; i++) {
            if (text[i] != L' ')
                pe_render_glyph(r, static_cast<int32_t>(i), 0, &text[i], 1, 1, fg_rgb, bold ? PE_STYLE_BOLD : 0);
        }
        if (pe_render_end(r)) {
            memset(out, 0, sizeof(*out));
            out->cell_width = cw;
            out->cell_height = ch;
            out->cells = static_cast<int32_t>(length);
            pe_render_read_pixel(r, 0, 0, &out->corner_rgb);
            for (size_t i = 0; i < length; i++) {
                int32_t ink = 0;
                for (int32_t y = 0; y < ch; y++) {
                    for (int32_t x = 0; x < cw; x++) {
                        uint32_t pixel = 0;
                        if (pe_render_read_pixel(r, static_cast<int32_t>(i) * cw + x, y, &pixel) && pixel != bg_rgb)
                            ink++;
                    }
                }
                out->ink[i] = ink;
            }
            result = PE_OK;
        }
    }
    pe_render_destroy(r);
    return result;
}

} // extern "C"
