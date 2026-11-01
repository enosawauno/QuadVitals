// The band's own context menu.
//
// TrackPopupMenu draws the system menu (white, square, unthemed), which next to the band's custom
// tooltip card and the dashboard looks like it belongs to a different program. This draws the same
// kind of card the tooltip uses — same radius, colours, shadow and font — and stays modal the way
// TrackPopupMenu is: it runs its own message loop and returns the chosen command id (0 = dismissed).
//
// Keyboard: Up/Down move the highlight, Enter activates, Esc cancels; hover moves it too. A click
// outside the card dismisses it (the window captures the mouse, so the click arrives here).
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
    class CustomMenu
    {
    public:
        struct Item
        {
            int id;                 // 0 = separator
            std::wstring label;
            bool enabled;
        };

        ~CustomMenu() { Destroy(); }

        /// <summary>Shows the menu at `anchor` (screen px) and blocks until it closes.</summary>
        int Show(HWND owner, const std::vector<Item>& items, POINT anchor, double scale)
        {
            if (items.empty()) return 0;
            if (scale < 0.5) scale = 1.0;
            _owner = owner;
            _items = items;
            _scale = scale;
            _result = 0;
            _closed = false;
            _hover = FirstEnabled();

            if (!EnsureWindow()) return 0;
            if (!Render(anchor))
            {
                Destroy();
                return 0;
            }

            // Foreground first, capture last: taking the foreground can hand the capture away,
            // and WM_CAPTURECHANGED would close the menu the instant it appeared.
            SetForegroundWindow(_hwnd);
            SetFocus(_hwnd);
            SetCapture(_hwnd);

            MSG message;
            while (!_closed && GetMessageW(&message, nullptr, 0, 0) > 0)
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
            ReleaseCapture();
            Destroy();
            return _result;
        }

    private:
        HWND _hwnd = nullptr;
        HWND _owner = nullptr;
        HINSTANCE _instance = nullptr;
        std::vector<Item> _items;
        std::vector<int> _rowTop;
        std::vector<int> _rowHeight;
        int _hover = -1;
        int _result = 0;
        bool _closed = false;
        double _scale = 1.0;
        POINT _anchor = { 0, 0 };
        int _width = 0;
        int _height = 0;
        int _shadow = 0;
        int _radius = 0;
        int _padX = 0;
        int _padY = 0;
        int _rowH = 0;
        int _sepGap = 0;
        int _fontPx = 12;
        ULONGLONG _shownAt = 0;

        static const wchar_t* ClassName() { return L"QuadVitalsMenuWindow"; }

        bool EnsureWindow()
        {
            if (_hwnd != nullptr) return true;
            _instance = GetModuleHandleW(nullptr);

            WNDCLASSEXW wc = { sizeof(wc) };
            wc.lpfnWndProc = Proc;
            wc.hInstance = _instance;
            wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            wc.lpszClassName = ClassName();
            RegisterClassExW(&wc);

            _hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
                                    ClassName(), L"", WS_POPUP,
                                    0, 0, 0, 0, nullptr, nullptr, _instance, nullptr);
            if (_hwnd == nullptr) return false;
            SetWindowLongPtrW(_hwnd, GWLP_USERDATA, (LONG_PTR)this);
            return true;
        }

        void Destroy()
        {
            if (_hwnd != nullptr) DestroyWindow(_hwnd);
            _hwnd = nullptr;
        }

        int FirstEnabled()
        {
            for (size_t i = 0; i < _items.size(); i++)
            {
                if (_items[i].id != 0 && _items[i].enabled) return (int)i;
            }
            return -1;
        }

        void Measure()
        {
            _radius = (int)(8.0 * _scale + 0.5);
            _padX = (int)(14.0 * _scale + 0.5);
            _padY = (int)(8.0 * _scale + 0.5);
            _rowH = (int)(30.0 * _scale + 0.5);
            _sepGap = (int)(9.0 * _scale + 0.5);
            _shadow = card::ShadowFor(_scale);
            _fontPx = (int)(12.0 * _scale + 0.5);
            if (_fontPx < 9) _fontPx = 9;

            int textW = 0;
            HDC screen = GetDC(nullptr);
            HDC memory = CreateCompatibleDC(screen);
            HFONT font = card::MakeFont(_fontPx);
            HGDIOBJ oldFont = SelectObject(memory, font);

            if (EnsureGdiplus())
            {
                Gdiplus::Graphics graphics(memory);
                graphics.SetTextRenderingHint(Gdiplus::TextRenderingHintClearTypeGridFit);
                Gdiplus::Font gdiFont(memory, font);
                Gdiplus::StringFormat format;
                format.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap | Gdiplus::StringFormatFlagsNoClip);
                for (size_t i = 0; i < _items.size(); i++)
                {
                    if (_items[i].id == 0) continue;
                    Gdiplus::RectF bounds(0, 0, 0, 0);
                    graphics.MeasureString(_items[i].label.c_str(), -1, &gdiFont, Gdiplus::PointF(0, 0), &format, &bounds);
                    int width = (int)(bounds.Width + 1.5f);
                    if (width > textW) textW = width;
                }
            }

            SelectObject(memory, oldFont);
            DeleteObject(font);
            DeleteDC(memory);
            ReleaseDC(nullptr, screen);

            int width = textW + _padX * 2 + (int)(18 * _scale);   // breathing room for the highlight
            if (width < (int)(130 * _scale)) width = (int)(130 * _scale);

            _rowTop.assign(_items.size(), -1);
            _rowHeight.assign(_items.size(), 0);
            int y = _padY;
            for (size_t i = 0; i < _items.size(); i++)
            {
                _rowTop[i] = y;
                if (_items[i].id == 0)
                {
                    _rowHeight[i] = _sepGap;
                    y += _sepGap;
                }
                else
                {
                    _rowHeight[i] = _rowH;
                    y += _rowH;
                }
            }
            _width = width;
            _height = y + _padY;
        }

        bool Render(POINT anchor)
        {
            Measure();
            _anchor = anchor;

            int surfaceW = _width + _shadow * 2;
            int surfaceH = _height + _shadow * 2;

            HDC screen = GetDC(nullptr);
            BITMAPINFO info = { 0 };
            info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = surfaceW;
            info.bmiHeader.biHeight = -surfaceH;
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
                ReleaseDC(nullptr, screen);
                return false;
            }

            HGDIOBJ oldBitmap = SelectObject(surface, bitmap);
            memset(bits, 0, (size_t)surfaceW * surfaceH * 4);

            HFONT font = card::MakeFont(_fontPx);
            HGDIOBJ oldFont = SelectObject(surface, font);

            if (EnsureGdiplus())
            {
                Gdiplus::Graphics graphics(surface);
                graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
                graphics.SetTextRenderingHint(Gdiplus::TextRenderingHintClearTypeGridFit);
                graphics.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);
                graphics.SetCompositingMode(Gdiplus::CompositingModeSourceOver);

                card::PaintCard(graphics, _shadow, _shadow, _width, _height, _radius, _shadow, _scale);

                Gdiplus::Font textFont(surface, font);
                Gdiplus::StringFormat left;
                left.SetAlignment(Gdiplus::StringAlignmentNear);
                left.SetLineAlignment(Gdiplus::StringAlignmentCenter);
                left.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap | Gdiplus::StringFormatFlagsNoClip);

                Gdiplus::SolidBrush text(Gdiplus::Color(255, 0xEC, 0xEC, 0xEC));
                Gdiplus::SolidBrush dim(Gdiplus::Color(255, 0x80, 0x80, 0x80));

                int inset = (int)(5.0 * _scale + 0.5);
                for (size_t i = 0; i < _items.size(); i++)
                {
                    int top = _shadow + _rowTop[i];
                    int height = _rowHeight[i];
                    if (top < 0) continue;

                    if (_items[i].id == 0)
                    {
                        int y = top + height / 2;
                        Gdiplus::Pen rule(Gdiplus::Color(255, 0x3C, 0x3C, 0x3C), (Gdiplus::REAL)(1.0 * _scale));
                        graphics.DrawLine(&rule, (Gdiplus::REAL)(_shadow + _padX), (Gdiplus::REAL)y,
                                          (Gdiplus::REAL)(_shadow + _width - _padX), (Gdiplus::REAL)y);
                        continue;
                    }

                    if ((int)i == _hover && _items[i].enabled)
                    {
                        Gdiplus::GraphicsPath pill;
                        card::AddRoundedRect(pill, _shadow + inset, top + 1,
                                             _width - inset * 2, height - 2,
                                             (int)(5.0 * _scale + 0.5));
                        Gdiplus::SolidBrush highlight(Gdiplus::Color(255, card::kHoverR, card::kHoverG, card::kHoverB));
                        graphics.FillPath(&highlight, &pill);
                    }

                    Gdiplus::RectF box((Gdiplus::REAL)(_shadow + _padX), (Gdiplus::REAL)top,
                                       (Gdiplus::REAL)(_width - _padX * 2), (Gdiplus::REAL)height);
                    graphics.DrawString(_items[i].label.c_str(), -1, &textFont, box, &left,
                                        _items[i].enabled ? &text : &dim);
                }
            }

            card::Premultiply(bits, surfaceW * surfaceH);

            POINT target = anchor;
            HMONITOR monitor = MonitorFromPoint(anchor, MONITOR_DEFAULTTONEAREST);
            MONITORINFO monitorInfo = { sizeof(monitorInfo) };
            if (GetMonitorInfoW(monitor, &monitorInfo))
            {
                const RECT& work = monitorInfo.rcWork;
                if (target.x + surfaceW > work.right) target.x = work.right - surfaceW;
                if (target.y + surfaceH > work.bottom) target.y = work.bottom - surfaceH;
                if (target.x < work.left) target.x = work.left;
                if (target.y < work.top) target.y = work.top;
            }

            POINT source = { 0, 0 };
            SIZE size = { surfaceW, surfaceH };
            BLENDFUNCTION blend = { AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screen, &target, &size, surface, &source, 0, &blend, ULW_ALPHA);
            ShowWindow(_hwnd, SW_SHOWNA);
            _shownAt = GetTickCount64();

            SelectObject(surface, oldBitmap);
            SelectObject(surface, oldFont);
            DeleteObject(font);
            DeleteObject(bitmap);
            DeleteDC(surface);
            ReleaseDC(nullptr, screen);
            return true;
        }

        /// <summary>Item index under a client point, or -1 (separators and gaps map to -1).</summary>
        int HitTest(int clientX, int clientY)
        {
            int y = clientY - _shadow;
            for (size_t i = 0; i < _items.size(); i++)
            {
                if (_items[i].id == 0) continue;
                int top = _rowTop[i];
                if (top < 0) continue;
                if (y >= top && y < top + _rowHeight[i]) return (int)i;
            }
            return -1;
        }

        void Close(int result)
        {
            _result = result;
            _closed = true;
            if (_hwnd != nullptr) PostMessageW(_hwnd, WM_NULL, 0, 0);
        }

        void MoveHover(bool down)
        {
            if (_hover < 0) { _hover = FirstEnabled(); Render(_anchor); return; }
            int index = _hover;
            for (int step = 0; step < (int)_items.size(); step++)
            {
                index += down ? 1 : -1;
                if (index < 0) index = (int)_items.size() - 1;
                if (index >= (int)_items.size()) index = 0;
                if (_items[index].id != 0 && _items[index].enabled) break;
            }
            _hover = index;
            Render(_anchor);
        }

        void ActivateHover()
        {
            if (_hover < 0 || _hover >= (int)_items.size()) return;
            if (_items[_hover].id == 0 || !_items[_hover].enabled) return;
            Close(_items[_hover].id);
        }

        static LRESULT CALLBACK Proc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
        {
            CustomMenu* self = (CustomMenu*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
            if (self == nullptr) return DefWindowProcW(hwnd, message, wParam, lParam);

            switch (message)
            {
            case WM_MOUSEACTIVATE:
                return MA_ACTIVATE;

            case WM_MOUSEMOVE:
            {
                int hit = self->HitTest(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                if (hit != self->_hover)
                {
                    self->_hover = hit;
                    self->Render(self->_anchor);
                }
                return 0;
            }

            case WM_LBUTTONUP:
            {
                int hit = self->HitTest(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                if (hit >= 0 && self->_items[hit].enabled) self->Close(self->_items[hit].id);
                else self->Close(0);
                return 0;
            }

            case WM_LBUTTONDOWN:
                // A press outside the card lands here thanks to the capture: treat it as dismiss.
                if (self->HitTest(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam)) < 0) self->Close(0);
                return 0;

            case WM_RBUTTONUP:
            case WM_MBUTTONUP:
                self->Close(0);
                return 0;

            case WM_KEYDOWN:
                switch (wParam)
                {
                case VK_DOWN: self->MoveHover(true); return 0;
                case VK_UP: self->MoveHover(false); return 0;
                case VK_RETURN: case VK_SPACE: self->ActivateHover(); return 0;
                case VK_ESCAPE: self->Close(0); return 0;
                default: return 0;
                }

            case WM_CAPTURECHANGED:
                // Lost the mouse (alt-tab, another popup): do not stay on screen. Ignore the
                // handover that happens while the menu is still being set up.
                if (!self->_closed && GetTickCount64() - self->_shownAt > 400) self->Close(0);
                return 0;

            case WM_DESTROY:
                self->_hwnd = nullptr;
                return 0;

            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
            }
        }
    };
}
