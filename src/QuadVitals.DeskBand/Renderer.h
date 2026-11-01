// Draws the configured element list from one snapshot, using the XMeters visual
// vocabulary measured in S3: outlined columns filled from the bottom, right-aligned
// values with up/down triangles, and a ring gauge with a green wedge.
#pragma once

#include <windows.h>
#include <vector>
#include "MetricsClient.h"
#include "OpaqueFill.h"
#include "Settings.h"

namespace pb
{
    class Renderer
    {
    public:
        Renderer() = default;
        ~Renderer();
        Renderer(const Renderer&) = delete;
        Renderer& operator=(const Renderer&) = delete;

        // Called when the element list changes so per-element smoothing/history is rebuilt.
        void Reset(const BandSettings& settings);

        // The taskbar backdrop this band sits on. Element surfaces have to be composed against
        // localX: they are blitted opaque, so a fixed colour would show as a slab on a
        // translucent taskbar.
        void SetTint(const BandTint& tint, int screenLeft);

        COLORREF TintAt(int localX) const { return _tint.At(_tintScreenLeft + localX); }
        const BandTint& Tint() const { return _tint; }
        int TintScreenLeft() const { return _tintScreenLeft; }

        // Draws the whole band. `scale` is dpi/96. Returns the height actually used.
        int Paint(HDC hdc, const BandSettings& settings, const Snapshot& snapshot, bool agentAlive,
                  int width, int height, double scale);

        // 实际布局宽度（物理像素）。文本元素按实测内容加宽：配置的 width 只是下限，reserve
        // 在本会话内只增不减（数值位数变化时不会每帧左右跳）。所以 `horizontalSpacing` 就是
        // 看得见的间隙，而不是被没画满的固定字段垫宽。
        int ElementWidthPx(size_t index, const Element& element, double scale) const;

        // 向 taskbar 申请的宽度（ptMinSize.x / 窗口宽度）。文本按「最宽可能值」算：band 宽度
        // 运行期不可改（S3），所以以后再变宽的数值不能把最后一个元素裁掉。
        int RequiredWidthPx(const BandSettings& settings, double scale);

        // Tooltip text: every configured metric plus source/age, one metric per line.
        static std::wstring BuildTooltip(const BandSettings& settings, const Snapshot& snapshot, bool agentAlive);

        // Shared formatting so tooltip and band agree (mirrors QuadVitals.Core.MetricFormat).
        // `drive` (0 = none) redirects disk-I/O metrics to one volume's slot (design 修订).
        static std::wstring FormatMetric(MetricId id, const Snapshot& snapshot, bool detailed, wchar_t drive = 0);
        static std::wstring FormatRate(double bytesPerSecond);
        static std::wstring FormatPercent(double percent);
        static std::wstring FormatTemperature(double celsius);

    private:
        // One text row exactly as drawn (hardware label + formatted value, "--" when offline).
        static std::wstring LineText(const TextLine& line, const Snapshot& snapshot, bool agentAlive, wchar_t drive = 0);
        // The widest string the row can ever show; sizes the band, never the layout.
        static std::wstring WidestLineText(const TextLine& line);

        // Width an element occupies given the field width its text block uses. Bars and rings
        // ignore the field; text appends the triangle gutter when a row draws one.
        int OuterWidthPx(const Element& element, int textFieldPx, double scale) const;
        // Text field for layout: max(configured minimum, widest value measured this session).
        int TextFieldWidthPx(size_t index, const Element& element, double scale) const;
        // Grows each text element's reserve; called once per paint before any element is laid out.
        void MeasureText(HDC hdc, const BandSettings& settings, const Snapshot& snapshot,
                         bool agentAlive, double scale);
        HDC MeasureDc();

        BandTint _tint;
        int _tintScreenLeft = 0;
        HDC _measureDc = nullptr;

        struct ElementState
        {
            std::vector<int> history;        // bars: 0..100 fill percentages, oldest first
            // CPU bars keep the split as well: system is painted first, user on top of it, so the
            // visible red sliver is only the part where system > user (XMeters behaviour).
            std::vector<int> historyUser;
            std::vector<int> historySystem;
            double ema1 = 0.0;           // text/bar smoothing
            double ema2 = 0.0;
            bool hasEma1 = false;
            bool hasEma2 = false;
            long long lastTickTicks = 0;      // last snapshot consumed (dedupe paints)
            long long lastSampleTicks = 0;    // last snapshot that passed the element refresh check
            // Widest text line seen in this session, in 1x px; only grows so the value's digits
            // move inside a stable field instead of the neighbours moving around.
            int reserve1x = 0;
        };

        void PushSample(const Element& element, ElementState& state, const Snapshot& snapshot, bool agentAlive);
        static double ValueOf(const Element& element, const Snapshot& snapshot, int lineIndex, bool agentAlive);

        static void DrawBarFill(HDC hdc, int cellX, int top, int cellWidth, int barHeight,
                                int inset, int percent, COLORREF colour);
        void DrawBar(HDC hdc, const Element& element, const ElementState& state,
                     const Snapshot& snapshot, int x, int top, int height, double scale);
        void DrawPie(HDC hdc, const Element& element, const ElementState& state,
                     const Snapshot& snapshot, bool agentAlive, int x, int top, double scale);
        void DrawText(HDC hdc, size_t index, const Element& element, const ElementState& state,
                      const Snapshot& snapshot, bool agentAlive, int x, int top, int height, double scale);

        std::vector<ElementState> _states;
    };
}
