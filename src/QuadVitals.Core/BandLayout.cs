using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// Width arithmetic for the taskbar band. Single source of truth: the DeskBand asks
    /// <see cref="TotalWidth"/> for its ptMinSize.x, and the settings UI shows the same number
    /// as a live preview. Measured against the S3 spike: the XMeters-mould default lands at
    /// ~268 px, and the real renderer needs 275 px (it adds the triangle gutters).
    /// </summary>
    public static class BandLayout
    {
        /// <summary>Width reserved for an up/down triangle inside a text element.</summary>
        // 11 = 2 px air after the number + the 9 px triangle (see Renderer::kIndicatorWidth).
        public const int IndicatorWidth = 11;

        public const int MinBandWidth = 40;
        public const int MaxBandWidth = 1200;

        /// <summary>
        /// Column count for a bar element that follows the CPU. Physical cores by default,
        /// logical processors when the capsule switch is on; falls back to the element's own
        /// value when the hardware counts are unknown.
        /// </summary>
        public static int ColumnsFor(BandElement element, int physicalCores, int logicalProcessors)
        {
            if (element == null) return 0;

            string mode = ColumnModes.Canonical(element.ColumnMode);
            if (mode == ColumnModes.Custom)
            {
                // 自定义：用元素自己的数字，但仍夹在 1..64，并且不超过线程数（上限由 UI 保证，
                // 这里再兜一次，防止手改 JSON 超出物理上限）。
                int custom = element.CustomColumns > 0 ? element.CustomColumns : element.HistoryColumns;
                if (custom <= 0) custom = 1;
                int cap = logicalProcessors > 0 ? logicalProcessors : 64;
                return Math.Max(1, Math.Min(cap, custom));
            }

            int wanted = mode == ColumnModes.Threads ? logicalProcessors : physicalCores;
            if (wanted > 0) return Math.Max(1, Math.Min(64, wanted));
            return element.HistoryColumns > 0 ? element.HistoryColumns : 16;
        }

        public static int ElementWidth(BandElement element)
        {
            if (element == null) return 0;
            if (!element.Enabled) return 0;

            if (element.IsType(BandElementType.Bar))
            {
                int columns = Math.Max(1, element.HistoryColumns);
                return columns * element.CellWidth + (columns - 1) * element.CellGap;
            }

            if (element.IsType(BandElementType.Pie))
            {
                return element.Diameter;
            }

            if (element.IsType(BandElementType.Text))
            {
                // 下界，不是真实宽度：DeskBand 按字体实测文字宽度排布，这里只能算
                // 「最小宽度 + 三角槽」。设置页只用它比较「配置改没改」，不能当实际带宽用。
                return element.Width + (HasIndicator(element) ? IndicatorWidth : 0);
            }

            return 0;
        }

        public static bool HasIndicator(BandElement element)
        {
            if (element == null || element.Lines == null) return false;
            foreach (TextLine line in element.Lines)
            {
                if (line == null) continue;
                if (line.Indicator == Indicator.Up || line.Indicator == Indicator.Down) return true;
            }
            return false;
        }

        /// <summary>
        /// Total width of the band: sum of enabled element widths, a horizontal gap between
        /// neighbours, and the trailing padding. This is what becomes ptMinSize.x.
        /// </summary>
        public static int TotalWidth(Settings settings)
        {
            if (settings == null || settings.Items == null) return MinBandWidth;

            int spacing = settings.Band == null ? 0 : Math.Max(0, settings.Band.HorizontalSpacing);
            int trailing = settings.Band == null ? 0 : Math.Max(0, settings.Band.TrailingPad);

            int width = 0;
            int enabledSeen = 0;

            foreach (BandElement element in settings.Items)
            {
                if (element == null || !element.Enabled) continue;
                int elementWidth = ElementWidth(element);
                if (elementWidth <= 0) continue;

                if (enabledSeen > 0) width += spacing;
                width += elementWidth;
                enabledSeen++;
            }

            if (enabledSeen == 0) return MinBandWidth;

            width += trailing;
            if (width < MinBandWidth) width = MinBandWidth;
            if (width > MaxBandWidth) width = MaxBandWidth;
            return width;
        }

        /// <summary>
        /// Height needed by the band: the taskbar gives 40 px; taller elements force more.
        /// The shell refuses a band whose ptMinSize.y equals the full taskbar height (S3),
        /// so callers keep ptMinSize.y at 24 and use this only for the drawing area.
        /// </summary>
        public static int DrawingHeight(Settings settings)
        {
            int height = settings != null && settings.Band != null ? settings.Band.Height : 40;
            if (settings == null || settings.Items == null) return height;

            foreach (BandElement element in settings.Items)
            {
                if (element == null || !element.Enabled) continue;
                if (element.IsType(BandElementType.Bar) || element.IsType(BandElementType.Pie))
                {
                    int wanted = element.IsType(BandElementType.Pie) ? element.Diameter : element.Height;
                    if (wanted > height) height = wanted;
                }
            }
            return height;
        }

        /// <summary>Columns the bar element should keep in its ring buffer.</summary>
        public static int HistoryColumns(BandElement element)
        {
            if (element == null || !element.IsType(BandElementType.Bar)) return 0;
            return Math.Max(1, element.HistoryColumns);
        }
    }
}
