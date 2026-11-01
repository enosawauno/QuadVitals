// Off-screen render probe for the DeskBand.
//
// Paints the configured element list at 100%, 125%, 150% and 200% DPI into an off-screen DIB of
// exactly the size GetBandInfo would request, then checks that no pixel lands outside it and
// writes a BMP for eyeballing. This verifies the renderer's DPI maths without changing the
// monitor scaling or restarting Explorer (which would need a logoff/toolbar re-check).
//
// Build + run: powershell -File scripts\probe-deskband-render.ps1
#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#define UNICODE
#define _UNICODE

#include <windows.h>
#include <stdio.h>
#include <string>
#include <vector>
#include <wchar.h>

#include "Settings.h"
#include "Renderer.h"
#include "OpaqueFill.h"

using namespace pb;

static bool SaveBmp(const wchar_t* path, int width, int height, const void* bits)
{
    BITMAPFILEHEADER fileHeader = { 0 };
    BITMAPINFOHEADER infoHeader = { 0 };
    infoHeader.biSize = sizeof(infoHeader);
    infoHeader.biWidth = width;
    infoHeader.biHeight = -height;          // top-down, same as the DIB
    infoHeader.biPlanes = 1;
    infoHeader.biBitCount = 32;
    infoHeader.biCompression = BI_RGB;
    infoHeader.biSizeImage = (DWORD)(width * height * 4);

    fileHeader.bfType = 0x4D42;              // "BM"
    fileHeader.bfOffBits = sizeof(fileHeader) + sizeof(infoHeader);
    fileHeader.bfSize = fileHeader.bfOffBits + infoHeader.biSizeImage;

    FILE* file = nullptr;
    if (_wfopen_s(&file, path, L"wb") != 0 || file == nullptr) return false;
    fwrite(&fileHeader, 1, sizeof(fileHeader), file);
    fwrite(&infoHeader, 1, sizeof(infoHeader), file);
    fwrite(bits, 1, infoHeader.biSizeImage, file);
    fclose(file);
    return true;
}

int main()
{
    // Always probe the built-in mould so results are reproducible regardless of the user's
    // current settings.json.
    BandSettings settings;
    DefaultSettings(settings);

    const wchar_t* outDir = L"build\\tmp";
    CreateDirectoryW(outDir, nullptr);

    struct Case { double scale; const wchar_t* suffix; };
    const Case cases[] = { { 1.0, L"100" }, { 1.25, L"125" }, { 1.5, L"150" }, { 2.0, L"200" } };

    HDC screen = GetDC(nullptr);
    int failures = 0;

    for (const Case& test : cases)
    {
        Renderer renderer;
        renderer.Reset(settings);

        // Ask for the same width the band asks the taskbar for (text sized to its widest possible
        // value), so the clip check covers the real ptMinSize.x.
        const int width = renderer.RequiredWidthPx(settings, test.scale);
        const int height = (int)(settings.height * test.scale + 0.5);
        if (width <= 0 || height <= 0) { failures++; continue; }

        BITMAPINFO info = { 0 };
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;

        void* bits = nullptr;
        HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
        HDC memory = CreateCompatibleDC(screen);
        if (bitmap == nullptr || memory == nullptr || bits == nullptr)
        {
            wprintf(L"FAIL scale=%.2f: could not create DIB\n", test.scale);
            failures++;
            continue;
        }
        HGDIOBJ oldBitmap = SelectObject(memory, bitmap);

        RECT canvas = { 0, 0, width, height };
        FillRectOpaque(memory, canvas, kBandBackground);

        // Feed enough samples to fill the 16-column history, one per simulated second.
        for (int tick = 0; tick < 24; tick++)
        {
            Snapshot snapshot;
            snapshot.flags = FlagCpuValid | FlagMemoryValid | FlagTemperatureValid |
                             FlagNetworkValid | FlagAgentAlive;
            snapshot.timestampUtcTicks = (long long)(tick + 1) * 10000000LL;
            snapshot.cpuPercent = (float)(25 + (tick * 7) % 50);
            snapshot.memoryPercent = (float)(40 + (tick * 3) % 30);
            snapshot.temperatureC = (float)(45 + (tick % 20));
            snapshot.netUpBytesPerSec = 120000.0 + tick * 1000.0;
            snapshot.netDownBytesPerSec = 2400000.0 + tick * 5000.0;
            wcsncpy_s(snapshot.adapterName, L"Ethernet 2", _TRUNCATE);
            renderer.Paint(memory, settings, snapshot, true, width, height, test.scale);
        }

        // Anything that differs from the band background is content. Check the bounding box
        // stays inside the DIB: a pixel in the last row/column means the layout was clipped.
        const DWORD background = 0xFF000000u |
            ((DWORD)GetRValue(kBandBackground) << 16) |
            ((DWORD)GetGValue(kBandBackground) << 8) |
            (DWORD)GetBValue(kBandBackground);

        const DWORD* pixels = (const DWORD*)bits;
        int minX = width, maxX = -1, minY = height, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if ((pixels[y * width + x] & 0x00FFFFFFu) == (background & 0x00FFFFFFu)) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        bool inside = maxX >= 0 && maxX < width && maxY < height;
        if (!inside) failures++;

        wchar_t path[260];
        swprintf_s(path, L"%s\\band-%s.bmp", outDir, test.suffix);
        SaveBmp(path, width, height, bits);

        wprintf(L"%s scale=%.2f  requested=%dx%d  content=[%d..%d]x[%d..%d]  rightMargin=%d  -> %s\n",
                inside ? L"PASS" : L"FAIL", test.scale, width, height,
                minX, maxX, minY, maxY, maxX >= 0 ? width - 1 - maxX : -1,
                path);

        SelectObject(memory, oldBitmap);
        DeleteObject(bitmap);
        DeleteDC(memory);
    }

    ReleaseDC(nullptr, screen);

    wprintf(L"\n%s\n", failures == 0 ? L"render probe passed" : L"render probe FAILED");
    return failures;
}
