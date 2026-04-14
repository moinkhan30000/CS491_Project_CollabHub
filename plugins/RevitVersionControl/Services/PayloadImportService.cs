using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitVersionControl.Services
{
    internal class PayloadImportRequest
    {
        public string RequestKey { get; set; }
        public string Marker { get; set; }
        public string RepoGuid { get; set; }
        public string ElementId { get; set; }
        public string Category { get; set; }
    }

    internal class PayloadImportService
    {
        private readonly Document _targetDocument;

        public PayloadImportService(Document targetDocument)
        {
            _targetDocument = targetDocument;
        }

        public Dictionary<string, CreationResult> CreateFromPayloadBatch(
            string projectId,
            PayloadRef payloadRef,
            IEnumerable<PayloadImportRequest> requests)
        {
            var results = new Dictionary<string, CreationResult>(StringComparer.OrdinalIgnoreCase);
            var requestList = (requests ?? Enumerable.Empty<PayloadImportRequest>())
                .Where(request => request != null && !string.IsNullOrWhiteSpace(request.RequestKey))
                .GroupBy(request => request.RequestKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (_targetDocument == null || string.IsNullOrWhiteSpace(projectId))
            {
                foreach (var request in requestList)
                    results[request.RequestKey] = CreationResult.Failed("Payload copy requires a tracked target project.");
                return results;
            }

            if (payloadRef == null || string.IsNullOrWhiteSpace(payloadRef.PayloadId))
            {
                foreach (var request in requestList)
                    results[request.RequestKey] = CreationResult.Failed("Payload metadata is missing.");
                return results;
            }

            string payloadPath = PayloadCacheService.GetExistingPayloadPath(projectId, payloadRef.PayloadId);
            if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            {
                foreach (var request in requestList)
                    results[request.RequestKey] = CreationResult.Failed($"Payload '{payloadRef.PayloadId}' is not available locally.");
                return results;
            }

            Document donorDocument = null;
            try
            {
                donorDocument = _targetDocument.Application.OpenDocumentFile(payloadPath);
                var pending = new List<PendingPayloadImport>();

                foreach (var request in requestList)
                {
                    if (string.IsNullOrWhiteSpace(request.Marker))
                    {
                        results[request.RequestKey] = CreationResult.Failed("Payload marker is missing.");
                        continue;
                    }

                    Element existing = FindExistingTargetElement(request);
                    if (existing != null)
                    {
                        results[request.RequestKey] = CreationResult.Success(existing);
                        continue;
                    }

                    Element sourceElement = FindSourceElement(donorDocument, request.Marker);
                    if (sourceElement == null)
                    {
                        results[request.RequestKey] = CreationResult.Failed(
                            $"Payload marker '{request.Marker}' was not found in donor file '{Path.GetFileName(payloadPath)}'.");
                        continue;
                    }

                    pending.Add(new PendingPayloadImport
                    {
                        Request = request,
                        SourceElement = sourceElement,
                    });
                }

                if (pending.Count == 0)
                    return results;

                try
                {
                    CopyPendingBatch(donorDocument, pending, results);
                }
                catch (Exception)
                {
                    CopyPendingIndividually(donorDocument, pending, results);
                }

                return results;
            }
            finally
            {
                try
                {
                    donorDocument?.Close(false);
                }
                catch
                {
                    // Ignore donor close failures.
                }
            }
        }

        public CreationResult CreateFromPayload(string projectId, PayloadRef payloadRef, string marker, string repoGuid)
        {
            var request = new PayloadImportRequest
            {
                RequestKey = BuildRequestKey(repoGuid, marker),
                Marker = marker,
                RepoGuid = repoGuid,
            };

            var results = CreateFromPayloadBatch(projectId, payloadRef, new[] { request });
            if (results.TryGetValue(request.RequestKey, out CreationResult result))
                return result;

            return CreationResult.Failed("Payload copy failed unexpectedly.");
        }

        private void CopyPendingBatch(
            Document donorDocument,
            List<PendingPayloadImport> pending,
            Dictionary<string, CreationResult> results)
        {
            var sourceIds = pending
                .Select(item => item.SourceElement.Id)
                .Distinct()
                .ToList();

            ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElements(
                donorDocument,
                sourceIds,
                _targetDocument,
                Transform.Identity,
                new CopyPasteOptions());

            ResolveCopiedResults(copiedIds, pending, results);
        }

        private void CopyPendingIndividually(
            Document donorDocument,
            IEnumerable<PendingPayloadImport> pending,
            Dictionary<string, CreationResult> results)
        {
            foreach (var item in pending)
            {
                try
                {
                    ICollection<ElementId> copiedIds = ElementTransformUtils.CopyElements(
                        donorDocument,
                        new List<ElementId> { item.SourceElement.Id },
                        _targetDocument,
                        Transform.Identity,
                        new CopyPasteOptions());

                    ResolveCopiedResults(copiedIds, new List<PendingPayloadImport> { item }, results);
                }
                catch (Exception ex)
                {
                    results[item.Request.RequestKey] = CreationResult.Failed($"Payload copy failed: {ex.Message}");
                }
            }
        }

        private void ResolveCopiedResults(
            ICollection<ElementId> copiedIds,
            IEnumerable<PendingPayloadImport> pending,
            Dictionary<string, CreationResult> results)
        {
            var assignedCopiedIds = new HashSet<long>();
            var copiedCandidates = (copiedIds ?? Array.Empty<ElementId>())
                .Select(id => _targetDocument.GetElement(id))
                .Where(element => element != null)
                .ToList();
            bool bundleProducedCopiedElements = copiedCandidates.Count > 0;

            foreach (var item in pending)
            {
                Element targetElement = FindExistingTargetElement(item.Request);
                if (targetElement == null)
                    targetElement = FindBestCopiedCandidate(item, copiedCandidates, assignedCopiedIds);

                if (targetElement == null)
                {
                    results[item.Request.RequestKey] = bundleProducedCopiedElements
                        ? CreationResult.SatisfiedByBundle(
                            "Imported with payload bundle.")
                        : CreationResult.Failed(
                            "Payload copy succeeded, but the copied element could not be identified.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(item.Request.RepoGuid))
                    RepoGuidService.SetRepoGuid(targetElement, item.Request.RepoGuid);

                assignedCopiedIds.Add(targetElement.Id.Value);
                results[item.Request.RequestKey] = CreationResult.Success(targetElement);
            }
        }

        private Element FindExistingTargetElement(PayloadImportRequest request)
        {
            return RepoGuidService.FindElement(_targetDocument, request?.RepoGuid, request?.ElementId);
        }

        private static Element FindSourceElement(Document donorDocument, string marker)
        {
            return new FilteredElementCollector(donorDocument)
                .WhereElementIsNotElementType()
                .FirstOrDefault(element =>
                    string.Equals(RepoGuidService.GetRepoGuid(element), marker, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(element.UniqueId, marker, StringComparison.OrdinalIgnoreCase));
        }

        private Element FindBestCopiedCandidate(
            PendingPayloadImport item,
            IEnumerable<Element> copiedCandidates,
            HashSet<long> assignedCopiedIds)
        {
            if (item == null)
                return null;

            var candidates = (copiedCandidates ?? Enumerable.Empty<Element>())
                .Where(candidate => candidate != null && !assignedCopiedIds.Contains(candidate.Id.Value))
                .ToList();

            if (!string.IsNullOrWhiteSpace(item.Request.Category))
            {
                Element categoryMatch = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Category?.Name, item.Request.Category, StringComparison.OrdinalIgnoreCase));
                if (categoryMatch != null)
                    return categoryMatch;
            }

            if (!string.IsNullOrWhiteSpace(item.SourceElement?.Category?.Name))
            {
                Element sourceCategoryMatch = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Category?.Name, item.SourceElement.Category?.Name, StringComparison.OrdinalIgnoreCase));
                if (sourceCategoryMatch != null)
                    return sourceCategoryMatch;
            }

            return candidates.FirstOrDefault();
        }

        private static string BuildRequestKey(string repoGuid, string marker)
        {
            if (!string.IsNullOrWhiteSpace(repoGuid))
                return "repo:" + repoGuid;

            if (!string.IsNullOrWhiteSpace(marker))
                return "marker:" + marker;

            return Guid.NewGuid().ToString("D");
        }

        private class PendingPayloadImport
        {
            public PayloadImportRequest Request { get; set; }
            public Element SourceElement { get; set; }
        }
    }
}
