using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace QuadVitals.UI
{
    /// <summary>
    /// Design tokens as code (the build box has no WPF XAML compiler: no .NET SDK,
    /// no PresentationBuildTasks, so everything is constructed in C#).
    /// </summary>
    public static class Theme
    {
        // page #1A1A1A, rounded blocks #303030.
        // backgrounds
        public static readonly Color BgBase = Color.FromRgb(0x1A, 0x1A, 0x1A);      // page (user-specified)
        public static readonly Color BgElevated = Color.FromRgb(0x1A, 0x1A, 0x1A);  // sidebar (same page tone)
        public static readonly Color SurfaceCard = Color.FromRgb(0x30, 0x30, 0x30); // rounded blocks
        public static readonly Color SurfaceHover = Color.FromRgb(0x3A, 0x3A, 0x3A);
        public static readonly Color BorderSubtle = Color.FromRgb(0x44, 0x44, 0x44); // only used for hairlines
        public static readonly Color BorderGlow = Color.FromRgb(0x51, 0x9A, 0xFF);
        // 浮层（下拉弹层）：比卡片亮一档 + 更强一点的描边，配合投影读出“悬在面板之上”。
        public static readonly Color SurfaceRaised = Color.FromRgb(0x3E, 0x3E, 0x3E);
        public static readonly Color BorderStrong = Color.FromRgb(0x4A, 0x4A, 0x4A);

        // text
        public static readonly Color TextPrimary = Color.FromRgb(0xFF, 0xFF, 0xFF);
        // 深色卡片上的可读性：说明文字不再用 2.6:1 的 #6E6E6E。三级灰提到 #9A9A9A（约 4.6:1），
        // 二级灰随之提亮一档；#6E6E6E 只留给占位/禁用这类可以忽略的信息。
        public static readonly Color TextSecondary = Color.FromRgb(0xB0, 0xB0, 0xB0);
        public static readonly Color TextTertiary = Color.FromRgb(0x9A, 0x9A, 0x9A);
        public static readonly Color TextFaint = Color.FromRgb(0x6E, 0x6E, 0x6E);

        // accents (bars / indicators)
        public static readonly Color AccentGreen = Color.FromRgb(0x1D, 0xB9, 0x54);   // gauge fill
        // 复选框实心填充用「哑光绿」：品牌绿满饱和铺在 16 px 小方块上会发光、发塑料。
        public static readonly Color AccentGreenFill = Color.FromRgb(0x1E, 0x9E, 0x53);
        public static readonly Color AccentGreenEdge = Color.FromRgb(0x17, 0x80, 0x3F);
        public static readonly Color AccentGreenSoft = Color.FromRgb(0x41, 0xD4, 0x84); // down triangle
        public static readonly Color AccentBlue = Color.FromRgb(0x51, 0x9A, 0xFF);    // up triangle
        public static readonly Color AccentOrange = Color.FromRgb(0xF6, 0xA7, 0x48);  // warning only (#F6A748)
        public static readonly Color AccentRed = Color.FromRgb(0xD2, 0x47, 0x47);     // danger (#D24747)
        public static readonly Color AccentRedDeep = Color.FromRgb(0xA8, 0x2F, 0x2F); // top of the fan danger ramp
        public static readonly Color AccentCyan = Color.FromRgb(0x4C, 0xC9, 0xC9);    // pump running (no bands)
        public static readonly Color TrackGrey = Color.FromRgb(0x4E, 0x51, 0x59);     // gauge track
        public static readonly Color ButtonGrey = Color.FromRgb(0x47, 0x47, 0x47);    // reference button
        // 铺在品牌绿上的文字（主按钮、侧栏选中滑块）：白字在 #1DB954 上只有 ~2.4:1，用近黑。
        public static readonly Color OnAccent = Color.FromRgb(0x0B, 0x0B, 0x0B);

        public static readonly Brush BgBaseBrush = Freeze(new SolidColorBrush(BgBase));
        public static readonly Brush BgElevatedBrush = Freeze(new SolidColorBrush(BgElevated));
        public static readonly Brush SurfaceCardBrush = Freeze(new SolidColorBrush(SurfaceCard));
        public static readonly Brush SurfaceHoverBrush = Freeze(new SolidColorBrush(SurfaceHover));
        public static readonly Brush BorderSubtleBrush = Freeze(new SolidColorBrush(BorderSubtle));
        public static readonly Brush SurfaceRaisedBrush = Freeze(new SolidColorBrush(SurfaceRaised));
        public static readonly Brush BorderStrongBrush = Freeze(new SolidColorBrush(BorderStrong));
        public static readonly Brush TextPrimaryBrush = Freeze(new SolidColorBrush(TextPrimary));
        public static readonly Brush TextSecondaryBrush = Freeze(new SolidColorBrush(TextSecondary));
        public static readonly Brush TextTertiaryBrush = Freeze(new SolidColorBrush(TextTertiary));
        public static readonly Brush TextFaintBrush = Freeze(new SolidColorBrush(TextFaint));
        public static readonly Brush AccentGreenBrush = Freeze(new SolidColorBrush(AccentGreen));
        public static readonly Brush AccentGreenFillBrush = Freeze(new SolidColorBrush(AccentGreenFill));
        public static readonly Brush AccentGreenEdgeBrush = Freeze(new SolidColorBrush(AccentGreenEdge));
        public static readonly Brush AccentGreenSoftBrush = Freeze(new SolidColorBrush(AccentGreenSoft));
        public static readonly Brush AccentOrangeBrush = Freeze(new SolidColorBrush(AccentOrange));
        public static readonly Brush AccentBlueBrush = Freeze(new SolidColorBrush(AccentBlue));
        public static readonly Brush AccentRedBrush = Freeze(new SolidColorBrush(AccentRed));
        public static readonly Brush OnAccentBrush = Freeze(new SolidColorBrush(OnAccent));
        public static readonly Brush ButtonGreyBrush = Freeze(new SolidColorBrush(ButtonGrey));

        public const double WindowRadius = 30;   // 窗口圆角；卡片仍是 25，别混成一个数
        public const double CardRadius = 25;
        public const double ControlRadius = 10;
        public const double CardPadding = 20;
        public const double CardGap = 20;      // measured gap between blocks
        public const double PagePadding = 24;

        // Motion: “进入” 一律缓动 + 很轻的回弹（要“微微晃动”，不要线性弹出来）。
        // 时长/幅度集中在这里，下拉弹层与居中面板共用。
        public const int MotionFadeMs = 140;
        public const int MotionPopMs = 190;

        /// <summary>颜色缓动 + 弹性：开关滑动用回弹而不是线性跳到位。</summary>
        public static IEasingFunction ToggleSpring()
        {
            return new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        }

        /// <summary>纯淡入用的缓动（两端都收，不要线性）。</summary>
        public static IEasingFunction EnterFade()
        {
            return new SineEase { EasingMode = EasingMode.EaseOut };
        }

        /// <summary>
        /// 入场“微微晃动”：从调用方设定的起始值出发，先慢起、在 55% 处略微超过 1，
        /// 再落回 1（不是 BackEase 那样开头一下子冲到位）。
        /// </summary>
        public static DoubleAnimationUsingKeyFrames EnterPop(double overshoot)
        {
            DoubleAnimationUsingKeyFrames animation = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(MotionPopMs)
            };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(overshoot, KeyTime.FromPercent(0.55),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0),
                new SineEase { EasingMode = EasingMode.EaseOut }));
            return animation;
        }

        public static readonly FontFamily UiFont =
            new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

        public static readonly FontFamily MonoFont =
            new FontFamily("Consolas, Cascadia Mono, Courier New");

        public static FontWeight SemiBold { get { return FontWeights.SemiBold; } }

        /// <summary>Numeric field font that keeps digits from jittering ("tabular" feel).</summary>
        public static readonly FontFamily NumberFont = new FontFamily("Segoe UI");

        public static Color ToColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            try
            {
                string text = hex.Trim();
                if (text.StartsWith("#", StringComparison.Ordinal) && text.Length == 9)
                {
                    // #AARRGGBB, same notation the settings file uses
                    byte a = Convert.ToByte(text.Substring(1, 2), 16);
                    byte r = Convert.ToByte(text.Substring(3, 2), 16);
                    byte g = Convert.ToByte(text.Substring(5, 2), 16);
                    byte b = Convert.ToByte(text.Substring(7, 2), 16);
                    if (a == 0) a = 255;
                    return Color.FromArgb(a, r, g, b);
                }
                if (text.StartsWith("#", StringComparison.Ordinal) && text.Length == 7)
                {
                    byte r = Convert.ToByte(text.Substring(1, 2), 16);
                    byte g = Convert.ToByte(text.Substring(3, 2), 16);
                    byte b = Convert.ToByte(text.Substring(5, 2), 16);
                    return Color.FromRgb(r, g, b);
                }
            }
            catch (Exception)
            {
                // fall through
            }
            return fallback;
        }

        public static string ToHex(Color color)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", color.A, color.R, color.G, color.B);
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
