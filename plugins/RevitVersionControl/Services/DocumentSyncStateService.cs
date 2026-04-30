using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace RevitVersionControl.Services
{
    public class DocumentSyncState
    {
        public string DocumentPath { get; set; }
        public string ProjectId { get; set; }
        public string ModelId { get; set; }
        public string CurrentCommitId { get; set; }
        public string CurrentBranchName { get; set; }
        public string MergeParentCommitId { get; set; }
        public DateTime LastSyncedAtUtc { get; set; }
        public DateTime? LastSyncedFileWriteUtc { get; set; }
    }

    public class ProjectSyncHint
    {
        public string ProjectId { get; set; }
        public string ModelId { get; set; }
        public string CurrentCommitId { get; set; }
        public string CurrentBranchName { get; set; }
        public string MergeParentCommitId { get; set; }
        public string LastKnownDocumentPath { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
    }

    public class AcceptedDocumentHint
    {
        public string DocumentPath { get; set; }
        public string ProjectId { get; set; }
        public DateTime AcceptedAtUtc { get; set; }
    }

    public class DocumentSyncStatus
    {
        public DocumentSyncState State { get; set; }
        public bool HasUnsavedChanges { get; set; }
        public bool HasSavedChangesSinceSync { get; set; }

        public bool HasTrackedCommit =>
            State != null
            && !string.IsNullOrWhiteSpace(State.ProjectId)
            && !string.IsNullOrWhiteSpace(State.CurrentCommitId);

        public bool HasLocalChanges => HasUnsavedChanges || HasSavedChangesSinceSync;

        public string Summary
        {
            get
            {
                if (!HasTrackedCommit)
                    return "Current synced version: unknown";

                string commitText = State.CurrentCommitId.Length > 8
                    ? State.CurrentCommitId.Substring(0, 8)
                    : State.CurrentCommitId;

                if (HasUnsavedChanges && HasSavedChangesSinceSync)
                    return $"Current synced version: {commitText} (unsaved and saved local changes detected)";

                if (HasUnsavedChanges)
                    return $"Current synced version: {commitText} (unsaved local changes detected)";

                if (HasSavedChangesSinceSync)
                    return $"Current synced version: {commitText} (saved local changes since last sync)";

                return $"Current synced version: {commitText} (clean)";
            }
        }
    }

    public static class DocumentSyncStateService
    {
        private static readonly string BaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitVersionControl");

        private static readonly string LegacyStateFilePath = Path.Combine(BaseDirectory, "document-sync-state.json");
        private static readonly string LegacyProjectHintFilePath = Path.Combine(BaseDirectory, "project-sync-hints.json");
        private static readonly string LegacyAcceptedHintsFilePath = Path.Combine(BaseDirectory, "accepted-documents.json");

        public static DocumentSyncState GetState(string documentPath)
        {
            if (string.IsNullOrWhiteSpace(documentPath))
                return null;

            var store = LoadStore();
            string key = NormalizePath(documentPath);
            return store.TryGetValue(key, out var state) ? state : null;
        }

        public static DocumentSyncState GetStateForProject(string documentPath, string projectId)
        {
            if (string.IsNullOrWhiteSpace(documentPath))
                return null;

            var exactState = GetState(documentPath);
            if (exactState != null
                && (string.IsNullOrWhiteSpace(projectId)
                    || string.Equals(exactState.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)))
            {
                return exactState;
            }

            if (string.IsNullOrWhiteSpace(projectId))
                return exactState;

            try
            {
                string currentFileName = Path.GetFileName(documentPath);
                if (string.IsNullOrWhiteSpace(currentFileName))
                    return exactState;

                var store = LoadStore();
                DocumentSyncState bestMatch = null;
                foreach (var entry in store.Values)
                {
                    if (entry == null
                        || !string.Equals(entry.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(
                        Path.GetFileName(entry.DocumentPath ?? string.Empty),
                        currentFileName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        bestMatch = entry;
                        break;
                    }
                }

                if (bestMatch != null)
                {
                    SaveState(documentPath, bestMatch.ProjectId, bestMatch.ModelId, bestMatch.CurrentCommitId, bestMatch.CurrentBranchName);
                    return GetState(documentPath);
                }

                var projectHint = GetProjectHint(projectId);
                if (projectHint != null && !string.IsNullOrWhiteSpace(projectHint.CurrentCommitId))
                {
                    SaveState(documentPath, projectHint.ProjectId, projectHint.ModelId, projectHint.CurrentCommitId, projectHint.CurrentBranchName);
                    return GetState(documentPath);
                }
            }
            catch
            {
                // Fall through to exact-match result.
            }

            return exactState;
        }

        public static DocumentSyncStatus GetStatus(string documentPath, bool hasUnsavedChanges)
        {
            var state = GetState(documentPath);
            return new DocumentSyncStatus
            {
                State = state,
                HasUnsavedChanges = hasUnsavedChanges,
                HasSavedChangesSinceSync = HasSavedChangesSinceSync(documentPath, state),
            };
        }

        public static DocumentSyncStatus GetStatusForProject(string documentPath, string projectId, bool hasUnsavedChanges)
        {
            var state = GetStateForProject(documentPath, projectId);
            return new DocumentSyncStatus
            {
                State = state,
                HasUnsavedChanges = hasUnsavedChanges,
                HasSavedChangesSinceSync = HasSavedChangesSinceSync(documentPath, state),
            };
        }

        public static void SaveState(string documentPath, string projectId, string modelId, string currentCommitId, string currentBranchName = "main", string mergeParentCommitId = null)
        {
            if (string.IsNullOrWhiteSpace(documentPath)
                || string.IsNullOrWhiteSpace(projectId)
                || string.IsNullOrWhiteSpace(currentCommitId))
            {
                return;
            }

            var store = LoadStore();
            string normalizedPath = NormalizePath(documentPath);
            store[normalizedPath] = new DocumentSyncState
            {
                DocumentPath = documentPath,
                ProjectId = projectId,
                ModelId = string.IsNullOrWhiteSpace(modelId) ? documentPath : modelId,
                CurrentCommitId = currentCommitId,
                CurrentBranchName = currentBranchName ?? "main",
                MergeParentCommitId = mergeParentCommitId,
                LastSyncedAtUtc = DateTime.UtcNow,
                LastSyncedFileWriteUtc = GetSafeLastWriteTimeUtc(documentPath),
            };

            PersistStore(store);
            SaveProjectHint(documentPath, projectId, modelId, currentCommitId, currentBranchName, mergeParentCommitId);
        }

        public static void SaveAcceptedDocumentHint(string documentPath, string projectId)
        {
            if (string.IsNullOrWhiteSpace(documentPath) || string.IsNullOrWhiteSpace(projectId))
                return;

            try
            {
                var hints = LoadAcceptedDocumentHints();
                hints[NormalizePath(documentPath)] = new AcceptedDocumentHint
                {
                    DocumentPath = documentPath,
                    ProjectId = projectId,
                    AcceptedAtUtc = DateTime.UtcNow,
                };
                PersistAcceptedDocumentHints(hints);
            }
            catch
            {
                // Ignore local accepted-hint persistence failures.
            }
        }

        public static bool WasAcceptedDocumentForProject(string documentPath, string projectId)
        {
            if (string.IsNullOrWhiteSpace(documentPath) || string.IsNullOrWhiteSpace(projectId))
                return false;

            try
            {
                var hints = LoadAcceptedDocumentHints();
                if (!hints.TryGetValue(NormalizePath(documentPath), out var hint) || hint == null)
                    return false;

                return string.Equals(hint.ProjectId, projectId, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasSavedChangesSinceSync(string documentPath, DocumentSyncState state)
        {
            if (state == null || !state.LastSyncedFileWriteUtc.HasValue)
                return false;

            var currentWriteTime = GetSafeLastWriteTimeUtc(documentPath);
            if (!currentWriteTime.HasValue)
                return false;

            return currentWriteTime.Value > state.LastSyncedFileWriteUtc.Value.AddSeconds(1);
        }

        private static Dictionary<string, DocumentSyncState> LoadStore()
        {
            try
            {
                string stateFilePath = GetStateFilePath();
                if (!File.Exists(stateFilePath))
                {
                    stateFilePath = LegacyStateFilePath;
                    if (!File.Exists(stateFilePath))
                        return new Dictionary<string, DocumentSyncState>(StringComparer.OrdinalIgnoreCase);
                }

                string json = File.ReadAllText(stateFilePath);
                var store = JsonConvert.DeserializeObject<Dictionary<string, DocumentSyncState>>(json);
                return store ?? new Dictionary<string, DocumentSyncState>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, DocumentSyncState>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveProjectHint(string documentPath, string projectId, string modelId, string currentCommitId, string currentBranchName = "main", string mergeParentCommitId = null)
        {
            try
            {
                var hints = LoadProjectHints();
                hints[projectId] = new ProjectSyncHint
                {
                    ProjectId = projectId,
                    ModelId = string.IsNullOrWhiteSpace(modelId) ? documentPath : modelId,
                    CurrentCommitId = currentCommitId,
                    CurrentBranchName = currentBranchName ?? "main",
                    MergeParentCommitId = mergeParentCommitId,
                    LastKnownDocumentPath = documentPath,
                    LastUpdatedUtc = DateTime.UtcNow,
                };

                PersistProjectHints(hints);
            }
            catch
            {
                // Ignore project-hint persistence failures.
            }
        }

        public static ProjectSyncHint GetProjectHint(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return null;

            var hints = LoadProjectHints();
            return hints.TryGetValue(projectId, out var hint) ? hint : null;
        }

        private static Dictionary<string, ProjectSyncHint> LoadProjectHints()
        {
            try
            {
                string projectHintFilePath = GetProjectHintFilePath();
                if (!File.Exists(projectHintFilePath))
                {
                    projectHintFilePath = LegacyProjectHintFilePath;
                    if (!File.Exists(projectHintFilePath))
                        return new Dictionary<string, ProjectSyncHint>(StringComparer.OrdinalIgnoreCase);
                }

                string json = File.ReadAllText(projectHintFilePath);
                var hints = JsonConvert.DeserializeObject<Dictionary<string, ProjectSyncHint>>(json);
                return hints ?? new Dictionary<string, ProjectSyncHint>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, ProjectSyncHint>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void PersistProjectHints(Dictionary<string, ProjectSyncHint> hints)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                string json = JsonConvert.SerializeObject(hints, Formatting.Indented);
                File.WriteAllText(GetProjectHintFilePath(), json);
            }
            catch
            {
                // Ignore local hint persistence failures.
            }
        }

        private static void PersistStore(Dictionary<string, DocumentSyncState> store)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                string json = JsonConvert.SerializeObject(store, Formatting.Indented);
                File.WriteAllText(GetStateFilePath(), json);
            }
            catch
            {
                // Ignore local state persistence failures.
            }
        }

        private static Dictionary<string, AcceptedDocumentHint> LoadAcceptedDocumentHints()
        {
            try
            {
                string acceptedHintsFilePath = GetAcceptedHintsFilePath();
                if (!File.Exists(acceptedHintsFilePath))
                {
                    acceptedHintsFilePath = LegacyAcceptedHintsFilePath;
                    if (!File.Exists(acceptedHintsFilePath))
                        return new Dictionary<string, AcceptedDocumentHint>(StringComparer.OrdinalIgnoreCase);
                }

                string json = File.ReadAllText(acceptedHintsFilePath);
                var hints = JsonConvert.DeserializeObject<Dictionary<string, AcceptedDocumentHint>>(json);
                return hints ?? new Dictionary<string, AcceptedDocumentHint>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, AcceptedDocumentHint>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void PersistAcceptedDocumentHints(Dictionary<string, AcceptedDocumentHint> hints)
        {
            try
            {
                Directory.CreateDirectory(BaseDirectory);
                string json = JsonConvert.SerializeObject(hints, Formatting.Indented);
                File.WriteAllText(GetAcceptedHintsFilePath(), json);
            }
            catch
            {
                // Ignore local accepted-hint persistence failures.
            }
        }

        private static DateTime? GetSafeLastWriteTimeUtc(string documentPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(documentPath) || !File.Exists(documentPath))
                    return null;

                return File.GetLastWriteTimeUtc(documentPath);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizePath(string documentPath)
        {
            try
            {
                return Path.GetFullPath(documentPath)
                    .Trim()
                    .ToLowerInvariant();
            }
            catch
            {
                return (documentPath ?? string.Empty).Trim().ToLowerInvariant();
            }
        }

        private static string GetStateFilePath()
        {
            return Path.Combine(BaseDirectory, $"document-sync-state.{GetCurrentUserScope()}.json");
        }

        private static string GetProjectHintFilePath()
        {
            return Path.Combine(BaseDirectory, $"project-sync-hints.{GetCurrentUserScope()}.json");
        }

        private static string GetAcceptedHintsFilePath()
        {
            return Path.Combine(BaseDirectory, $"accepted-documents.{GetCurrentUserScope()}.json");
        }

        private static string GetCurrentUserScope()
        {
            string email = ApiClient.Instance.CurrentUserEmail;
            if (string.IsNullOrWhiteSpace(email))
                return "anonymous";

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                email = email.Replace(invalidChar, '_');

            email = email.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(email) ? "anonymous" : email;
        }
    }
}
