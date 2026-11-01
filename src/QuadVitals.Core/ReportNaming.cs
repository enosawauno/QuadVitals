using System;
using System.Globalization;
using System.IO;

namespace QuadVitals.Core
{
    /// <summary>
    /// 硬件检测报告的文件名规则：`yyyyMMdd-硬件检测报告_QuadVitals.md`。
    ///
    /// 一天里连续导出多份时编号递增：第一份不带序号，第二份 `-2`，第三份 `-3`……；
    /// 序号取"第一个空位"，所以删掉中间某份之后再导出会把它补上，而不是一路涨到很大的数字。
    /// 只有文件名与存在性判断，不写文件：落盘在调用方（设置窗口的保存对话框）。
    /// </summary>
    public static class ReportNaming
    {
        /// <summary>不含扩展名的默认名，例如 `20261102-硬件检测报告_QuadVitals`。</summary>
        public static string BaseName(DateTime localTime)
        {
            return localTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                   "-硬件检测报告_QuadVitals";
        }

        /// <summary>
        /// 返回该目录下第一个可用的完整文件名（含扩展名）。扩展名允许带或不带点，
        /// 统一转小写比较；目录不存在时按"没有同名文件"处理，由调用方在保存时暴露错误。
        /// </summary>
        public static string NextFileName(string folder, DateTime localTime, string extension)
        {
            string ext = NormalizeExtension(extension);
            string baseName = BaseName(localTime);
            if (string.IsNullOrEmpty(folder)) return baseName + ext;

            string first = Path.Combine(folder, baseName + ext);
            if (!File.Exists(first)) return first;

            for (int index = 2; index < 1000; index++)
            {
                string candidate = Path.Combine(folder,
                    baseName + "-" + index.ToString(CultureInfo.InvariantCulture) + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return first;
        }

        /// <summary>`"md"` / `".MD"` → `".md"`；空值返回空串。</summary>
        public static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension)) return string.Empty;
            string text = extension.Trim().ToLowerInvariant();
            if (text.Length == 0) return string.Empty;
            return text[0] == '.' ? text : "." + text;
        }
    }
}
