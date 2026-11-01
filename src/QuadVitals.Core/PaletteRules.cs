using System;
using System.Collections.Generic;

namespace QuadVitals.Core
{
    /// <summary>
    /// 预设色板（settings.json 的 `palette`）的增删规则：去重 + 末尾淘汰。
    ///
    /// 为什么放在 Core：色板是持久化数据，规则必须能被断言覆盖 —— 「加满 16 个后加第 17 个会挤掉
    /// 最早那个」这种语义如果埋在 UI 的点击回调里，只能靠手点验证。
    /// </summary>
    public static class PaletteRules
    {
        /// <summary>预设上限。再多一行色块就会挤到色值输入框上。</summary>
        public const int MaxPresets = 16;

        /// <summary>
        /// 加入一个颜色（必须是 `#AARRGGBB` 规范形式）。已在列表里则不动；满员时挤掉**最早的
        /// 非受保护色**（内置默认预设不参与淘汰，否则自定义色会被默认色挤没）。
        /// 返回是否真的改了色板。
        /// </summary>
        public static bool Add(List<string> palette, string colour, IReadOnlyList<string> protectedColours = null)
        {
            if (palette == null || string.IsNullOrEmpty(colour)) return false;

            for (int i = 0; i < palette.Count; i++)
            {
                if (string.Equals(palette[i], colour, StringComparison.OrdinalIgnoreCase)) return false;
            }

            while (palette.Count >= MaxPresets)
            {
                int victim = -1;
                for (int i = 0; i < palette.Count; i++)
                {
                    if (!IsProtected(palette[i], protectedColours)) { victim = i; break; }
                }
                // 整张色板都是受保护色：宁可不加，也不动默认色。
                if (victim < 0) return false;
                palette.RemoveAt(victim);
            }

            palette.Add(colour);
            return true;
        }

        /// <summary>
        /// 删除一个颜色；至少保留 1 个（删空后 Normalize 会回退默认色板，看着像“删了又回来”）。
        /// <paramref name="protectedColours"/> 里的颜色（内置默认预设）不允许删除 ——
        /// 删除默认预设后没有任何恢复入口，所以不允许删除。
        /// </summary>
        public static bool Remove(List<string> palette, string colour, IReadOnlyList<string> protectedColours = null)
        {
            if (palette == null || palette.Count <= 1) return false;
            if (IsProtected(colour, protectedColours)) return false;

            for (int i = 0; i < palette.Count; i++)
            {
                if (string.Equals(palette[i], colour, StringComparison.OrdinalIgnoreCase))
                {
                    palette.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>这个颜色是不是内置默认预设（不可删）。</summary>
        public static bool IsProtected(string colour, IReadOnlyList<string> protectedColours)
        {
            if (protectedColours == null || string.IsNullOrEmpty(colour)) return false;
            for (int i = 0; i < protectedColours.Count; i++)
            {
                if (string.Equals(protectedColours[i], colour, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
