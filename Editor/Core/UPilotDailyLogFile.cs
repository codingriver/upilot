using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace CodingRiver.UPilot
{
    /// <summary>Append-only local-calendar logs, governed by the single project retention setting.</summary>
    internal sealed class UPilotDailyLogFile
    {
        private static readonly object FileLock = new();
        private static readonly UTF8Encoding Utf8 = new(false);
        private readonly string _basePath;
        private readonly Func<int> _retention;
        private DateTime? _cleanedDate;
        private int _cleanedDays;

        internal UPilotDailyLogFile(string basePath, Func<int> retention = null)
        {
            _basePath = Path.GetFullPath(basePath);
            _retention = retention ?? (() => UPilotLoggingConfig.Normalize(UPilotProjectConfig.Current.logging?.retentionDays ?? 3));
        }

        internal string PathFor(DateTime date) => Path.Combine(Path.GetDirectoryName(_basePath),
            Path.GetFileNameWithoutExtension(_basePath) + "-" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

        internal bool Append(string text, DateTime today, Action<string, string, Exception> report = null)
        {
            string path = PathFor(today);
            report ??= ReportError;
            try
            {
                lock (FileLock)
                {
                    RequireSafePath(path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    RequireSafePath(path);
                    int days = UPilotLoggingConfig.Normalize(_retention());
                    if (_cleanedDate != today.Date || _cleanedDays != days)
                    {
                        _cleanedDate = today.Date;
                        _cleanedDays = days; // One cleanup attempt per date/effective setting.
                        CleanExpired(today.Date, days, report);
                    }
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    if (stream.Length > 0)
                    {
                        stream.Seek(-1, SeekOrigin.End);
                        if (stream.ReadByte() != '\n') stream.WriteByte((byte)'\n');
                    }
                    stream.Seek(0, SeekOrigin.End);
                    byte[] bytes = Utf8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                }
                Logger.FileDiagnosticRecovered(path);
                return true;
            }
            catch (Exception ex) { report("write", path, ex); return false; }
        }

        private bool TryDate(string path, out DateTime date)
        {
            string name = Path.GetFileName(path), prefix = Path.GetFileNameWithoutExtension(_basePath) + "-";
            date = default;
            return name.Length == prefix.Length + 14 && name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(".log", StringComparison.Ordinal)
                && DateTime.TryParseExact(name.Substring(prefix.Length, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date);
        }

        private void CleanExpired(DateTime today, int days, Action<string, string, Exception> report)
        {
            string directory = Path.GetDirectoryName(_basePath);
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    if (!TryDate(path, out var date) || date >= today.AddDays(1 - days)) continue;
                    try
                    {
                        if (!IsRegularFile(path)) continue;
                        RequireSafePath(path);
                        File.Delete(path);
                    }
                    catch (Exception ex) { report("cleanup", path, ex); }
                }
            }
            catch (Exception ex) { report("cleanup", directory, ex); }
        }

        internal static string ResolveExisting(string basePath, DateTime? today = null)
        {
            var log = new UPilotDailyLogFile(basePath);
            var date = (today ?? DateTime.Now).Date;
            string current = log.PathFor(date);
            try
            {
                RequireSafePath(Path.GetDirectoryName(basePath));
                if (File.Exists(current) && IsRegularFile(current)) return current;
                string latest = null;
                DateTime latestDate = DateTime.MinValue;
                if (Directory.Exists(Path.GetDirectoryName(basePath)))
                    foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(basePath)))
                        if (log.TryDate(path, out var candidate) && candidate <= date && candidate > latestDate && IsRegularFile(path))
                        { latest = path; latestDate = candidate; }
                if (latest != null) return latest;
            }
            catch (Exception ex) { ReportError("resolve", basePath, ex); }
            return current;
        }

        private static bool IsRegularFile(string path) =>
            (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;

        internal static void RequireSafePath(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Log path contains a symbolic link or junction: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static void ReportError(string operation, string path, Exception ex) =>
            Logger.ReportFileDiagnostic(operation, path, ex);
    }
}
