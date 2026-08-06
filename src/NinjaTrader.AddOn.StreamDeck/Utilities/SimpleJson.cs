using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace NinjaTrader.NinjaScript.AddOns.StreamDeck.Utilities
{
    /// <summary>
    /// Minimal JSON serializer/deserializer for NT8 NinjaScript compatibility.
    /// No external dependencies — handles the bridge protocol message format.
    /// </summary>
    public static class SimpleJson
    {
        #region Serialization

        public static string Serialize(object obj)
        {
            if (obj == null) return "null";
            var sb = new StringBuilder();
            WriteValue(sb, obj);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (value is string s)
            {
                WriteString(sb, s);
                return;
            }

            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (value is int i)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is long l)
            {
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is double d)
            {
                // NaN/Infinity are not valid JSON: emitting them would make the receiver
                // reject the whole message, taking the entire state feed down with it.
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("G", CultureInfo.InvariantCulture));
                return;
            }

            if (value is float f)
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) sb.Append("null");
                else sb.Append(f.ToString("G", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal dec)
            {
                sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is Dictionary<string, object> dict)
            {
                WriteDictionary(sb, dict);
                return;
            }

            if (value is Array arr)
            {
                WriteArray(sb, arr);
                return;
            }

            if (value is IEnumerable enumerable)
            {
                WriteEnumerable(sb, enumerable);
                return;
            }

            // Anonymous types and regular objects — use reflection
            WriteObject(sb, value);
        }

        private static void WriteString(StringBuilder sb, string s)
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
                        if (c < ' ')
                            sb.AppendFormat("\\u{0:X4}", (int)c);
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void WriteDictionary(StringBuilder sb, Dictionary<string, object> dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kvp in dict)
            {
                if (kvp.Value == null) continue;
                if (!first) sb.Append(',');
                WriteString(sb, kvp.Key);
                sb.Append(':');
                WriteValue(sb, kvp.Value);
                first = false;
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, Array arr)
        {
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                WriteValue(sb, arr.GetValue(i));
            }
            sb.Append(']');
        }

        private static void WriteEnumerable(StringBuilder sb, IEnumerable enumerable)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in enumerable)
            {
                if (!first) sb.Append(',');
                WriteValue(sb, item);
                first = false;
            }
            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, object obj)
        {
            sb.Append('{');
            var props = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            bool first = true;
            foreach (var prop in props)
            {
                if (prop.GetIndexParameters().Length > 0) continue;

                var val = prop.GetValue(obj, null);
                if (val == null) continue;
                if (!first) sb.Append(',');
                WriteString(sb, prop.Name);
                sb.Append(':');
                WriteValue(sb, val);
                first = false;
            }
            sb.Append('}');
        }

        #endregion

        #region Deserialization

        public static Dictionary<string, object> Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            int index = 0;
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == '{')
                return ReadObject(json, ref index);
            return null;
        }

        private static object ReadValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length) return null;

            char c = json[index];

            if (c == '"') return ReadString(json, ref index);
            if (c == '{') return ReadObject(json, ref index);
            if (c == '[') return ReadArray(json, ref index);
            if (c == 't' || c == 'f') return ReadBool(json, ref index);
            if (c == 'n') return ReadNull(json, ref index);
            if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(json, ref index);

            return null;
        }

        private static string ReadString(string json, ref int index)
        {
            index++; // skip opening quote
            var sb = new StringBuilder();
            while (index < json.Length)
            {
                char c = json[index];
                if (c == '\\')
                {
                    index++;
                    if (index >= json.Length) break;
                    char esc = json[index];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (index + 4 < json.Length)
                            {
                                var hex = json.Substring(index + 1, 4);
                                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber));
                                index += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else if (c == '"')
                {
                    index++; // skip closing quote
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
                index++;
            }
            return sb.ToString();
        }

        private static Dictionary<string, object> ReadObject(string json, ref int index)
        {
            index++; // skip {
            var dict = new Dictionary<string, object>();
            SkipWhitespace(json, ref index);

            if (index < json.Length && json[index] == '}')
            {
                index++;
                return dict;
            }

            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"') break;

                string key = ReadString(json, ref index);
                SkipWhitespace(json, ref index);

                if (index < json.Length && json[index] == ':')
                    index++;

                object value = ReadValue(json, ref index);
                dict[key] = value;

                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (index < json.Length && json[index] == '}')
                {
                    index++;
                    break;
                }
            }

            return dict;
        }

        private static List<object> ReadArray(string json, ref int index)
        {
            index++; // skip [
            var list = new List<object>();
            SkipWhitespace(json, ref index);

            if (index < json.Length && json[index] == ']')
            {
                index++;
                return list;
            }

            while (index < json.Length)
            {
                list.Add(ReadValue(json, ref index));
                SkipWhitespace(json, ref index);

                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (index < json.Length && json[index] == ']')
                {
                    index++;
                    break;
                }
            }

            return list;
        }

        private static object ReadNumber(string json, ref int index)
        {
            int start = index;
            bool isFloat = false;

            if (json[index] == '-') index++;
            while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
            if (index < json.Length && json[index] == '.')
            {
                isFloat = true;
                index++;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
            }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                isFloat = true;
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
            }

            string numStr = json.Substring(start, index - start);

            if (isFloat)
            {
                double d;
                if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    return d;
            }
            else
            {
                int i;
                if (int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                    return i;
                long l;
                if (long.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    return l;
            }

            return 0;
        }

        private static bool ReadBool(string json, ref int index)
        {
            if (json.Substring(index).StartsWith("true"))
            {
                index += 4;
                return true;
            }
            index += 5;
            return false;
        }

        private static object ReadNull(string json, ref int index)
        {
            index += 4;
            return null;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && (json[index] == ' ' || json[index] == '\t' || json[index] == '\n' || json[index] == '\r'))
                index++;
        }

        #endregion

        #region Helpers

        public static string GetString(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object val;
            if (dict.TryGetValue(key, out val) && val is string)
                return (string)val;
            return null;
        }

        public static int? GetInt(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object val;
            if (!dict.TryGetValue(key, out val)) return null;
            if (val is int) return (int)val;
            if (val is long) return (int)(long)val;
            if (val is double) return (int)(double)val;
            return null;
        }

        public static double? GetDouble(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object val;
            if (!dict.TryGetValue(key, out val)) return null;
            if (val is double) return (double)val;
            if (val is int) return (double)(int)val;
            if (val is long) return (double)(long)val;
            return null;
        }

        /// <summary>
        /// Absent, null or malformed reads as false. Deliberate: this backs the guard policy, and
        /// a payload that could not be understood must never be taken for permission to trade.
        /// </summary>
        public static bool GetBool(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return false;
            object val;
            if (!dict.TryGetValue(key, out val) || val == null) return false;
            if (val is bool) return (bool)val;

            var text = val as string;
            if (text != null) return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);

            return false;
        }

        #endregion
    }
}
