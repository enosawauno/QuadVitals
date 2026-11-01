#include "Renderer.h"
#include "OpaqueFill.h"

#include <math.h>
#include <strsafe.h>
#include <stdio.h>

namespace pb
{
    namespace
    {
        const double kEmaAlpha = 0.45;          // ~3 s to settle at 1 Hz: steady without lag
        // Triangle gutter. 11 px = 2 px air after the number + a 9 px triangle, so the arrow no
        // longer touches the digits (it used to start exactly at the text's right edge).
        const int kIndicatorWidth = 11;
        // Slender arrow: same 9 px base, 11 px tall instead of 7 (the old shape read as a blob).
        const int kIndicatorHeight = 11;
        // Text row height. 11 px is the largest that still fits two rows inside the 26 px content
        // band, which is what keeps the text's ink edges on the bars' edges.
        const int kTextFontPx = 11;
        const COLORREF kInvalidGrey = RGB(0x6E, 0x78, 0x82);

        // Whether any row of a text element draws a triangle: the gutter (and its 11 px) belongs
        // to the element only in that case.
        bool HasIndicator(const Element& element)
        {
            for (const TextLine& line : element.lines)
            {
                if (line.indicator != 0) return true;
            }
            return false;
        }

        int ClampPercent(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        // Refresh gates compare snapshot timestamps, not paint times: a 1 s timer that fires
        // 3 ms early must not skip a whole sample (which would halve the visible refresh rate).
        const long long kRefreshToleranceTicks = 1000000LL; // 100 ms

        int Px(double scale, int value) { return (int)(value * scale + 0.5); }

        // Opaque fill: plain GDI lets the taskbar's backdrop glow through (see OpaqueFill.h).
        void FillColor(HDC hdc, const RECT& rc, COLORREF color)
        {
            FillRectOpaque(hdc, rc, color);
        }

        void FrameColor(HDC hdc, const RECT& rc, COLORREF color)
        {
            HBRUSH brush = CreateSolidBrush(color);
            FrameRect(hdc, &rc, brush);
            DeleteObject(brush);
        }

        // Cached: the band repaints at 1 Hz inside Explorer, so font churn is not acceptable.
        // Several sizes are alive at once (text at 1x for measuring, the same text at 3x for
        // rasterising, the ring centre on top). A single-slot cache deleted the handle that a
        // caller still held, which silently produced garbage widths.
        HFONT MakeFont(int pixelHeight, int weight)
        {
            struct Slot { HFONT font; int height; int weight; };
            static Slot slots[6] = { { nullptr, -1, 0 }, { nullptr, -1, 0 }, { nullptr, -1, 0 },
                                     { nullptr, -1, 0 }, { nullptr, -1, 0 }, { nullptr, -1, 0 } };

            for (int i = 0; i < 6; i++)
            {
                if (slots[i].font != nullptr && slots[i].height == pixelHeight && slots[i].weight == weight)
                {
                    return slots[i].font;
                }
            }

            LOGFONTW lf = { 0 };
            lf.lfHeight = -pixelHeight;
            lf.lfWeight = weight;
            // Greyscale antialiasing, not ClearType: every glyph is drawn into a memory DC and
            // blitted, and subpixel rendering there leaves red/blue fringes on the dark band
            // (measured: a 45x10 text box carried #191971 / #363319 / #2D1919 edge pixels).
            lf.lfQuality = ANTIALIASED_QUALITY;
            StringCchCopyW(lf.lfFaceName, ARRAYSIZE(lf.lfFaceName), L"Segoe UI");
            HFONT font = CreateFontIndirectW(&lf);
            if (font == nullptr) return nullptr;

            for (int i = 0; i < 6; i++)
            {
                if (slots[i].font == nullptr || slots[i].height < 0)
                {
                    slots[i].font = font;
                    slots[i].height = pixelHeight;
                    slots[i].weight = weight;
                    return font;
                }
            }
            // Cache full: reuse slot 0 (the previous font is deleted only if nobody else holds it,
            // which cannot be known here, so leak-proof recycling is safer than deleting).
            return font;
        }

        int TextWidthPx(HDC hdc, HFONT font, const wchar_t* text)
        {
            HGDIOBJ old = SelectObject(hdc, font);
            SIZE size = { 0, 0 };
            GetTextExtentPoint32W(hdc, text, (int)wcslen(text), &size);
            SelectObject(hdc, old);
            return size.cx;
        }

        // `slack` widens the clip box on the left only. GDI right-aligns the *advance* box to
        // rc.right, but an antialiased glyph can paint a pixel or two left of it; without the
        // slack the first character of a row loses its left edge ("CPU" showing as "|PU").
        // rc.right stays put, so the alignment itself does not move.
        void DrawTextRight(HDC hdc, HFONT font, COLORREF color, const wchar_t* text,
                           int right, int top, int height, int slack = 0)
        {
            int width = TextWidthPx(hdc, font, text);
            HGDIOBJ oldFont = SelectObject(hdc, font);
            RECT rc = { right - width - slack, top, right, top + height };
            SetTextColor(hdc, color);
            DrawTextW(hdc, text, -1, &rc, DT_RIGHT | DT_TOP | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(hdc, oldFont);
        }

        // Where the glyph ink actually starts and ends inside an 11 px layout box.
        // Line placement is expressed in ink, not in layout boxes: the user asked for the top
        // row's ink to touch the bars' top edge and the bottom row's ink to touch their bottom
        // edge, and the layout box carries one or two rows of internal leading above the caps.
        struct InkMetrics
        {
            int top = 0;
            int bottom = 0;
        };

        InkMetrics MeasureInk(HFONT measureFont, HFONT drawFont, int fontHeight, int supersample)
        {
            // Measured through the same path the band uses (supersample + downsample): the soft
            // downsample ramp sticks out a pixel beyond the hinted 1x ink, and without that the
            // "text ink touches the bar edge" alignment quietly loses a pixel.
            struct CacheEntry { HFONT font; int height; int scale; InkMetrics ink; };
            static CacheEntry cache[6] = { { nullptr, -1, 1, { 0, 0 } }, { nullptr, -1, 1, { 0, 0 } },
                                           { nullptr, -1, 1, { 0, 0 } }, { nullptr, -1, 1, { 0, 0 } },
                                           { nullptr, -1, 1, { 0, 0 } }, { nullptr, -1, 1, { 0, 0 } } };
            for (int slot = 0; slot < 6; slot++)
            {
                if (cache[slot].font == measureFont && cache[slot].height == fontHeight &&
                    cache[slot].scale == supersample)
                {
                    return cache[slot].ink;
                }
            }

            InkMetrics ink = { 0, fontHeight - 1 };
            int width = fontHeight * 6;
            if (width < 96) width = 96;
            int height = fontHeight * 3;
            if (height < 12) height = 12;

            BITMAPINFO info = { 0 };
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;

            HDC screen = GetDC(nullptr);
            void* bits = nullptr;
            HDC memory = screen != nullptr ? CreateCompatibleDC(screen) : nullptr;
            HBITMAP bitmap = memory != nullptr
                ? CreateDIBSection(memory, &info, DIB_RGB_COLORS, &bits, nullptr, 0) : nullptr;

            if (screen != nullptr && memory != nullptr && bitmap != nullptr && bits != nullptr)
            {
                HGDIOBJ oldBitmap = SelectObject(memory, bitmap);
                DWORD* pixels = (DWORD*)bits;
                for (int i = 0; i < width * height; i++) pixels[i] = 0xFF1A1A1Au;

                {
                    OpaqueSurface probe(memory, 0, 0, width, height, RGB(0x1A, 0x1A, 0x1A), supersample);
                    if (probe.Ok())
                    {
                        // Digits + "KB/s": the widest ink span of any value this band prints
                        // ('/' has the lowest descender, the digits the highest cap).
                        // The probe surface is supersampled, so the text and its box have to be
                        // given in surface pixels; a 1x box clipped the glyphs and the measured
                        // ink came out several rows short.
                        DrawTextRight(probe.Hdc(), drawFont, RGB(255, 255, 255), L"8 KB/s",
                                      (width - 1) * supersample, 0, fontHeight * 2 * supersample,
                                      4 * supersample);
                    }
                }

                int first = -1;
                int last = -1;
                for (int row = 0; row < height; row++)
                {
                    bool inked = false;
                    for (int column = 0; column < width && !inked; column++)
                    {
                        DWORD value = pixels[row * width + column];
                        int b = (int)(value & 0xFF);
                        int g = (int)((value >> 8) & 0xFF);
                        int r = (int)((value >> 16) & 0xFF);
                        if (abs(b - 0x1A) > 3 || abs(g - 0x1A) > 3 || abs(r - 0x1A) > 3)
                        {
                            inked = true;
                        }
                    }
                    if (inked)
                    {
                        if (first < 0) first = row;
                        last = row;
                    }
                }
                if (first >= 0 && last >= first)
                {
                    ink.top = first;
                    ink.bottom = last;
                }

                SelectObject(memory, oldBitmap);
            }

            if (bitmap != nullptr) DeleteObject(bitmap);
            if (memory != nullptr) DeleteDC(memory);
            if (screen != nullptr) ReleaseDC(nullptr, screen);

            for (int slot = 0; slot < 6; slot++)
            {
                if (cache[slot].font == nullptr || cache[slot].font == measureFont)
                {
                    cache[slot].font = measureFont;
                    cache[slot].height = fontHeight;
                    cache[slot].scale = supersample;
                    cache[slot].ink = ink;
                    return ink;
                }
            }
            cache[0].font = measureFont;
            cache[0].height = fontHeight;
            cache[0].scale = supersample;
            cache[0].ink = ink;
            return ink;
        }

        // Filled triangle with the same GDI+ surface the ring uses: a 7x6 px triangle drawn by
        // GDI Polygon() is all staircase on its two diagonals.
        void DrawTriangle(HDC hdc, int cx, int cy, int halfW, int halfH, bool up, COLORREF color,
                          COLORREF background)
        {
            POINT points[3];
            if (up)
            {
                points[0].x = cx; points[0].y = cy - halfH;
                points[1].x = cx - halfW; points[1].y = cy + halfH;
                points[2].x = cx + halfW; points[2].y = cy + halfH;
            }
            else
            {
                points[0].x = cx; points[0].y = cy + halfH;
                points[1].x = cx - halfW; points[1].y = cy - halfH;
                points[2].x = cx + halfW; points[2].y = cy - halfH;
            }

            int left = cx - halfW;
            int top = cy - halfH;
            int width = halfW * 2 + 1;
            int height = halfH * 2 + 1;
            // The arrow is 9 px wide: a flat tint is indistinguishable at that size.
            OpaqueSurface surface(hdc, left, top, width, height, background);
            Gdiplus::Graphics* graphics = surface.Ok() ? surface.Graphics() : nullptr;
            if (graphics != nullptr)
            {
                Gdiplus::PointF local[3];
                for (int i = 0; i < 3; i++)
                {
                    local[i].X = (Gdiplus::REAL)(points[i].x - left);
                    local[i].Y = (Gdiplus::REAL)(points[i].y - top);
                }
                Gdiplus::SolidBrush brush(ToGdiplus(color));
                graphics->FillPolygon(&brush, local, 3);
                return;
            }

            HBRUSH brush = CreateSolidBrush(color);
            HGDIOBJ oldBrush = SelectObject(hdc, brush);
            HGDIOBJ oldPen = SelectObject(hdc, GetStockObject(NULL_PEN));
            Polygon(hdc, points, 3);
            SelectObject(hdc, oldPen);
            SelectObject(hdc, oldBrush);
            DeleteObject(brush);
        }

        // Same geometry as Renderer::DrawBarFill, but filled straight onto the shared bar surface
        // (coordinates local to it, no per-rect DIB).
        void FillBarRect(Gdiplus::Graphics* graphics, Gdiplus::Brush* brush, int cellX, int cellWidth,
                         int barHeight, int inset, int percent)
        {
            int inner = barHeight - inset * 2;
            int fillHeight = MulDiv(inner, percent, 100);
            if (fillHeight < 1) fillHeight = 1;
            int width = cellWidth - inset * 2;
            if (width <= 0 || inner <= 0) return;
            if (fillHeight > inner) fillHeight = inner;
            graphics->FillRectangle(brush, cellX + inset, barHeight - inset - fillHeight, width, fillHeight);
        }
    }

    // ---------------------------------------------------------------- formatting

    std::wstring Renderer::FormatRate(double bytesPerSecond)
    {
        if (!(bytesPerSecond >= 0.0) || bytesPerSecond != bytesPerSecond) return L"--";

        double value = bytesPerSecond;
        // Lower-case "s" everywhere: the dashboard (QuadVitals.Core.MetricFormat) prints the same
        // suffix, and two different spellings of the unit across two windows is worse than either.
        const wchar_t* unit = L"B/s";
        if (value >= 1024.0) { value /= 1024.0; unit = L"KB/s"; }
        if (value >= 1024.0) { value /= 1024.0; unit = L"MB/s"; }
        if (value >= 1024.0) { value /= 1024.0; unit = L"GB/s"; }

        // Whole numbers only: the band is 12 px tall per row, and a decimal point costs a glyph
        // slot while adding no decision value for "how fast is the network right now".
        wchar_t number[32];
        StringCchPrintfW(number, ARRAYSIZE(number), L"%.0f", floor(value + 0.5));

        std::wstring text(number);
        text += L" ";
        text += unit;
        return text;
    }

    std::wstring Renderer::FormatPercent(double percent)
    {
        if (percent != percent) return L"--";
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        wchar_t buffer[32];
        StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"%.0f%%", percent);
        return std::wstring(buffer);
    }

    std::wstring Renderer::FormatTemperature(double celsius)
    {
        if (celsius != celsius) return L"--";
        wchar_t buffer[32];
        StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"%.0f\u2103", celsius);
        return std::wstring(buffer);
    }

    // Hardware the value belongs to: a bare "65" says nothing about *what* is hot, and once
    // a GPU sensor exists the two temperatures have to be told apart at a glance.
    static const wchar_t* HardwareLabel(MetricId id)
    {
        switch (id)
        {
        case MetricId::Temperature: return L"CPU ";
        case MetricId::GpuTemp: return L"GPU ";
        default: return L"";
        }
    }

    // Adaptive temperature colour. A temperature line has no fixed colour: the number is only
    // useful together with "is this fine". Bands mirror Core.MetricTones/TemperatureZones
    // (CPU 80/90, GPU 75/83) and use the dashboard's tone colours so both windows agree.
    static const COLORREF kTempSafe = RGB(0x41, 0xD4, 0x84);
    static const COLORREF kTempWarn = RGB(0xF6, 0xA7, 0x48);
    static const COLORREF kTempDanger = RGB(0xD2, 0x47, 0x47);

    static COLORREF TemperatureColour(double celsius, double warning, double danger)
    {
        if (celsius != celsius) return kTempSafe;
        if (celsius >= danger) return kTempDanger;
        if (celsius >= warning) return kTempWarn;
        return kTempSafe;
    }

    std::wstring Renderer::FormatMetric(MetricId id, const Snapshot& snapshot, bool detailed, wchar_t drive)
    {
        // Per-drive disk I/O: the element chose a volume, so read that slot instead of the
        // whole-system total. A missing/invalid slot is "--", never a fake zero.
        if (drive != 0 && (id == MetricId::DiskIo || id == MetricId::DiskIoRead || id == MetricId::DiskIoWrite))
        {
            bool valid = false;
            double read = 0.0;
            double write = 0.0;
            if (!snapshot.TryVolume(drive, valid, read, write) || !valid) return L"--";
            double value = (id == MetricId::DiskIoWrite) ? write : (id == MetricId::DiskIoRead ? read : read + write);
            return FormatRate(value);
        }

        switch (id)
        {
        case MetricId::Cpu:
            if (!snapshot.Has(FlagCpuValid)) return L"--";
            return detailed ? FormatPercent(snapshot.cpuPercent) : FormatPercent(snapshot.cpuPercent);
        case MetricId::Memory:
            if (!snapshot.Has(FlagMemoryValid)) return L"--";
            return FormatPercent(snapshot.memoryPercent);
        case MetricId::Temperature:
            if (!snapshot.Has(FlagTemperatureValid)) return L"--";
            return FormatTemperature(snapshot.temperatureC);
        case MetricId::NetUp:
            if (!snapshot.Has(FlagNetworkValid)) return L"--";
            return FormatRate(snapshot.netUpBytesPerSec);
        case MetricId::NetDown:
            if (!snapshot.Has(FlagNetworkValid)) return L"--";
            return FormatRate(snapshot.netDownBytesPerSec);
        case MetricId::Gpu:
            if (!snapshot.Has(FlagGpuValid)) return L"--";
            return FormatPercent(snapshot.gpuLoadPercent);
        case MetricId::GpuMemory:
            if (!snapshot.Has(FlagGpuValid)) return L"--";
            return FormatPercent(snapshot.gpuMemoryPercent);
        case MetricId::GpuTemp:
            if (!snapshot.Has(FlagGpuTemperatureValid)) return L"--";
            return FormatTemperature(snapshot.gpuTemperatureC);
        case MetricId::Disk:
            if (!snapshot.Has(FlagDiskValid)) return L"--";
            return FormatPercent(snapshot.diskPercent);
        case MetricId::DiskIo:
            if (!snapshot.Has(FlagDiskIoValid)) return L"--";
            return FormatRate((double)snapshot.diskIoReadBytesPerSec + (double)snapshot.diskIoWriteBytesPerSec);
        case MetricId::DiskIoRead:
            if (!snapshot.Has(FlagDiskIoValid)) return L"--";
            return FormatRate(snapshot.diskIoReadBytesPerSec);
        case MetricId::DiskIoWrite:
            if (!snapshot.Has(FlagDiskIoValid)) return L"--";
            return FormatRate(snapshot.diskIoWriteBytesPerSec);
        default:
            return L"--";
        }
    }

    // ---------------------------------------------------------------- element width

    Renderer::~Renderer()
    {
        if (_measureDc != nullptr) { DeleteDC(_measureDc); _measureDc = nullptr; }
    }

    HDC Renderer::MeasureDc()
    {
        if (_measureDc == nullptr) _measureDc = CreateCompatibleDC(nullptr);
        return _measureDc;
    }

    std::wstring Renderer::LineText(const TextLine& line, const Snapshot& snapshot, bool agentAlive, wchar_t drive)
    {
        if (!agentAlive) return L"--";
        std::wstring text = HardwareLabel(line.metric);
        text += FormatMetric(line.metric, snapshot, false, drive);
        return text;
    }

    std::wstring Renderer::WidestLineText(const TextLine& line)
    {
        std::wstring text = HardwareLabel(line.metric);
        if (line.metric == MetricId::Temperature || line.metric == MetricId::GpuTemp)
        {
            // 99 ℃：CPU/GPU 温度的实际最大值就是两位数，按三位数预留给条宽会白占一截。
            text += FormatTemperature(99.0);
        }
        else if (IsPercentMetric(line.metric))
        {
            text += FormatPercent(100.0);
        }
        else
        {
            // "1023 MB/s" is the widest string the rate formatter can produce (4 digits, and a
            // bigger value would have moved up to GB/s).
            text += FormatRate(1023.0 * 1024.0 * 1024.0);
        }
        return text;
    }

    int Renderer::OuterWidthPx(const Element& element, int textFieldPx, double scale) const
    {
        switch (element.type)
        {
        case ElementType::Bar:
        {
            // Per-part rounding: DrawBar advances by Px(cellWidth) + Px(cellGap) per column, so
            // scaling the whole sum once would be up to 3 px short at 125%.
            int columns = element.historyColumns < 1 ? 1 : element.historyColumns;
            return columns * Px(scale, element.cellWidth) + (columns - 1) * Px(scale, element.cellGap);
        }
        case ElementType::Pie:
            return Px(scale, element.diameter);
        case ElementType::Text:
            return textFieldPx + (HasIndicator(element) ? Px(scale, kIndicatorWidth) : 0);
        default:
            return 0;
        }
    }

    int Renderer::TextFieldWidthPx(size_t index, const Element& element, double scale) const
    {
        int floorPx = Px(scale, element.width);
        int reservePx = index < _states.size() ? Px(scale, _states[index].reserve1x) : 0;
        return reservePx > floorPx ? reservePx : floorPx;
    }

    int Renderer::ElementWidthPx(size_t index, const Element& element, double scale) const
    {
        if (!element.enabled) return 0;
        int textFieldPx = element.type == ElementType::Text
            ? TextFieldWidthPx(index, element, scale) : 0;
        return OuterWidthPx(element, textFieldPx, scale);
    }

    void Renderer::MeasureText(HDC hdc, const BandSettings& settings, const Snapshot& snapshot,
                               bool agentAlive, double scale)
    {
        if (hdc == nullptr || _states.size() != settings.items.size()) return;

        int fontHeight = Px(scale, kTextFontPx);
        HFONT font = MakeFont(fontHeight < 8 ? 8 : fontHeight, FW_NORMAL);
        if (font == nullptr) return;

        for (size_t i = 0; i < settings.items.size(); i++)
        {
            const Element& element = settings.items[i];
            if (!element.enabled || element.type != ElementType::Text) continue;

            int widest = 0;
            for (size_t j = 0; j < element.lines.size() && j < 2; j++)
            {
                int width = TextWidthPx(hdc, font, LineText(element.lines[j], snapshot, agentAlive, element.drive).c_str());
                if (width > widest) widest = width;
            }

            // Round up to 1x units so the field is never narrower than what was measured.
            int oneX = (int)(widest / scale + 0.999);
            if (oneX > _states[i].reserve1x) _states[i].reserve1x = oneX;
        }
    }

    int Renderer::RequiredWidthPx(const BandSettings& settings, double scale)
    {
        if (scale < 0.5) scale = 1.0;

        HDC hdc = MeasureDc();
        int fontHeight = Px(scale, kTextFontPx);
        HFONT font = hdc != nullptr ? MakeFont(fontHeight < 8 ? 8 : fontHeight, FW_NORMAL) : nullptr;

        int width = 0;
        int enabledSeen = 0;
        for (size_t i = 0; i < settings.items.size(); i++)
        {
            const Element& element = settings.items[i];
            if (!element.enabled || element.type == ElementType::Unknown) continue;

            int textFieldPx = 0;
            if (element.type == ElementType::Text)
            {
                int widest = 0;
                for (size_t j = 0; j < element.lines.size() && j < 2; j++)
                {
                    int measured = font != nullptr
                        ? TextWidthPx(hdc, font, WidestLineText(element.lines[j]).c_str())
                        : 0;
                    if (measured > widest) widest = measured;
                }
                int floorPx = Px(scale, element.width);
                textFieldPx = widest > floorPx ? widest : floorPx;
            }

            int elementWidth = OuterWidthPx(element, textFieldPx, scale);
            if (elementWidth <= 0) continue;
            if (enabledSeen > 0) width += Px(scale, settings.horizontalSpacing);
            width += elementWidth;
            enabledSeen++;
        }

        int minWidth = Px(scale, 40);
        int maxWidth = Px(scale, 1200);
        if (enabledSeen == 0) return minWidth;

        width += Px(scale, settings.trailingPad);
        if (width < minWidth) width = minWidth;
        if (width > maxWidth) width = maxWidth;
        return width;
    }

    // ---------------------------------------------------------------- sampling

    void Renderer::SetTint(const BandTint& tint, int screenLeft)
    {
        _tint = tint;
        _tintScreenLeft = screenLeft;
    }

    void Renderer::Reset(const BandSettings& settings)
    {
        _states.clear();
        _states.resize(settings.items.size());
    }

    double Renderer::ValueOf(const Element& element, const Snapshot& snapshot, int lineIndex, bool agentAlive)
    {
        if (!agentAlive) return NAN;

        MetricId id = element.type == ElementType::Text
            ? (lineIndex >= 0 && lineIndex < (int)element.lines.size() ? element.lines[lineIndex].metric : MetricId::None)
            : element.metric;

        if (element.drive != 0 && (id == MetricId::DiskIo || id == MetricId::DiskIoRead || id == MetricId::DiskIoWrite))
        {
            bool valid = false;
            double read = 0.0;
            double write = 0.0;
            if (!snapshot.TryVolume(element.drive, valid, read, write) || !valid) return NAN;
            return (id == MetricId::DiskIoWrite) ? write : (id == MetricId::DiskIoRead ? read : read + write);
        }

        switch (id)
        {
        case MetricId::Cpu: return snapshot.Has(FlagCpuValid) ? snapshot.cpuPercent : NAN;
        case MetricId::Memory: return snapshot.Has(FlagMemoryValid) ? snapshot.memoryPercent : NAN;
        case MetricId::Temperature: return snapshot.Has(FlagTemperatureValid) ? snapshot.temperatureC : NAN;
        case MetricId::NetUp: return snapshot.Has(FlagNetworkValid) ? snapshot.netUpBytesPerSec : NAN;
        case MetricId::NetDown: return snapshot.Has(FlagNetworkValid) ? snapshot.netDownBytesPerSec : NAN;
        case MetricId::Gpu: return snapshot.Has(FlagGpuValid) ? snapshot.gpuLoadPercent : NAN;
        case MetricId::GpuMemory: return snapshot.Has(FlagGpuValid) ? snapshot.gpuMemoryPercent : NAN;
        case MetricId::GpuTemp: return snapshot.Has(FlagGpuTemperatureValid) ? snapshot.gpuTemperatureC : NAN;
        case MetricId::Disk: return snapshot.Has(FlagDiskValid) ? snapshot.diskPercent : NAN;
        case MetricId::DiskIo:
            return snapshot.Has(FlagDiskIoValid)
                ? (double)snapshot.diskIoReadBytesPerSec + (double)snapshot.diskIoWriteBytesPerSec
                : NAN;
        case MetricId::DiskIoRead:
            return snapshot.Has(FlagDiskIoValid) ? (double)snapshot.diskIoReadBytesPerSec : NAN;
        case MetricId::DiskIoWrite:
            return snapshot.Has(FlagDiskIoValid) ? (double)snapshot.diskIoWriteBytesPerSec : NAN;
        default: return NAN;
        }
    }

    void Renderer::PushSample(const Element& element, ElementState& state, const Snapshot& snapshot, bool agentAlive)
    {
        // One history entry per published sample, not per paint: the band repaints on resize.
        if (state.lastTickTicks == snapshot.timestampUtcTicks) return;
        state.lastTickTicks = snapshot.timestampUtcTicks;

        // Element-level refresh: a bar/text/pie can be slower than the band timer.
        int interval = element.refreshMs < 200 ? 200 : element.refreshMs;
        long long intervalTicks = (long long)interval * 10000LL;
        if (state.lastSampleTicks != 0 && snapshot.timestampUtcTicks > 0 &&
            snapshot.timestampUtcTicks - state.lastSampleTicks < intervalTicks - kRefreshToleranceTicks)
        {
            return;
        }
        state.lastSampleTicks = snapshot.timestampUtcTicks;

        double raw = ValueOf(element, snapshot, 0, agentAlive);
        if (element.type == ElementType::Bar)
        {
            int percent = 0;
            if (raw == raw)
            {
                if (IsPercentMetric(element.metric))
                {
                    percent = (int)(raw + 0.5);
                }
                else
                {
                    // Rates have no natural 0..100 scale: normalize against the visible window.
                    int peak = 1;
                    for (int value : state.history) { if (value > peak) peak = value; }
                    double scaleTop = peak;
                    if (element.metric == MetricId::Temperature) scaleTop = 100.0;
                    percent = scaleTop > 0 ? (int)(raw * 100.0 / scaleTop + 0.5) : 0;
                }
            }
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            state.history.push_back(percent);

            int userPercent = percent;
            int systemPercent = 0;
            if (element.metric == MetricId::Cpu)
            {
                // Old agents leave the split at zero: fall back to the total in the user colour so
                // the bar never goes blank.
                int splitUser = (int)(snapshot.cpuUserPercent + 0.5);
                int splitSystem = (int)(snapshot.cpuSystemPercent + 0.5);
                if (splitUser > 0 || splitSystem > 0)
                {
                    userPercent = ClampPercent(splitUser);
                    systemPercent = ClampPercent(splitSystem);
                }
            }
            state.historyUser.push_back(userPercent);
            state.historySystem.push_back(systemPercent);

            int keep = element.historyColumns < 1 ? 1 : element.historyColumns;
            while ((int)state.history.size() > keep) state.history.erase(state.history.begin());
            while ((int)state.historyUser.size() > keep) state.historyUser.erase(state.historyUser.begin());
            while ((int)state.historySystem.size() > keep) state.historySystem.erase(state.historySystem.begin());
            return;
        }

        if (element.type == ElementType::Pie)
        {
            double value = ValueOf(element, snapshot, 0, agentAlive);
            if (value == value)
            {
                state.ema1 = state.hasEma1 ? state.ema1 + kEmaAlpha * (value - state.ema1) : value;
                state.hasEma1 = true;
            }
            return;
        }

        if (element.type == ElementType::Text)
        {
            for (int i = 0; i < (int)element.lines.size() && i < 2; i++)
            {
                double value = ValueOf(element, snapshot, i, agentAlive);
                if (value != value) continue;
                if (i == 0)
                {
                    state.ema1 = state.hasEma1 ? state.ema1 + kEmaAlpha * (value - state.ema1) : value;
                    state.hasEma1 = true;
                }
                else
                {
                    state.ema2 = state.hasEma2 ? state.ema2 + kEmaAlpha * (value - state.ema2) : value;
                    state.hasEma2 = true;
                }
            }
        }
    }

    // ---------------------------------------------------------------- drawing

    void Renderer::DrawBarFill(HDC hdc, int cellX, int top, int cellWidth, int barHeight,
                               int inset, int percent, COLORREF colour)
    {
        int inner = barHeight - inset * 2;
        int fillHeight = MulDiv(inner, percent, 100);
        if (fillHeight < 1) fillHeight = 1;
        RECT fill = { cellX + inset, top + barHeight - inset - fillHeight,
                      cellX + cellWidth - inset, top + barHeight - inset };
        if (fill.top < fill.bottom) FillColor(hdc, fill, colour);
    }

    void Renderer::DrawBar(HDC hdc, const Element& element, const ElementState& state,
                           const Snapshot& snapshot, int x, int top, int height, double scale)
    {
        int cellWidth = Px(scale, element.cellWidth);
        int cellGap = Px(scale, element.cellGap);
        int inset = Px(scale, 1);
        int barHeight = Px(scale, element.height);

        int columns = element.historyColumns < 1 ? 1 : element.historyColumns;
        int drawn = (int)state.history.size();

        // The whole bar goes through one opaque GDI+ surface: cells, frames and fills are
        // axis-aligned, so they stay exactly one pixel wide (integer coordinates + no pixel
        // offset), while the surface still keeps the taskbar backdrop out of the pixels.
        int barWidth = columns * cellWidth + (columns - 1) * cellGap;
        OpaqueSurface surface(hdc, x, top, barWidth, barHeight, &_tint, _tintScreenLeft + x,
                              kBandBackground);
        Gdiplus::Graphics* graphics = surface.Ok() ? surface.Graphics() : nullptr;
        if (graphics != nullptr)
        {
            Gdiplus::SolidBrush frameBrush(ToGdiplus(element.outline));
            Gdiplus::SolidBrush kernelBrush(ToGdiplus(element.kernel));
            Gdiplus::SolidBrush userBrush(ToGdiplus(element.fill));

            // One column per logical processor: each core has its own load, so the columns must
            // differ. When the agent does not publish per-core data the total is used for every
            // column (the old behaviour) instead of drawing nothing.
            int totalPercent = drawn > 0 ? state.history[drawn - 1] : 0;
            int systemPercent = !state.historySystem.empty() ? state.historySystem.back() : 0;
            int userPercent = !state.historyUser.empty() ? state.historyUser.back() : totalPercent;

            for (int i = 0; i < columns; i++)
            {
                int cellX = i * (cellWidth + cellGap);
                // 1 px frame drawn as four integer rects: a pen would straddle two rows.
                graphics->FillRectangle(&frameBrush, cellX, 0, cellWidth, 1);
                graphics->FillRectangle(&frameBrush, cellX, barHeight - 1, cellWidth, 1);
                graphics->FillRectangle(&frameBrush, cellX, 0, 1, barHeight);
                graphics->FillRectangle(&frameBrush, cellX + cellWidth - 1, 0, 1, barHeight);

                bool perCore = i < (int)snapshot.coreCount;
                int percent = perCore ? (int)snapshot.corePercents[i] : totalPercent;
                int columnUser = perCore ? (int)snapshot.coreUserPercents[i] : userPercent;
                int columnSystem = perCore ? (int)snapshot.coreSystemPercents[i] : systemPercent;

                if (drawn <= 0 && !perCore) continue;

                // Kernel first, user painted over it: the visible red is only the part where the
                // kernel share exceeds the user share (XMeters does the same, and that is what the
                // band is compared against).
                if (columnSystem > 0)
                {
                    FillBarRect(graphics, &kernelBrush, cellX, cellWidth, barHeight, inset, columnSystem);
                }
                if (columnUser > 0)
                {
                    FillBarRect(graphics, &userBrush, cellX, cellWidth, barHeight, inset, columnUser);
                }
                else if (percent > 0)
                {
                    FillBarRect(graphics, &userBrush, cellX, cellWidth, barHeight, inset, percent);
                }
            }
            (void)height;
            return;
        }

        // GDI+ refused to start: same drawing with plain GDI (hard edges, never a blank element).
        int totalPercent = drawn > 0 ? state.history[drawn - 1] : 0;
        int systemPercent = !state.historySystem.empty() ? state.historySystem.back() : 0;
        int userPercent = !state.historyUser.empty() ? state.historyUser.back() : totalPercent;

        for (int i = 0; i < columns; i++)
        {
            int cellX = x + i * (cellWidth + cellGap);
            RECT outer = { cellX, top, cellX + cellWidth, top + barHeight };

            FillColor(hdc, outer, TintAt(cellX));      // cells sit on the band surface
            FrameColor(hdc, outer, element.outline);

            bool perCore = i < (int)snapshot.coreCount;
            int percent = perCore ? (int)snapshot.corePercents[i] : totalPercent;
            int columnUser = perCore ? (int)snapshot.coreUserPercents[i] : userPercent;
            int columnSystem = perCore ? (int)snapshot.coreSystemPercents[i] : systemPercent;

            if (drawn <= 0 && !perCore) continue;

            // Same order as the GDI+ path: kernel first, user over it.
            if (columnSystem > 0)
            {
                DrawBarFill(hdc, cellX, top, cellWidth, barHeight, inset, columnSystem, element.kernel);
            }
            if (columnUser > 0)
            {
                DrawBarFill(hdc, cellX, top, cellWidth, barHeight, inset, columnUser, element.fill);
            }
            else if (percent > 0)
            {
                DrawBarFill(hdc, cellX, top, cellWidth, barHeight, inset, percent, element.fill);
            }
        }
        (void)height;
    }

    void Renderer::DrawPie(HDC hdc, const Element& element, const ElementState& state,
                           const Snapshot& snapshot, bool agentAlive, int x, int top, double scale)
    {
        int diameter = Px(scale, element.diameter);

        double percent = state.hasEma1 ? state.ema1 : 0.0;
        if (percent < 0) percent = 0;
        if (percent > 100) percent = 100;

        // Value inside the ring: a whole number without a unit (the ring is only 26 px,
        // "63%" or "50 C" would not fit). Grey "--" when the agent is stale or the metric invalid.
        // Only the gradient ring carries a number: a solid wedge already reads as a share, and the
        // user asked for the solid form to stay empty on every metric (memory, CPU, GPU).
        std::wstring text;
        COLORREF colour = kInvalidGrey;
        int fontHeight = Px(scale, 11);
        if (fontHeight < 8) fontHeight = 8;
        HFONT font = nullptr;

        if (element.gradientRing)
        {
            text = L"--";
            if (agentAlive)
            {
                double value = 0.0;
                bool valid = false;
                switch (element.metric)
                {
                case MetricId::Cpu:
                    valid = snapshot.Has(FlagCpuValid);
                    value = snapshot.cpuPercent;
                    break;
                case MetricId::Memory:
                    valid = snapshot.Has(FlagMemoryValid);
                    value = snapshot.memoryPercent;
                    break;
                case MetricId::Temperature:
                    valid = snapshot.Has(FlagTemperatureValid);
                    value = snapshot.temperatureC;
                    break;
                case MetricId::Gpu:
                    valid = snapshot.Has(FlagGpuValid);
                    value = snapshot.gpuLoadPercent;
                    break;
                case MetricId::GpuMemory:
                    valid = snapshot.Has(FlagGpuValid);
                    value = snapshot.gpuMemoryPercent;
                    break;
                case MetricId::GpuTemp:
                    valid = snapshot.Has(FlagGpuTemperatureValid);
                    value = snapshot.gpuTemperatureC;
                    break;
                case MetricId::Disk:
                    valid = snapshot.Has(FlagDiskValid);
                    value = snapshot.diskPercent;
                    break;
                default:
                    break;
                }

                if (valid && value == value)
                {
                    int rounded = (int)(value + 0.5);
                    if (rounded < 0) rounded = 0;
                    wchar_t buffer[8];
                    StringCchPrintfW(buffer, ARRAYSIZE(buffer), L"%d", rounded);
                    text = buffer;
                    colour = element.outline;
                }
            }
            font = MakeFont(fontHeight, FW_NORMAL);
        }

        // Composed through an opaque DIB so the colour on screen is exactly element.fill
        // (see OpaqueFill.h: the taskbar adds its backdrop to plain GDI path fills). The centre
        // number rides along on the same surface: drawn on a memory DC it uses greyscale AA and
        // leaves no ClearType fringes or darkened glyph box on the band.
        if (element.gradientRing)
        {
            // 颜色是沿弧线的色带：0% 绿 → 80% 黄 → 90% 红（之后保持红），与温度分档同一条色阶。
            DrawGradientRingOpaque(hdc, x, top, diameter, (int)(percent + 0.5),
                                   kTempSafe, kTempWarn, kTempDanger, 80.0, 90.0,
                                   TintAt(x), scale,
                                   font != nullptr ? text.c_str() : nullptr, RGB(255, 255, 255), font);
            return;
        }

        DrawRingGaugeOpaque(hdc, x, top, diameter, (int)(percent + 0.5),
                            element.fill, element.outline, TintAt(x), scale,
                            font != nullptr ? text.c_str() : nullptr, colour, font);
    }

    void Renderer::DrawText(HDC hdc, size_t index, const Element& element, const ElementState& state,
                            const Snapshot& snapshot, bool agentAlive, int x, int top, int height, double scale)
    {
        int indicatorWidth = Px(scale, kIndicatorWidth);

        int lineCount = (int)element.lines.size();
        if (lineCount > 2) lineCount = 2;

        // Glyphs are rasterised at 3x and downsampled: at 12 px a hinted greyscale glyph covers one
        // or two columns per stem, so its edges are steps whatever the hinting does. The layout
        // maths below stays in 1x units (and the ink probe measures the 1x font), so alignment is
        // the one that was verified pixel by pixel; only the rasterisation is scaled.
        const int kSuperSample = 3;

        int fontHeight = Px(scale, kTextFontPx);
        if (fontHeight < 8) fontHeight = 8;
        HFONT measureFont = MakeFont(fontHeight, FW_NORMAL);

        // Field = the widest value this element has shown (or the configured minimum), never the
        // old fixed 52 px: that padding was invisible inside the element and made the gap to the
        // next element larger than `horizontalSpacing`.
        int fieldWidth = TextFieldWidthPx(index, element, scale);

        // How wide the current value really is; the surface reaches leftPad px further left so the
        // first glyph's antialiased edge is not cut at the surface boundary.
        int widestText = 0;
        for (int i = 0; i < lineCount; i++)
        {
            int width = TextWidthPx(hdc, measureFont,
                                    LineText(element.lines[i], snapshot, agentAlive, element.drive).c_str());
            if (width > widestText) widestText = width;
        }
        int overflow = widestText - fieldWidth;
        if (overflow < 0) overflow = 0;

        // The triangle gutter is part of an element's width only when a row actually draws one.
        int gutter = HasIndicator(element) ? indicatorWidth : 0;

        int leftPad = Px(scale, 2) + overflow;
        int surfaceWidth = fieldWidth + gutter;
        OpaqueSurface surface(hdc, x - leftPad, top, surfaceWidth + leftPad, height, &_tint,
                              _tintScreenLeft + x - leftPad, kBandBackground, kSuperSample);
        bool composited = surface.Ok() && surface.Graphics() != nullptr;
        int S = composited ? surface.Scale() : 1;

        HDC target = composited ? surface.Hdc() : hdc;
        Gdiplus::Graphics* graphics = composited ? surface.Graphics() : nullptr;
        int originX = composited ? leftPad : x;
        int originY = composited ? 0 : top;

        HFONT drawFont = (S > 1) ? MakeFont(fontHeight * S, FW_NORMAL) : measureFont;

        // Rows are placed by glyph ink, not by layout boxes: the first row's ink top lines up with
        // the bar cells' top edge and the last row's ink bottom with their bottom edge.
        InkMetrics ink = MeasureInk(measureFont, drawFont, fontHeight, S);
        int layoutHeight = fontHeight * 2;
        if (layoutHeight < ink.bottom + 2) layoutHeight = ink.bottom + 2;

        // The whole text element is drawn on one opaque surface: GDI's ClearType leaves a faint
        // darker rectangle behind the glyph box and paints colour fringes on a dark band, and the
        // surface is what keeps the taskbar backdrop out of the pixels.
        // The field is as wide as the widest value shown so far, so a value that gains a digit
        // moves the digits inside a stable box instead of pushing the neighbours around.
        int halfWidth = Px(scale, 4);                 // base stays 2 * halfWidth + 1
        int indicatorHeight = Px(scale, kIndicatorHeight);
        if (indicatorHeight < 5) indicatorHeight = 5;
        int halfHeight = (indicatorHeight - 1) / 2;   // apex + base rows
        int air = Px(scale, 2);
        if (air < 1) air = 1;

        for (int i = 0; i < (int)element.lines.size() && i < 2; i++)
        {
            const TextLine& line = element.lines[i];
            bool hasIndicator = line.indicator != 0;

            std::wstring text = LineText(line, snapshot, agentAlive, element.drive);

            int layoutTop;
            if (lineCount <= 1)
            {
                // Single row sits like the first row of a two-row block: ink top on the band
                // top, so a lone temperature lines up with the network element's top row.
                layoutTop = originY - ink.top;
            }
            else if (i == 0)
            {
                layoutTop = originY - ink.top;
            }
            else
            {
                // The ink probe now measures through the same supersampled path as the render, so
                // the ink bottom is where it actually lands: place it on the band's last row.
                layoutTop = originY + height - 1 - ink.bottom;
            }

            // The text's right edge is the element's right edge: whatever the field has over the
            // current value stays on the left, so the gap to the next element is exactly the
            // configured spacing.
            int textRight = originX + fieldWidth;
            COLORREF colour = text == L"--" ? kInvalidGrey : line.color;
            if (line.metric == MetricId::Temperature && snapshot.Has(FlagTemperatureValid))
            {
                colour = TemperatureColour(snapshot.temperatureC, 80.0, 90.0);
            }
            else if (line.metric == MetricId::GpuTemp && snapshot.Has(FlagGpuTemperatureValid))
            {
                colour = TemperatureColour(snapshot.gpuTemperatureC, 75.0, 83.0);
            }
            DrawTextRight(target, drawFont, colour, text.c_str(),
                          textRight * S, layoutTop * S, layoutHeight * S, 4 * S);

            if (hasIndicator)
            {
                // 2 px of air between the last digit and the arrow: the triangle ink spans
                // [textRight + 2, textRight + 10] inside the 11 px gutter.
                int triangleCenterX = (textRight + air + halfWidth) * S;
                bool up = line.indicator > 0;
                // The arrow grows out of the value instead of floating in the middle of the row:
                // the up arrow's base sits on the text's ink bottom, the down arrow hangs from the
                // ink top.
                int inkTopY = layoutTop + ink.top;
                int inkBottomY = layoutTop + ink.bottom;
                // The arrow grows out of the value: its base sits on the text's ink bottom (up) or
                // its tip hangs from the ink top (down).
                int triangleCenterY = up ? (inkBottomY - halfHeight) : (inkTopY + halfHeight);
                // Keep the arrow inside the surface even if the ink probe is off by a pixel.
                int lowest = halfHeight * S;
                int highest = (height - 1 - halfHeight) * S;
                int center = triangleCenterY * S;
                if (center < lowest) center = lowest;
                if (center > highest) center = highest;
                int halfW = halfWidth * S;
                int halfH = halfHeight * S;
                Gdiplus::PointF points[3];
                if (up)
                {
                    points[0].X = (Gdiplus::REAL)triangleCenterX; points[0].Y = (Gdiplus::REAL)(center - halfH);
                    points[1].X = (Gdiplus::REAL)(triangleCenterX - halfW); points[1].Y = (Gdiplus::REAL)(center + halfH);
                    points[2].X = (Gdiplus::REAL)(triangleCenterX + halfW); points[2].Y = (Gdiplus::REAL)(center + halfH);
                }
                else
                {
                    points[0].X = (Gdiplus::REAL)triangleCenterX; points[0].Y = (Gdiplus::REAL)(center + halfH);
                    points[1].X = (Gdiplus::REAL)(triangleCenterX - halfW); points[1].Y = (Gdiplus::REAL)(center - halfH);
                    points[2].X = (Gdiplus::REAL)(triangleCenterX + halfW); points[2].Y = (Gdiplus::REAL)(center - halfH);
                }
                if (graphics != nullptr)
                {
                    Gdiplus::SolidBrush brush(ToGdiplus(line.color));
                    graphics->FillPolygon(&brush, points, 3);
                }
                else
                {
                    DrawTriangle(target, triangleCenterX, center, halfW, halfH, up, line.color,
                                 TintAt(triangleCenterX - halfW));
                }
            }
        }

        // font is cached and reused across paints; do not delete it here
    }
int Renderer::Paint(HDC hdc, const BandSettings& settings, const Snapshot& snapshot, bool agentAlive,
                        int width, int height, double scale)
    {
        if (_states.size() != settings.items.size()) Reset(settings);

        SetBkMode(hdc, TRANSPARENT);

        // Measure first: every text element's width has to be known before the first element is
        // laid out, otherwise the second one starts from a stale field width.
        MeasureText(hdc, settings, snapshot, agentAlive, scale);

        int contentHeight = Px(scale, settings.ContentHeight());
        int top = (height - contentHeight) / 2;
        if (top < 0) top = 0;

        int x = 0;
        for (size_t i = 0; i < settings.items.size(); i++)
        {
            const Element& element = settings.items[i];
            if (!element.enabled) continue;

            PushSample(element, _states[i], snapshot, agentAlive);

            switch (element.type)
            {
            case ElementType::Bar:
                DrawBar(hdc, element, _states[i], snapshot, x, top, contentHeight, scale);
                break;
            case ElementType::Pie:
                DrawPie(hdc, element, _states[i], snapshot, agentAlive, x, top, scale);
                break;
            case ElementType::Text:
                DrawText(hdc, i, element, _states[i], snapshot, agentAlive, x, top, contentHeight, scale);
                break;
            default:
                break;
            }

            // Same helper the band's requested width uses: what is drawn and what was asked for
            // cannot drift apart at any DPI.
            x += ElementWidthPx(i, element, scale);
            x += Px(scale, settings.horizontalSpacing);
            if (x > width) break;
        }

        return contentHeight;
    }

}
