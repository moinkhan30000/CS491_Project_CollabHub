using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace RevitVersionControl.Services
{
    internal class SnapshotCacheEnvelope
    {
        public string ProjectId { get; set; }
        public string ModelId { get; set; }
        public string CommitId { get; set; }
        public DateTime CachedAtUtc { get; set; }
        public ElementSnapshot Snapshot { get; set; }
    }

    public static class SnapshotCacheService
    {
        private static readonly string CacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitVersionControl",
            "snapshot-cache");

        public static ElementSnapshot GetSnapshot(string projectId, string modelId, string commitId)
        {
            if (string.IsNullOrWhiteSpace(projectId)
                || string.IsNullOrWhiteSpace(modelId)
                || string.IsNullOrWhiteSpace(commitId))
            {
                return null;
            }

            try
            {
                string path = GetSnapshotPath(projectId, modelId, commitId);
                if (!File.Exists(path))
                    return null;

                string json = File.ReadAllText(path);
                var envelope = JsonConvert.DeserializeObject<SnapshotCacheEnvelope>(json);
                return envelope?.Snapshot;
            }
            catch
            {
                return null;
            }
        }

        public static void SaveSnapshot(string projectId, string modelId, string commitId, ElementSnapshot snapshot)
        {
            if (snapshot == null
                || string.IsNullOrWhiteSpace(projectId)
                || string.IsNullOrWhiteSpace(modelId)
                || string.IsNullOrWhiteSpace(commitId))
            {
                return;
            }

            try
            {
                string directory = GetModelCacheDirectory(projectId, modelId);
                Directory.CreateDirectory(directory);

                var envelope = new SnapshotCacheEnvelope
                {
                    ProjectId = projectId,
                    ModelId = modelId,
                    CommitId = commitId,
                    CachedAtUtc = DateTime.UtcNow,
                    Snapshot = snapshot,
                };

                string json = JsonConvert.SerializeObject(envelope);
                File.WriteAllText(GetSnapshotPath(projectId, modelId, commitId), json);
            }
            catch
            {
                // Ignore local cache persistence failures.
            }
        }

        private static string GetSnapshotPath(string projectId, string modelId, string commitId)
        {
            return Path.Combine(GetModelCacheDirectory(projectId, modelId), $"{commitId}.json");
        }

        private static string GetModelCacheDirectory(string projectId, string modelId)
        {
            string modelKey = ComputeStableHash(projectId + "|" + modelId);
            return Path.Combine(CacheRoot, modelKey);
        }

        private static string ComputeStableHash(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                byte[] hash = sha.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
