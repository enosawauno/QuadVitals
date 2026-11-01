using System;
using System.Globalization;

namespace QuadVitals.Core
{
    /// <summary>
    /// 把手工输入的颜色文本解析成 settings.json 里的规范形式 `#AARRGGBB`。
    ///
    /// 为什么不只认一种写法：颜色在不同地方有不同习惯 —— CSS 里写 `rgb(0,204,106)`，
    /// 设计稿里写 `#00CC6A`，从别处拷来的常常是不带 `#` 的 `00cc6a`。设置页与取色器都是
    /// 手输入口，只认 `#AARRGGBB` 会让“看起来完全正常的颜色”被当成非法值静默回退。
    ///
    /// 接受的形式（大小写、前后空格、`#` 有无都不敏感）：
    ///   #AARRGGBB / AARRGGBB
    ///   #RRGGBB   / RRGGBB          → alpha = FF
    ///   rgb(r,g,b) / rgba(r,g,b,a)  → a 为 0-1 小数或 0-255 整数，缺省 FF
    ///
    /// 一律输出大写 `#AARRGGBB`；解析失败返回 false，由调用方决定回退到哪个旧值。
    /// </summary>
    public static class ColorText
    {
        public static bool TryParse(string text, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(text)) return false;

            string value = text.Trim();
            if (value.Length == 0) return false;

            if (value.StartsWith("rgba", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseFunction(value, out canonical);
            }

            if (value[0] == '#') value = value.Substring(1);
            if (value.Length != 6 && value.Length != 8) return false;
            if (!IsHexDigits(value)) return false;

            string alpha = value.Length == 8 ? value.Substring(0, 2) : "FF";
            string rgb = value.Length == 8 ? value.Substring(2, 6) : value;
            canonical = ("#" + alpha + rgb).ToUpperInvariant();
            return true;
        }

        /// <summary>规范形式（`#AARRGGBB`）与否；settings.json 只认这个。</summary>
        public static bool IsCanonical(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 9 || text[0] != '#') return false;
            return IsHexDigits(text.Substring(1));
        }

        private static bool TryParseFunction(string value, out string canonical)
        {
            canonical = null;

            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close < open) return false;

            string name = value.Substring(0, open).Trim();
            bool withAlpha = string.Equals(name, "rgba", StringComparison.OrdinalIgnoreCase);
            if (!withAlpha && !string.Equals(name, "rgb", StringComparison.OrdinalIgnoreCase)) return false;

            string body = value.Substring(open + 1, close - open - 1);
            string[] parts = body.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != (withAlpha ? 4 : 3)) return false;

            int red, green, blue;
            if (!TryChannel(parts[0], out red)) return false;
            if (!TryChannel(parts[1], out green)) return false;
            if (!TryChannel(parts[2], out blue)) return false;

            int alpha = 255;
            if (withAlpha && !TryAlpha(parts[3], out alpha)) return false;

            canonical = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}",
                alpha, red, green, blue);
            return true;
        }

        private static bool TryChannel(string part, out int channel)
        {
            channel = 0;
            int parsed;
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return false;
            if (parsed < 0 || parsed > 255) return false;
            channel = parsed;
            return true;
        }

        /// <summary>
        /// alpha 两种习惯都收：CSS 的 0–1 小数，以及 `#AARRGGBB` 那套 0–255 整数。
        /// （0 与 1 在这里没有被解释成 0–255 的整数，否则 `rgba(...,1)` 会变成几乎全透明。）
        /// </summary>
        private static bool TryAlpha(string part, out int alpha)
        {
            alpha = 255;
            double parsed;
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return false;
            if (parsed < 0) return false;

            if (parsed <= 1.0)
            {
                alpha = (int)Math.Round(parsed * 255.0);
                return true;
            }
            if (parsed > 255) return false;
            alpha = (int)Math.Round(parsed);
            return true;
        }

        private static bool IsHexDigits(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return value.Length > 0;
        }
    }
}
