// File: MiniJson.cs
// Dependency-free JSON reader/writer for configuration, model responses and replayed log
// lines. Plain C# so the same code runs in Unity (no Newtonsoft in this project) and in the
// headless evaluation tools. Values: Dictionary<string, object>, List<object>, string,
// double, bool, null.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AdaptiveAR.Decision
{
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) return null;
            int i = 0;
            object v = ParseValue(json, ref i);
            SkipWs(json, ref i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            return Parse(json) as Dictionary<string, object>;
        }

        // ---------------------------------------------------------------- accessors

        public static string GetString(Dictionary<string, object> o, string key, string fallback = null)
        {
            if (o == null || !o.TryGetValue(key, out object v) || v == null) return fallback;
            return v is string s ? s : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double GetDouble(Dictionary<string, object> o, string key, double fallback)
        {
            if (o == null || !o.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is double d) return d;
            if (v is bool b) return b ? 1 : 0;
            double parsed; return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        public static double? GetNullableDouble(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out object v) || v == null) return null;
            if (v is double d) return d;
            double parsed; return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
        }

        public static int GetInt(Dictionary<string, object> o, string key, int fallback) { return (int)Math.Round(GetDouble(o, key, fallback)); }

        public static bool GetBool(Dictionary<string, object> o, string key, bool fallback)
        {
            if (o == null || !o.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is bool b) return b;
            if (v is double d) return d != 0;
            return string.Equals(Convert.ToString(v), "true", StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out object v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<object> GetList(Dictionary<string, object> o, string key)
        {
            if (o == null || !o.TryGetValue(key, out object v)) return null;
            return v as List<object>;
        }

        // ---------------------------------------------------------------- writer

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case float f: WriteNumber(sb, f); break;
                case double d: WriteNumber(sb, d); break;
                case Dictionary<string, object> o:
                    sb.Append('{'); bool first = true;
                    foreach (KeyValuePair<string, object> kv in o)
                    {
                        if (!first) sb.Append(','); first = false;
                        WriteString(sb, kv.Key); sb.Append(':'); Write(sb, kv.Value);
                    }
                    sb.Append('}'); break;
                case System.Collections.IEnumerable e:
                    sb.Append('['); bool f1 = true;
                    foreach (object item in e) { if (!f1) sb.Append(','); f1 = false; Write(sb, item); }
                    sb.Append(']'); break;
                default: WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------- parser

        private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of JSON");
            char c = s[i];
            if (c == '{') return ParseObjectAt(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && Match(s, i, "true")) { i += 4; return true; }
            if (c == 'f' && Match(s, i, "false")) { i += 5; return false; }
            if (c == 'n' && Match(s, i, "null")) { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        private static bool Match(string s, int i, string word) { return string.CompareOrdinal(s, i, word, 0, word.Length) == 0; }

        private static Dictionary<string, object> ParseObjectAt(string s, ref int i)
        {
            var o = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                o[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
            throw new FormatException("unterminated object");
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var a = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (i < s.Length)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
            throw new FormatException("unterminated array");
        }

        private static string ParseString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("expected string at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && ("+-0123456789.eE".IndexOf(s[i]) >= 0)) i++;
            if (start == i) throw new FormatException("unexpected character '" + s[i] + "' at " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
