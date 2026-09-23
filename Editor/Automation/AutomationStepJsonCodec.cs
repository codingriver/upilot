using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UnityEngine;

namespace CodingRiver.UPilot.Automation
{
    /// <summary>Strict wire validation before converting to internal models. No new JSON dependency.</summary>
    internal static class AutomationStepJsonCodec
    {
        [Serializable] private sealed class ResultWire { public string status; public string errorCode; }
        [Serializable] private sealed class ErrorWire { public string message; public string diagnostic; }

        internal static XElement ParseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Expected nonempty JSON.");
            RequireSingleValue(json);
            try
            {
                using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max);
                var root = XElement.Load(reader);
                foreach (var element in root.DescendantsAndSelf())
                {
                    if ((string)element.Attribute("type") == "object"
                        && element.Elements().GroupBy(PropertyName, StringComparer.Ordinal).Any(g => g.Count() != 1))
                        throw new FormatException("Duplicate JSON property.");
                    if ((string)element.Attribute("type") == "number"
                        && (element.Value == "NaN" || element.Value.Contains("Infinity")))
                        throw new FormatException("Non-finite JSON number.");
                    // Unity's framework reader materializes null as text, which its writer rejects.
                    if ((string)element.Attribute("type") == "null") element.RemoveNodes();
                }
                return root;
            }
            catch (XmlException ex) { throw new FormatException("Invalid JSON.", ex); }
        }
        private static void RequireSingleValue(string json)
        {
            // The framework reader stops at the first root. Check its input boundary, leaving
            // object/array syntax and escaping validation to that reader.
            string text = json.Trim(' ', '\t', '\r', '\n');
            if (text.Length == 0) throw new FormatException("Empty JSON.");
            if (text[0] != '{' && text[0] != '[' && text[0] != '"')
            {
                if (text != "true" && text != "false" && text != "null"
                    && !Regex.IsMatch(text, @"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z"))
                    throw new FormatException("Invalid JSON scalar or trailing content.");
                return;
            }
            int depth = 0;
            bool quoted = false, escaped = false;
            for (int i = 0; i < text.Length; i++)
            {
                char value = text[i];
                if (quoted)
                {
                    if (escaped) { escaped = false; continue; }
                    if (value == '\\') { escaped = true; continue; }
                    if (value != '"') continue;
                    quoted = false;
                }
                else if (value == '"') { quoted = true; continue; }
                else if (value == '{' || value == '[') depth++;
                else if (value == '}' || value == ']') depth--;
                if (depth == 0 && !quoted && i != text.Length - 1)
                    throw new FormatException("Trailing content after JSON value.");
            }
            if (quoted || depth != 0) throw new FormatException("Incomplete JSON.");
        }
        internal static XElement ParseObject(string json)
        {
            var root = ParseJson(json);
            RequireType(root, "object");
            return root;
        }
        internal static string WriteJson(XElement element)
        {
            using var stream = new MemoryStream();
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false))
            { element.WriteTo(writer); writer.Flush(); }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        private static string PropertyName(XElement element) => (string)element.Attribute("item") ?? element.Name.LocalName;
        internal static void SetProperty(XElement root, string key, XElement value)
        {
            foreach (var old in root.Elements().Where(e => PropertyName(e) == key).ToArray()) old.Remove();
            // The framework JSON/XML mapping supports keys that are not valid XML names.
            var property = new XElement(XName.Get("item", "item"), new XAttribute("item", key),
                new XAttribute("type", (string)value.Attribute("type") ?? "string"), value.Nodes());
            root.Add(property);
        }
        internal static void RequireType(XElement element, string type)
        {
            if (element == null || (string)element.Attribute("type") != type)
                throw new FormatException("Expected JSON " + type + ".");
        }
        internal static string RequiredString(XElement root, string field, bool nonempty = true)
        {
            var value = root.Element(field);
            RequireType(value, "string");
            if (nonempty && string.IsNullOrWhiteSpace(value.Value)) throw new FormatException(field + " must be nonempty.");
            return value.Value;
        }
        private static string OptionalString(XElement root, string field)
        {
            return root.Element(field) == null ? "" : RequiredString(root, field, false);
        }
        internal static AutomationStepValidation Validation(string json)
        {
            var root = ParseObject(json);
            RequireType(root.Element("ok"), "boolean");
            var result = new AutomationStepValidation { ok = bool.Parse(root.Element("ok").Value) };
            var diagnostics = root.Element("diagnostics");
            if (diagnostics != null)
            {
                RequireType(diagnostics, "array");
                foreach (var item in diagnostics.Elements())
                {
                    RequireType(item, "object");
                    string severity = item.Element("severity") == null ? "error" : RequiredString(item, "severity");
                    if (severity != "error" && severity != "warning" && severity != "info")
                        throw new FormatException("Unknown diagnostic severity.");
                    result.diagnostics.Add(new AutomationDiagnostic
                    { code = RequiredString(item, "code"), message = RequiredString(item, "message"), severity = severity });
                }
            }
            bool errors = result.diagnostics.Any(d => d.severity == "error");
            if (result.ok == errors) throw new FormatException("ok and error diagnostics disagree.");
            return result;
        }
        internal static AutomationStepResult Result(string json, bool cleanup = false)
        {
            var root = ParseObject(json);
            string status = RequiredString(root, "status");
            string code = OptionalString(root, "errorCode");
            if (!Enum.TryParse(status, false, out AutomationStepStatus parsed)
                || !Enum.IsDefined(typeof(AutomationStepStatus), parsed) || parsed.ToString() != status
                || parsed == AutomationStepStatus.TimedOut
                || (cleanup && parsed != AutomationStepStatus.Running && parsed != AutomationStepStatus.Succeeded
                    && parsed != AutomationStepStatus.SucceededWithWarnings && parsed != AutomationStepStatus.Failed))
                throw new FormatException("Unknown or forbidden result status: " + status);
            if ((parsed == AutomationStepStatus.Failed || parsed == AutomationStepStatus.Canceled) && string.IsNullOrWhiteSpace(code))
                throw new FormatException("Failed/Canceled requires errorCode.");
            return AutomationStepResult.Result(parsed, code);
        }
        internal static AutomationStepError Error(string json, string frozenCode)
        {
            var root = ParseObject(json);
            return new AutomationStepError
            { code = frozenCode, message = RequiredString(root, "message"), diagnostic = OptionalString(root, "diagnostic") };
        }
        internal static string Restore(string json, out string errorCode)
        {
            var root = ParseObject(json);
            string status = RequiredString(root, "status");
            errorCode = OptionalString(root, "errorCode");
            if (status != "Restored" && status != "Unsupported" && status != "Failed")
                throw new FormatException("Unknown Restore status.");
            if (status == "Failed" && string.IsNullOrWhiteSpace(errorCode)) throw new FormatException("Restore failure requires errorCode.");
            return status;
        }
        internal static string ResultJson(string status, string code = "")
        {
            var json = JsonUtility.ToJson(new ResultWire { status = status, errorCode = code });
            Result(json);
            return json;
        }
        internal static string ErrorJson(string message, string diagnostic = "") =>
            JsonUtility.ToJson(new ErrorWire { message = message ?? "", diagnostic = diagnostic ?? "" });
        internal static string ValidationErrorJson(string code, string message)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("Validation errors require code and message.");
            return JsonUtility.ToJson(AutomationStepValidation.Invalid(code, message));
        }
    }
}
