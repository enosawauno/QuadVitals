using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace QuadVitals.Core
{
    public static class BandElementType
    {
        public const string Bar = "bar";       // history column array
        public const string Pie = "pie";       // ring + wedge
        public const string Text = "text";     // 1..2 values, optional up/down triangle

        public static bool IsKnown(string type)
        {
            return string.Equals(type, Bar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, Pie, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, Text, StringComparison.OrdinalIgnoreCase);
        }

        public static string Canonical(string type)
        {
            if (string.Equals(type, Bar, StringComparison.OrdinalIgnoreCase)) return Bar;
            if (string.Equals(type, Pie, StringComparison.OrdinalIgnoreCase)) return Pie;
            if (string.Equals(type, Text, StringComparison.OrdinalIgnoreCase)) return Text;
            return null;
        }
    }

    /// <summary>Where a bar element's column count comes from.</summary>
    public static class ColumnModes
    {
        public const string Cores = "cores";     // physical cores
        public const string Threads = "threads"; // logical processors
        public const string Custom = "custom";   // whatever the user typed (capped at threads)

        public static bool IsKnown(string mode)
        {
            return string.Equals(mode, Cores, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, Threads, StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, Custom, StringComparison.OrdinalIgnoreCase);
        }

        public static string Canonical(string mode)
        {
            if (string.Equals(mode, Threads, StringComparison.OrdinalIgnoreCase)) return Threads;
            if (string.Equals(mode, Custom, StringComparison.OrdinalIgnoreCase)) return Custom;
            return Cores;
        }
    }

    public static class Indicator
    {
        public const string None = "none";
        public const string Up = "up";
        public const string Down = "down";
    }

    /// <summary>
    /// 环形元素的画法：实心扇形（默认，先画扇形再叠白色环）或渐变环（深色轨道 + 从 12 点钟
    /// 顺时针的渐变弧，不画白描边）。只对 `pie` 有意义，其它类型忽略。
    /// </summary>
    public static class PieStyle
    {
        public const string Solid = "solid";
        public const string Gradient = "gradient";

        public static bool IsKnown(string style)
        {
            return string.Equals(style, Solid, StringComparison.OrdinalIgnoreCase)
                || string.Equals(style, Gradient, StringComparison.OrdinalIgnoreCase);
        }

        public static string Canonical(string style)
        {
            return string.Equals(style, Gradient, StringComparison.OrdinalIgnoreCase) ? Gradient : Solid;
        }
    }

    /// <summary>Per-element colours, "#AARRGGBB" (same notation XMeters uses).</summary>
    [DataContract]
    public sealed class ElementColors
    {
        /// <summary>Primary fill: bar fill, pie used-wedge.</summary>
        [DataMember(Name = "fill")]
        public string Fill { get; set; }

        [DataMember(Name = "outline")]
        public string Outline { get; set; }

        /// <summary>
        /// 柱状元素的 CPU 内核态颜色（先画它，再用用户态色覆盖，所以看得见的只有“内核超过用户态”
        /// 的那一段）。旧配置没有这个字段 → Normalize 补默认红，原生侧同样默认 #FFFF0000。
        /// </summary>
        [DataMember(Name = "kernel")]
        public string Kernel { get; set; }

        public ElementColors Clone()
        {
            return new ElementColors { Fill = Fill, Outline = Outline, Kernel = Kernel };
        }
    }

    /// <summary>One row of a text element.</summary>
    [DataContract]
    public sealed class TextLine
    {
        [DataMember(Name = "metric")]
        public string Metric { get; set; }

        [DataMember(Name = "color")]
        public string Color { get; set; }

        /// <summary>"none" | "up" | "down" triangle drawn right of the value.</summary>
        [DataMember(Name = "indicator")]
        public string Indicator { get; set; }

        public TextLine Clone()
        {
            return new TextLine { Metric = Metric, Color = Color, Indicator = Indicator };
        }
    }

    /// <summary>
    /// One rendered item in the taskbar band. The band is an ordered list of these;
    /// nothing about the layout is hard coded any more.
    /// </summary>
    [DataContract]
    public sealed class BandElement
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        /// <summary>"bar" | "pie" | "text".</summary>
        [DataMember(Name = "type")]
        public string Type { get; set; }

        [DataMember(Name = "enabled")]
        public bool Enabled { get; set; }

        /// <summary>Metric key from the catalog; unused by "text" (it uses Lines).</summary>
        [DataMember(Name = "metric")]
        public string Metric { get; set; }

        /// <summary>bar: how many columns of history (one per refresh tick).</summary>
        [DataMember(Name = "historyColumns")]
        public int HistoryColumns { get; set; }

        /// <summary>
        /// bar: where the column count comes from. "cores" = physical cores,
        /// "threads" = logical processors, "custom" = the number stored in
        /// <see cref="HistoryColumns"/> (the elements page caps it at the thread count).
        ///
        /// The resolved count is still written into <see cref="HistoryColumns"/>, so the native
        /// band keeps reading a single plain number and needs no CPU knowledge.
        /// </summary>
        [DataMember(Name = "columnMode")]
        public string ColumnMode { get; set; }

        /// <summary>Legacy flag; kept so older settings.json files still load (migrated in Normalize).</summary>
        [DataMember(Name = "useLogicalCores")]
        public bool UseLogicalCores { get; set; }

        /// <summary>
        /// The number the user typed in 「自定义列数」. Kept separately from
        /// <see cref="HistoryColumns"/> (which always holds the *effective* count the band draws)
        /// so switching to 核心数量 / 线程数量 and back does not lose the custom value
        /// (a half-typed number must survive a round trip through the combo).
        /// </summary>
        [DataMember(Name = "customColumns")]
        public int CustomColumns { get; set; }

        /// <summary>
        /// 文本元素的**最小**宽度（px）。实际宽度取它和「实测文字宽度」的较大者：band 用实测值排布，
        /// 这样「元素间距」就是看得见的间距，而不是被没画满的固定字段垫宽。只有需要预留
        /// 位数变化的空间时才把它调大。
        /// </summary>
        [DataMember(Name = "width")]
        public int Width { get; set; }

        /// <summary>bar / pie height in px.</summary>
        [DataMember(Name = "height")]
        public int Height { get; set; }

        /// <summary>bar column outer width in px.</summary>
        [DataMember(Name = "cellWidth")]
        public int CellWidth { get; set; }

        /// <summary>bar gap between columns in px.</summary>
        [DataMember(Name = "cellGap")]
        public int CellGap { get; set; }

        /// <summary>pie outer diameter in px.</summary>
        [DataMember(Name = "diameter")]
        public int Diameter { get; set; }

        /// <summary>环形画法（只对 pie 有意义，见 <see cref="PieStyle"/>）：`solid` / `gradient`。</summary>
        [DataMember(Name = "style")]
        public string Style { get; set; }

        [DataMember(Name = "colors")]
        public ElementColors Colors { get; set; }

        [DataMember(Name = "lines")]
        public List<TextLine> Lines { get; set; }

        /// <summary>
        /// 磁盘 I/O 采样的盘符（"C:"）。空 = 全部磁盘（系统合计）。
        /// 只对绑 diskIo / diskIoRead / diskIoWrite 的文本元素有意义；其它元素在 Normalize 里清空。
        /// </summary>
        [DataMember(Name = "drive")]
        public string Drive { get; set; }

        [DataMember(Name = "refreshMs")]
        public int RefreshMs { get; set; }

        public bool IsType(string type)
        {
            return string.Equals(Type, type, StringComparison.OrdinalIgnoreCase);
        }

        public BandElement Clone()
        {
            BandElement copy = (BandElement)MemberwiseClone();
            copy.Colors = Colors == null ? null : Colors.Clone();
            if (Lines != null)
            {
                copy.Lines = new List<TextLine>(Lines.Count);
                foreach (TextLine line in Lines) copy.Lines.Add(line.Clone());
            }
            return copy;
        }

        public override string ToString()
        {
            return (Id ?? "?") + ":" + Type + (Enabled ? "" : "(off)");
        }
    }

    [DataContract]
    public sealed class BandSettings
    {
        /// <summary>Taskbar band height; the shell allocates ptMaxSize.y from this.</summary>
        [DataMember(Name = "height")]
        public int Height { get; set; }

        /// <summary>Gap inserted between two adjacent enabled elements.</summary>
        [DataMember(Name = "horizontalSpacing")]
        public int HorizontalSpacing { get; set; }

        /// <summary>Trailing padding at the right edge of the band.</summary>
        [DataMember(Name = "trailingPad")]
        public int TrailingPad { get; set; }

        [DataMember(Name = "outlineColor")]
        public string OutlineColor { get; set; }

        [DataMember(Name = "showTooltip")]
        public bool ShowTooltip { get; set; }

        [DataMember(Name = "reorderable")]
        public bool Reorderable { get; set; }
    }

    /// <summary>
    /// settings.json v3: band-level settings plus the ordered element list,
    /// metric keys limited to the catalog.
    /// </summary>
    [DataContract]
    public sealed class Settings
    {
        public const int CurrentVersion = 3;
        public const int DefaultRefreshMs = 1000;
        public const int MinRefreshMs = 200;
        public const int MaxRefreshMs = 10000;
        public const int MinElementRefreshMs = 200;
        public const int MaxElementRefreshMs = 60000;

        // 柱状元素的几何是产品外观常量：UI 不提供输入框，后端也不信任文件里的值。
        // 越界值同样被拉回这三个数，所以旧 settings.json 里的自定义值会在下次 Normalize 时被纠正。
        public const int BarCellWidth = 8;
        public const int BarCellGap = 2;
        // One content height for every element type: the bars set it and the ring and the text rows
        // have to line up with them. 28 leaves 6 px above and below in a 40 px taskbar.
        // 28 -> 26: the bar got a touch shorter so the strip breathes a little more inside the
        // 40 px taskbar (7 px above/below instead of 6), and every aligned element follows it.
        public const int BarHeight = 26;

        /// <summary>环形直径 = 柱高（不是另一个独立数值）：两种元素在条上占据同一条内容带。</summary>
        public const int PieDiameter = BarHeight;

        /// <summary>
        /// 文本元素的最小宽度上限/下限。下限只保证 "--" 也有地方画；正常宽度由原生渲染器按字体实测
        /// 文字宽度决定，所以这里不是「字段宽」，调大只用于预留位数变化的空间。
        /// </summary>
        public const int MinTextWidth = 12;
        public const int MaxTextWidth = 400;

        /// <summary>概览网速曲线的画法：圆滑（贝塞尔 + 平滑）或原始折线。</summary>
        public const string CurveSmooth = "smooth";
        public const string CurvePolyline = "polyline";

        /// <summary>概览四格占用指示器的画法：连成一体的胶囊条，或 16 个小分段（默认）。</summary>
        public const string IndicatorCapsule = "capsule";
        public const string IndicatorSegments = "segments";

        /// <summary>渲染时机：失焦也继续渲染（默认）。</summary>
        public const string RenderPolicyAlways = "always";

        /// <summary>渲染时机：失焦时停止渲染，回到前台后恢复。</summary>
        public const string RenderPolicyUnfocused = "unfocused";

        /// <summary>渲染时机：前台窗口占满整块显示器时停止渲染。</summary>
        public const string RenderPolicyFullscreen = "fullscreen";

        // One palette for the settings UI, the band defaults and the tests: the old XMeters
        // greens (#00CC6A/#FF0000) used to leak back in through hand-written defaults.
        public const string DefaultGreen = "#FF1DB954";
        public const string DefaultGreenSoft = "#FF41D484";
        public const string DefaultBlue = "#FF519AFF";
        public const string DefaultWhite = "#FFFFFFFF";
        public const string DefaultGrey = "#FF8F8F8F";
        public const string DefaultOrange = "#FFFFA94D";
        /// <summary>
        /// CPU 内核态的默认色。它和用户态色一样是普通可配颜色，常量只做默认值；
        /// 缺字段或非法值的旧文件回退到它。
        /// </summary>
        public const string DefaultSystemRed = "#FFFF0000";
        /// <summary>Preset hues that are not part of the accent set.</summary>
        public const string DefaultViolet = "#FFA78BFA";
        public const string DefaultCyan = "#FF51C4FF";

        // 色板里只保留**一个**绿色（品牌绿）：#41D484 与 #00CC6A 同属绿系，
        // 三个绿色挤在一行里既难区分也用不上。
        // DefaultGreenSoft 仍作为常量保留（下行走势三角/图表用），只是不进预设。
        private static readonly string[] PaletteColors =
        {
            DefaultGreen, DefaultWhite, DefaultGrey, DefaultBlue, DefaultOrange,
            DefaultViolet, DefaultCyan
        };

        private static readonly IReadOnlyList<string> PaletteView = Array.AsReadOnly(PaletteColors);

        public static IReadOnlyList<string> DefaultPalette { get { return PaletteView; } }

        [DataMember(Name = "version")]
        public int Version { get; set; }

        [DataMember(Name = "refreshMs")]
        public int RefreshMs { get; set; }

        [DataMember(Name = "band")]
        public BandSettings Band { get; set; }

        [DataMember(Name = "palette")]
        public List<string> Palette { get; set; }

        /// <summary>
        /// 按角色分开的预设色板（fill / kernel / outline / line）。旧配置没有这个字段 → null，
        /// 表示“还没自定义过”，UI 退回内置默认色板。
        /// </summary>
        [DataMember(Name = "colorPresets")]
        public ColorPresets ColorPresets { get; set; }

        /// <summary>
        /// 取色器里的开关：打开后每次点「确定」都把这个颜色加进预设（末尾淘汰制，见
        /// <see cref="PaletteRules"/>）。默认关：不主动修改色板。
        /// </summary>
        [DataMember(Name = "autoAddPreset")]
        public bool AutoAddPreset { get; set; }

        [DataMember(Name = "items")]
        public List<BandElement> Items { get; set; }

        [DataMember(Name = "networkAdapter")]
        public string NetworkAdapter { get; set; }

        [DataMember(Name = "curveStyle")]
        public string CurveStyle { get; set; }

        [DataMember(Name = "indicatorStyle")]
        public string IndicatorStyle { get; set; }

        /// <summary>
        /// Pause all dashboard animations while on battery. Default true; the user can turn it off
        /// and then the battery state no longer caps animations (RDP/tier 0 still do).
        /// </summary>
        [DataMember(Name = "pauseAnimationsOnBattery")]
        public bool? PauseAnimationsOnBattery { get; set; }

        /// <summary>
        /// 何时渲染仪表盘动画：<c>always</c>（默认，失焦也渲染）、<c>unfocused</c>（失焦停）、
        /// <c>fullscreen</c>（前台全屏时停）。它只决定“渲染时机”，不改风扇的 hover/always 动效模式，
        /// 也不绕过 RDP / WPF Tier 0 / 电池这些环境上限。
        /// </summary>
        [DataMember(Name = "renderPolicy")]
        public string RenderPolicy { get; set; }

        /// <summary>
        /// 概览四格各自显示哪个指标（设置弹窗里的四个下拉）。不在目录里的值回退到占用，
        /// 因为“占用”永远是有的那个指标。
        /// </summary>
        [DataMember(Name = "deviceMetrics")]
        public DeviceMetricChoices DeviceMetrics { get; set; }

        [DataMember(Name = "startAgentWithWindows")]
        public bool StartAgentWithWindows { get; set; }

        /// <summary>
        /// 概览「风扇」卡显示哪一路。-1 = 自动（第一路有读数的通道）；否则是 SuperIO 的通道号。
        /// 通道号是唯一的稳定身份：LHM 只给位置名，标签由板型表决定（见 <see cref="FanLabels"/>）。
        /// </summary>
        [DataMember(Name = "fanChannel")]
        public int FanChannel { get; set; }

        public static Settings CreateDefault()
        {
            Settings settings = new Settings
            {
                Version = CurrentVersion,
                RefreshMs = DefaultRefreshMs,
                AutoAddPreset = false,
                FanChannel = -1,
                Band = new BandSettings
                {
                    Height = 40,
                    HorizontalSpacing = 3,
                    TrailingPad = 2,
                    OutlineColor = DefaultWhite,
                    ShowTooltip = true,
                    Reorderable = true
                },
                // Sampled from the reference screenshot (page #191919, card #303030,
                // green #1DB954, blue #519AFF, soft green #41D484, text #FFFFFF / #8F8F8F).
                Palette = new List<string>(PaletteColors),
                NetworkAdapter = "auto",
                CurveStyle = CurveSmooth,
                IndicatorStyle = IndicatorSegments,
                PauseAnimationsOnBattery = true,
                RenderPolicy = RenderPolicyAlways,
                DeviceMetrics = new DeviceMetricChoices(),
                StartAgentWithWindows = false
            };

            // Default band = the XMeters mould measured in S3.
            settings.Items = new List<BandElement>
            {
                new BandElement
                {
                    Id = "cpu-bars",
                    Type = BandElementType.Bar,
                    Enabled = true,
                    Metric = "cpu",
                    HistoryColumns = 16,
                    CellWidth = BarCellWidth,
                    CellGap = BarCellGap,
                    Height = BarHeight,
                    ColumnMode = ColumnModes.Cores,
                    CustomColumns = 16,
                    Colors = new ElementColors
                    {
                        Fill = DefaultGreen,
                        Outline = DefaultWhite,
                        Kernel = DefaultSystemRed
                    },
                    RefreshMs = DefaultRefreshMs
                },
                new BandElement
                {
                    Id = "net-text",
                    Type = BandElementType.Text,
                    Enabled = true,
                    Width = MinTextWidth,
                    Lines = new List<TextLine>
                    {
                        new TextLine { Metric = "netUp", Color = DefaultBlue, Indicator = Indicator.Up },
                        new TextLine { Metric = "netDown", Color = DefaultGreenSoft, Indicator = Indicator.Down }
                    },
                    RefreshMs = DefaultRefreshMs
                },
                new BandElement
                {
                    Id = "mem-pie",
                    Type = BandElementType.Pie,
                    Enabled = true,
                    Metric = "memory",
                    Diameter = PieDiameter,
                    Height = PieDiameter,
                    Style = PieStyle.Solid,
                    Colors = new ElementColors
                    {
                        Fill = DefaultGreen,
                        Outline = DefaultWhite
                    },
                    RefreshMs = DefaultRefreshMs
                }
            };

            return settings;
        }

        /// <summary>Deep copy, used by the settings UI so edits can be cancelled.</summary>
        public Settings Clone()
        {
            Settings copy = (Settings)MemberwiseClone();
            if (Band != null)
            {
                copy.Band = new BandSettings
                {
                    Height = Band.Height,
                    HorizontalSpacing = Band.HorizontalSpacing,
                    TrailingPad = Band.TrailingPad,
                    OutlineColor = Band.OutlineColor,
                    ShowTooltip = Band.ShowTooltip,
                    Reorderable = Band.Reorderable
                };
            }
            copy.Palette = Palette == null ? null : new List<string>(Palette);
            // 色板是可变容器：共用一个实例时，克隆体上的增删会改到原件，"保存草稿"与
            // "确定时加入预设"两条路径互相覆盖。
            copy.ColorPresets = ColorPresets == null ? null : ColorPresets.Clone();
            if (Items != null)
            {
                copy.Items = new List<BandElement>(Items.Count);
                foreach (BandElement item in Items) copy.Items.Add(item.Clone());
            }
            return copy;
        }

        /// <summary>
        /// 元素页只改元素列表与条间距，其余设置（色板、渲染策略、开机自启…）可能在页面打开后
        /// 被别的弹窗各自保存过。保存草稿时把它们从当前设置拿回来；否则一次"保存元素"就会
        /// 把别人刚存的值改回打开页面那一刻的旧值。
        /// </summary>
        public static Settings MergeBandEdits(Settings current, Settings draft)
        {
            if (draft == null) return current == null ? null : current.Clone();
            if (current == null) return draft.Clone();

            Settings merged = current.Clone();
            if (merged.Band == null) merged.Band = draft.Band;
            else if (draft.Band != null) merged.Band.HorizontalSpacing = draft.Band.HorizontalSpacing;

            merged.Items = new List<BandElement>(draft.Items == null ? 0 : draft.Items.Count);
            if (draft.Items != null)
            {
                foreach (BandElement item in draft.Items) merged.Items.Add(item.Clone());
            }
            return merged;
        }

        /// <summary>
        /// Fills in anything a hand-edited or half-written settings.json left out, and drops
        /// elements that reference unknown types/metrics. Never throws: the band must survive
        /// a broken config.
        /// </summary>
        /// <summary>Unknown / empty values fall back to the smooth curve, never to a blank chart.</summary>
        public static bool IsKnownCurveStyle(string value)
        {
            return string.Equals(value, CurveSmooth, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, CurvePolyline, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Unknown / empty values fall back to "always render".</summary>
        public static bool IsKnownRenderPolicy(string value)
        {
            return string.Equals(value, RenderPolicyAlways, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, RenderPolicyUnfocused, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, RenderPolicyFullscreen, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Unknown / empty values fall back to the segmented indicator.</summary>
        public static bool IsKnownIndicatorStyle(string value)
        {
            return string.Equals(value, IndicatorCapsule, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, IndicatorSegments, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 单个角色的预设列表：非法值换成默认色（与 palette 同口径）、超过上限按末尾淘汰丢最早的。
        /// null 保持 null（= 还没自定义过，UI 用内置默认色板）。
        /// </summary>
        private void NormalizePresets(List<string> presets)
        {
            if (presets == null) return;

            Settings defaults = CreateDefault();
            for (int i = presets.Count - 1; i >= 0; i--)
            {
                if (!IsHexColor(presets[i]))
                {
                    presets[i] = defaults.Palette[i % defaults.Palette.Count];
                }
            }
            while (presets.Count > PaletteRules.MaxPresets) presets.RemoveAt(0);
        }

        public List<string> Normalize()
        {
            List<string> warnings = new List<string>();
            Settings defaults = CreateDefault();

            // 读到这里时 Version 还是文件里的值（Normalize 末尾才抬到当前版本）。
            bool legacyTextWidth = Version > 0 && Version < CurrentVersion;
            if (Version <= 0) Version = CurrentVersion;
            else if (Version < CurrentVersion) Version = CurrentVersion;
            if (RefreshMs < MinRefreshMs || RefreshMs > MaxRefreshMs)
            {
                RefreshMs = DefaultRefreshMs;
                warnings.Add("refreshMs 越界，已恢复默认 1000");
            }

            if (Band == null) { Band = defaults.Band; warnings.Add("缺少 band 段，已用默认值"); }
            if (Band.Height < 24 || Band.Height > 64) { Band.Height = defaults.Band.Height; warnings.Add("band.height 越界，已恢复 40"); }
            if (Band.HorizontalSpacing < 0 || Band.HorizontalSpacing > 40) { Band.HorizontalSpacing = 3; warnings.Add("band.horizontalSpacing 越界，已恢复 3"); }
            if (Band.TrailingPad < 0 || Band.TrailingPad > 40) { Band.TrailingPad = 2; }
            if (!IsHexColor(Band.OutlineColor)) { Band.OutlineColor = DefaultWhite; }

            if (Palette == null || Palette.Count == 0) { Palette = defaults.Palette; warnings.Add("调色板为空，已用默认色板"); }
            else
            {
                for (int i = 0; i < Palette.Count; i++)
                {
                    if (!IsHexColor(Palette[i])) Palette[i] = defaults.Palette[i % defaults.Palette.Count];
                }
            }

            if (ColorPresets == null) ColorPresets = new ColorPresets();
            NormalizePresets(ColorPresets.Fill);
            NormalizePresets(ColorPresets.Kernel);
            NormalizePresets(ColorPresets.Outline);
            NormalizePresets(ColorPresets.Line);

            if (string.IsNullOrEmpty(NetworkAdapter)) NetworkAdapter = "auto";

            if (!IsKnownCurveStyle(CurveStyle))
            {
                if (!string.IsNullOrEmpty(CurveStyle)) warnings.Add("curveStyle 非法，已恢复默认圆滑曲线");
                CurveStyle = CurveSmooth;
            }

            if (!PauseAnimationsOnBattery.HasValue) PauseAnimationsOnBattery = true;

            if (!IsKnownRenderPolicy(RenderPolicy))
            {
                if (!string.IsNullOrEmpty(RenderPolicy)) warnings.Add("renderPolicy 非法，已恢复为全局重绘");
                RenderPolicy = RenderPolicyAlways;
            }

            if (!IsKnownIndicatorStyle(IndicatorStyle))
            {
                if (!string.IsNullOrEmpty(IndicatorStyle)) warnings.Add("indicatorStyle 非法，已恢复默认分段指示器");
                IndicatorStyle = IndicatorSegments;
            }

            if (DeviceMetrics == null) DeviceMetrics = new DeviceMetricChoices();
            DeviceMetrics.Normalize();

            if (FanChannel == 0 || FanChannel < -1 || FanChannel > 32)
            {
                if (FanChannel != 0) warnings.Add("fanChannel 非法，已恢复自动");
                FanChannel = -1;
            }

            if (Items == null || Items.Count == 0)
            {
                Items = defaults.Items;
                warnings.Add("items 为空，已恢复默认三元素");
                return warnings;
            }

            List<BandElement> kept = new List<BandElement>(Items.Count);
            int index = 0;
            foreach (BandElement item in Items)
            {
                index++;
                if (item == null) { warnings.Add("items[" + index + "] 为空，已丢弃"); continue; }

                string canonical = BandElementType.Canonical(item.Type);
                if (canonical == null) { warnings.Add("items[" + index + "] 类型未知（" + item.Type + "），已丢弃"); continue; }
                item.Type = canonical;

                if (string.IsNullOrEmpty(item.Id)) item.Id = canonical + "-" + index;
                if (item.RefreshMs < MinElementRefreshMs || item.RefreshMs > MaxElementRefreshMs)
                {
                    item.RefreshMs = RefreshMs;
                }

                if (item.Colors == null) item.Colors = new ElementColors();
                if (!IsHexColor(item.Colors.Fill)) item.Colors.Fill = DefaultGreen;
                if (!IsHexColor(item.Colors.Outline)) item.Colors.Outline = Band.OutlineColor;
                if (!IsHexColor(item.Colors.Kernel)) item.Colors.Kernel = DefaultSystemRed;

                if (item.IsType(BandElementType.Bar))
                {
                    if (!MetricCatalog.IsKnownKey(item.Metric)) { warnings.Add("items[" + index + "] 指标未知（" + item.Metric + "），已丢弃"); continue; }
                    // HistoryColumns is still the value the native band reads; the UI recomputes it
                    // from the CPU counts whenever the capsule switch is used.
                    if (item.HistoryColumns < 1 || item.HistoryColumns > 64) item.HistoryColumns = 16;
                    if (item.CustomColumns < 1 || item.CustomColumns > 64) item.CustomColumns = item.HistoryColumns;
                    if (!ColumnModes.IsKnown(item.ColumnMode))
                    {
                        // 旧文件只有 useLogicalCores 开关，迁移过来
                        item.ColumnMode = item.UseLogicalCores ? ColumnModes.Threads : ColumnModes.Cores;
                    }
                    else
                    {
                        item.ColumnMode = ColumnModes.Canonical(item.ColumnMode);
                    }
                    // 固定几何：不接受文件里的自定义值，UI 也不再暴露这三个字段。
                    if (item.CellWidth != BarCellWidth) item.CellWidth = BarCellWidth;
                    if (item.CellGap != BarCellGap) item.CellGap = BarCellGap;
                    if (item.Height != BarHeight) item.Height = BarHeight;
                }
                else if (item.IsType(BandElementType.Pie))
                {
                    if (!MetricCatalog.IsKnownKey(item.Metric)) { warnings.Add("items[" + index + "] 指标未知（" + item.Metric + "），已丢弃"); continue; }
                    if (item.Diameter != PieDiameter) item.Diameter = PieDiameter;
                    item.Style = PieStyle.Canonical(item.Style);
                }
                else if (item.IsType(BandElementType.Text))
                {
                    if (item.Lines == null || item.Lines.Count == 0) { warnings.Add("items[" + index + "] 文本元素没有行，已丢弃"); continue; }
                    List<TextLine> lines = new List<TextLine>(item.Lines.Count);
                    int lineIndex = 0;
                    foreach (TextLine line in item.Lines)
                    {
                        lineIndex++;
                        if (line == null) continue;
                        if (!MetricCatalog.IsKnownKey(line.Metric)) { warnings.Add("items[" + index + "].lines[" + lineIndex + "] 指标未知，已丢弃"); continue; }
                        if (!IsHexColor(line.Color)) line.Color = DefaultWhite;
                        if (line.Indicator == null) line.Indicator = Indicator.None;
                        line.Indicator = line.Indicator.ToLowerInvariant();
                        if (line.Indicator != Indicator.Up && line.Indicator != Indicator.Down) line.Indicator = Indicator.None;
                        lines.Add(line);
                    }
                    if (lines.Count == 0) { warnings.Add("items[" + index + "] 文本元素没有有效行，已丢弃"); continue; }
                    if (lines.Count > 2) { lines.RemoveRange(2, lines.Count - 2); warnings.Add("items[" + index + "] 文本行超过 2 行，已截断"); }
                    item.Lines = lines;
                    if (item.Width < MinTextWidth || item.Width > MaxTextWidth) item.Width = MinTextWidth;
                    // v3：width 从「固定字段宽」改成「最小宽度」。旧值 52 是当时的固定默认值，不是
                    // 手写的；留着它会继续把「元素间距」垫宽，所以迁移成最小值。
                    if (legacyTextWidth) item.Width = MinTextWidth;

                    bool diskElement = false;
                    foreach (TextLine line in lines)
                    {
                        if (IsDiskIoKey(line.Metric)) { diskElement = true; break; }
                    }
                    item.Drive = diskElement ? CanonicalDrive(item.Drive) : string.Empty;
                }

                kept.Add(item);
            }

            if (kept.Count == 0)
            {
                Items = defaults.Items;
                warnings.Add("没有可用的 item，已恢复默认三元素");
                return warnings;
            }

            Items = kept;

            bool anyEnabled = false;
            foreach (BandElement item in Items) { if (item.Enabled) { anyEnabled = true; break; } }
            if (!anyEnabled)
            {
                Items[0].Enabled = true;
                warnings.Add("所有元素都被停用，已启用第一个");
            }

            return warnings;
        }

        public static bool IsHexColor(string value)
        {            if (string.IsNullOrEmpty(value)) return false;
            if (value.Length != 9 || value[0] != '#') return false;
            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>磁盘指标键（全部 = diskIo，逐盘 = diskIoRead / diskIoWrite）。</summary>
        public static bool IsDiskIoKey(string key)
        {
            return string.Equals(key, "diskIo", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "diskIoRead", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "diskIoWrite", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 盘符统一成 "C:"；空/非法返回空串（= 全部磁盘）。只做格式校验，不判断盘是否存在：
        /// 盘可能拔掉、映射盘可能断开，元素不应该因为一次加载就被改掉。
        /// </summary>
        public static string CanonicalDrive(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string trimmed = value.Trim().ToUpperInvariant();
            if (trimmed.Length == 1 && trimmed[0] >= 'A' && trimmed[0] <= 'Z') return trimmed + ":";
            if (trimmed.Length == 2 && trimmed[0] >= 'A' && trimmed[0] <= 'Z' && trimmed[1] == ':') return trimmed;
            return string.Empty;
        }

        // ---- persistence -------------------------------------------------------------

        public string ToJson()
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Settings));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, this);
                string compact = Encoding.UTF8.GetString(stream.ToArray());
                return JsonPretty.Print(compact);
            }
        }

        /// <summary>
        /// Reads settings only when the file exists and parses. Returns false for a missing,
        /// half-written or corrupt file (with a warning for the latter) so a live reloader can
        /// keep the last known-good values instead of resetting to defaults.
        /// </summary>
        public static bool TryLoad(string path, out Settings settings, out List<string> warnings)
        {
            settings = null;
            warnings = new List<string>();

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            Settings loaded;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                string json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(Settings));
                    loaded = (Settings)serializer.ReadObject(stream);
                }
            }
            catch (Exception ex)
            {
                warnings.Add("settings.json 读取失败（" + ex.GetType().Name + "），已保留上一次的有效值");
                return false;
            }

            if (loaded == null)
            {
                warnings.Add("settings.json 内容为空，已保留上一次的有效值");
                return false;
            }

            warnings.AddRange(loaded.Normalize());
            settings = loaded;
            return true;
        }

        /// <summary>
        /// Loads settings. A missing file yields defaults ("first run"); a corrupt file also
        /// yields defaults but reports it, so the caller can log once (never a dialog).
        /// </summary>
        public static Settings Load(string path, out List<string> warnings)
        {
            Settings loaded;
            if (TryLoad(path, out loaded, out warnings)) return loaded;

            Settings fresh = CreateDefault();
            fresh.Normalize();
            return fresh;
        }

        public void Save(string path)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = ToJson();
            string temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static string DefaultPath
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(Path.Combine(appData, "QuadVitals"), "settings.json");
            }
        }

        public int EnabledItemCount
        {
            get
            {
                int count = 0;
                if (Items != null) { foreach (BandElement item in Items) { if (item.Enabled) count++; } }
                return count;
            }
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "v{0} items={1} enabled={2} refresh={3}ms",
                Version, Items == null ? 0 : Items.Count, EnabledItemCount, RefreshMs);
        }
    }
}
