using System.Text;

namespace DeadlockScout.Steam.Vdf;

/// <summary>Parser for Valve KeyValues text (loginusers.vdf, localconfig.vdf, libraryfolders.vdf, *.acf).</summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var root = new VdfNode();
        int pos = 0;
        ParseInto(root, text, ref pos, topLevel: true);
        return root;
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    private static void ParseInto(VdfNode parent, string s, ref int pos, bool topLevel)
    {
        while (true)
        {
            SkipWhitespaceAndComments(s, ref pos);
            if (pos >= s.Length)
            {
                if (topLevel) return;
                throw new FormatException("Unexpected end of VDF: missing '}'.");
            }
            if (s[pos] == '}')
            {
                if (topLevel) throw new FormatException($"Unexpected '}}' at {pos}.");
                pos++;
                return;
            }
            string key = ReadToken(s, ref pos);
            SkipWhitespaceAndComments(s, ref pos);
            if (pos >= s.Length) throw new FormatException($"Key '{key}' has no value.");
            if (s[pos] == '{')
            {
                pos++;
                var obj = new VdfNode();
                parent.Add(key, obj);
                ParseInto(obj, s, ref pos, topLevel: false);
            }
            else
            {
                string value = ReadToken(s, ref pos);
                parent.Add(key, new VdfNode(value));
            }
        }
    }

    private static void SkipWhitespaceAndComments(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            char c = s[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            break;
        }
    }

    private static string ReadToken(string s, ref int pos)
    {
        if (s[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    pos++;
                    sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', var o => o });
                }
                else sb.Append(s[pos]);
                pos++;
            }
            if (pos >= s.Length) throw new FormatException("Unterminated string in VDF.");
            pos++;
            return sb.ToString();
        }
        int start = pos;
        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '{' && s[pos] != '}') pos++;
        if (pos == start) throw new FormatException($"Unexpected character '{s[pos]}' at {pos}.");
        return s[start..pos];
    }
}
