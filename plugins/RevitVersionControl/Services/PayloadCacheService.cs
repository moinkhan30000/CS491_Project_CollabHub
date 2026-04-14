using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RevitVersionControl.Services
{
    internal static class PayloadCacheService
    {
        private static readonly string PayloadRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitVersionControl",
            "payload-cache");

        private static readonly string StagingRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitVersionControl",
            "payload-staging");

        public static string GetPayloadPath(string projectId, string payloadId, string extension = ".rvt")
        {
            return Path.Combine(GetPayloadDirectory(projectId), payloadId + NormalizeExtension(extension));
        }

        public static string GetExistingPayloadPath(string projectId, string payloadId)
        {
            string projectDirectory = GetPayloadDirectory(projectId);
            if (!Directory.Exists(projectDirectory))
                return null;

            var matches = Directory.GetFiles(projectDirectory, payloadId + ".*");
            foreach (string match in matches)
            {
                if (string.Equals(Path.GetExtension(match), ".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                return match;
            }

            return null;
        }

        public static string GetStagingPayloadPath(string projectId, string extension = ".rvt")
        {
            string directory = Path.Combine(StagingRoot, SanitizeSegment(projectId));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, Guid.NewGuid().ToString("N") + NormalizeExtension(extension));
        }

        public static string ComputeContentHash(string filePath)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        public static void TryDeleteFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return;

            try
            {
                File.Delete(filePath);
            }
            catch
            {
            }
        }

        private static string GetPayloadDirectory(string projectId)
        {
            string directory = Path.Combine(PayloadRoot, SanitizeSegment(projectId));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string SanitizeSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unknown";

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                value = value.Replace(invalidChar, '_');

            return value;
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return ".rvt";

            return extension.StartsWith('.') ? extension : "." + extension;
        }
    }
}
