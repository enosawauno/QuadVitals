using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace QuadVitals.Core
{
    /// <summary>配色行的角色。预设色板按角色分开存，互不影响。</summary>
    public static class ColorRoles
    {
        public const string Fill = "fill";        // 用户态 / 环的已用色
        public const string Kernel = "kernel";    // 系统内核
        public const string Outline = "outline";  // 外边框 / 环的描边色
        public const string Line = "line";        // 文本元素每一行的颜色

        public static bool IsKnown(string role)
        {
            return string.Equals(role, Fill, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, Kernel, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, Outline, StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, Line, StringComparison.OrdinalIgnoreCase);
        }

        public static string Canonical(string role)
        {
            if (string.Equals(role, Kernel, StringComparison.OrdinalIgnoreCase)) return Kernel;
            if (string.Equals(role, Outline, StringComparison.OrdinalIgnoreCase)) return Outline;
            if (string.Equals(role, Line, StringComparison.OrdinalIgnoreCase)) return Line;
            return Fill;
        }
    }

    /// <summary>
    /// 按角色分开的预设色板：
    /// 三个配色行共用一份 palette 时，在用户态里加一个颜色会同时出现在系统内核和外边框里。
    /// 每个角色一份；为空时由 UI 退回内置默认色板（空的列表不代表“清空所有颜色”）。
    /// </summary>
    [DataContract]
    public sealed class ColorPresets
    {
        [DataMember(Name = "fill")]
        public List<string> Fill { get; set; }

        [DataMember(Name = "kernel")]
        public List<string> Kernel { get; set; }

        [DataMember(Name = "outline")]
        public List<string> Outline { get; set; }

        [DataMember(Name = "line")]
        public List<string> Line { get; set; }

        /// <summary>该角色当前的列表；可能为 null（表示“还没自定义过”）。</summary>
        public List<string> Get(string role)
        {
            switch (ColorRoles.Canonical(role))
            {
                case ColorRoles.Kernel: return Kernel;
                case ColorRoles.Outline: return Outline;
                case ColorRoles.Line: return Line;
                default: return Fill;
            }
        }

        public void Set(string role, List<string> list)
        {
            switch (ColorRoles.Canonical(role))
            {
                case ColorRoles.Kernel: Kernel = list; break;
                case ColorRoles.Outline: Outline = list; break;
                case ColorRoles.Line: Line = list; break;
                default: Fill = list; break;
            }
        }

        public ColorPresets Clone()
        {
            return new ColorPresets
            {
                Fill = Fill == null ? null : new List<string>(Fill),
                Kernel = Kernel == null ? null : new List<string>(Kernel),
                Outline = Outline == null ? null : new List<string>(Outline),
                Line = Line == null ? null : new List<string>(Line)
            };
        }
    }
}
