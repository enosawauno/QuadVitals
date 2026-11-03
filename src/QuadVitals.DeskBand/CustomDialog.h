// Confirmation card for a destructive band command.
//
// The band draws its own tooltip and menu because the system ones are white and square and read as a
// different application (the owner asked for that twice). A MessageBox would be the third visual
// language in the same corner, so this is the same card the menu uses: rounded, shadowed, dark, with
// the app's red for the destructive button.
//
// Keyboard: Esc cancels, Enter cancels too (a destructive action must not fire from muscle memory);
// only a click on the confirm button returns true. Clicking outside the card cancels.
#pragma once

#include <windows.h>
#include <windowsx.h>
#include <objidl.h>
#include <gdiplus.h>
#include <string>

#include "CardStyle.h"
#include "OpaqueFill.h"

namespace pb
{
    class CustomDialog
    {
    public:
        ~CustomDialog() { Destroy(); }

        /// <summary>Shows the card centred on the owner's monitor. True only when confirm was clicked.</summary>
        bool Confirm(HWND owner, const std::wstring& title, const std::wstring& message,
                     const std::wstring& confirmLabel, const std::wstring& cancelLabel, double scale)
        {
            if (scale < 0.5) scale = 1.0;
            _owner = owner;
            _title = title;
            _message = message;
            _confirmLabel = confirmLabel;
            _cancelLabel = cancelLabel;
            _scale = scale;
            _hover = -1;
            _result = false;
            _closed = false;

            if (!EnsureWindow()) return false;
            if (!Render())
            {
                Destroy();
                return false;
            }

            SetForegroundWindow(_hwnd);
            SetFocus(_hwnd);
            SetCapture(_hwnd);

            MSG message2;
            while (!_closed && GetMessageW(&message2, nullptr, 0, 0) > 0)
            {
                TranslateMessage(&message2);
                DispatchMessageW(&message2);
            }
            ReleaseCapture();
            Destroy();
            return _result;
        }

    private:
        HWND _hwnd = nullptr;
        HWND _owner = nullptr;
        HINSTANCE _instance = nullptr;
        std::wstring _title;
        std::wstring _message;
        std::wstring _confirmLabel;
        std::wstring _cancelLabel;
        double _scale = 1.0;
        int _width = 0;
        int _height = 0;
        int _shadow = 0;
        int _radius = 0;
        int _pad = 0;
        int _titleH = 0;
        int _messageH = 0;
        int _buttonH = 0;
        int _buttonW = 0;
        int _cancelW = 0;
        int _gap = 0;
        int _fontPx = 12;
        int _titlePx = 13;
        int _hover = -1;        // 0 = cancel, 1 = confirm
        bool _result = false;
        bool _closed = false;

        static const wchar_t* ClassName() { return L"QuadVitalsDialogWindow"; }

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

        int MeasureLine(HDC dc, HFONT font, const std::wstring& text)
        {
            if (!EnsureGdiplus()) return 0;
            Gdiplus::Graphics graphics(dc);
            Gdiplus::Font gdiFont(dc, font);
            Gdiplus::StringFormat format;
            format.SetFormatFlags(Gdiplus::StringFormatFlagsNoWrap | Gdiplus::StringFormatFlagsNoClip);
            Gdiplus::RectF bounds(0, 0, 0, 0);
            graphics.MeasureString(text.c_str(), -1, &gdiFont, Gdiplus::PointF(0, 0), &format, &bounds);
            return (int)(bounds.Width + 1.5f);
        }

        int MeasureWrapped(HDC dc, HFONT font, const std::wstring& text, int width)
        {
            if (!EnsureGdiplus()) return 0;
            Gdiplus::Graphics graphics(dc);
            Gdiplus::Font gdiFont(dc, font);
            Gdiplus::RectF layout(0, 0, (Gdiplus::REAL)width, 1000);
            Gdiplus::RectF bounds(0, 0, 0, 0);
            graphics.MeasureString(text.c_str(), -1, &gdiFont, layout, nullptr, &bounds);
            return (int)(bounds.Height + 1.5f);
        }

        void Measure()
        {
            _radius = (int)(10.0 * _scale + 0.5);
            _pad = (int)(22.0 * _scale + 0.5);
            _gap = (int)(10.0 * _scale + 0.5);
            _buttonH = (int)(32.0 * _scale + 0.5);
            _shadow = card::ShadowFor(_scale);
            _fontPx = (int)(12.0 * _scale + 0.5);
            _titlePx = (int)(13.5 * _scale + 0.5);
            if (_fontPx < 9) _fontPx = 9;
            if (_titlePx < 10) _titlePx = 10;

            _width = (int)(384.0 * _scale + 0.5);
            int textW = _width - _pad * 2;

            HDC screen = GetDC(nullptr);
            HDC memory = CreateCompatibleDC(screen);
            HFONT titleFont = card::MakeFont(_titlePx);
            HFONT bodyFont = card::MakeFont(_fontPx);
            HGDIOBJ old = SelectObject(memory, bodyFont);

            _titleH = _titlePx + (int)(6 * _scale);
            _messageH = MeasureWrapped(memory, bodyFont, _message, textW);

            SelectObject(memory, titleFont);
            int confirmText = MeasureLine(memory, titleFont, _confirmLabel);
            int cancelText = MeasureLine(memory, titleFont, _cancelLabel);

            SelectObject(memory, old);
            DeleteObject(titleFont);
            DeleteObject(bodyFont);
            DeleteDC(memory);
            ReleaseDC(nullptr, screen);

            int buttonPad = (int)(20.0 * _scale + 0.5);
            _buttonW = confirmText + buttonPad * 2;
            _cancelW = cancelText + buttonPad * 2;
            if (_buttonW < (int)(84 * _scale)) _buttonW = (int)(84 * _scale);
            if (_cancelW < (int)(84 * _scale)) _cancelW = (int)(84 * _scale);
            if (_buttonH < (int)(26 * _scale)) _buttonH = (int)(26 * _scale);

            _height = _pad + _titleH + (int)(8 * _scale) + _messageH + (int)(20 * _scale) + _buttonH + _pad;
        }

        bool Render()
        {
            Measure();

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

            HFONT titleFont = card::MakeFont(_titlePx);
            HFONT bodyFont = card::MakeFont(_fontPx);
            HFONT oldFont = (HFONT)SelectObject(surface, bodyFont);

            if (EnsureGdiplus())
            {
                Gdiplus::Graphics graphics(surface);
                graphics.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
                graphics.SetTextRenderingHint(Gdiplus::TextRenderingHintClearTypeGridFit);
                graphics.SetPixelOffsetMode(Gdiplus::PixelOffsetModeHalf);
                graphics.SetCompositingMode(Gdiplus::CompositingModeSourceOver);

                card::PaintCard(graphics, _shadow, _shadow, _width, _height, _radius, _shadow, _scale);

                int x = _shadow + _pad;
                int y = _shadow + _pad;
                int textW = _width - _pad * 2;

                Gdiplus::StringFormat alignment;
                alignment.SetAlignment(Gdiplus::StringAlignmentNear);
                alignment.SetLineAlignment(Gdiplus::StringAlignmentNear);

                Gdiplus::SolidBrush primary(Gdiplus::Color(255, 0xEC, 0xEC, 0xEC));
                Gdiplus::SolidBrush secondary(Gdiplus::Color(255, 0xB4, 0xB4, 0xB4));

                Gdiplus::Font titleFace(surface, titleFont);
                Gdiplus::RectF titleBox((Gdiplus::REAL)x, (Gdiplus::REAL)y, (Gdiplus::REAL)textW,
                                        (Gdiplus::REAL)_titleH);
                graphics.DrawString(_title.c_str(), -1, &titleFace, titleBox, &alignment, &primary);

                y += _titleH + (int)(8 * _scale);
                Gdiplus::Font bodyFace(surface, bodyFont);
                Gdiplus::RectF bodyBox((Gdiplus::REAL)x, (Gdiplus::REAL)y, (Gdiplus::REAL)textW,
                                       (Gdiplus::REAL)_messageH);
                graphics.DrawString(_message.c_str(), -1, &bodyFace, bodyBox, &alignment, &secondary);

                // Buttons: cancel on the left of confirm, both bottom-right.
                int buttonTop = _shadow + _height - _pad - _buttonH;
                int confirmLeft = _shadow + _width - _pad - _buttonW;
                int cancelLeft = confirmLeft - _gap - _cancelW;

                DrawButton(graphics, surface, titleFont, cancelLeft, buttonTop, _cancelW, _cancelLabel,
                           Gdiplus::Color(255, 0x3A, 0x3A, 0x3A), Gdiplus::Color(255, 0x46, 0x46, 0x46),
                           Gdiplus::Color(255, 0xEC, 0xEC, 0xEC), _hover == 0);
                DrawButton(graphics, surface, titleFont, confirmLeft, buttonTop, _buttonW, _confirmLabel,
                           Gdiplus::Color(255, 0xD2, 0x47, 0x47), Gdiplus::Color(255, 0xE0, 0x5A, 0x5A),
                           Gdiplus::Color(255, 0xFF, 0xFF, 0xFF), _hover == 1);
            }

            card::Premultiply(bits, surfaceW * surfaceH);

            HMONITOR monitor = MonitorFromWindow(_owner != nullptr ? _owner : GetForegroundWindow(),
                                                 MONITOR_DEFAULTTONEAREST);
            MONITORINFO monitorInfo = { sizeof(monitorInfo) };
            RECT work = { 0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN) };
            if (GetMonitorInfoW(monitor, &monitorInfo)) work = monitorInfo.rcWork;

            POINT target;
            target.x = work.left + ((work.right - work.left) - surfaceW) / 2;
            target.y = work.top + ((work.bottom - work.top) - surfaceH) / 2;

            POINT source = { 0, 0 };
            SIZE size = { surfaceW, surfaceH };
            BLENDFUNCTION blend = { AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
            UpdateLayeredWindow(_hwnd, screen, &target, &size, surface, &source, 0, &blend, ULW_ALPHA);
            ShowWindow(_hwnd, SW_SHOWNA);

            SelectObject(surface, oldFont);
            DeleteObject(titleFont);
            DeleteObject(bodyFont);
            SelectObject(surface, oldBitmap);
            DeleteObject(bitmap);
            DeleteDC(surface);
            ReleaseDC(nullptr, screen);
            return true;
        }

        void DrawButton(Gdiplus::Graphics& graphics, HDC surface, HFONT font,
                        int x, int y, int width, const std::wstring& label,
                        Gdiplus::Color idle, Gdiplus::Color hover, Gdiplus::Color text, bool hot)
        {
            int height = _buttonH;
            Gdiplus::GraphicsPath pill;
            card::AddRoundedRect(pill, x, y, width, height, height / 2);
            Gdiplus::SolidBrush fill(hot ? hover : idle);
            graphics.FillPath(&fill, &pill);

            Gdiplus::Font face(surface, font);
            Gdiplus::StringFormat centre;
            centre.SetAlignment(Gdiplus::StringAlignmentCenter);
            centre.SetLineAlignment(Gdiplus::StringAlignmentCenter);
            Gdiplus::RectF box((Gdiplus::REAL)x, (Gdiplus::REAL)y, (Gdiplus::REAL)width, (Gdiplus::REAL)height);
            Gdiplus::SolidBrush brush(text);
            graphics.DrawString(label.c_str(), -1, &face, box, &centre, &brush);
        }

        int HitTest(POINT client)
        {
            int buttonTop = _shadow + _height - _pad - _buttonH;
            int confirmLeft = _shadow + _width - _pad - _buttonW;
            int cancelLeft = confirmLeft - _gap - _cancelW;
            if (client.y < buttonTop - (int)(6 * _scale) || client.y > buttonTop + _buttonH + (int)(6 * _scale)) return -1;
            if (client.x >= cancelLeft && client.x <= cancelLeft + _cancelW) return 0;
            if (client.x >= confirmLeft && client.x <= confirmLeft + _buttonW) return 1;
            return -1;
        }

        static LRESULT CALLBACK Proc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
        {
            CustomDialog* self = (CustomDialog*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
            if (self == nullptr) return DefWindowProcW(hwnd, message, wParam, lParam);

            switch (message)
            {
            case WM_MOUSEMOVE:
            {
                POINT point = { GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam) };
                int hit = self->HitTest(point);
                if (hit != self->_hover)
                {
                    self->_hover = hit;
                    self->Render();
                }
                return 0;
            }
            case WM_LBUTTONUP:
            {
                POINT point = { GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam) };
                int hit = self->HitTest(point);
                if (hit >= 0)
                {
                    self->_result = (hit == 1);
                    self->_closed = true;
                }
                return 0;
            }
            case WM_KEYDOWN:
                if (wParam == VK_ESCAPE || wParam == VK_RETURN)
                {
                    self->_result = false;
                    self->_closed = true;
                }
                return 0;
            case WM_CAPTURECHANGED:
                if (!self->_closed && (HWND)lParam != hwnd)
                {
                    self->_result = false;
                    self->_closed = true;
                }
                return 0;
            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
            }
        }
    };
}
