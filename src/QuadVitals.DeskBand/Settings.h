// settings.json v2 as the DeskBand sees it: an ordered list of elements, each with a
// type (bar/pie/text), a metric from the closed catalog, colours and sizes.
// Everything here has a default that matches QuadVitals.Core.Settings.CreateDefault(), so a
// missing or broken file still renders the XMeters mould.
#pragma once

#include <windows.h>
#include <string>
#include <vector>

namespace pb
{
    enum class MetricId
    {
        None = 0,
        Cpu,
        Memory,
        Temperature,
        NetUp,
        NetDown,
        Gpu,
        GpuMemory,
        GpuTemp,
        Disk,
        DiskIo,
        DiskIoRead,
        DiskIoWrite
    };

    MetricId MetricFromKey(const std::wstring& key);
    const wchar_t* MetricKey(MetricId id);
    const wchar_t* MetricDisplayName(MetricId id);
    bool IsPercentMetric(MetricId id);

    enum class ElementType
    {
        Unknown = 0,
        Bar,
        Pie,
        Text
    };

    ElementType ElementTypeFromString(const std::wstring& text);

    // "#AARRGGBB" -> COLORREF. Falls back to the given colour on anything malformed.
    COLORREF ParseColor(const std::wstring& text, COLORREF fallback);

    struct TextLine
    {
        MetricId metric = MetricId::None;
        COLORREF color = RGB(255, 255, 255);
        int indicator = 0;   // 0 none, 1 up, -1 down
    };

    struct Element
    {
        ElementType type = ElementType::Unknown;
        MetricId metric = MetricId::None;
        bool enabled = true;

        int historyColumns = 16;
        // 文本元素的最小宽度：实际宽度取它和实测文字宽度的较大者（Renderer，design）。
        int width = 12;
        int height = 25;
        int cellWidth = 9;
        int cellGap = 2;
        int diameter = 25;

        // 环形画法（只对 pie 有意义）：false = 实心扇形 + 白描边，true = 深色轨道 + 12 点钟
        // 起的渐变弧（settings.json 的 `style: "gradient"`）。
        bool gradientRing = false;

        // Per-element refresh, ms. 0/out-of-range is normalised to the global refreshMs when the
        // file is parsed.
        int refreshMs = 1000;

        // 磁盘 I/O 元素采样的盘符（'C'..）。0 = 全部磁盘（系统合计）。只对绑定 diskIo* 的
        // 文本元素有意义；其它元素解析时会被置 0。
        wchar_t drive = 0;

        COLORREF fill = RGB(0x1D, 0xB9, 0x54);
        COLORREF outline = RGB(0xFF, 0xFF, 0xFF);
        // CPU 内核态色：先画它、再用 fill 覆盖（可见的只有“内核超过用户态”那段）。
        // 默认 #FF0000，可在显示项里改（settings.json 的 colors.kernel）。
        COLORREF kernel = RGB(0xFF, 0x00, 0x00);

        std::vector<TextLine> lines;
    };

    struct BandSettings
    {
        int height = 40;
        int horizontalSpacing = 3;
        int trailingPad = 2;
        int refreshMs = 1000;
        COLORREF outline = RGB(0xFF, 0xFF, 0xFF);
        bool showTooltip = true;
        std::vector<Element> items;

        // Widest element height, clamped into the taskbar strip.
        int ContentHeight() const;
    };

    // 带高 / 带宽的计算在 Renderer（需要字体实测，design）：
    //   Renderer::ElementWidthPx   实际布局宽度（文本 = max(最小宽度, 实测内容)）
    //   Renderer::RequiredWidthPx  申请给 taskbar 的宽度（文本按最宽可能值算，运行期宽不得变）

    // Built-in XMeters mould (no file access). The render probe uses this so its DPI checks do
    // not depend on the user's current settings.
    void DefaultSettings(BandSettings& out);

    // Loads %APPDATA%\QuadVitals\settings.json. Returns false when the file is missing or broken;
    // the output is then the default mould, so the band never renders blank.
    bool LoadSettings(BandSettings& out, std::wstring* pathUsed = nullptr);

    std::wstring SettingsPath();
}
