using System.Text;

namespace QuadVitals.Core
{
    /// <summary>
    /// Minimal JSON re-indenter. DataContractJsonSerializer emits a single line, but
    /// settings.json is meant to be hand-editable and diff-friendly, so we pretty print it.
    /// Whitespace outside string literals is all that changes, which keeps the value intact.
    /// </summary>
    public static class JsonPretty
    {
        public static string Print(string json)
        {
            if (string.IsNullOrEmpty(json)) return json;

            StringBuilder output = new StringBuilder(json.Length + 128);
            int depth = 0;
            bool inString = false;
            bool escaped = false;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];

                if (inString)
                {
                    output.Append(c);
                    if (escaped) { escaped = false; }
                    else if (c == '\\') { escaped = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        output.Append(c);
                        break;

                    case '{':
                    case '[':
                    {
                        output.Append(c);
                        char close = c == '{' ? '}' : ']';
                        int next = SkipWhitespace(json, i + 1);
                        if (next < json.Length && json[next] == close)
                        {
                            // "{}" / "[]" stays on one line
                            output.Append(close);
                            i = next;
                        }
                        else
                        {
                            depth++;
                            output.Append('\n');
                            output.Append(' ', depth * 2);
                        }
                        break;
                    }

                    case '}':
                    case ']':
                        depth--;
                        output.Append('\n');
                        output.Append(' ', depth * 2);
                        output.Append(c);
                        break;

                    case ',':
                        output.Append(",\n");
                        output.Append(' ', depth * 2);
                        break;

                    case ':':
                        output.Append(": ");
                        break;

                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                        break;

                    default:
                        output.Append(c);
                        break;
                }
            }

            return output.ToString();
        }

        private static int SkipWhitespace(string json, int index)
        {
            while (index < json.Length && (json[index] == ' ' || json[index] == '\t' || json[index] == '\r' || json[index] == '\n'))
            {
                index++;
            }
            return index;
        }
    }
}
