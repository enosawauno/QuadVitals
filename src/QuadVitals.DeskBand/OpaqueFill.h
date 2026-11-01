// Opaque rectangle fill for taskbar bands.
//
// The taskbar composites its child bands over a tinted backdrop and *adds* that backdrop to
// pixels painted with plain GDI (measured on this box: R+9 / G+34 / B+23, clamped at 255).
// XMeters's "dark green" background (#092117) is really pure black plus that glow.
//
// Painting through a 32bpp DIB whose alpha is 255 makes our pixels opaque, so the screen shows
// exactly the colour we asked for. Everything that must match the design tokens pixel-for-pixel
// (band background, bar cells and fills) goes through this helper.
#pragma once

#include <windows.h>
// GDI+ declares its own IStream-based overloads; WIN32_LEAN_AND_MEAN leaves objidl.h out,
// so pull it in first or gdiplus.h fails to compile.
#include <objidl.h>
#include <gdiplus.h>
#include <vector>
#include <math.h>

namespace pb
{
    // Band surface colour: the reference screenshot's page background (#1A1A1A).
    static const COLORREF kBandBackground = RGB(0x1A, 0x1A, 0x1A);

    /// <summary>
    /// The taskbar's own backdrop, sampled from the two ends of the bar.
    ///
    /// A band that paints a fixed #1A1A1A panel is visibly a black slab on a taskbar with
    /// transparency on (the bar is the wallpaper blurred through the accent tint). Sampling the
    /// tint at the far left and far right and interpolating gives the band the same look without
    /// switching to per-pixel alpha, which would mean rendering text through GDI+.
    /// The samples avoid the band itself (it covers the full bar height) and avoid icon rows.
    /// </summary>
    struct BandTint
    {
        COLORREF Left = kBandBackground;
        COLORREF Right = kBandBackground;
        RECT Tray = { 0, 0, 0, 0 };
        bool Sampled = false;

        bool Valid() const { return Sampled && Tray.right - Tray.left >= 8; }

        COLORREF At(int screenX) const
        {
            if (!Valid()) return kBandBackground;
            int width = Tray.right - Tray.left;
            int offset = screenX - Tray.left;
            if (offset < 0) offset = 0;
            if (offset > width) offset = width;

            int leftR = GetRValue(Left), leftG = GetGValue(Left), leftB = GetBValue(Left);
            int rightR = GetRValue(Right), rightG = GetGValue(Right), rightB = GetBValue(Right);
            int r = leftR + (rightR - leftR) * offset / width;
            int g = leftG + (rightG - leftG) * offset / width;
            int b = leftB + (rightB - leftB) * offset / width;
            return RGB(r, g, b);
        }
    };

    inline void FillRectOpaque(HDC hdc, const RECT& rc, COLORREF color)
    {
        int width = rc.right - rc.left;
        int height = rc.bottom - rc.top;
        if (width <= 0 || height <= 0) return;

        BITMAPINFO info = { 0 };
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;   // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        BYTE b = GetBValue(color), g = GetGValue(color), r = GetRValue(color);
        DWORD value = ((DWORD)0xFF << 24) | ((DWORD)r << 16) | ((DWORD)g << 8) | b;
        std::vector<DWORD> pixels((size_t)width * (size_t)height, value);

        StretchDIBits(hdc, rc.left, rc.top, width, height, 0, 0, width, height,
                      pixels.data(), &info, DIB_RGB_COLORS, SRCCOPY);
    }

    /// <summary>Fills a rectangle with the tint gradient (screenLeft = rc.left in screen pixels).</summary>
    inline void FillTintOpaque(HDC hdc, const RECT& rc, const BandTint& tint, int screenLeft)
    {
        int width = rc.right - rc.left;
        int height = rc.bottom - rc.top;
        if (width <= 0 || height <= 0) return;

        if (!tint.Valid())
        {
            FillRectOpaque(hdc, rc, kBandBackground);
            return;
        }

        BITMAPINFO info = { 0 };
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;      // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        std::vector<DWORD> pixels((size_t)width * (size_t)height);
        for (int column = 0; column < width; column++)
        {
            COLORREF colour = tint.At(screenLeft + column);
            DWORD value = ((DWORD)0xFF << 24) | ((DWORD)GetRValue(colour) << 16) |
                          ((DWORD)GetGValue(colour) << 8) | (DWORD)GetBValue(colour);
            for (int row = 0; row < height; row++) pixels[(size_t)row * width + column] = value;
        }

        StretchDIBits(hdc, rc.left, rc.top, width, height, 0, 0, width, height,
                      pixels.data(), &info, DIB_RGB_COLORS, SRCCOPY);
    }

    /// <summary>
    /// GDI+ is started once per process and never shut down: the band lives inside Explorer and
    /// tearing GDI+ down while other components still use it would be worse than leaving it up.
    /// Returns false only if the runtime refuses to start, in which case the ring falls back to
    /// the plain-GDI path (hard edges, but never a blank element).
    /// </summary>
    inline bool EnsureGdiplus()
    {
        static const bool ready = []() -> bool
        {
            Gdiplus::GdiplusStartupInput input;
            ULONG_PTR token = 0;
            return Gdiplus::GdiplusStartup(&token, &input, nullptr) == Gdiplus::Ok;
        }();
        return ready;
    }

    inline Gdiplus::Color ToGdiplus(COLORREF colour)
    {
        return Gdiplus::Color(255, GetRValue(colour), GetGValue(colour), GetBValue(colour));
    }

    /// <summary>
    /// A 32bpp top-down DIB with a GDI+ surface on top, blitted to the band with alpha forced to
    /// 255. Everything antialiased (ring, triangles, cell frames) has to be composed against the
    /// band surface and then blitted opaque: plain GDI on the band HDC lets the taskbar backdrop
    /// shine through, and a half-covered AA pixel would otherwise blend with that backdrop.
    ///
    /// Coordinates handed to Graphics() are local to the surface (0..width, 0..height).
    /// </summary>
    class OpaqueSurface
    {
    public:
        // `supersample` renders into a larger surface; everything drawn on it must use scaled
        // coordinates (Scale() tells the caller by how much). The destructor downsamples with
        // bicubic, which is what makes 12 px glyphs look smooth: at 1x a hinted grayscale glyph
        // only ever covers one or two columns per stem, so its edges are steps no matter what.
        OpaqueSurface(HDC dst, int left, int top, int width, int height, COLORREF background,
                      int supersample = 1)
            : OpaqueSurface(dst, left, top, width, height, nullptr, 0, background, supersample)
        {
        }

        // Per-column fill from the taskbar tint: the band background is a gradient, so filling an
        // element with one flat sample leaves a visible rectangle once the element is wider than a
        // few pixels. `screenLeft` is the screen x of the surface's first column.
        OpaqueSurface(HDC dst, int left, int top, int width, int height, const BandTint* tint,
                      int screenLeft, COLORREF background, int supersample = 1)
            : _dst(dst), _left(left), _top(top), _width(width), _height(height),
              _scale(supersample < 1 ? 1 : supersample)
        {
            if (width <= 0 || height <= 0) return;

            int surfaceWidth = width * _scale;
            int surfaceHeight = height * _scale;

            BITMAPINFO info = { 0 };
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = surfaceWidth;
            info.bmiHeader.biHeight = -surfaceHeight;      // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;

            _memory = CreateCompatibleDC(dst);
            _bitmap = CreateDIBSection(dst, &info, DIB_RGB_COLORS, &_bits, nullptr, 0);
            if (_memory == nullptr || _bitmap == nullptr || _bits == nullptr) { Release(); return; }

            _previous = SelectObject(_memory, _bitmap);
            if (tint != nullptr && tint->Valid())
            {
                FillGradient(surfaceWidth, surfaceHeight, *tint, screenLeft);
            }
            else
            {
                RECT canvas = { 0, 0, surfaceWidth, surfaceHeight };
                FillRectOpaque(_memory, canvas, background);
            }
            // A fresh memory DC starts with an opaque white background: any GDI text drawn on it
            // would paint white boxes behind the glyphs.
            SetBkMode(_memory, TRANSPARENT);

            if (EnsureGdiplus())
            {
                _graphics = new Gdiplus::Graphics(_memory);
                _graphics->SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
                // Half puts integer coordinates on pixel centres, so an axis-aligned rect from
                // (x, y) to (x + w, y + h) covers whole pixels and stays exactly 1 px wide.
                _graphics->SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);
                _graphics->SetCompositingMode(Gdiplus::CompositingModeSourceOver);
            }
        }

        ~OpaqueSurface()
        {
            if (!Ok()) { Release(); return; }

            if (_scale == 1)
            {
                // Force full opacity so the blit is not treated as translucent.
                ForceOpaque(_bits, _width * _height);
                Blit(_bits, _width, _height);
                Release();
                return;
            }

            // Downsample: wrap the big DIB in a GDI+ bitmap and draw it into a 1x DIB with
            // high-quality interpolation, then blit that.
            int bigWidth = _width * _scale;
            int bigHeight = _height * _scale;
            ForceOpaque(_bits, bigWidth * bigHeight);

            BITMAPINFO info = { 0 };
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = _width;
            info.bmiHeader.biHeight = -_height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;

            void* smallBits = nullptr;
            HDC smallMemory = CreateCompatibleDC(_dst);
            HBITMAP smallBitmap = CreateDIBSection(_dst, &info, DIB_RGB_COLORS, &smallBits, nullptr, 0);
            bool done = false;
            if (smallMemory != nullptr && smallBitmap != nullptr && smallBits != nullptr && EnsureGdiplus())
            {
                HGDIOBJ previousSmall = SelectObject(smallMemory, smallBitmap);
                ForceOpaque(smallBits, _width * _height);

                Gdiplus::Bitmap source(bigWidth, bigHeight, bigWidth * 4,
                                       PixelFormat32bppARGB, (BYTE*)_bits);
                Gdiplus::Bitmap target(_width, _height, _width * 4,
                                       PixelFormat32bppARGB, (BYTE*)smallBits);
                Gdiplus::Graphics graphics(&target);
                graphics.SetCompositingMode(Gdiplus::CompositingModeSourceCopy);
                // Bilinear, not bicubic: at 12 px a glyph stem is 1 device pixel, and bicubic's
                // negative lobes thin such a stem to the point where the left arc of a 'C'
                // disappears.
                graphics.SetInterpolationMode(Gdiplus::InterpolationModeHighQualityBilinear);
                graphics.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);
                graphics.DrawImage(&source, Gdiplus::Rect(0, 0, _width, _height),
                                   0, 0, bigWidth, bigHeight, Gdiplus::UnitPixel);
                done = true;

                SelectObject(smallMemory, previousSmall);
            }

            if (done) Blit(smallBits, _width, _height);
            else Blit(_bits, bigWidth, bigHeight);   // fall back to the unscaled surface

            if (smallBitmap != nullptr) DeleteObject(smallBitmap);
            if (smallMemory != nullptr) DeleteDC(smallMemory);
            Release();
        }

        bool Ok() const { return _memory != nullptr && _bitmap != nullptr && _bits != nullptr; }
        Gdiplus::Graphics* Graphics() const { return _graphics; }
        int Scale() const { return _scale; }
        HDC Hdc() const { return _memory; }

    private:
        OpaqueSurface(const OpaqueSurface&);
        OpaqueSurface& operator=(const OpaqueSurface&);

        void Release()
        {
            delete _graphics;
            _graphics = nullptr;
            if (_memory != nullptr && _previous != nullptr) { SelectObject(_memory, _previous); _previous = nullptr; }
            if (_bitmap != nullptr) { DeleteObject(_bitmap); _bitmap = nullptr; }
            if (_memory != nullptr) { DeleteDC(_memory); _memory = nullptr; }
            _bits = nullptr;
        }

        HDC _dst;
        int _left;
        int _top;
        int _width;
        int _height;
        HDC _memory = nullptr;
        HBITMAP _bitmap = nullptr;
        void* _bits = nullptr;
        HGDIOBJ _previous = nullptr;
        Gdiplus::Graphics* _graphics = nullptr;
        int _scale = 1;

        // Writes the tint gradient straight into the DIB: column c of the surface covers logical
        // column c / _scale, whose colour is tint.At(screenLeft + c / _scale).
        void FillGradient(int surfaceWidth, int surfaceHeight, const BandTint& tint, int screenLeft)
        {
            COLORREF* row = new (std::nothrow) COLORREF[surfaceWidth];
            if (row == nullptr) return;
            for (int column = 0; column < surfaceWidth; column++)
            {
                COLORREF colour = tint.At(screenLeft + column / _scale);
                row[column] = ((DWORD)0xFF << 24) | ((DWORD)GetRValue(colour) << 16) |
                              ((DWORD)GetGValue(colour) << 8) | (DWORD)GetBValue(colour);
            }
            DWORD* pixels = (DWORD*)_bits;
            for (int y = 0; y < surfaceHeight; y++)
            {
                DWORD* target = pixels + (size_t)y * surfaceWidth;
                for (int x = 0; x < surfaceWidth; x++) target[x] = row[x];
            }
            delete[] row;
        }

        static void ForceOpaque(void* bits, int count)
        {
            DWORD* pixels = (DWORD*)bits;
            for (int i = 0; i < count; i++) pixels[i] |= 0xFF000000u;
        }

        void Blit(void* bits, int width, int height)
        {
            BITMAPINFO info = { 0 };
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;
            StretchDIBits(_dst, _left, _top, _width, _height, 0, 0, width, height,
                          bits, &info, DIB_RGB_COLORS, SRCCOPY);
        }
    };

    /// <summary>
    /// Colour of the ring at a given occupancy: green up to the warning point, green->yellow up to
    /// the warning, yellow->red across the warning..danger band, red above. This is the *band* the
    /// ring paints, not a per-zone solid colour: the whole arc runs green -> yellow -> red as it
    /// grows (user's correction: "一整圈由绿色到黄色再到红色的渐变").
    /// </summary>
    inline COLORREF RingRampColour(double percent, double warnAt, double dangerAt,
                                   COLORREF safe, COLORREF warn, COLORREF danger)
    {
        if (percent != percent) percent = 0;
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;
        if (dangerAt <= warnAt) dangerAt = warnAt + 1;

        double t;
        COLORREF a, b;
        if (percent < warnAt) { a = safe; b = warn; t = percent / warnAt; }
        else if (percent < dangerAt) { a = warn; b = danger; t = (percent - warnAt) / (dangerAt - warnAt); }
        else return danger;

        int r = (int)(GetRValue(a) + (GetRValue(b) - GetRValue(a)) * t + 0.5);
        int g = (int)(GetGValue(a) + (GetGValue(b) - GetGValue(a)) * t + 0.5);
        int bl = (int)(GetBValue(a) + (GetBValue(b) - GetBValue(a)) * t + 0.5);
        return RGB(r, g, bl);
    }

    /// <summary>
    /// Gradient ring: a dark track plus an arc that starts at 12 o'clock and runs clockwise by the
    /// value. The arc colour follows the occupancy ramp (green -> yellow at the warning point ->
    /// red at the danger point) **along the arc**, and there is no white outline. Same opaque DIB
    /// + GDI+ path as the solid gauge: alpha forced to 255 so the taskbar backdrop cannot glow
    /// through, anti-aliasing on.
    /// </summary>
    inline void DrawGradientRingOpaque(HDC dst, int left, int top, int diameter, int percent,
                                       COLORREF safe, COLORREF warn, COLORREF danger,
                                       double warnAt, double dangerAt, COLORREF background,
                                       double scale,
                                       const wchar_t* centerText, COLORREF centerColor, HFONT centerFont)
    {
        if (diameter < 6) return;
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        int size = diameter + 2;
        BITMAPINFO info = { 0 };
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = size;
        info.bmiHeader.biHeight = -size;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HDC memory = CreateCompatibleDC(dst);
        HBITMAP bitmap = CreateDIBSection(dst, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (memory == nullptr || bitmap == nullptr || bits == nullptr)
        {
            if (bitmap != nullptr) DeleteObject(bitmap);
            if (memory != nullptr) DeleteDC(memory);
            return;
        }

        HGDIOBJ oldBitmap = SelectObject(memory, bitmap);
        RECT canvas = { 0, 0, size, size };
        FillRectOpaque(memory, canvas, background);

        int radius = diameter / 2;
        int cx = radius;
        int cy = radius;

        // 3 px of design ink at 100%: the 2.6 px band read as a hairline next to the 11 px centre
        // number, and with the colour ramp the band is the thing that carries the value.
        double thickness = (scale > 1.0 ? scale : 1.0) * 3.0;
        double outer = radius;
        double inner = outer - thickness;
        if (inner < 1.5) inner = 1.5;

        COLORREF valueColour = RingRampColour(percent, warnAt, dangerAt, safe, warn, danger);
        COLORREF trackColour = RGB((GetRValue(background) * 78 + GetRValue(valueColour) * 22) / 100,
                                   (GetGValue(background) * 78 + GetGValue(valueColour) * 22) / 100,
                                   (GetBValue(background) * 78 + GetBValue(valueColour) * 22) / 100);

        bool smooth = EnsureGdiplus();
        if (smooth)
        {
            Gdiplus::Graphics graphics(memory);
            graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
            graphics.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);
            graphics.SetCompositingMode(Gdiplus::CompositingModeSourceOver);

            Gdiplus::GraphicsPath track;
            track.AddEllipse((Gdiplus::REAL)(cx - outer), (Gdiplus::REAL)(cy - outer),
                             (Gdiplus::REAL)(outer * 2), (Gdiplus::REAL)(outer * 2));
            track.AddEllipse((Gdiplus::REAL)(cx - inner), (Gdiplus::REAL)(cy - inner),
                             (Gdiplus::REAL)(inner * 2), (Gdiplus::REAL)(inner * 2));
            Gdiplus::SolidBrush trackBrush(ToGdiplus(trackColour));
            graphics.FillPath(&trackBrush, &track);

            if (percent > 0)
            {
                // GDI+ has no conic brush, so the arc is stroked in short segments and each one
                // takes the ramp colour of its own position: at 2 degrees per segment the colour
                // step is under 2 units, which reads as one continuous green -> yellow -> red
                // sweep instead of a per-zone flat colour.
                double sweep = 360.0 * percent / 100.0;
                // 4 degrees per segment with round caps: at a 26 px ring a 2-degree step is under
                // one pixel of arc, so every segment lands as a half-covered dot and the whole
                // band reads dark. 4 degrees + round caps + a 0.5 degree overlap keeps the band
                // solid while the colour still moves under 3 units per segment.
                const double stepDegrees = 4.0;
                int segments = (int)(sweep / stepDegrees);
                if (segments < 1) segments = 1;

                double inset = thickness / 2.0;
                Gdiplus::Pen pen(Gdiplus::Color(255, 0, 0, 0), (Gdiplus::REAL)thickness);
                pen.SetStartCap(Gdiplus::LineCapRound);
                pen.SetEndCap(Gdiplus::LineCapRound);
                for (int i = 0; i < segments; i++)
                {
                    double a0 = i * stepDegrees;
                    double a1 = (i + 1 == segments) ? sweep : (i + 1) * stepDegrees;
                    double middle = (a0 + a1) / 2.0 / 360.0 * 100.0;
                    pen.SetColor(ToGdiplus(RingRampColour(middle, warnAt, dangerAt, safe, warn, danger)));
                    graphics.DrawArc(&pen, (Gdiplus::REAL)(cx - radius + inset),
                                     (Gdiplus::REAL)(cy - radius + inset),
                                     (Gdiplus::REAL)(diameter - thickness),
                                     (Gdiplus::REAL)(diameter - thickness),
                                     (Gdiplus::REAL)(-90.0 + a0), (Gdiplus::REAL)(a1 - a0 + 0.5));
                }
            }
        }
        else
        {
            // GDI fallback: no ramp, but the geometry (track + arc from 12 o'clock) still holds.
            int penWidth = (int)(thickness + 0.5);
            if (penWidth < 1) penWidth = 1;
            HPEN trackPen = CreatePen(PS_SOLID, penWidth, trackColour);
            HGDIOBJ oldPen = SelectObject(memory, trackPen);
            HGDIOBJ oldBrush = SelectObject(memory, GetStockObject(NULL_BRUSH));
            Ellipse(memory, cx - radius + penWidth, cy - radius + penWidth,
                    cx + radius - penWidth, cy + radius - penWidth);
            SelectObject(memory, oldBrush);
            SelectObject(memory, oldPen);
            DeleteObject(trackPen);

            if (percent > 0)
            {
                int radiusPx = radius - penWidth;
                double endRad = (360.0 * percent / 100.0 - 90.0) * 3.14159265358979 / 180.0;
                int endX = cx + (int)(radiusPx * cos(endRad));
                int endY = cy + (int)(radiusPx * sin(endRad));
                HPEN arcPen = CreatePen(PS_SOLID, penWidth, valueColour);
                oldPen = SelectObject(memory, arcPen);
                Arc(memory, cx - radiusPx, cy - radiusPx, cx + radiusPx, cy + radiusPx,
                    cx, cy - radiusPx, endX, endY);
                SelectObject(memory, oldPen);
                DeleteObject(arcPen);
            }
        }

        if (centerText != nullptr && centerFont != nullptr)
        {
            HGDIOBJ oldFont = SelectObject(memory, centerFont);
            RECT textRect = { 0, 0, radius * 2 + 1, radius * 2 + 1 };
            SetBkMode(memory, TRANSPARENT);
            SetTextColor(memory, centerColor);
            ::DrawTextW(memory, centerText, -1, &textRect,
                        DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(memory, oldFont);
        }

        DWORD* pixels = (DWORD*)bits;
        for (int i = 0; i < size * size; i++) pixels[i] |= 0xFF000000u;

        StretchDIBits(dst, left, top, size, size, 0, 0, size, size, bits, &info, DIB_RGB_COLORS, SRCCOPY);

        SelectObject(memory, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(memory);
    }

    /// <summary>
    /// Ring gauge drawn through a 32bpp DIB with alpha forced to 255. GDI path fills (Pie) reset
    /// the alpha channel, which lets the taskbar backdrop glow through again, so the wedge is
    /// composed off-screen and blitted as an opaque bitmap.
    ///
    /// The drawing itself is GDI+ with anti-aliasing: at 25 px (31 px at 125%) GDI's Pie/Ellipse
    /// quantise every edge to whole pixels, which reads as a visibly stepped circle.
    /// </summary>
    inline void DrawRingGaugeOpaque(HDC dst, int left, int top, int diameter, int percent,
                                    COLORREF fill, COLORREF outline, COLORREF background,
                                    double scale = 1.0,
                                    const wchar_t* centerText = nullptr,
                                    COLORREF centerColor = 0,
                                    HFONT centerFont = nullptr)
    {
        if (diameter < 6) return;
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        int size = diameter + 2;               // 1 px breathing room for the outline
        BITMAPINFO info = { 0 };
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = size;
        info.bmiHeader.biHeight = -size;       // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HDC memory = CreateCompatibleDC(dst);
        HBITMAP bitmap = CreateDIBSection(dst, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (memory == nullptr || bitmap == nullptr || bits == nullptr)
        {
            if (bitmap != nullptr) DeleteObject(bitmap);
            if (memory != nullptr) DeleteDC(memory);
            return;
        }

        HGDIOBJ oldBitmap = SelectObject(memory, bitmap);

        RECT canvas = { 0, 0, size, size };
        FillRectOpaque(memory, canvas, background);

        int radius = diameter / 2;
        // The ring's outer edge has to land on the band's content rows: offsetting the centre by
        // the surface's 1 px breathing room pushed the whole circle one pixel down, so it no longer
        // lined up with the bar cells' top and bottom edges.
        int cx = radius;
        int cy = radius;

        bool smooth = EnsureGdiplus();
        if (smooth)
        {
            Gdiplus::Graphics graphics(memory);
            graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
            // Half-pixel offsets: the DIB is pixel-addressed, GDI+ paths are coordinate-based.
            graphics.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);
            graphics.SetCompositingMode(Gdiplus::CompositingModeSourceOver);

            if (percent > 0)
            {
                int wedge = radius - 1;
                Gdiplus::SolidBrush brush(ToGdiplus(fill));
                // GDI+ angles are degrees clockwise from the +x axis (y points down), so 12 o'clock
                // is -90 and a positive sweep runs clockwise like the mould does.
                graphics.FillPie(&brush, (Gdiplus::REAL)(cx - wedge), (Gdiplus::REAL)(cy - wedge),
                                 (Gdiplus::REAL)(wedge * 2), (Gdiplus::REAL)(wedge * 2),
                                 -90.0f, (Gdiplus::REAL)(360.0 * percent / 100.0));
            }

            // The outline is a filled annulus, not a 1 px pen: a centred 1 px pen is ~50% covered
            // on every pixel at this radius, so anti-aliasing would turn the white ring grey.
            // Filling the band keeps full white where the ring covers a whole pixel and only
            // feathers the inner/outer boundaries.
            // 1 px of design ink needs ~1.5 px of coverage at 100%: with exactly 1.0 the annulus
            // never fills a whole pixel, so every pixel is feathered and the ring reads grey.
            double thickness = (scale > 1.0 ? scale : 1.0) * 1.0;
            double outerRadius = radius;
            double innerRadius = outerRadius - thickness;
            if (innerRadius < 2.0) innerRadius = 2.0;
            Gdiplus::GraphicsPath ring;
            ring.AddEllipse((Gdiplus::REAL)(cx - outerRadius), (Gdiplus::REAL)(cy - outerRadius),
                            (Gdiplus::REAL)(outerRadius * 2), (Gdiplus::REAL)(outerRadius * 2));
            ring.AddEllipse((Gdiplus::REAL)(cx - innerRadius), (Gdiplus::REAL)(cy - innerRadius),
                            (Gdiplus::REAL)(innerRadius * 2), (Gdiplus::REAL)(innerRadius * 2));
            Gdiplus::SolidBrush ringBrush(ToGdiplus(outline));
            graphics.FillPath(&ringBrush, &ring);
        }
        else
        {
            if (percent > 0)
            {
                int wedge = radius - 1;
                double endRad = (360.0 * percent / 100.0 - 90.0) * 3.14159265358979 / 180.0;
                int endX = cx + (int)(wedge * cos(endRad) + 0.5);
                int endY = cy + (int)(wedge * sin(endRad) + 0.5);
                HBRUSH brush = CreateSolidBrush(fill);
                HGDIOBJ oldBrush = SelectObject(memory, brush);
                HGDIOBJ oldPen = SelectObject(memory, GetStockObject(NULL_PEN));
                Pie(memory, cx - wedge, cy - wedge, cx + wedge + 1, cy + wedge + 1, cx, cy - wedge, endX, endY);
                SelectObject(memory, oldPen);
                SelectObject(memory, oldBrush);
                DeleteObject(brush);
            }

            HPEN pen = CreatePen(PS_SOLID, 1, outline);
            HGDIOBJ oldPenRing = SelectObject(memory, pen);
            HGDIOBJ oldBrushRing = SelectObject(memory, GetStockObject(NULL_BRUSH));
            Ellipse(memory, cx - radius, cy - radius, cx + radius + 1, cy + radius + 1);
            SelectObject(memory, oldBrushRing);
            SelectObject(memory, oldPenRing);
            DeleteObject(pen);
        }

        // Force full opacity so the blit is not treated as translucent.
        // The centre number (CPU / temperature rings) is drawn here, on the memory DC: GDI then
        // falls back to greyscale antialiasing, so the band gets the glyphs only, without
        // ClearType colour fringes or the faint darkened box ClearType leaves behind.
        if (centerText != nullptr && centerFont != nullptr)
        {
            HGDIOBJ oldFont = SelectObject(memory, centerFont);
            RECT textRect = { 0, 0, radius * 2 + 1, radius * 2 + 1 };
            SetBkMode(memory, TRANSPARENT);
            SetTextColor(memory, centerColor);
            ::DrawTextW(memory, centerText, -1, &textRect,
                        DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(memory, oldFont);
        }

        DWORD* pixels = (DWORD*)bits;
        for (int i = 0; i < size * size; i++) pixels[i] |= 0xFF000000u;

        StretchDIBits(dst, left, top, size, size, 0, 0, size, size, bits, &info, DIB_RGB_COLORS, SRCCOPY);

        SelectObject(memory, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(memory);
    }
}
