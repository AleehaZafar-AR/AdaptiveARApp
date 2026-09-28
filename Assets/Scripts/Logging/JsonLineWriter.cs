// File: JsonLineWriter.cs
// Minimal JSON object builder for one log line.
//
// Written by hand rather than with JsonUtility because the research record needs
// three things JsonUtility cannot give:
//   1. STABLE FIELD ORDER - fields appear in the order written, every run.
//   2. EXPLICIT NULLS - "not measured" must serialise as null, not as 0.
//   3. Nullable value types, which JsonUtility does not support at all.
//
// Plain C# with no Unity dependency, so the same records can be produced by a
// headless replay of the decision layer.

using System.Globalization;
using System.Text;

namespace AdaptiveAR.Logging
{
    public class JsonLineWriter
    {
        private readonly StringBuilder _sb = new StringBuilder(512);
        private bool _hasField;

        public JsonLineWriter()
        {
            _sb.Append('{');
        }

        public JsonLineWriter Str(string key, string value)
        {
            Separator(key);
            if (value == null) _sb.Append("null");
            else Escape(value);
            return this;
        }

        public JsonLineWriter Num(string key, long value)
        {
            Separator(key);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonLineWriter Num(string key, int value)
        {
            return Num(key, (long)value);
        }

        public JsonLineWriter Num(string key, float value)
        {
            Separator(key);
            AppendFloat(value);
            return this;
        }

        /// <summary>Writes a float, or an explicit null when the value is absent.</summary>
        public JsonLineWriter NumOrNull(string key, float? value)
        {
            Separator(key);
            if (value.HasValue) AppendFloat(value.Value);
            else _sb.Append("null");
            return this;
        }

        /// <summary>Writes an int, or an explicit null when the value is absent.</summary>
        public JsonLineWriter NumOrNull(string key, int? value)
        {
            Separator(key);
            if (value.HasValue) _sb.Append(value.Value.ToString(CultureInfo.InvariantCulture));
            else _sb.Append("null");
            return this;
        }

        public JsonLineWriter Bool(string key, bool value)
        {
            Separator(key);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonLineWriter Null(string key)
        {
            Separator(key);
            _sb.Append("null");
            return this;
        }

        /// <summary>Opens a nested object. Must be closed with EndObject().</summary>
        public JsonLineWriter BeginObject(string key)
        {
            Separator(key);
            _sb.Append('{');
            _hasField = false;
            return this;
        }

        public JsonLineWriter EndObject()
        {
            _sb.Append('}');
            _hasField = true;
            return this;
        }

        /// <summary>Writes a nested object as null, for an absent sub-record.</summary>
        public JsonLineWriter NullObject(string key)
        {
            return Null(key);
        }

        /// <summary>Finishes the line. The result contains no newline characters.</summary>
        public string Build()
        {
            _sb.Append('}');
            return _sb.ToString();
        }

        private void Separator(string key)
        {
            if (_hasField) _sb.Append(',');
            Escape(key);
            _sb.Append(':');
            _hasField = true;
        }

        private void AppendFloat(float value)
        {
            // R round-trips exactly; invariant culture keeps decimal points consistent
            // regardless of the device locale.
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                _sb.Append("null");
                return;
            }
            _sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void Escape(string s)
        {
            _sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': _sb.Append("\\\""); break;
                    case '\\': _sb.Append("\\\\"); break;
                    case '\n': _sb.Append("\\n"); break;
                    case '\r': _sb.Append("\\r"); break;
                    case '\t': _sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            _sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _sb.Append(c);
                        break;
                }
            }
            _sb.Append('"');
        }
    }
}
