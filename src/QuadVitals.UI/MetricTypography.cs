using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuadVitals.UI
{
    /// <summary>
    /// 卡片主体数值的统一排版。来源是 FOOps 设置页「本地环境」四张硬件卡的主数值：
    /// Arial Black、40 px、900、44 px 行高；单位 17 px、同一字体。
    /// 设备四格、风扇 RPM 与功耗卡主数字从这里取；网速上下行保留原来的 18 px SemiBold，避免把曲线挤掉。
    /// </summary>
    public static class MetricTypography
    {
        public static readonly FontFamily Digits = new FontFamily("Arial Black");
        public const double ValueSize = 40;
        public const double UnitSize = 17;
        public const double LineHeight = 44;
        public static readonly FontWeight ValueWeight = FontWeights.Black;

        public static void ApplyValue(TextBlock text)
        {
            if (text == null) return;
            text.FontFamily = Digits;
            text.FontSize = ValueSize;
            text.FontWeight = ValueWeight;
            text.LineHeight = LineHeight;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        }

        public static void ApplyUnit(TextBlock text)
        {
            if (text == null) return;
            text.FontFamily = Digits;
            text.FontSize = UnitSize;
            text.FontWeight = ValueWeight;
            text.LineHeight = UnitSize;
            text.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        }
    }
}
