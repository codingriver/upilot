using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace codingriver.upilot.UIFlow
{
    public static class UIFlowUtility
    {
        public static string UtcNowString()
        {
            return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        public static int DurationMs(DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc)
        {
            double duration = (endedAtUtc - startedAtUtc).TotalMilliseconds;
            return duration <= 0 ? 0 : (int)Math.Round(duration, MidpointRounding.AwayFromZero);
        }

        public static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unnamed";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            return builder.ToString();
        }

        public static string NullIfWhiteSpace(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public static string EnsureRelativeTo(string rootPath, string filePath)
        {
            Uri rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(rootPath)));
            Uri fileUri = new Uri(Path.GetFullPath(filePath));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        public static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            if (path[path.Length - 1] == Path.DirectorySeparatorChar || path[path.Length - 1] == Path.AltDirectorySeparatorChar)
            {
                return path;
            }

            return path + Path.DirectorySeparatorChar;
        }
    }
}
