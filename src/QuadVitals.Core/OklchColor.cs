using System;

namespace QuadVitals.Core
{
    /// <summary>
    /// OKLCh 色彩（感知均匀的极坐标 Lab），用来复刻进度条渐变。
    ///
    /// 为什么不用 sRGB 直接插值：色阶是 `color-mix(in oklch, …)`，L/C/H 三个分量各自插值；
    /// 在 sRGB 里线性插色会把浅端混成发灰的粉绿（R/B 差被压小），这也是第一版看着“没那条色阶”的原因。
    ///
    /// 数值取 Björn Ottosson 的 OKLab 论文常量，sRGB 传输函数按 IEC 61966-2-1。
    /// </summary>
    public struct OklchColor
    {
        public double L;
        public double C;
        public double H;   // 弧度

        public OklchColor(double lightness, double chroma, double hueRadians)
        {
            L = lightness;
            C = chroma;
            H = hueRadians;
        }

        /// <summary>同一色相、指定亮度与彩度上限（`oklch(from c L min(c, cap) H)`）。</summary>
        public OklchColor With(double lightness, double chromaLimit)
        {
            double chroma = C;
            if (chroma > chromaLimit) chroma = chromaLimit;
            return new OklchColor(lightness, chroma, H);
        }

        /// <summary>同色相、彩度乘一个系数（`calc(c * 0.8)`）。</summary>
        public OklchColor ScaleChroma(double factor)
        {
            return new OklchColor(L, C * factor, H);
        }

        /// <summary>在 OKLCh 空间里线性插值（等价于 `color-mix(in oklch, a, b, t)`）。</summary>
        public static OklchColor Lerp(OklchColor from, OklchColor to, double t)
        {
            if (t <= 0) return from;
            if (t >= 1) return to;
            // 色相走最短弧：两端色相相同（我们只变 L/C）时结果与直接插值一致。
            double delta = to.H - from.H;
            while (delta > Math.PI) delta -= 2 * Math.PI;
            while (delta < -Math.PI) delta += 2 * Math.PI;
            return new OklchColor(
                from.L + (to.L - from.L) * t,
                from.C + (to.C - from.C) * t,
                from.H + delta * t);
        }

        public static OklchColor FromRgb(byte r, byte g, byte b)
        {
            double linearR = ToLinear(r);
            double linearG = ToLinear(g);
            double linearB = ToLinear(b);

            double l = 0.4122214708 * linearR + 0.5363325363 * linearG + 0.0514459929 * linearB;
            double m = 0.2119034982 * linearR + 0.6806995451 * linearG + 0.1073969566 * linearB;
            double s = 0.0883024619 * linearR + 0.2817188376 * linearG + 0.6299787005 * linearB;

            double l_ = Math.Pow(l, 1.0 / 3.0);
            double m_ = Math.Pow(m, 1.0 / 3.0);
            double s_ = Math.Pow(s, 1.0 / 3.0);

            double lightness = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_;
            double a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_;
            double bb = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_;

            double chroma = Math.Sqrt(a * a + bb * bb);
            double hue = Math.Atan2(bb, a);
            return new OklchColor(lightness, chroma, hue);
        }

        /// <summary>转回 sRGB；超出色域的分量按 0/255 夹紧（WPF 也只有 8 位 sRGB）。</summary>
        public byte[] ToRgb()
        {
            double a = C * Math.Cos(H);
            double b = C * Math.Sin(H);

            double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
            double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
            double s_ = L - 0.0894841775 * a - 1.2914855480 * b;

            double l = l_ * l_ * l_;
            double m = m_ * m_ * m_;
            double s = s_ * s_ * s_;

            double r = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            double blue = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

            return new[] { ToSrgb(r), ToSrgb(g), ToSrgb(blue) };
        }

        private static double ToLinear(byte channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        /// <summary>线性光 → sRGB 8 位，越界夹紧。</summary>
        private static byte ToSrgb(double linear)
        {
            if (double.IsNaN(linear) || linear <= 0) return 0;
            if (linear >= 1) return 255;
            double value = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
            if (value <= 0) return 0;
            if (value >= 1) return 255;
            return (byte)Math.Round(value * 255.0);
        }
    }
}
