using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CodingRiver.UPilot.Automation
{
    public sealed partial class AutomationStepExecutor
    {
        internal string WriteTextArtifact(string runId, string instanceId, string kind, string fileName, string content)
        {
            string published = null;
            WriteCheckpoint(runId, instanceId, () =>
            {
                // All admission checks precede directory creation and file effects.
                RequireArtifactRegistration(kind);
                RequireArtifactFileName(fileName);
                if (content == null) throw new ArgumentNullException(nameof(content));
                var bytes = new UTF8Encoding(false, true).GetBytes(content);
                string digest = AutomationStepRunStore.HashText(content);
                string directory = Path.Combine(_report.DirectoryPath, "attachments", AutomationStepRunStore.HashText(instanceId));
                string path = Path.GetFullPath(Path.Combine(directory, fileName));
                var registered = _run.registeredArtifacts?.FirstOrDefault(a => SamePath(a.path, path));
                if (registered != null)
                {
                    var actual = RegisterArtifactCore(instanceId, kind, path);
                    if (actual.bytes != bytes.LongLength || actual.sha256 != digest)
                        throw new IOException("STEP_ARTIFACT_CONTENT_CONFLICT");
                    published = path;
                    return;
                }
                // Never adopt an unregistered file, including an orphan left by a failed save.
                if (File.Exists(path) || Directory.Exists(path)) throw new IOException("STEP_ARTIFACT_PATH_CONFLICT");
                AutomationReportWriter.PrepareArtifactDirectory(directory);
                string temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
                bool created = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true;
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    // File.Move without overwrite also rejects a name created concurrently.
                    File.Move(temporary, path);
                    created = false;
                    var artifact = RegisterArtifactCore(instanceId, kind, path);
                    if (artifact.bytes != bytes.LongLength || artifact.sha256 != digest)
                        throw new IOException("STEP_ARTIFACT_CONTENT_MISMATCH");
                    published = path;
                }
                catch (Exception writeError)
                {
                    if (created)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception cleanupError)
                        { throw new AggregateException("Artifact write and temporary cleanup failed.", writeError, cleanupError); }
                    }
                    throw;
                }
            }, "STEP_ARTIFACT_WRITE_FAILED");
            // WriteCheckpoint has durably saved the registration before exposing success.
            return published;
        }

        private static void RequireArtifactFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName == "." || fileName == ".."
                || fileName.EndsWith(".", StringComparison.Ordinal) || fileName.EndsWith(" ", StringComparison.Ordinal)
                || fileName.Any(c => c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0)
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(fileName))
                throw new ArgumentException("STEP_ARTIFACT_FILENAME_INVALID");
            string stem = fileName.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
                || ((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && stem.Length == 4 && ("123456789\u00b9\u00b2\u00b3".IndexOf(stem[3]) >= 0)))
                throw new ArgumentException("STEP_ARTIFACT_FILENAME_RESERVED");
        }
    }
}
