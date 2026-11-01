// QuadVitals taskbar DeskBand (native C++ COM, design).
//
// Thin by design: it only reads settings.json and the shared-memory snapshot
// and draws. No sensor access, no network, no blocking calls, and every entry point is guarded.
//
// Layout comes from settings.json and the drawing vocabulary from the XMeters mould
// measured in S3. Width is recomputed from the element list, so ptMinSize.x always
// matches what the renderer needs.

#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#define UNICODE
#define _UNICODE

#include <windows.h>
#include <windowsx.h>
#include <shlobj.h>
#include <shlobj_core.h>
#include <shobjidl.h>
#include <ocidl.h>
#include <commctrl.h>
#include <shellapi.h>
#include <strsafe.h>
#include <algorithm>
#include <vector>

#include "CustomMenu.h"
#include "CustomTooltip.h"
#include "Json.h"
#include "MetricsClient.h"
#include "OpaqueFill.h"
#include "Renderer.h"
#include "Settings.h"

// {3F0A6E2C-9B14-4D7A-8E55-1C2D3B4A5F60}
static const CLSID CLSID_QuadVitalsDeskBand =
{ 0x3F0A6E2C, 0x9B14, 0x4D7A, { 0x8E, 0x55, 0x1C, 0x2D, 0x3B, 0x4A, 0x5F, 0x60 } };

static HINSTANCE g_hInst = nullptr;
static LONG g_cRef = 0;
static const wchar_t* kWindowClass = L"QuadVitalsBandWindow";
static const UINT_PTR kTimerId = 1;
static const UINT_PTR kTooltipTimerId = 2;
static const UINT kTooltipDelayMs = 2000;   // hover this long before the card appears
static const UINT_PTR kClickTimerId = 3;
static const UINT kMsgSettingsChanged = WM_APP + 1;

namespace
{
    const COLORREF kInvalidFrame = RGB(0xFF, 0x6B, 0x6B);

    // Average colour of a small block of the screen. Used for the taskbar tint only.
    /// <summary>
    /// Median colour of a small screen block plus its channel spread. The median ignores a stray
    /// white pixel (icon glyph / text edge) that a plain average would smear into the whole band.
    /// Returns false when the block could not be read.
    /// </summary>
    bool SampleQuietBlock(HDC screen, int left, int top, int width, int height,
                          COLORREF* out, int* spread)
    {
        if (out == nullptr || width <= 0 || height <= 0) return false;

        BITMAPINFO info = { 0 };
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HDC memory = CreateCompatibleDC(screen);
        HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (memory == nullptr || bitmap == nullptr || bits == nullptr)
        {
            if (bitmap != nullptr) DeleteObject(bitmap);
            if (memory != nullptr) DeleteDC(memory);
            return false;
        }

        HGDIOBJ previous = SelectObject(memory, bitmap);
        BOOL copied = BitBlt(memory, 0, 0, width, height, screen, left, top, SRCCOPY);
        SelectObject(memory, previous);

        bool ok = false;
        if (copied)
        {
            const int count = width * height;
            DWORD* pixels = (DWORD*)bits;
            std::vector<int> r(count), g(count), b(count);
            for (int i = 0; i < count; i++)
            {
                DWORD value = pixels[i];
                b[i] = (int)(value & 0xFF);
                g[i] = (int)((value >> 8) & 0xFF);
                r[i] = (int)((value >> 16) & 0xFF);
            }
            std::sort(r.begin(), r.end());
            std::sort(g.begin(), g.end());
            std::sort(b.begin(), b.end());
            int mid = count / 2;
            *out = RGB(r[mid], g[mid], b[mid]);

            if (spread != nullptr)
            {
                // Spread across the channels' percentiles: a block that mixes an icon with the bar
                // is not a usable sample of the bar.
                int lo = r[count / 10], hi = r[count - 1 - count / 10];
                *spread = hi - lo;
            }
            ok = true;
        }

        DeleteObject(bitmap);
        DeleteDC(memory);
        return ok;
    }

    /// <summary>
    /// One side's backdrop colour: walks a few offsets outward until it finds a block that looks
    /// like plain bar (low spread, not bright) instead of an icon or a neighbouring band.
    /// </summary>
    bool SampleSide(HDC screen, int startX, int step, int row, int width, int height, COLORREF* out)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int x = startX + step * attempt * 5;
            COLORREF colour = 0;
            int spread = 255;
            if (!SampleQuietBlock(screen, x, row, width, height, &colour, &spread)) continue;

            int luminance = (GetRValue(colour) * 30 + GetGValue(colour) * 59 + GetBValue(colour) * 11) / 100;
            if (spread > 40) continue;      // mixed content (icon next to bar colour)
            if (luminance > 140) continue;  // the taskbar backdrop is dark; a bright block is content
            *out = colour;
            return true;
        }
        return false;
    }

    // The bar's backdrop is sampled immediately outside the band, not at the bar's ends: the
    // extreme left/right of the taskbar carry the start-button and show-desktop highlights and
    // come out several shades brighter than the tint where the band actually sits.
    pb::BandTint SampleTaskbarTint(HWND hwnd)
    {
        pb::BandTint tint;
        if (hwnd == nullptr) return tint;

        HWND root = GetAncestor(hwnd, GA_ROOT);
        RECT tray = { 0, 0, 0, 0 };
        RECT band = { 0, 0, 0, 0 };
        if (root == nullptr || !GetWindowRect(root, &tray)) return tint;
        if (!GetWindowRect(hwnd, &band)) return tint;
        if (tray.right - tray.left < 32 || tray.bottom - tray.top < 8) return tint;

        const int stripWidth = 6;
        const int stripHeight = 5;
        int row = tray.top + 2;

        int leftX = band.left - 2 - stripWidth;
        int rightX = band.right + 2;
        if (leftX < tray.left) leftX = tray.left;
        if (rightX + stripWidth > tray.right) rightX = tray.right - stripWidth;

        HDC screen = GetDC(nullptr);
        if (screen == nullptr) return tint;

        COLORREF left = pb::kBandBackground;
        COLORREF right = pb::kBandBackground;
        bool haveLeft = SampleSide(screen, leftX, -1, row, stripWidth, stripHeight, &left) ||
                        SampleSide(screen, leftX, +1, row, stripWidth, stripHeight, &left);
        bool haveRight = SampleSide(screen, rightX, +1, row, stripWidth, stripHeight, &right) ||
                         SampleSide(screen, rightX, -1, row, stripWidth, stripHeight, &right);
        ReleaseDC(nullptr, screen);
        if (!haveLeft && !haveRight) return tint;
        if (!haveLeft) left = right;
        if (!haveRight) right = left;

        // The two sides should describe the same blurred backdrop. When they do not, one of them
        // caught something bright (an icon, a neighbouring band); the bar is dark by design, so
        // the darker sample is the bar and the bright one is content to ignore.
        int dr = abs((int)GetRValue(left) - (int)GetRValue(right));
        int dg = abs((int)GetGValue(left) - (int)GetGValue(right));
        int db = abs((int)GetBValue(left) - (int)GetBValue(right));
        if (dr > 16 || dg > 16 || db > 16)
        {
            int leftLuma = GetRValue(left) * 30 + GetGValue(left) * 59 + GetBValue(left) * 11;
            int rightLuma = GetRValue(right) * 30 + GetGValue(right) * 59 + GetBValue(right) * 11;
            COLORREF darker = leftLuma <= rightLuma ? left : right;
            left = darker;
            right = darker;
        }

        tint.Left = left;
        tint.Right = right;
        tint.Tray = tray;
        tint.Sampled = true;
        return tint;
    }

    double DpiScaleOf(HWND hwnd)
    {
        typedef UINT(WINAPI* GetDpiForWindowFn)(HWND);
        static GetDpiForWindowFn fn = (GetDpiForWindowFn)GetProcAddress(
            GetModuleHandleW(L"user32.dll"), "GetDpiForWindow");
        UINT dpi = 96;
        if (fn != nullptr && hwnd != nullptr) dpi = fn(hwnd);
        if (dpi < 72) dpi = 96;
        return (double)dpi / 96.0;
    }
}


class CDeskBand : public IDeskBand, public IObjectWithSite, public IPersistStream
{
public:
    CDeskBand() : _ref(1)
    {
        InterlockedIncrement(&g_cRef);
    }

    ~CDeskBand()
    {
        _tooltip.Destroy();
        if (_site != nullptr) { _site->Release(); _site = nullptr; }
        InterlockedDecrement(&g_cRef);
    }

    // ---- IUnknown ----------------------------------------------------------------

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        *ppv = nullptr;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IOleWindow) ||
            IsEqualIID(riid, IID_IDockingWindow) || IsEqualIID(riid, IID_IDeskBand))
        {
            *ppv = static_cast<IDeskBand*>(this);
        }
        else if (IsEqualIID(riid, IID_IObjectWithSite))
        {
            *ppv = static_cast<IObjectWithSite*>(this);
        }
        else if (IsEqualIID(riid, IID_IPersist) || IsEqualIID(riid, IID_IPersistStream))
        {
            *ppv = static_cast<IPersistStream*>(this);
        }
        else
        {
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }

    STDMETHODIMP_(ULONG) Release() override
    {
        LONG count = InterlockedDecrement(&_ref);
        if (count == 0) delete this;
        return count;
    }

    // ---- IObjectWithSite --------------------------------------------------------

    STDMETHODIMP SetSite(IUnknown* site) override
    {
        if (_site != nullptr) { _site->Release(); _site = nullptr; }
        _site = site;
        if (_site != nullptr) _site->AddRef();

        HWND parent = nullptr;
        if (site != nullptr)
        {
            IOleWindow* window = nullptr;
            if (SUCCEEDED(site->QueryInterface(IID_IOleWindow, (void**)&window)))
            {
                window->GetWindow(&parent);
                window->Release();
            }
        }
        _parent = parent;

        if (_parent != nullptr) EnsureWindow();
        else
        {
            _tooltip.Destroy();
            if (_hwnd != nullptr) { DestroyWindow(_hwnd); _hwnd = nullptr; }
            if (_refreshEvent != nullptr) { CloseHandle(_refreshEvent); _refreshEvent = nullptr; }
        }

        return S_OK;
    }

    STDMETHODIMP GetSite(REFIID riid, void** ppvSite) override
    {
        if (ppvSite == nullptr) return E_POINTER;
        *ppvSite = nullptr;
        if (_site == nullptr) return E_FAIL;
        return _site->QueryInterface(riid, ppvSite);
    }

    // ---- IPersistStream ---------------------------------------------------------

    STDMETHODIMP GetClassID(CLSID* clsid) override
    {
        if (clsid == nullptr) return E_POINTER;
        *clsid = CLSID_QuadVitalsDeskBand;
        return S_OK;
    }
    STDMETHODIMP IsDirty() override { return S_FALSE; }
    STDMETHODIMP Load(IStream*) override { return S_OK; }
    STDMETHODIMP Save(IStream*, BOOL) override { return S_OK; }
    STDMETHODIMP GetSizeMax(ULARGE_INTEGER* size) override
    {
        if (size == nullptr) return E_POINTER;
        size->QuadPart = 0;
        return S_OK;
    }

    // ---- IOleWindow -------------------------------------------------------------

    STDMETHODIMP GetWindow(HWND* hwnd) override
    {
        if (hwnd == nullptr) return E_POINTER;
        EnsureWindow();
        *hwnd = _hwnd;
        return S_OK;
    }
    STDMETHODIMP ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }

    // ---- IDockingWindow ---------------------------------------------------------

    STDMETHODIMP ShowDW(BOOL show) override
    {
        if (_hwnd != nullptr) ShowWindow(_hwnd, show ? SW_SHOW : SW_HIDE);
        return S_OK;
    }
    STDMETHODIMP CloseDW(DWORD) override
    {
        _tooltip.Destroy();
        if (_hwnd != nullptr) { DestroyWindow(_hwnd); _hwnd = nullptr; }
        return S_OK;
    }
    STDMETHODIMP ResizeBorderDW(LPCRECT, IUnknown*, BOOL) override { return E_NOTIMPL; }

    // ---- IDeskBand --------------------------------------------------------------

    STDMETHODIMP GetBandInfo(DWORD, DWORD, DESKBANDINFO* info) override
    {
        if (info == nullptr) return E_INVALIDARG;
        if (!_settingsLoaded) ReloadSettings();

        // The taskbar is per-monitor DPI aware and our drawing is scaled by the same factor,
        // so the requested physical size must be scaled too - otherwise at 125/150% the
        // renderer draws 1.25x/1.5x content into a 1x-wide band and clips the right edge.
        double scale = DpiScaleOf(_hwnd != nullptr ? _hwnd : _parent);
        int minY = (int)(24 * scale + 0.5);
        int maxY = (int)(_settings.height * scale + 0.5);

        info->dwMask = DBIM_MINSIZE | DBIM_MAXSIZE | DBIM_TITLE | DBIM_MODEFLAGS;
        info->ptMinSize.x = _renderer.RequiredWidthPx(_settings, scale);   // 文本按最宽可能值申请
        info->ptMinSize.y = minY;                          // 40 (= taskbar height) makes the band refuse
        info->ptMaxSize.x = (int)(1200 * scale + 0.5);
        info->ptMaxSize.y = maxY;
        info->ptIntegral.x = 1;
        info->ptIntegral.y = 1;
        info->dwModeFlags = DBIMF_NORMAL | DBIMF_VARIABLEHEIGHT;

        // Empty title on purpose: the taskbar would otherwise draw it as a text label next to the
        // band and eat 115-135 px (S3 measurement).
        StringCchCopyW(info->wszTitle, ARRAYSIZE(info->wszTitle), L"");
        return S_OK;
    }

private:
    // ---- lifecycle --------------------------------------------------------------

    void ReloadSettings()
    {
        std::wstring path;
        pb::BandSettings loaded;
        pb::LoadSettings(loaded, &path);

        _settings = loaded;
        _settingsLoaded = true;
        _renderer.Reset(_settings);

        // The global refresh drives the paint timer; an element may still be slower on its own.
        if (_hwnd != nullptr)
        {
            UINT interval = (UINT)(_settings.refreshMs < 200 ? 200 : _settings.refreshMs);
            SetTimer(_hwnd, kTimerId, interval, nullptr);
        }

        WIN32_FILE_ATTRIBUTE_DATA data;
        if (!path.empty() && GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
        {
            _settingsWriteTime = ((long long)data.ftLastWriteTime.dwHighDateTime << 32) |
                                  data.ftLastWriteTime.dwLowDateTime;
        }
        else
        {
            _settingsWriteTime = 0;
        }
    }

    void CheckSettingsChanged()
    {
        std::wstring path = pb::SettingsPath();
        if (path.empty()) return;

        WIN32_FILE_ATTRIBUTE_DATA data;
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
        {
            if (_settingsWriteTime != 0) ReloadSettings(); // file removed: fall back to defaults
            return;
        }

        long long writeTime = ((long long)data.ftLastWriteTime.dwHighDateTime << 32) |
                               data.ftLastWriteTime.dwLowDateTime;
        if (writeTime != _settingsWriteTime) ReloadSettings();
    }

    void EnsureWindow()
    {
        if (_hwnd != nullptr || _parent == nullptr) return;

        WNDCLASSEXW wc = { sizeof(wc) };
        // CS_DBLCLKS is what turns the second press into WM_LBUTTONDBLCLK; without it a
        // double-click is just two single clicks and the "open dashboard" branch never runs.
        wc.style = CS_DBLCLKS;
        wc.lpfnWndProc = WindowProc;
        wc.hInstance = g_hInst;
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.lpszClassName = kWindowClass;
        RegisterClassExW(&wc);

        if (!_settingsLoaded) ReloadSettings();

        double scale = DpiScaleOf(_parent);
        _hwnd = CreateWindowExW(0, kWindowClass, L"QuadVitals", WS_CHILD | WS_VISIBLE,
                                0, 0, _renderer.RequiredWidthPx(_settings, scale),
                                (int)(_settings.height * scale + 0.5),
                                _parent, nullptr, g_hInst, this);
        if (_hwnd == nullptr) return;

        _tooltip.Create(_hwnd, g_hInst);
        EnsureTint(true);
        UINT interval = (UINT)(_settings.refreshMs < 200 ? 200 : _settings.refreshMs);
        SetTimer(_hwnd, kTimerId, interval, nullptr);

        // Paint immediately so the band is never blank while waiting for the first sample.
        Refresh(true);
    }

    // The tint is re-sampled when it can change (theme, wallpaper, accent colour, taskbar moved)
    // and at most every few seconds otherwise: two small BitBlts per sample, nothing in the paint hot path.
    void EnsureTint(bool force = false)
    {
        ULONGLONG now = GetTickCount64();
        if (!force && _tint.Sampled && now - _tintSampledAt < 10000) return;
        _tintSampledAt = now;
        _tint = SampleTaskbarTint(_hwnd);
    }

    void TintChanged()
    {
        EnsureTint(true);
        if (_hwnd != nullptr) InvalidateRect(_hwnd, nullptr, FALSE);
    }

    /// <summary>
    /// Keeps an open card in sync with the newest sample. The card is drawn by us, so an update is
    /// a fresh layered blit rather than a message to the shell's tooltip class.
    /// </summary>
    void UpdateTooltip()
    {
        if (!_tooltip.Visible()) return;
        ShowTooltip();
    }

    void ShowTooltip()
    {
        if (!_settings.showTooltip || _hwnd == nullptr) return;

        // Anchor under the pointer, offset so the cursor never covers the first row.
        POINT anchor = { _hoverPoint.x + (LONG)(12 * DpiScaleOf(_hwnd)),
                         _hoverPoint.y + (LONG)(22 * DpiScaleOf(_hwnd)) };

        bool alive = _snapshot.IsAlive();
        std::wstring text = pb::Renderer::BuildTooltip(_settings, _snapshot, alive);
        _tooltip.Show(text, anchor, DpiScaleOf(_hwnd));
    }

    void HideTooltip()
    {
        KillTimer(_hwnd, kTooltipTimerId);
        _tooltip.Hide();
    }

    /// <summary>Non-blocking check of the dashboard's refresh event; opens it lazily.</summary>
    bool ConsumeRefreshRequest()
    {
        if (_refreshEvent == nullptr)
        {
            _refreshEvent = OpenEventW(SYNCHRONIZE, FALSE, L"Local\\QuadVitals.Refresh.v1");
            if (_refreshEvent == nullptr) return false;
        }
        return WaitForSingleObject(_refreshEvent, 0) == WAIT_OBJECT_0;
    }

    void Refresh(bool force)
    {
        pb::Snapshot snapshot;
        if (_metrics.TryRead(snapshot))
        {
            bool changed = force || snapshot.sequence != _snapshot.sequence;
            _snapshot = snapshot;
            if (changed) InvalidateRect(_hwnd, nullptr, FALSE);
        }
        else if (force || _metrics.ChannelOpen())
        {
            // No agent yet, or it just went away: paint "--" once, do not keep repainting.
            _snapshot = pb::Snapshot();
            InvalidateRect(_hwnd, nullptr, FALSE);
        }

        UpdateTooltip();
    }

    void Paint(HDC hdc)
    {
        RECT rc;
        GetClientRect(_hwnd, &rc);
        int width = rc.right - rc.left;
        int height = rc.bottom - rc.top;
        if (width <= 0 || height <= 0) return;

        EnsureTint();
        RECT windowRect;
        GetWindowRect(_hwnd, &windowRect);
        pb::FillTintOpaque(hdc, rc, _tint, windowRect.left);
        _renderer.SetTint(_tint, windowRect.left);

        double scale = DpiScaleOf(_hwnd);
        bool alive = _snapshot.IsAlive();

        int used = _renderer.Paint(hdc, _settings, _snapshot, alive, width, height, scale);

        // Faint marker when the content is taller than the strip we were given.
        if (used > height + 1)
        {
            HBRUSH frame = CreateSolidBrush(kInvalidFrame);
            FrameRect(hdc, &rc, frame);
            DeleteObject(frame);
        }
    }

    /// <summary>
    /// Context menu on the band's own dark card (the system menu is white and square, which looked
    /// like a different application next to the tooltip and the dashboard).
    /// </summary>
    void ShowContextMenu()
    {
        HideTooltip();

        std::vector<pb::CustomMenu::Item> items;
        items.push_back({ 1, L"\u7ACB\u5373\u5237\u65B0", true });
        items.push_back({ 6, L"\u53D1\u9001\u53CD\u9988\u2026", true });
        items.push_back({ 0, L"", true });
        items.push_back({ 4, L"\u5173\u4E8E QuadVitals", true });

        POINT cursor;
        GetCursorPos(&cursor);
        pb::CustomMenu menu;
        int command = menu.Show(_hwnd, items, cursor, DpiScaleOf(_hwnd));

        switch (command)
        {
        case 1: Refresh(true); break;
        case 4: OpenDashboard(L"--about"); break;
        case 6: OpenDashboard(L"--feedback"); break;
        default: break;
        }
    }



    void OpenTaskManager()
    {
        ShellExecuteW(_hwnd, L"open", L"taskmgr.exe", nullptr, nullptr, SW_SHOWNORMAL);
    }

    void OpenDashboard(const wchar_t* arguments = nullptr)
    {
        wchar_t exePath[MAX_PATH * 2] = { 0 };
        GetModuleFileNameW(g_hInst, exePath, ARRAYSIZE(exePath));

        std::wstring dir(exePath);
        size_t slash = dir.find_last_of(L'\\');
        if (slash != std::wstring::npos) dir = dir.substr(0, slash);

        std::wstring candidates[2] = {
            dir + L"\\QuadVitals.Settings.exe",
            dir + L"\\..\\ui\\QuadVitals.Settings.exe"
        };
        for (int i = 0; i < 2; i++)
        {
            if (GetFileAttributesW(candidates[i].c_str()) == INVALID_FILE_ATTRIBUTES) continue;
            ShellExecuteW(_hwnd, L"open", candidates[i].c_str(), arguments, nullptr, SW_SHOWNORMAL);
            return;
        }
        MessageBoxW(_hwnd, L"\u627E\u4E0D\u5230 QuadVitals.Settings.exe\uFF08\u5148\u8FD0\u884C scripts\\build-ui.ps1\uFF09",
                    L"QuadVitals", MB_OK | MB_ICONWARNING);
    }




    static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        CDeskBand* self = nullptr;

        if (message == WM_NCCREATE)
        {
            CREATESTRUCTW* create = (CREATESTRUCTW*)lParam;
            self = (CDeskBand*)create->lpCreateParams;
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)self);
            return TRUE;
        }

        self = (CDeskBand*)GetWindowLongPtrW(hwnd, GWLP_USERDATA);
        if (self == nullptr) return DefWindowProcW(hwnd, message, wParam, lParam);

        switch (message)
        {
        case WM_SETTINGCHANGE:
        case WM_THEMECHANGED:
        case WM_DISPLAYCHANGE:
        case WM_DWMCOMPOSITIONCHANGED:
            // Wallpaper, accent colour, transparency or taskbar position can all change the tint.
            self->TintChanged();
            return 0;
        case WM_ERASEBKGND:
            return 1;

        case WM_PAINT:
        {
            PAINTSTRUCT ps;
            HDC hdc = BeginPaint(hwnd, &ps);
            self->Paint(hdc);
            EndPaint(hwnd, &ps);
            return 0;
        }

        case WM_SIZE:
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_DPICHANGED:
            // The shell normally recreates the band when the monitor DPI changes; if it only
            // resizes us, repaint with the new scale (the renderer reads DPI per paint).
            InvalidateRect(hwnd, nullptr, FALSE);
            return 0;

        case WM_TIMER:
            if (wParam == kClickTimerId && self != nullptr)
            {
                KillTimer(hwnd, kClickTimerId);
                self->OpenTaskManager();
                return 0;
            }
            if (wParam == kTooltipTimerId && self != nullptr)
            {
                KillTimer(hwnd, kTooltipTimerId);
                self->ShowTooltip();
                return 0;
            }
            if (wParam == kTimerId && self != nullptr)
            {
                self->CheckSettingsChanged();
                // The dashboard's refresh button sets a named event: repaint even when the
                // sequence did not change, otherwise pressing it looks like it did nothing.
                self->Refresh(self->ConsumeRefreshRequest());
            }
            return 0;

        case WM_MOUSEMOVE:
        {
            // Hover with our own delay: the shell tooltip class is what drew the white box.
            POINT point = { GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam) };
            ClientToScreen(hwnd, &point);
            if (!self->_tooltip.Visible())
            {
                self->_hoverPoint = point;
                if (!self->_hoverTracking)
                {
                    TRACKMOUSEEVENT track = { sizeof(track), TME_LEAVE, hwnd, 0 };
                    self->_hoverTracking = TrackMouseEvent(&track) != FALSE;
                    SetTimer(hwnd, kTooltipTimerId, kTooltipDelayMs, nullptr);
                }
            }
            return 0;
        }

        case WM_MOUSELEAVE:
            self->_hoverTracking = false;
            self->HideTooltip();
            return 0;

        case WM_MOUSEWHEEL:
            self->HideTooltip();
            break;

        case WM_CONTEXTMENU:
            self->HideTooltip();
            self->ShowContextMenu();
            return 0;

        case WM_LBUTTONDOWN:
            self->HideTooltip();
            return 0;

        case WM_LBUTTONUP:
            if (self->_doubleClickInProgress)
            {
                self->_doubleClickInProgress = false;
                return 0;
            }
            SetTimer(hwnd, kClickTimerId, GetDoubleClickTime(), nullptr);
            return 0;

        case WM_LBUTTONDBLCLK:
            self->_doubleClickInProgress = true;
            KillTimer(hwnd, kClickTimerId);
            self->HideTooltip();
            self->OpenDashboard();
            return 0;

        default:
            break;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    LONG _ref;
    HWND _hwnd = nullptr;
    HWND _parent = nullptr;
    pb::CustomTooltip _tooltip;
    POINT _hoverPoint = { 0, 0 };
    bool _hoverTracking = false;
    bool _doubleClickInProgress = false;
    IUnknown* _site = nullptr;

    pb::BandSettings _settings;
    bool _settingsLoaded = false;
    long long _settingsWriteTime = 0;
    pb::Renderer _renderer;
    pb::MetricsClient _metrics;
    // 面板「刷新」用的命名事件；Agent 不在时打不开，那就当它一直是没信号。
    HANDLE _refreshEvent = nullptr;
    pb::Snapshot _snapshot;
    pb::BandTint _tint;
    ULONGLONG _tintSampledAt = 0;
};

class CClassFactory : public IClassFactory
{
public:
    CClassFactory() : _ref(1) { InterlockedIncrement(&g_cRef); }
    ~CClassFactory() { InterlockedDecrement(&g_cRef); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        *ppv = nullptr;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        return E_NOINTERFACE;
    }

    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG count = InterlockedDecrement(&_ref);
        if (count == 0) delete this;
        return count;
    }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        if (ppv == nullptr) return E_POINTER;

        CDeskBand* band = new CDeskBand();
        if (band == nullptr) return E_OUTOFMEMORY;

        HRESULT hr = band->QueryInterface(riid, ppv);
        band->Release();
        return hr;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_cRef);
        else InterlockedDecrement(&g_cRef);
        return S_OK;
    }

private:
    LONG _ref;
};

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_hInst = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (!IsEqualGUID(clsid, CLSID_QuadVitalsDeskBand)) return CLASS_E_CLASSNOTAVAILABLE;
    if (ppv == nullptr) return E_POINTER;

    CClassFactory* factory = new CClassFactory();
    if (factory == nullptr) return E_OUTOFMEMORY;

    HRESULT hr = factory->QueryInterface(riid, ppv);
    factory->Release();
    return hr;
}

extern "C" HRESULT __stdcall DllCanUnloadNow()
{
    return (g_cRef == 0) ? S_OK : S_FALSE;
}
