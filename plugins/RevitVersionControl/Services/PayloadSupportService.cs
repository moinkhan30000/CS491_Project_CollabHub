using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
    internal class PayloadPreparationResult
    {
        public bool Success { get; set; } = true;
        public string ErrorMessage { get; set; }
        public List<PayloadRef> PayloadRefs { get; } = new List<PayloadRef>();
    }

    internal static class PayloadSupportService
    {
        internal const string SaveRequiredMessage =
            "This publish includes new payload-backed additions that require a saved donor model. Save the Revit file, then publish again.";

        public static PayloadPreparationResult PreparePayloadBackedChanges(
            Document document,
            string projectId,
            List<Change> changes,
            bool hasUnsavedChanges)
        {
            var result = new PayloadPreparationResult();
            var payloadChanges = (changes ?? new List<Change>())
                .Where(IsPayloadBackedAddition)
                .ToList();

            if (payloadChanges.Count == 0)
                return result;

            if (document == null || string.IsNullOrWhiteSpace(projectId))
            {
                result.Success = false;
                result.ErrorMessage = "Payload-backed publish requires an open tracked document and project.";
                return result;
            }

            if (hasUnsavedChanges)
            {
                result.Success = false;
                result.ErrorMessage = SaveRequiredMessage;
                return result;
            }

            if (string.IsNullOrWhiteSpace(document.PathName) || !File.Exists(document.PathName))
            {
                result.Success = false;
                result.ErrorMessage = "Payload-backed publish requires a saved document on disk.";
                return result;
            }

            try
            {
                var donorBuild = PayloadDonorBuilder.Build(document, projectId, payloadChanges);
                string donorPath = donorBuild?.DonorPath;
                bool deleteDonorAfterUse = donorBuild?.IsTemporary == true
                                           && !string.Equals(donorPath, document.PathName, StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(donorPath) || !File.Exists(donorPath))
                {
                    result.Success = false;
                    result.ErrorMessage = "Failed to prepare donor payload source.";
                    return result;
                }

                string contentHash = PayloadCacheService.ComputeContentHash(donorPath);
                var categories = payloadChanges
                    .Select(change => change.Category)
                    .Where(category => !string.IsNullOrWhiteSpace(category))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var markers = payloadChanges
                    .Select(GetPayloadMarker)
                    .Where(marker => !string.IsNullOrWhiteSpace(marker))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                PayloadRef payloadRef = null;
                var statusTask = Task.Run(async () =>
                    await ApiClient.Instance.GetPayloadStatusAsync(projectId, contentHash));
                var status = statusTask.GetAwaiter().GetResult();
                if (status != null && status.Exists && status.Payload != null)
                    payloadRef = status.Payload;

                if (payloadRef == null)
                {
                    try
                    {
                        var uploadTask = Task.Run(async () =>
                            await ApiClient.Instance.UploadPayloadAsync(projectId, contentHash, donorPath, categories, markers));
                        payloadRef = uploadTask.GetAwaiter().GetResult();
                    }
                    finally
                    {
                        if (deleteDonorAfterUse)
                            PayloadCacheService.TryDeleteFile(donorPath);
                    }
                }
                else if (deleteDonorAfterUse)
                {
                    PayloadCacheService.TryDeleteFile(donorPath);
                }

                if (payloadRef == null)
                {
                    result.Success = false;
                    result.ErrorMessage = string.IsNullOrWhiteSpace(ApiClient.Instance.LastError)
                        ? "Failed to upload donor payload."
                        : $"Failed to upload donor payload.\n\n{ApiClient.Instance.LastError}";
                    return result;
                }

                result.PayloadRefs.Add(payloadRef);
                foreach (var change in payloadChanges)
                {
                    change.NewData ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    change.NewData["createStrategy"] = "payload_copy";
                    change.NewData["payloadId"] = payloadRef.PayloadId;
                    change.NewData["payloadContentHash"] = payloadRef.ContentHash;
                    change.NewData["payloadStorageUrl"] = payloadRef.StorageUrl;
                    change.NewData["payloadMarker"] = GetPayloadMarker(change);
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = $"Failed to prepare donor payload.\n\n{ex.Message}";
                return result;
            }
        }

        public static bool HasUnsavedPayloadBackedAdditions(Document document, ElementSnapshot baselineSnapshot)
        {
            if (document == null)
                return false;

            if (baselineSnapshot == null)
                return false;

            var baselineKeys = BuildTrackingKeys(baselineSnapshot);
            var collector = new FilteredElementCollector(document)
                .WhereElementIsNotElementType();

            foreach (Element element in collector)
            {
                string category = element?.Category?.Name;
                if (!SyncCategoryRules.ShouldUsePayloadForAddedElement(category))
                    continue;

                string trackingKey = BuildTrackingKey(RepoGuidService.GetRepoGuid(element), element?.UniqueId);
                if (!string.IsNullOrWhiteSpace(trackingKey) && !baselineKeys.Contains(trackingKey))
                    return true;
            }

            return false;
        }

        public static bool EnsurePayloadsAvailable(string projectId, IEnumerable<Change> changes, out string errorMessage)
        {
            errorMessage = null;
            var requiredPayloads = GetRequiredPayloads(changes).ToList();
            if (requiredPayloads.Count == 0)
                return true;

            if (string.IsNullOrWhiteSpace(projectId))
            {
                errorMessage = "Payload-backed changes require a tracked project context before they can be applied.";
                return false;
            }

            foreach (var reference in requiredPayloads)
            {
                string existingPath = PayloadCacheService.GetExistingPayloadPath(projectId, reference.PayloadId);
                if (!string.IsNullOrWhiteSpace(existingPath) && File.Exists(existingPath))
                    continue;

                string extension = ".rvt";
                if (!string.IsNullOrWhiteSpace(reference.StorageUrl))
                    extension = Path.GetExtension(reference.StorageUrl);

                string destinationPath = PayloadCacheService.GetPayloadPath(projectId, reference.PayloadId, extension);
                var downloadTask = Task.Run(async () =>
                    await ApiClient.Instance.DownloadPayloadAsync(projectId, reference.PayloadId, destinationPath));
                bool downloaded = downloadTask.GetAwaiter().GetResult();

                if (!downloaded)
                {
                    errorMessage = string.IsNullOrWhiteSpace(ApiClient.Instance.LastError)
                        ? $"Failed to download payload '{reference.PayloadId}'."
                        : $"Failed to download payload '{reference.PayloadId}'.\n\n{ApiClient.Instance.LastError}";
                    return false;
                }
            }

            return true;
        }

        public static bool TryGetPayloadReference(Change change, out PayloadRef payloadRef, out string marker)
        {
            payloadRef = null;
            marker = null;

            if (change?.NewData == null)
                return false;

            if (!change.NewData.TryGetValue("payloadId", out object payloadIdValue)
                && !change.NewData.TryGetValue("payload", out payloadIdValue))
            {
                return false;
            }

            string payloadId = payloadIdValue?.ToString();
            if (string.IsNullOrWhiteSpace(payloadId))
                return false;

            change.NewData.TryGetValue("payloadStorageUrl", out object storageUrlValue);
            change.NewData.TryGetValue("payloadContentHash", out object contentHashValue);
            change.NewData.TryGetValue("payloadMarker", out object markerValue);

            marker = markerValue?.ToString();
            payloadRef = new PayloadRef
            {
                PayloadId = payloadId,
                StorageUrl = storageUrlValue?.ToString(),
                ContentHash = contentHashValue?.ToString(),
                Categories = new List<string>(),
                Markers = string.IsNullOrWhiteSpace(marker)
                    ? new List<string>()
                    : new List<string> { marker },
            };
            return true;
        }

        private static IEnumerable<PayloadRef> GetRequiredPayloads(IEnumerable<Change> changes)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in changes ?? Enumerable.Empty<Change>())
            {
                if (!TryGetPayloadReference(change, out PayloadRef payloadRef, out _))
                    continue;

                if (payloadRef == null || string.IsNullOrWhiteSpace(payloadRef.PayloadId))
                    continue;

                if (seen.Add(payloadRef.PayloadId))
                    yield return payloadRef;
            }
        }

        private static bool IsPayloadBackedAddition(Change change)
        {
            if (change == null || !string.Equals(change.ChangeType, "added", StringComparison.OrdinalIgnoreCase))
                return false;

            string category = change.Category;
            if (string.IsNullOrWhiteSpace(category) && change.NewData != null)
                category = change.NewData.TryGetValue("category", out object categoryValue)
                    ? categoryValue?.ToString()
                    : null;

            return SyncCategoryRules.ShouldUsePayloadForAddedElement(category);
        }

        private static string GetPayloadMarker(Change change)
        {
            return change?.ElementId ?? change?.RepoGuid;
        }

        private static HashSet<string> BuildTrackingKeys(ElementSnapshot snapshot)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (snapshot?.Elements == null)
                return keys;

            foreach (object rawElement in snapshot.Elements)
            {
                try
                {
                    JObject element = rawElement as JObject ?? JObject.FromObject(rawElement);
                    string trackingKey = BuildTrackingKey(
                        element["repoGuid"]?.ToString(),
                        element["id"]?.ToString());
                    if (!string.IsNullOrWhiteSpace(trackingKey))
                        keys.Add(trackingKey);
                }
                catch
                {
                    // Ignore malformed snapshot items while checking the fast save guard.
                }
            }

            return keys;
        }

        private static string BuildTrackingKey(string repoGuid, string uniqueId)
        {
            if (!string.IsNullOrWhiteSpace(repoGuid))
                return "repo:" + repoGuid;

            if (!string.IsNullOrWhiteSpace(uniqueId))
                return "id:" + uniqueId;

            return null;
        }
    }
}
