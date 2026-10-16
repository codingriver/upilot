// UPilot Editor - user-wide download preferences.
// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Globalization;
using System.Runtime.Serialization.Json;
using System.Text;
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
                RequirePath(path);
                string json;
                try { json = File.ReadAllText(path); }
                catch (FileNotFoundException) { return new UPilotUserSettingsData(); }
                catch (DirectoryNotFoundException) { return new UPilotUserSettingsData(); }
                var settings = Parse(json);
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
                RequirePath(path);
                Validate(settings);
                UPilotPortRegistry.AtomicWrite(path, JsonUtility.ToJson(settings, true));
                return true;
            }
            catch (Exception ex)
            {
                ReportFailure("save", path, ex, out error);
                return false;
            }
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

        private static UPilotUserSettingsData Parse(string json)
        {
            RequireSingleObject(json);
            using var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max);
            var root = XElement.Load(reader);
            while (reader.Read())
                if (reader.NodeType != XmlNodeType.Whitespace && reader.NodeType != XmlNodeType.SignificantWhitespace)
                    throw new InvalidDataException("通用设置只能包含一个 JSON 对象。");
            if ((string)root.Attribute("type") != "object")
                throw new InvalidDataException("通用设置必须是 JSON 对象。");
            var settings = new UPilotUserSettingsData();
            var foundSize = false;
            var foundConcurrency = false;
            foreach (var field in root.Elements())
            {
                var name = (string)field.Attribute("item") ?? field.Name.LocalName;
                if (name != "maxSegmentSizeBytes" && name != "maxConcurrentSegmentRequests") continue;
                if ((string)field.Attribute("type") != "number" ||
                    !long.TryParse(field.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidDataException(name + " 必须是整数。");
                if (name == "maxSegmentSizeBytes")
                {
                    if (foundSize) throw new InvalidDataException(name + " 重复。");
                    foundSize = true;
                    settings.maxSegmentSizeBytes = value;
                }
                else
                {
                    if (foundConcurrency) throw new InvalidDataException(name + " 重复。");
                    foundConcurrency = true;
                    settings.maxConcurrentSegmentRequests = checked((int)value);
                }
            }
            return settings;
        }

        private static void RequireSingleObject(string json)
        {
            // Unity's JSON reader can silently stop after the first root object.
            var text = json.Trim(' ', '\t', '\r', '\n');
            if (text.Length == 0 || text[0] != '{')
                throw new InvalidDataException("通用设置必须是 JSON 对象。");
            var depth = 0;
            var inString = false;
            for (var index = 0; index < text.Length; index++)
            {
                var ch = text[index];
                if (inString && ch == '\\') { index++; continue; }
                if (ch == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (ch == '{') depth++;
                else if (ch == '}' && --depth == 0)
                {
                    if (index != text.Length - 1)
                        throw new InvalidDataException("通用设置只能包含一个 JSON 对象。");
                    return;
                }
            }
            throw new InvalidDataException("通用设置 JSON 对象不完整。");
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
