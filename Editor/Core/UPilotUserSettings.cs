// UPilot Editor - user-wide preferences and project port references.
// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using UnityEngine;

namespace CodingRiver.UPilot
{
    [Serializable]
    internal sealed class UPilotUserSettingsData
    {
        public long maxSegmentSizeBytes = UPilotDownloadHelper.DefaultMaxSegmentSizeBytes;
        public int maxConcurrentSegmentRequests = UPilotDownloadHelper.DefaultMaxConcurrentSegmentRequests;
    }

    internal static class UPilotUserSettings
    {
        internal static string ConfigPath
        {
            get
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrWhiteSpace(profile) ? "" : Path.Combine(profile, ".upilot", "settings.json");
            }
        }

        internal static UPilotUserSettingsData Load(out string error) => Load(ConfigPath, out error);

        internal static UPilotUserSettingsData Load(string path, out string error)
        {
            error = "";
            try
            {
                var settings = ReadDownloadSettings(ReadDocument(path));
                Validate(settings);
                return settings;
            }
            catch (Exception ex)
            {
                ReportFailure("load", path, ex, out error);
                return new UPilotUserSettingsData();
            }
        }

        internal static bool TrySave(UPilotUserSettingsData settings, out string error) =>
            TrySave(ConfigPath, settings, out error);

        internal static bool TrySave(string path, UPilotUserSettingsData settings, out string error)
        {
            error = "";
            try
            {
                Validate(settings);
                Update(path, root =>
                {
                    SetProperty(root, "maxSegmentSizeBytes", Number(settings.maxSegmentSizeBytes));
                    SetProperty(root, "maxConcurrentSegmentRequests", Number(settings.maxConcurrentSegmentRequests));
                    return true;
                });
                return true;
            }
            catch (Exception ex)
            {
                ReportFailure("save", path, ex, out error);
                return false;
            }
        }

        // Registry failures are deliberately thrown to its caller, which reports the operation
        // and settings path without changing the authoritative project-configuration result.
        internal static Dictionary<string, int[]> ReadProjectPorts(string path)
        {
            try { return ReadProjectPorts(ReadDocument(path)); }
            catch (Exception ex)
            {
                throw new InvalidDataException($"读取端口登记失败：path={path}; error={ex.Message}", ex);
            }
        }

        internal static void SetProjectPorts(string path, string project, int ws, int http)
        {
            project = NormalizeProjectPath(project);
            UPilotPortRegistry.ValidatePorts(ws, http);
            Update(path, root =>
            {
                var ports = ReadProjectPorts(root);
                var existingTable = FindProperty(root, "projectPorts");
                if (ports.TryGetValue(project, out var current) && current[0] == ws && current[1] == http &&
                    existingTable.Elements().All(field => PropertyName(field) == NormalizeProjectPath(PropertyName(field))))
                    return false;
                ports[project] = new[] { ws, http };
                var table = new XElement("projectPorts", new XAttribute("type", "object"));
                foreach (var entry in ports)
                {
                    var pair = new XElement("item", new XAttribute("type", "array"),
                        Number(entry.Value[0]), Number(entry.Value[1]));
                    SetProperty(table, entry.Key, pair);
                }
                SetProperty(root, "projectPorts", table);
                return true;
            });
        }

        internal static void ClearProjectPorts(string path) => Update(path, root =>
        {
            // Explicit clear also repairs an invalid registry section in an otherwise valid document.
            SetProperty(root, "projectPorts", new XElement("projectPorts", new XAttribute("type", "object")));
            return true;
        });

        internal static string NormalizeProjectPath(string project)
        {
            if (string.IsNullOrWhiteSpace(project) || !Path.IsPathRooted(project) ||
                (Path.DirectorySeparatorChar == '\\' && Path.GetPathRoot(project).Length < 3))
                throw new IOException("工程路径必须是绝对路径。");
            var full = Path.GetFullPath(project);
            var root = Path.GetPathRoot(full);
            while (full.Length > root.Length && (full[full.Length - 1] == Path.DirectorySeparatorChar ||
                   full[full.Length - 1] == Path.AltDirectorySeparatorChar))
                full = full.Substring(0, full.Length - 1);
            return full.Replace(Path.DirectorySeparatorChar, '/');
        }

        internal static string GetLockName(string path)
        {
            RequirePath(path);
            var normalized = Path.GetFullPath(path);
            if (Path.DirectorySeparatorChar == '\\') normalized = normalized.ToUpperInvariant();
            using var hash = SHA256.Create();
            return "UPilot.UserSettings." + BitConverter.ToString(
                hash.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "");
        }

        private static void Update(string path, Func<XElement, bool> change)
        {
            using var mutex = new Mutex(false, GetLockName(path));
            bool acquired;
            try { acquired = mutex.WaitOne(5000); }
            catch (AbandonedMutexException ex)
            {
                acquired = true;
                Debug.LogError($"[UPilotUserSettings] operation=acquire-write-lock; path={path}; " +
                               $"前一写入进程异常退出，已取得锁，将重新读取原文件。error={ex}");
            }
            if (!acquired) throw new IOException($"等待用户配置写入锁超时（5 秒）：{path}");
            try
            {
                // Never use Load's fallback defaults here: an unreadable document must survive.
                var root = ReadDocument(path);
                if (!change(root)) return;
                using var stream = new MemoryStream();
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, new UTF8Encoding(false), false, true, "  "))
                {
                    root.WriteTo(writer);
                    writer.Flush();
                }
                // Escaped slashes are equivalent JSON, but project path keys should remain readable.
                UPilotPortRegistry.AtomicWrite(path, Encoding.UTF8.GetString(stream.ToArray()).Replace("\\/", "/"));
            }
            finally { mutex.ReleaseMutex(); }
        }

        internal static UPilotDownloadOptions CreateDownloadOptions(UPilotUserSettingsData settings)
        {
            Validate(settings);
            return new UPilotDownloadOptions
            {
                MaxSegmentSizeBytes = settings.maxSegmentSizeBytes,
                MaxConcurrentSegmentRequests = settings.maxConcurrentSegmentRequests,
            };
        }

        private static XElement ReadDocument(string path)
        {
            RequirePath(path);
            string json;
            try
            {
                // Atomic replacement must remain possible while another editor reads the old file.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var textReader = new StreamReader(stream);
                json = textReader.ReadToEnd();
            }
            catch (FileNotFoundException) { return new XElement("root", new XAttribute("type", "object")); }
            catch (DirectoryNotFoundException) { return new XElement("root", new XAttribute("type", "object")); }
            RequireSingleObject(json);
            using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max);
            var root = XElement.Load(reader);
            while (reader.Read())
                if (reader.NodeType != XmlNodeType.Whitespace && reader.NodeType != XmlNodeType.SignificantWhitespace)
                    throw new InvalidDataException("通用设置只能包含一个 JSON 对象。");
            if ((string)root.Attribute("type") != "object")
                throw new InvalidDataException("通用设置必须是 JSON 对象。");
            foreach (var element in root.DescendantsAndSelf())
            {
                // The framework reader materializes null as text; its writer needs an empty element.
                if ((string)element.Attribute("type") == "null") element.RemoveNodes();
                if ((string)element.Attribute("type") == "number" &&
                    (element.Value == "NaN" || element.Value.Contains("Infinity")))
                    throw new InvalidDataException("通用设置包含非法 JSON 数值。");
            }
            return root;
        }

        private static UPilotUserSettingsData ReadDownloadSettings(XElement root)
        {
            var settings = new UPilotUserSettingsData();
            var size = FindProperty(root, "maxSegmentSizeBytes");
            if (size != null) settings.maxSegmentSizeBytes = ReadInteger(size, "maxSegmentSizeBytes");
            var concurrency = FindProperty(root, "maxConcurrentSegmentRequests");
            if (concurrency != null) settings.maxConcurrentSegmentRequests = checked((int)ReadInteger(concurrency, "maxConcurrentSegmentRequests"));
            return settings;
        }

        private static Dictionary<string, int[]> ReadProjectPorts(XElement root)
        {
            var entries = new Dictionary<string, int[]>(Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var table = FindProperty(root, "projectPorts");
            if (table == null) return entries;
            if ((string)table.Attribute("type") != "object")
                throw new InvalidDataException("端口登记格式损坏：projectPorts 必须是 JSON 对象。");
            foreach (var field in table.Elements())
            {
                var project = NormalizeProjectPath(PropertyName(field));
                var pair = field.Elements().ToArray();
                if ((string)field.Attribute("type") != "array" || pair.Length != 2)
                    throw new InvalidDataException($"端口登记格式损坏：{project} 必须是 [WS端口, HTTP端口]。");
                var ws = checked((int)ReadInteger(pair[0], project + " WS"));
                var http = checked((int)ReadInteger(pair[1], project + " HTTP"));
                UPilotPortRegistry.ValidatePorts(ws, http);
                if (entries.ContainsKey(project))
                    throw new InvalidDataException($"端口登记存在重复项目路径：{project}");
                entries.Add(project, new[] { ws, http });
            }
            return entries;
        }

        private static long ReadInteger(XElement field, string name)
        {
            if ((string)field.Attribute("type") != "number" ||
                !long.TryParse(field.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException(name + " 必须是整数。");
            return value;
        }

        private static string PropertyName(XElement field) => (string)field.Attribute("item") ?? field.Name.LocalName;

        private static XElement FindProperty(XElement root, string name)
        {
            var fields = root.Elements().Where(field => PropertyName(field) == name).ToArray();
            if (fields.Length > 1) throw new InvalidDataException(name + " 重复。");
            return fields.SingleOrDefault();
        }

        private static XElement Number(long value) => new XElement("item", new XAttribute("type", "number"),
            value.ToString(CultureInfo.InvariantCulture));

        private static void SetProperty(XElement root, string name, XElement value)
        {
            foreach (var field in root.Elements().Where(field => PropertyName(field) == name).ToArray()) field.Remove();
            // This mapping preserves arbitrary JSON property names, including absolute project paths.
            root.Add(new XElement(XName.Get("item", "item"), new XAttribute("item", name),
                new XAttribute("type", (string)value.Attribute("type")), value.Nodes()));
        }

        private static void RequireSingleObject(string json)
        {
            // The framework reader accepts malformed numbers, trailing commas and extra roots.
            // Validate grammar before mapping to XML so no update can rewrite a damaged document.
            new JsonSyntax(json).RequireObject();
        }

        private sealed class JsonSyntax
        {
            private readonly string _json;
            private int _index;
            private char Current => _index < _json.Length ? _json[_index] : '\0';

            internal JsonSyntax(string json) => _json = json;

            internal void RequireObject()
            {
                SkipWhitespace();
                if (Current != '{') Fail();
                ReadValue(0);
                SkipWhitespace();
                if (_index != _json.Length) Fail();
            }

            private void ReadValue(int depth)
            {
                if (depth > 128) throw new InvalidDataException("通用设置 JSON 嵌套超过 128 层。");
                SkipWhitespace();
                if (Take('{'))
                {
                    SkipWhitespace();
                    if (Take('}')) return;
                    do
                    {
                        SkipWhitespace();
                        ReadString();
                        SkipWhitespace();
                        Need(':');
                        ReadValue(depth + 1);
                        SkipWhitespace();
                        if (Take('}')) return;
                        Need(',');
                    } while (true);
                }
                if (Take('['))
                {
                    SkipWhitespace();
                    if (Take(']')) return;
                    do
                    {
                        ReadValue(depth + 1);
                        SkipWhitespace();
                        if (Take(']')) return;
                        Need(',');
                    } while (true);
                }
                if (Current == '"') { ReadString(); return; }
                if (Current == 't') { ReadLiteral("true"); return; }
                if (Current == 'f') { ReadLiteral("false"); return; }
                if (Current == 'n') { ReadLiteral("null"); return; }
                Take('-');
                if (!Take('0')) ReadDigits();
                if (Take('.')) ReadDigits();
                if (Take('e') || Take('E'))
                {
                    if (!Take('+')) Take('-');
                    ReadDigits();
                }
            }

            private void ReadString()
            {
                Need('"');
                while (_index < _json.Length)
                {
                    var value = _json[_index++];
                    if (value == '"') return;
                    if (value < ' ') Fail();
                    if (value != '\\') continue;
                    if (_index == _json.Length) Fail();
                    var escape = _json[_index++];
                    if (escape == 'u')
                    {
                        for (var digit = 0; digit < 4; digit++)
                        {
                            var hex = Current;
                            if (!(IsDigit(hex) || (hex >= 'a' && hex <= 'f') || (hex >= 'A' && hex <= 'F'))) Fail();
                            _index++;
                        }
                    }
                    else if ("\"\\/bfnrt".IndexOf(escape) < 0) Fail();
                }
                Fail();
            }

            private void ReadDigits()
            {
                if (!IsDigit(Current)) Fail();
                do { _index++; } while (IsDigit(Current));
            }

            private void ReadLiteral(string literal)
            {
                if (_json.Length - _index < literal.Length ||
                    string.CompareOrdinal(_json, _index, literal, 0, literal.Length) != 0) Fail();
                _index += literal.Length;
            }

            private void SkipWhitespace()
            {
                while (Current == ' ' || Current == '\t' || Current == '\r' || Current == '\n') _index++;
            }

            private bool Take(char token)
            {
                if (Current != token) return false;
                _index++;
                return true;
            }

            private void Need(char token)
            {
                if (!Take(token)) Fail();
            }

            private static bool IsDigit(char value) => value >= '0' && value <= '9';

            private void Fail() => throw new InvalidDataException($"通用设置 JSON 语法错误，位置 {_index}。");
        }

        private static void RequirePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("无法确定当前系统用户目录，不能读写通用设置。");
        }

        private static void Validate(UPilotUserSettingsData settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (settings.maxSegmentSizeBytes <= 0 || settings.maxSegmentSizeBytes % 1024 != 0)
                throw new ArgumentOutOfRangeException(nameof(settings.maxSegmentSizeBytes), "分片大小必须是正整数 KiB。");
            if (settings.maxConcurrentSegmentRequests <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings.maxConcurrentSegmentRequests), "最大同时下载片数必须是正整数。");
        }

        private static void ReportFailure(string operation, string path, Exception exception, out string error)
        {
            error = $"{path}: {exception.Message}";
            Debug.LogError($"[UPilotUserSettings] operation={operation}; path={path}; error={exception}");
        }
    }
}
