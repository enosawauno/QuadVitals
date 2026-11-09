using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuadVitals.Core;

namespace QuadVitals.UI
{
    /// <summary>
    /// One overview tile: small label, big value + unit, thick pill bar, caption.
    /// </summary>
    public class MetricTile : StackPanel
    {
        private readonly TextBlock _label = new TextBlock();
        private readonly TextBlock _value = new TextBlock();
        private readonly TextBlock _unit = new TextBlock();
        private readonly TextBlock _caption = new TextBlock();
        private readonly MetricBar _bar;
        private readonly Color _accent;

        public MetricTile(string label, Color accent)
        {
            Orientation = Orientation.Vertical;
            _accent = accent;
            _bar = new MetricBar(accent, Theme.TrackGrey);

            _label.Text = label;
            _label.FontSize = 12;
            _label.Foreground = Theme.TextTertiaryBrush;
            Children.Add(_label);

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _value.Text = "--";
            MetricTypography.ApplyValue(_value);
            _value.Foreground = Theme.TextPrimaryBrush;
            row.Children.Add(_value);

            MetricTypography.ApplyUnit(_unit);
            _unit.Foreground = Theme.TextSecondaryBrush;
            _unit.VerticalAlignment = VerticalAlignment.Bottom;
            _unit.Margin = new Thickness(4, 0, 0, 4);
            row.Children.Add(_unit);
            Children.Add(row);

            _bar.Margin = new Thickness(0, 12, 0, 0);
            Children.Add(_bar);

            _caption.FontSize = CaptionFontSizeDefault;
            _caption.Foreground = Theme.TextSecondaryBrush;
            // 一行：说明文字折成两行时整排格子的对齐就被拉歪了（CPU 格出现过），
            // 所以宁精简文案也不允许换行。放不下时先整行一起缩字号（见 CaptionFontSize），
            // 缩到地板仍放不下才交给省略号，不要硬切半个字。
            _caption.TextWrapping = TextWrapping.NoWrap;
            _caption.TextTrimming = TextTrimming.CharacterEllipsis;
            _caption.Margin = new Thickness(0, 10, 0, 0);
            Children.Add(_caption);
        }

        /// <summary>说明行的设计字号。</summary>
        public const double CaptionFontSizeDefault = 11.5;

        /// <summary>说明行的最小字号；再小就比格子上面的标签还难认，交给省略号。</summary>
        public const double CaptionFontSizeFloor = 8.5;

        public double CaptionFontSize
        {
            get { return _caption.FontSize; }
            set { _caption.FontSize = value; }
        }

        /// <summary>
        /// 说明行在给定字号下的单行宽度（同字体、同字重、不含外边距）。
        /// 调用方用它先量再选字号：同排四格必须用同一个字号，否则一行里会看到四种大小。
        /// </summary>
        public double CaptionWidthAt(double fontSize)
        {
            string text = _caption.Text;
            if (string.IsNullOrEmpty(text)) return 0;
            FormattedText measured = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(_caption.FontFamily, _caption.FontStyle, _caption.FontWeight, _caption.FontStretch),
                fontSize,
                _caption.Foreground,
                1.0);
            return measured.WidthIncludingTrailingWhitespace;
        }

        /// <summary>格子左上角的标签（`CPU · 占用` / `CPU · 温度` …），跟着所选指标变。</summary>
        public string Label
        {
            get { return _label.Text; }
            set { _label.Text = value; }
        }

        /// <summary>走马循环：淡入度与目标转速，内部缓动逼近（见 MetricBar.SetIoWave）。</summary>
        public void SetIoWave(double waveOpacity, double cyclesPerSecond)
        {
            _bar.SetIoWave(waveOpacity, cyclesPerSecond);
        }

        /// <summary>走马只在鼠标停在这一格时才跑（见 MetricBar.WaveOnHover）。</summary>
        public bool IoWaveOnHover
        {
            get { return _bar.WaveOnHover; }
            set { _bar.WaveOnHover = value; }
        }

        /// <summary>胶囊条还是 16 段小分段；从 settings 的 indicatorStyle 来，切换后立即重画。</summary>
        public MetricBarStyle BarStyle
        {
            get { return _bar.BarStyle; }
            set { _bar.BarStyle = value; }
        }

        public void SetValue(string value, string unit, double fraction, string caption,
                             MetricTone tone = MetricTone.Normal, Brush valueBrush = null)
        {
            _value.Text = value ?? "--";
            _unit.Text = unit ?? string.Empty;
            _bar.SetValue(fraction);
            _caption.Text = caption ?? string.Empty;
            // “没有数据”和“数据是 0”必须一眼分开：说明行用警告色，有数据时才是次级灰。
            bool noData = string.IsNullOrEmpty(value) || value == MetricFormat.Invalid;
            _caption.Foreground = noData ? Theme.AccentOrangeBrush : Theme.TextSecondaryBrush;

            Color barColor = _accent;
            Brush textBrush = Theme.TextPrimaryBrush;
            if (tone == MetricTone.Warning)
            {
                barColor = Theme.AccentOrange;
                textBrush = Theme.AccentOrangeBrush;
            }
            else if (tone == MetricTone.Danger)
            {
                barColor = Theme.AccentRed;
                textBrush = Theme.AccentRedBrush;
            }

            _bar.SetTone(barColor);
            _value.Foreground = valueBrush ?? textBrush;
        }
    }
}
