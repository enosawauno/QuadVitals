#include "Settings.h"
#include "Json.h"

#include <shlobj.h>
#include <stdio.h>

namespace pb
{
    namespace
    {
        MetricId MetricFromKeyInner(const std::wstring& key)
        {
            if (key == L"cpu") return MetricId::Cpu;
            if (key == L"memory") return MetricId::Memory;
            if (key == L"temperature") return MetricId::Temperature;
            if (key == L"netUp") return MetricId::NetUp;
            if (key == L"netDown") return MetricId::NetDown;
            if (key == L"gpu") return MetricId::Gpu;
            if (key == L"gpuMemory") return MetricId::GpuMemory;
            if (key == L"gpuTemp") return MetricId::GpuTemp;
            if (key == L"disk") return MetricId::Disk;
            if (key == L"diskIo") return MetricId::DiskIo;
            if (key == L"diskIoRead") return MetricId::DiskIoRead;
            if (key == L"diskIoWrite") return MetricId::DiskIoWrite;
            return MetricId::None;
        }

    // Content height shared by every element type (bars, ring, text). 28 leaves 6 px above and
    // below in a 40 px taskbar, so the vertical centring is exact rather than off by one.
    const int kContentHeight = 26;

        Element MakeBar(MetricId metric, int columns, int cellWidth, int cellGap, int height,
                        COLORREF fill, COLORREF outline)
        {
            Element element;
            element.type = ElementType::Bar;
            element.metric = metric;
            element.historyColumns = columns;
            element.cellWidth = cellWidth;
            element.cellGap = cellGap;
            element.height = height;
            element.fill = fill;
            element.outline = outline;
            element.refreshMs = 1000;
            return element;
        }

        Element MakePie(MetricId metric, int diameter, COLORREF fill, COLORREF outline)
        {
            Element element;
            element.type = ElementType::Pie;
            element.metric = metric;
            element.diameter = diameter;
            element.height = diameter;
            element.fill = fill;
            element.outline = outline;
            return element;
        }

        Element MakeText(int width, COLORREF row1, MetricId m1, int ind1,
                         COLORREF row2, MetricId m2, int ind2)
        {
            Element element;
            element.type = ElementType::Text;
            element.width = width;
            TextLine line1; line1.metric = m1; line1.color = row1; line1.indicator = ind1;
            TextLine line2; line2.metric = m2; line2.color = row2; line2.indicator = ind2;
            element.lines.push_back(line1);
            element.lines.push_back(line2);
            return element;
        }

        // Default band = the XMeters mould measured in S3.
        // Keep in sync with QuadVitals.Core.Settings.DefaultGreen/DefaultGreenSoft/DefaultBlue.
        // The old XMeters palette (#00CC6A/#FF0000) must not creep back in through defaults.
        const COLORREF kGreen = RGB(0x1D, 0xB9, 0x54);
        const COLORREF kGreenSoft = RGB(0x41, 0xD4, 0x84);
        const COLORREF kBlue = RGB(0x51, 0x9A, 0xFF);
        const COLORREF kWhite = RGB(0xFF, 0xFF, 0xFF);

        void ApplyDefaults(BandSettings& settings)
        {
            settings.height = 40;
            settings.horizontalSpacing = 3;
            settings.trailingPad = 2;
            settings.refreshMs = 1000;
            settings.outline = kWhite;
            settings.showTooltip = true;
            settings.items.clear();
            settings.items.push_back(MakeBar(MetricId::Cpu, 16, 9, 2, kContentHeight, kGreen, kWhite));
            settings.items.push_back(MakeText(12, kBlue, MetricId::NetUp, 1,
                                                  kGreenSoft, MetricId::NetDown, -1));
            settings.items.push_back(MakePie(MetricId::Memory, kContentHeight, kGreen, kWhite));
        }

        int ClampInt(int value, int minValue, int maxValue, int fallback)
        {
            if (value < minValue || value > maxValue) return fallback;
            return value;
        }
    }

    void DefaultSettings(BandSettings& out)
    {
        ApplyDefaults(out);
    }

    MetricId MetricFromKey(const std::wstring& key) { return MetricFromKeyInner(key); }

    const wchar_t* MetricKey(MetricId id)
    {
        switch (id)
        {
        case MetricId::Cpu: return L"cpu";
        case MetricId::Memory: return L"memory";
        case MetricId::Temperature: return L"temperature";
        case MetricId::NetUp: return L"netUp";
        case MetricId::NetDown: return L"netDown";
        case MetricId::Gpu: return L"gpu";
        case MetricId::GpuMemory: return L"gpuMemory";
        case MetricId::GpuTemp: return L"gpuTemp";
        case MetricId::Disk: return L"disk";
        case MetricId::DiskIo: return L"diskIo";
        case MetricId::DiskIoRead: return L"diskIoRead";
        case MetricId::DiskIoWrite: return L"diskIoWrite";
        default: return L"";
        }
    }

    const wchar_t* MetricDisplayName(MetricId id)
    {
        switch (id)
        {
        case MetricId::Cpu: return L"CPU \u5360\u7528";
        case MetricId::Memory: return L"\u5185\u5B58\u5360\u7528";
        case MetricId::Temperature: return L"CPU \u6E29\u5EA6";
        case MetricId::NetUp: return L"\u4E0A\u884C\u901F\u5EA6";
        case MetricId::NetDown: return L"\u4E0B\u884C\u901F\u5EA6";
        case MetricId::Gpu: return L"\u663E\u5361\u5360\u7528";
        case MetricId::GpuMemory: return L"\u663E\u5B58\u5360\u7528";
        case MetricId::GpuTemp: return L"GPU \u6E29\u5EA6";
        case MetricId::Disk: return L"\u78C1\u76D8\u5360\u7528";
        case MetricId::DiskIo: return L"\u78C1\u76D8 I/O";
        case MetricId::DiskIoRead: return L"\u78C1\u76D8\u8BFB\u53D6";
        case MetricId::DiskIoWrite: return L"\u78C1\u76D8\u5199\u5165";
        default: return L"\u2014";
        }
    }

    bool IsPercentMetric(MetricId id)
    {
        return id == MetricId::Cpu || id == MetricId::Memory || id == MetricId::Gpu ||
               id == MetricId::GpuMemory || id == MetricId::Disk;
    }

    ElementType ElementTypeFromString(const std::wstring& text)
    {
        if (text == L"bar") return ElementType::Bar;
        if (text == L"pie") return ElementType::Pie;
        if (text == L"text") return ElementType::Text;
        return ElementType::Unknown;
    }

    COLORREF ParseColor(const std::wstring& text, COLORREF fallback)
    {
        if (text.size() != 9 || text[0] != L'#') return fallback;

        wchar_t* end = nullptr;
        unsigned long value = wcstoul(text.c_str() + 1, &end, 16);
        if (end == nullptr || *end != L'\0') return fallback;

        // Stored as #AARRGGBB; GDI needs BGR and has no alpha (the taskbar strip is opaque).
        BYTE r = (BYTE)((value >> 16) & 0xFF);
        BYTE g = (BYTE)((value >> 8) & 0xFF);
        BYTE b = (BYTE)(value & 0xFF);
        return RGB(r, g, b);
    }

    int BandSettings::ContentHeight() const
    {
        // One height for every element type (bars, ring, text rows): the bars decide how tall the
        // strip's content is and everything else has to line up with them. The value is a product
        // constant, not a per-element setting, so a hand-edited file cannot skew the alignment.
        return kContentHeight;
    }

    std::wstring SettingsPath()
    {
        wchar_t buffer[MAX_PATH * 2] = { 0 };
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_APPDATA, nullptr, 0, buffer)))
        {
            std::wstring path(buffer);
            path += L"\\QuadVitals\\settings.json";
            return path;
        }
        return L"";
    }

    bool LoadSettings(BandSettings& out, std::wstring* pathUsed)
    {
        ApplyDefaults(out);

        std::wstring path = SettingsPath();
        if (pathUsed != nullptr) *pathUsed = path;
        if (path.empty()) return false;

        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return false;

        LARGE_INTEGER size = { 0 };
        if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 4 * 1024 * 1024)
        {
            CloseHandle(file);
            return false;
        }

        std::string text((size_t)size.QuadPart, '\0');
        DWORD read = 0;
        BOOL ok = ReadFile(file, &text[0], (DWORD)text.size(), &read, nullptr);
        CloseHandle(file);
        if (!ok || read != text.size()) return false;

        // Strip a UTF-8 BOM: notepad writes one and the parser would reject it.
        if (text.size() >= 3 && (unsigned char)text[0] == 0xEF && (unsigned char)text[1] == 0xBB &&
            (unsigned char)text[2] == 0xBF)
        {
            text.erase(0, 3);
        }

        JsonValue root;
        if (!ParseJson(text, root) || !root.IsObject()) return false;

        BandSettings loaded;
        ApplyDefaults(loaded);

        // settings.json v3 把文本元素的 width 从「固定字段宽」改成「最小宽度」；旧文件里的 52
        // 是当时的固定默认值，不是显式选择，继续当最小宽度会把「元素间距」垫宽。
        int fileVersion = root.GetInt(L"version", 1);

        loaded.refreshMs = ClampInt(root.GetInt(L"refreshMs", 1000), 200, 10000, 1000);

        const JsonValue* band = root.Get(L"band");
        if (band != nullptr && band->IsObject())
        {
            loaded.height = ClampInt(band->GetInt(L"height", 40), 24, 64, 40);
            loaded.horizontalSpacing = ClampInt(band->GetInt(L"horizontalSpacing", 3), 0, 40, 3);
            loaded.trailingPad = ClampInt(band->GetInt(L"trailingPad", 2), 0, 40, 2);
            loaded.outline = ParseColor(band->GetString(L"outlineColor", L"#FFFFFFFF"), RGB(255, 255, 255));
            loaded.showTooltip = band->GetBool(L"showTooltip", true);
        }

        const JsonValue* items = root.Get(L"items");
        if (items == nullptr || !items->IsArray() || items->arrayValue.empty()) return true; // defaults stand

        std::vector<Element> parsed;
        for (const JsonValue& item : items->arrayValue)
        {
            if (!item.IsObject()) continue;

            Element element;
            element.type = ElementTypeFromString(item.GetString(L"type", L""));
            if (element.type == ElementType::Unknown) continue;

            element.enabled = item.GetBool(L"enabled", true);
            element.metric = MetricFromKey(item.GetString(L"metric", L""));
            element.historyColumns = ClampInt(item.GetInt(L"historyColumns", 16), 1, 64, 16);
            element.width = ClampInt(item.GetInt(L"width", 12), 12, 400, 12);
            if (fileVersion < 3) element.width = 12;
            element.height = kContentHeight;   // fixed: see BarHeight in QuadVitals.Core.Settings
            // Bar geometry is a fixed product constant (Settings.cpp mirrors
            // QuadVitals.Core.Settings.BarCellWidth/BarCellGap); the file value is ignored on
            // purpose, same as the C# side, so a hand-edited JSON cannot change the look.
            element.cellWidth = 8;
            element.cellGap = 2;
            element.diameter = kContentHeight;  // ring diameter == bar height, always
            element.gradientRing = _wcsicmp(item.GetString(L"style", L"solid").c_str(), L"gradient") == 0;
            element.refreshMs = ClampInt(item.GetInt(L"refreshMs", loaded.refreshMs),
                                         200, 60000, loaded.refreshMs);

            // 盘符只取第一个字母；其它写法当作“全部磁盘”，不因为一个手改的字段把元素丢掉。
            std::wstring drive = item.GetString(L"drive", L"");
            if (!drive.empty())
            {
                wchar_t letter = drive[0];
                if (letter >= L'a' && letter <= L'z') letter = (wchar_t)(letter - L'a' + L'A');
                if (letter >= L'A' && letter <= L'Z') element.drive = letter;
            }

            const JsonValue* colors = item.Get(L"colors");
            if (colors != nullptr && colors->IsObject())
            {
                element.fill = ParseColor(colors->GetString(L"fill", L"#FF1DB954"), kGreen);
                element.outline = ParseColor(colors->GetString(L"outline", L"#FFFFFFFF"), kWhite);
                element.kernel = ParseColor(colors->GetString(L"kernel", L"#FFFF0000"), RGB(0xFF, 0x00, 0x00));
            }
            else
            {
                element.outline = loaded.outline;
            }

            if (element.type == ElementType::Bar || element.type == ElementType::Pie)
            {
                if (element.metric == MetricId::None) continue; // unknown metric: drop the element
            }
            else if (element.type == ElementType::Text)
            {
                const JsonValue* lines = item.Get(L"lines");
                if (lines == nullptr || !lines->IsArray()) continue;

                for (const JsonValue& lineValue : lines->arrayValue)
                {
                    if (!lineValue.IsObject()) continue;
                    TextLine line;
                    line.metric = MetricFromKey(lineValue.GetString(L"metric", L""));
                    if (line.metric == MetricId::None) continue;
                    line.color = ParseColor(lineValue.GetString(L"color", L"#FFFFFFFF"), RGB(255, 255, 255));

                    std::wstring indicator = lineValue.GetString(L"indicator", L"none");
                    if (indicator == L"up") line.indicator = 1;
                    else if (indicator == L"down") line.indicator = -1;
                    else line.indicator = 0;

                    element.lines.push_back(line);
                    if (element.lines.size() >= 2) break;
                }
                if (element.lines.empty()) continue;
            }

            parsed.push_back(element);
        }

        if (parsed.empty()) return true; // keep defaults rather than render nothing

        bool anyEnabled = false;
        for (const Element& element : parsed) { if (element.enabled) { anyEnabled = true; break; } }
        if (!anyEnabled) parsed[0].enabled = true;

        loaded.items = parsed;
        out = loaded;
        return true;
    }
}
