using System;
using System.Text;
using System.Text.RegularExpressions;

namespace QuadVitals.Settings
{
    /// <summary>
    /// Re-flows plain-text licence documents before they are shown: otherwise the left half of the
    /// panel stays empty because the source is hard-wrapped at ~70 columns.
    ///
    /// MPL 2.0 / Apache 2.0 ship as text hard-wrapped at ~70 columns. Shown in a 900 px panel
    /// those lines only fill the left half, and justification cannot help because every physical
    /// line counts as its own "last line". Joining wrapped lines back into paragraphs lets the
    /// viewer wrap and justify them properly.
    ///
    /// Only the whitespace *between* words is touched: words, punctuation and numbering are
    /// preserved. A line starts a new block when it is indented less than the current block,
    /// is a list marker ("(a)", "1.2.", "-", "•"), or is an all-caps heading ("ACCEPTANCE").
    /// Anything else is a wrapped continuation of the current paragraph.
    /// </summary>
    internal static class PlainTextReflow
    {
        private static readonly Regex ListMarker =
            new Regex(@"^(\d+(\.\d+)*\.?|\([0-9a-zA-Z]{1,3}\)|[-*\u2022\u00B7])(\s|$)",
                      RegexOptions.CultureInvariant);

        /// <summary>"-----", "=====", "*****" — a rule stands alone and never absorbs the next line.</summary>
        private static readonly Regex RuleLine =
            new Regex(@"^[-=_*~\s]{3,}$", RegexOptions.CultureInvariant);

        /// <summary>"Project: …", "Version: …" — a short label plus a value is one row, not prose.</summary>
        private static readonly Regex LabelLine =
            new Regex(@"^[A-Za-z][A-Za-z0-9 ]{0,24}:\s", RegexOptions.CultureInvariant);

        private const char NewLine = (char)10;

        public static string Unwrap(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            StringBuilder output = new StringBuilder(text.Length);

            int blockIndent = -1;
            bool paragraphOpen = false;

            foreach (string raw in lines)
            {
                if (raw.Trim().Length == 0)
                {
                    if (paragraphOpen) output.Append('\n');
                    output.Append('\n');
                    paragraphOpen = false;
                    blockIndent = -1;
                    continue;
                }

                int indent = LeadingSpaces(raw);
                string content = raw.Trim();

                if (RuleLine.IsMatch(content))
                {
                    // Rules close the current paragraph and never merge into the next line.
                    if (paragraphOpen) output.Append(NewLine);
                    output.Append(content);
                    output.Append(NewLine);
                    paragraphOpen = false;
                    blockIndent = -1;
                    continue;
                }

                bool startsBlock = !paragraphOpen
                    || LabelLine.IsMatch(content)
                    || indent < blockIndent
                    || ListMarker.IsMatch(content)
                    || IsUpperHeading(content);

                if (startsBlock)
                {
                    if (paragraphOpen) output.Append('\n');
                    output.Append(content);
                    blockIndent = indent;
                }
                else
                {
                    output.Append(Joiner(output, content));
                    output.Append(content);
                }
                paragraphOpen = true;
            }

            return output.ToString();
        }

        private static int LeadingSpaces(string line)
        {
            int count = 0;
            foreach (char c in line)
            {
                if (c == ' ') count++;
                else if (c == '\t') count += 4;
                else break;
            }
            return count;
        }

        /// <summary>
        /// Space between the previous line and this one, except where a space would be wrong:
        /// a hyphenated break ("cover-" + "ed"), or a CJK boundary (Chinese uses no word spaces).
        /// </summary>
        private static string Joiner(StringBuilder output, string next)
        {
            if (output.Length == 0) return null;
            char previous = output[output.Length - 1];
            if (previous == '\n' || previous == ' ' || previous == '\t') return null;
            if (previous == '-' || previous == '\u2014') return null;
            if (IsCjk(previous) || IsCjk(next[0])) return null;
            return " ";
        }

        private static bool IsCjk(char value)
        {
            return (value >= 0x3000 && value <= 0x303F)     // CJK punctuation
                || (value >= 0x3400 && value <= 0x4DBF)     // extension A
                || (value >= 0x4E00 && value <= 0x9FFF)     // unified ideographs
                || (value >= 0xFF00 && value <= 0xFF60);    // full-width forms
        }

        /// <summary>"ACCEPTANCE", "COPYRIGHT LICENSE", "EXHIBIT A" — headings stand alone.</summary>
        private static bool IsUpperHeading(string content)
        {
            bool hasLetter = false;
            foreach (char c in content)
            {
                if (!char.IsLetter(c)) continue;
                hasLetter = true;
                if (!char.IsUpper(c)) return false;
            }
            return hasLetter && content.Length > 3;
        }
    }
}
