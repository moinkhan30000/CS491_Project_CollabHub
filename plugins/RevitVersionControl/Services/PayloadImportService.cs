using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitVersionControl.Services
{
    internal class PayloadImportService
    {
        private readonly Document _targetDocument;

        public PayloadImportService(Document targetDocument)
        {
            _targetDocument = targetDocument;
        }

        public CreationResult CreateFromPayload(string projectId, PayloadRef payloadRef, string marker, string repoGuid)
        {
            if (_targetDocument == null || string.IsNullOrWhiteSpace(projectId))
                return CreationResult.Failed("Payload copy requires a tracked target project.");

            if (payloadRef == null || string.IsNullOrWhiteSpace(payloadRef.PayloadId))
                return CreationResult.Failed("Payload metadata is missing.");

            if (string.IsNullOrWhiteSpace(marker))
                return CreationResult.Failed("Payload marker is missing.");

            string payloadPath = PayloadCacheService.GetExistingPayloadPath(projectId, payloadRef.PayloadId);
            if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
                return CreationResult.Failed($"Payload '{payloadRef.PayloadId}' is not available locally.");

            Document donorDocument = null;
            try
            {
                donorDocument = _targetDocument.Application.OpenDocumentFile(payloadPath);
                Element sourceElement = new FilteredElementCollector(donorDocument)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault(element =>
                        string.Equals(RepoGuidService.GetRepoGuid(element), marker, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(element.UniqueId, marker, StringComparison.OrdinalIgnoreCase));

                if (sourceElement == null)
                {
                    return CreationResult.Failed(
                        $"Payload marker '{marker}' was not found in donor file '{Path.GetFileName(payloadPath)}'.");
                }

                var copiedIds = ElementTransformUtils.CopyElements(
                    donorDocument,
                    new List<ElementId> { sourceElement.Id },
                    _targetDocument,
                    Transform.Identity,
                    new CopyPasteOptions());

                Element copiedElement = null;
                if (!string.IsNullOrWhiteSpace(repoGuid))
                    copiedElement = RepoGuidService.FindElement(_targetDocument, repoGuid, null);

                if (copiedElement == null)
                {
                    foreach (ElementId copiedId in copiedIds)
                    {
                        Element candidate = _targetDocument.GetElement(copiedId);
                        if (candidate == null)
                            continue;

                        if (string.Equals(candidate.Category?.Name, sourceElement.Category?.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            copiedElement = candidate;
                            break;
                        }

                        copiedElement ??= candidate;
                    }
                }

                if (copiedElement == null)
                    return CreationResult.Failed("Payload copy succeeded, but the copied element could not be identified.");

                if (!string.IsNullOrWhiteSpace(repoGuid))
                    RepoGuidService.SetRepoGuid(copiedElement, repoGuid);

                return CreationResult.Success(copiedElement);
            }
            catch (Exception ex)
            {
                return CreationResult.Failed($"Payload copy failed: {ex.Message}");
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
    }
}
