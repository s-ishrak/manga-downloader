using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MangaDownloader.Core.Naming
{
    /// <summary>
    /// Expands HDoujin-style formatting strings: <c>%TITLE%_%PAGE%</c>.
    /// <list type="bullet">
    /// <item><c>%A||B%</c> uses the first non-empty alternative (an unknown word is taken literally);</item>
    /// <item><c>%ARTIST(2)%</c> keeps the first 2 list items, <c>%PAGE(000)%</c> zero-pads to 3 digits;</item>
    /// <item><c>%IF(a=b,then,else)%</c> (also <c>!=</c>, or a bare value tested for non-empty), <c>%REGEX(v,pattern,group)%</c>, <c>%REPLACE(v,a,b)%</c>, <c>%UPPER(v)%</c>, <c>%LOWER(v)%</c>;</item>
    /// <item>names are case-insensitive and arguments may themselves contain tokens.</item>
    /// </list>
    /// A <c>%</c> that does not start a valid token is kept as a literal.
    /// </summary>
    public sealed class NameFormatter
    {
        private readonly IDictionary<string, string> values;

        public NameFormatter(IDictionary<string, string> values)
        {
            this.values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public static string Format(string format, IDictionary<string, string> values) => new NameFormatter(values).Expand(format ?? "");

        public string Expand(string text)
        {
            var sb = new StringBuilder();
            var i = 0;

            while (i < text.Length)
            {
                if (text[i] == '%' && TryParseToken(text, i, out var result, out var end))
                {
                    sb.Append(result);
                    i = end;
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }

            return sb.ToString();
        }

        private struct Alternative
        {
            public string Name;
            public string Args;   // null when there is no (...)
        }

        private bool TryParseToken(string s, int start, out string result, out int end)
        {
            result = null;
            end = start;

            var alternatives = new List<Alternative>();
            var j = start + 1;

            while (true)
            {
                var nameStart = j;

                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_' || s[j] == '#'))
                    j++;

                if (j == nameStart)
                    return false;

                var alt = new Alternative { Name = s.Substring(nameStart, j - nameStart) };

                if (j < s.Length && s[j] == '(')
                {
                    var close = FindClosingParen(s, j);

                    if (close < 0)
                        return false;

                    alt.Args = s.Substring(j + 1, close - j - 1);
                    j = close + 1;
                }

                alternatives.Add(alt);

                if (j + 1 < s.Length && s[j] == '|' && s[j + 1] == '|')
                {
                    j += 2;
                    continue;
                }

                if (j < s.Length && s[j] == '%')
                {
                    end = j + 1;
                    break;
                }

                return false;
            }

            // A lone unknown name is not a token (leave the text alone rather than silently dropping it).
            if (alternatives.Count == 1 && !IsKnown(alternatives[0]))
                return false;

            result = "";

            for (var k = 0; k < alternatives.Count; k++)
            {
                var alt = alternatives[k];
                var value = IsKnown(alt) ? Evaluate(alt) : (k > 0 ? alt.Name : null);

                if (!string.IsNullOrEmpty(value))
                {
                    result = value;
                    break;
                }
            }

            return true;
        }

        private static readonly HashSet<string> Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IF", "REGEX", "REPLACE", "UPPER", "LOWER", "TRIM" };

        private bool IsKnown(Alternative alt) => values.ContainsKey(alt.Name) || (alt.Args != null && Functions.Contains(alt.Name));

        private static int FindClosingParen(string s, int open)
        {
            var depth = 0;

            for (var i = open; i < s.Length; i++)
            {
                if (s[i] == '\\')
                {
                    i++;
                    continue;
                }

                if (s[i] == '(')
                    depth++;
                else if (s[i] == ')' && --depth == 0)
                    return i;
            }

            return -1;
        }

        private static List<string> SplitArgs(string args)
        {
            var parts = new List<string>();
            var depth = 0;
            var current = new StringBuilder();

            for (var i = 0; i < args.Length; i++)
            {
                var c = args[i];

                if (c == '\\' && i + 1 < args.Length)
                {
                    current.Append(c).Append(args[++i]);
                    continue;
                }

                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth--;

                if (c == ',' && depth == 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            parts.Add(current.ToString());
            return parts;
        }

        private string Evaluate(Alternative alt)
        {
            if (alt.Args != null && Functions.Contains(alt.Name) && !values.ContainsKey(alt.Name))
                return EvaluateFunction(alt.Name.ToUpperInvariant(), SplitArgs(alt.Args));

            var value = values[alt.Name] ?? "";
            var arg = alt.Args?.Trim();

            if (string.IsNullOrEmpty(arg) || value.Length == 0)
                return value;

            if (arg.All(c => c == '0'))
                return long.TryParse(value, out var n) ? n.ToString().PadLeft(arg.Length, '0') : value;

            if (int.TryParse(arg, out var limit) && limit > 0)
                return string.Join(", ", value.Split(new[] { ", " }, StringSplitOptions.None).Take(limit));

            return value;
        }

        private string EvaluateFunction(string name, List<string> rawArgs)
        {
            string Arg(int i) => i < rawArgs.Count ? Expand(rawArgs[i]) : "";

            switch (name)
            {
                case "IF":
                    return Condition(rawArgs.Count > 0 ? rawArgs[0] : "") ? Arg(1) : Arg(2);

                case "REGEX":
                {
                    var m = Regex.Match(Arg(0), Arg(1));
                    var group = int.TryParse(Arg(2), out var g) ? g : (m.Groups.Count > 1 ? 1 : 0);
                    return m.Success && group < m.Groups.Count ? m.Groups[group].Value : "";
                }

                case "REPLACE":
                    return string.IsNullOrEmpty(Arg(1)) ? Arg(0) : Arg(0).Replace(Arg(1), Arg(2));

                case "UPPER":
                    return Arg(0).ToUpperInvariant();

                case "LOWER":
                    return Arg(0).ToLowerInvariant();

                case "TRIM":
                    return Arg(0).Trim();

                default:
                    return "";
            }
        }

        private bool Condition(string raw)
        {
            var ne = raw.IndexOf("!=", StringComparison.Ordinal);

            if (ne >= 0)
                return !string.Equals(Expand(raw.Substring(0, ne)).Trim(), Expand(raw.Substring(ne + 2)).Trim(), StringComparison.OrdinalIgnoreCase);

            var eq = IndexOfTopLevelEquals(raw);

            if (eq >= 0)
                return string.Equals(Expand(raw.Substring(0, eq)).Trim(), Expand(raw.Substring(eq + 1)).Trim(), StringComparison.OrdinalIgnoreCase);

            return !string.IsNullOrWhiteSpace(Expand(raw));
        }

        private static int IndexOfTopLevelEquals(string s)
        {
            var depth = 0;

            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '(')
                    depth++;
                else if (s[i] == ')')
                    depth--;
                else if (s[i] == '=' && depth == 0)
                    return i;
            }

            return -1;
        }
    }
}
