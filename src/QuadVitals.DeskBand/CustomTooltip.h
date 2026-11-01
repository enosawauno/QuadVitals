// Custom tooltip for the taskbar band.
//
// The shell's tooltip class draws a white system box, which looks nothing like the dashboard and
// cannot be styled. This draws its own layered popup: a dark rounded card, label/value rows with
// the value right-aligned, and a muted hint line at the bottom. The whole surface is composited
// with UpdateLayeredWindow, so the corners are antialiased and the window never paints on top of
// a stale frame (no flicker), and it is click-through (no input, no focus stealing).
#pragma once

#include <windows.h>
#include <objidl.h>
#include <gdiplus.h>
#include <string>
#include <vector>

#include "CardStyle.h"
#include "OpaqueFill.h"

namespace pb
{
    class CustomTooltip
    {
    public:
        ~CustomTooltip() { Destroy(); }

        void Create(HWND owner, HINSTANCE instance)
        {
            _owner = owner;
            if (_hwnd != nullptr) return;

            WNDCLASSEXW wc = { sizeof(wc) };
            wc.lpfnWndProc = Proc;
            wc.hInstance = instance;
            wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            wc.lpszClassName = kClass;
            RegisterClassExW(&wc);

            _hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                kClass, L"", WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, instance, nullptr);
        }

        void Destroy()
        {
            if (_hwnd != nullptr) DestroyWindow(_hwnd);
            _hwnd = nullptr;
        }

        bool Visible() const { return _visible; }

        void Hide()
        {
            _visible = false;
            _lastText.clear();
            if (_hwnd != nullptr) ShowWindow(_hwnd, SW_HIDE);
        }

        /// <summary>
        /// Renders `text` (the same multi-line string the band always produced) as a card whose
        /// top-left sits at `anchor` (screen px), clamped inside the monitor's work area.
        /// Lines of the form "label  value" become a two-column row; everything else is a muted
        /// line, and the last line is treated as the hint and separated by a rule.
        /// </summary>
        void Show(const std::wstring& text, POINT anchor, double scale)
        {
            if (_hwnd == nullptr || text.empty()) return;
            if (scale < 0.5) scale = 1.0;
            // The band refreshes once a second; re-blitting an identical card would just burn CPU.
            if (_visible && text == _lastText) return;

            std::vector<Row> rows;
            std::wstring hint;
            Parse(text, rows, hint);

            int fontPx = (int)(12.0 * scale + 0.5);
            if (fontPx < 9) fontPx = 9;
            int padX = (int)(12.0 * scale + 0.5);
            int padY = (int)(10.0 * scale + 0.5);
            int lineH = (int)(fontPx * 1.55 + 0.5);
            int gapLabel = (int)(20.0 * scale + 0.5);
            int radius = (int)(8.0 * scale + 0.5);
            int maxWidth = (int)(360.0 * scale + 0.5);

            HDC screen = GetDC(nullptr);
            HDC memory = CreateCompatibleDC(screen);
            HFONT font = card::MakeFont(fontPx);
            HGDIOBJ oldFont = SelectObject(memory, font);

            // Measure with GDI+ (the same path that draws): GetTextExtentPoint32 and DrawString do
            // not agree on trailing advance, which used to clip the hint line's last characters.
            int rowW = 0;
            int hintW = 0;
            if (EnsureGdiplus())
            {
                Gdiplus::Graphics measure(memory);
                measure.SetTextRenderingHint(Gdiplus::TextRenderingHintClearTypeGridFit);
                Gdiplus::Font measureFont(memory, font);
                Gdiplus::StringFormat format;
                format.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap | Gdiplus::StringFormatFlagsNoClip);
                for (size_t i = 0; i < rows.size(); i++)
                {
                    int lw = MeasureWidth(&measure, &measureFont, &format, rows[i].label);
                    int vw = rows[i].value.empty() ? 0 : MeasureWidth(&measure, &measureFont, &format, rows[i].value);
                    int w = rows[i].value.empty() ? lw : lw + gapLabel + vw;
                    if (w > rowW) rowW = w;
                }
                if (!hint.empty()) hintW = MeasureWidth(&measure, &measureFont, &format, hint);
            }

            int contentW = rowW;
            if (hintW > contentW) contentW = hintW;
            if (contentW > maxWidth) contentW = maxWidth;

            // The card is drawn inside a slightly larger surface so it can cast a soft shadow,
            // the way the dashboard's cards do; a hard-edged popup on a bright wallpaper looks pasted on.
            int shadow = card::ShadowFor(scale);

            int cardW = contentW + padX * 2;
            int cardH = padY * 2 + (int)rows.size() * lineH;
            if (!hint.empty()) cardH += (int)(6.0 * scale + 0.5) + 1 + lineH;

            int width = cardW + shadow * 2;
            int height = cardH + shadow * 2;

            // Layered surface: 32bpp ARGB, premultiplied after drawing so UpdateLayeredWindow can
            // blend the antialiased corners against whatever is behind the popup.
            BITMAPINFO info = { 0 };
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height;   // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;

            void* bits = nullptr;
            HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
            HDC surface = CreateCompatibleDC(screen);
            if (bitmap == nullptr || surface == nullptr || bits == nullptr)
            {
                if (bitmap != nullptr) DeleteObject(bitmap);
                if (surface != nullptr) DeleteDC(surface);
                SelectObject(memory, oldFont);
                DeleteObject(font);
                DeleteDC(memory);
                ReleaseDC(nullptr, screen);
                return;
            }

            HGDIOBJ oldSurfaceBitmap = SelectObject(surface, bitmap);
            memset(bits, 0, (size_t)width * height * 4);
            HFONT surfaceFont = font;
            SelectObject(surface, surfaceFont);

            if (EnsureGdiplus())
            {
                Gdiplus::Graphics graphics(surface);
                graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
                graphics.SetTextRenderingHint(Gdiplus::TextRenderingHintClearTypeGridFit);
                graphics.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);
                graphics.SetCompositingMode(Gdiplus::CompositingModeSourceOver);

                card::PaintCard(graphics, shadow, shadow, cardW, cardH, radius, shadow, scale);

                Gdiplus::Font textFont(surface, font);
                Gdiplus::StringFormat left;
                left.SetAlignment(Gdiplus::StringAlignmentNear);
                left.SetLineAlignment(Gdiplus::StringAlignmentNear);
                left.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap | Gdiplus::StringFormatFlagsNoClip);
                // StringFormat is not copyable (protected copy ctor): build the right-aligned one.
                Gdiplus::StringFormat right;
                right.SetAlignment(Gdiplus::StringAlignmentFar);
                right.SetLineAlignment(Gdiplus::StringAlignmentNear);
                right.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap | Gdiplus::StringFormatFlagsNoClip);

                Gdiplus::SolidBrush labelBrush(Gdiplus::Color(255, 0x9A, 0x9A, 0x9A));
                Gdiplus::SolidBrush valueBrush(Gdiplus::Color(255, 0xEC, 0xEC, 0xEC));
                Gdiplus::SolidBrush hintBrush(Gdiplus::Color(255, 0x7A, 0x7A, 0x7A));

                int y = shadow + padY;
                for (size_t i = 0; i < rows.size(); i++)
                {
                    Gdiplus::RectF box((Gdiplus::REAL)(shadow + padX), (Gdiplus::REAL)y,
                                       (Gdiplus::REAL)contentW, (Gdiplus::REAL)lineH);
                    graphics.DrawString(rows[i].label.c_str(), -1, &textFont, box, &left, &labelBrush);
                    if (!rows[i].value.empty())
                    {
                        graphics.DrawString(rows[i].value.c_str(), -1, &textFont, box, &right, &valueBrush);
                    }
                    y += lineH;
                }

                if (!hint.empty())
                {
                    y += (int)(6.0 * scale + 0.5);
                    Gdiplus::Pen rule(Gdiplus::Color(255, 0x3C, 0x3C, 0x3C), (Gdiplus::REAL)(1.0 * scale));
                    graphics.DrawLine(&rule, (Gdiplus::REAL)(shadow + padX), (Gdiplus::REAL)y,
                                      (Gdiplus::REAL)(width - shadow - padX), (Gdiplus::REAL)y);
                    y += 1;
                    Gdiplus::RectF box((Gdiplus::REAL)(shadow + padX), (Gdiplus::REAL)y,
                                       (Gdiplus::REAL)contentW, (Gdiplus::REAL)lineH);
                    graphics.DrawString(hint.c_str(), -1, &textFont, box, &left, &hintBrush);
                }
            }

            card::Premultiply(bits, width * height);

            // Clamp inside the work area of the monitor under the anchor point.
            POINT target = anchor;
            HMONITOR monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
            MONITORINFO monitorInfo = { sizeof(monitorInfo) };
            if (GetMonitorInfoW(monitor, &monitorInfo))
            {
                const RECT& work = monitorInfo.rcWork;
                if (target.x + width > work.right) target.x = work.right - width;
                if (target.y + height > work.bottom) target.y = work.bottom - height;
                if (target.x < work.left) target.x = work.left;
                if (target.y < work.top) target.y = work.top;
            }
            if (target.x + width > (LONG)GetSystemMetrics(SM_CXVIRTUALSCREEN))
            {
                target.x = (LONG)GetSystemMetrics(SM_CXVIRTUALSCREEN) - width;
            }

            POINT source = { 0, 0 };
            SIZE size = { width, height };
            BLENDFUNCTION blend = { AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screen, &target, &size, surface, &source, 0, &blend, ULW_ALPHA);
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _visible = true;
            _lastText = text;

            SelectObject(surface, oldSurfaceBitmap);
            SelectObject(memory, oldFont);
            DeleteObject(font);
            DeleteObject(bitmap);
            DeleteDC(surface);
            DeleteDC(memory);
            ReleaseDC(nullptr, screen);
        }

    private:
        struct Row
        {
            std::wstring label;
            std::wstring value;
        };

        static const wchar_t* const kClass;

        HWND _hwnd = nullptr;
        HWND _owner = nullptr;
        bool _visible = false;
        std::wstring _lastText;

        static LRESULT CALLBACK Proc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
            case WM_NCHITTEST:
                return HTTRANSPARENT;   // a tooltip must never eat a click
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
            }
        }




        static int MeasureWidth(Gdiplus::Graphics* graphics, Gdiplus::Font* font,
                                Gdiplus::StringFormat* format, const std::wstring& text)
        {
            if (text.empty()) return 0;
            Gdiplus::RectF bounds(0, 0, 0, 0);
            graphics->MeasureString(text.c_str(), -1, font, Gdiplus::PointF(0, 0), format, &bounds);
            int width = (int)(bounds.Width + 1.5f);   // rounding + the last glyph's overhang
            return width;
        }

        /// <summary>
        /// "CPU 占用   7 %" → {label, value}; "内存   21.8 / 63.7 GB" → too; "更新于 0.8 秒前"
        /// (single spaces) stays a muted one-column row, and the trailing hint becomes the footer.
        /// </summary>
        static void Parse(const std::wstring& text, std::vector<Row>& rows, std::wstring& hint)
        {
            std::vector<std::wstring> lines;
            std::wstring current;
            for (size_t i = 0; i < text.size(); i++)
            {
                wchar_t c = text[i];
                if (c == L'\r') continue;
                if (c == L'\n') { if (!current.empty()) lines.push_back(current); current.clear(); continue; }
                current += c;
            }
            if (!current.empty()) lines.push_back(current);
            if (lines.empty()) return;

            // The last line of BuildTooltip is the "double-click / right-click" hint.
            hint = lines.back();
            lines.pop_back();

            for (size_t i = 0; i < lines.size(); i++)
            {
                Row row;
                size_t split = lines[i].find(L"  ");
                if (split == std::wstring::npos)
                {
                    row.label = lines[i];
                }
                else
                {
                    row.label = lines[i].substr(0, split);
                    std::wstring value = lines[i].substr(split);
                    while (!value.empty() && value[0] == L' ') value.erase(value.begin());
                    row.value = value;
                }
                rows.push_back(row);
            }
        }
    };

    const wchar_t* const CustomTooltip::kClass = L"QuadVitalsTooltipWindow";
}
