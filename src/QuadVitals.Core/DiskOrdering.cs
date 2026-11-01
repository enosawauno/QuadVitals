using System;
using System.Collections.Generic;

namespace QuadVitals.Core
{
    /// <summary>
    /// 硬盘段落的排序规则：「硬盘 1」= C 盘所在的那块盘，往后按盘符字母递增。
    ///
    /// 一块盘上可以挂多个分区（C: 与 F: 同盘），也可能出现分区字母不连续的情况；
    /// 这块盘的**位置由它最小的盘符决定**（也就是第一个分区），标题里再把所有字母列出来。
    /// 纯逻辑，不读设备：盘符到物理盘的对应在 Settings/HardwareReport 里由 WMI 关联表推出。
    /// </summary>
    public static class DiskOrdering
    {
        /// <summary>一块盘以及它上面的盘符（已按字母排序）。</summary>
        public sealed class Group
        {
            public Group(int diskIndex)
            {
                DiskIndex = diskIndex;
                Letters = new List<string>();
            }

            public int DiskIndex { get; private set; }
            public IList<string> Letters { get; private set; }

            /// <summary>决定这块盘排在第几位：它最小的那个盘符。</summary>
            public string FirstLetter
            {
                get { return Letters.Count == 0 ? string.Empty : Letters[0]; }
            }
        }

        /// <summary>
        /// 入参是「盘符, 物理盘号」的对应表（顺序无所谓、可能乱序），返回按盘符排好序的分组：
        /// 组序 = 各组首个盘符的字母序，组内字母升序。同一块盘只出现一次。
        /// </summary>
        public static IList<Group> Order(IEnumerable<KeyValuePair<string, int>> lettersByDisk)
        {
            List<Group> groups = new List<Group>();
            if (lettersByDisk == null) return groups;

            Dictionary<int, Group> byIndex = new Dictionary<int, Group>();
            foreach (KeyValuePair<string, int> pair in lettersByDisk)
            {
                string letter = Normalize(pair.Key);
                if (letter.Length == 0) continue;

                Group group;
                if (!byIndex.TryGetValue(pair.Value, out group))
                {
                    group = new Group(pair.Value);
                    byIndex.Add(pair.Value, group);
                    groups.Add(group);
                }
                if (!group.Letters.Contains(letter)) group.Letters.Add(letter);
            }

            foreach (Group group in groups)
            {
                List<string> sorted = new List<string>(group.Letters);
                sorted.Sort(StringComparer.Ordinal);
                group.Letters.Clear();
                foreach (string letter in sorted) group.Letters.Add(letter);
            }

            groups.Sort((left, right) => string.CompareOrdinal(left.FirstLetter, right.FirstLetter));
            return groups;
        }

        /// <summary>“c:” / “C” / “C:” 都收成 “C:”；空值返回空串。</summary>
        public static string Normalize(string letter)
        {
            if (string.IsNullOrEmpty(letter)) return string.Empty;
            string text = letter.Trim().ToUpperInvariant();
            if (text.Length == 0) return string.Empty;
            if (text.Length == 1) return text + ":";
            return text.Substring(0, 2);
        }
    }
}
