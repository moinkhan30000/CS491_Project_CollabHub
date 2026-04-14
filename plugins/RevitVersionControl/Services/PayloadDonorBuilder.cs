using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitVersionControl.Services
{
    internal class PayloadDonorBuildResult
    {
        public string DonorPath { get; set; }
        public bool IsTemporary { get; set; }
        public bool UsedFallback { get; set; }
        public string FallbackReason { get; set; }
    }

    internal static class PayloadDonorBuilder
    {
        public static PayloadDonorBuildResult Build(
            Document sourceDocument,
            string projectId,
            IEnumerable<Change> payloadChanges)
        {
            if (sourceDocument == null)
                throw new ArgumentNullException(nameof(sourceDocument));

            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project id is required.", nameof(projectId));

            string sourcePath = sourceDocument.PathName;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("A saved source document is required to build a donor payload.", sourcePath);

            string stagingPath = null;
            Document donorDocument = null;
            bool saveChanges = false;
            bool deleteStagingOnClose = false;

            try
            {
                var rootSeeds = ResolveRootSeeds(sourceDocument, payloadChanges);
                if (rootSeeds.Count == 0)
                {
                    return new PayloadDonorBuildResult
                    {
                        DonorPath = sourcePath,
                        IsTemporary = false,
                        UsedFallback = true,
                        FallbackReason = "No payload root elements were found in the current document."
                    };
                }

                stagingPath = PayloadCacheService.GetStagingPayloadPath(projectId, ".rvt");
                File.Copy(sourcePath, stagingPath, true);

                donorDocument = sourceDocument.Application.OpenDocumentFile(stagingPath);
                using (var transaction = new Transaction(donorDocument, "Build Payload Donor"))
                {
                    transaction.Start();

                    StampRootRepoGuids(donorDocument, rootSeeds);
                    var donorRoots = ResolveDonorRoots(donorDocument, rootSeeds);
                    if (donorRoots.Count == 0)
                        throw new InvalidOperationException("Unable to locate payload root elements in the donor copy.");

                    var keepIds = BuildKeepSet(donorDocument, donorRoots);
                    PruneDonor(donorDocument, keepIds);

                    transaction.Commit();
                }

                saveChanges = true;
                return new PayloadDonorBuildResult
                {
                    DonorPath = stagingPath,
                    IsTemporary = true
                };
            }
            catch (Exception ex)
            {
                deleteStagingOnClose = true;

                return new PayloadDonorBuildResult
                {
                    DonorPath = sourcePath,
                    IsTemporary = false,
                    UsedFallback = true,
                    FallbackReason = ex.Message
                };
            }
            finally
            {
                if (donorDocument != null)
                {
                    try
                    {
                        donorDocument.Close(saveChanges);
                    }
                    catch
                    {
                    }
                }

                if (deleteStagingOnClose && !string.IsNullOrWhiteSpace(stagingPath))
                    PayloadCacheService.TryDeleteFile(stagingPath);
            }
        }

        private static List<PayloadRootSeed> ResolveRootSeeds(Document sourceDocument, IEnumerable<Change> payloadChanges)
        {
            return (payloadChanges ?? Enumerable.Empty<Change>())
                .Select(change =>
                {
                    Element sourceElement = RepoGuidService.FindElement(sourceDocument, change?.RepoGuid, change?.ElementId);
                    if (sourceElement == null)
                        return null;

                    return new PayloadRootSeed
                    {
                        UniqueId = sourceElement.UniqueId,
                        RepoGuid = change.RepoGuid ?? RepoGuidService.GetRepoGuid(sourceElement),
                    };
                })
                .Where(seed => seed != null && !string.IsNullOrWhiteSpace(seed.UniqueId))
                .GroupBy(seed => seed.UniqueId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        }

        private static void StampRootRepoGuids(Document donorDocument, IEnumerable<PayloadRootSeed> rootSeeds)
        {
            foreach (var rootSeed in rootSeeds ?? Enumerable.Empty<PayloadRootSeed>())
            {
                if (string.IsNullOrWhiteSpace(rootSeed.RepoGuid))
                    continue;

                Element donorElement = SafeGetElementByUniqueId(donorDocument, rootSeed.UniqueId);
                if (donorElement == null)
                    continue;

                if (string.Equals(RepoGuidService.GetRepoGuid(donorElement), rootSeed.RepoGuid, StringComparison.OrdinalIgnoreCase))
                    continue;

                RepoGuidService.SetRepoGuid(donorElement, rootSeed.RepoGuid);
            }
        }

        private static List<Element> ResolveDonorRoots(Document donorDocument, IEnumerable<PayloadRootSeed> rootSeeds)
        {
            var donorRoots = new List<Element>();
            foreach (var rootSeed in rootSeeds ?? Enumerable.Empty<PayloadRootSeed>())
            {
                Element donorElement = SafeGetElementByUniqueId(donorDocument, rootSeed.UniqueId);
                if (donorElement != null)
                    donorRoots.Add(donorElement);
            }

            return donorRoots;
        }

        private static HashSet<long> BuildKeepSet(Document donorDocument, IEnumerable<Element> donorRoots)
        {
            var keepIds = new HashSet<long>();
            var queue = new Queue<Element>();

            foreach (var root in donorRoots ?? Enumerable.Empty<Element>())
            {
                if (root == null)
                    continue;

                if (keepIds.Add(root.Id.Value))
                    queue.Enqueue(root);
            }

            while (queue.Count > 0)
            {
                Element current = queue.Dequeue();
                EnqueueElement(donorDocument, current.GetTypeId(), keepIds, queue);
                EnqueueElement(donorDocument, current.LevelId, keepIds, queue);

                if (current is FamilyInstance familyInstance)
                {
                    EnqueueElement(keepIds, queue, familyInstance.Host);
                    EnqueueElement(keepIds, queue, familyInstance.SuperComponent);

                    foreach (ElementId subComponentId in familyInstance.GetSubComponentIds() ?? Array.Empty<ElementId>())
                        EnqueueElement(donorDocument, subComponentId, keepIds, queue);
                }

                try
                {
                    foreach (ElementId dependentId in current.GetDependentElements(null) ?? Array.Empty<ElementId>())
                        EnqueueElement(donorDocument, dependentId, keepIds, queue);
                }
                catch
                {
                }
            }

            return keepIds;
        }

        private static void PruneDonor(Document donorDocument, HashSet<long> keepIds)
        {
            var deleteIds = new List<ElementId>();
            var candidates = new FilteredElementCollector(donorDocument)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .ToElements();

            foreach (Element candidate in candidates)
            {
                if (!CanPruneElement(candidate, keepIds))
                    continue;

                deleteIds.Add(candidate.Id);
            }

            if (deleteIds.Count > 0)
                donorDocument.Delete(deleteIds);
        }

        private static bool CanPruneElement(Element element, HashSet<long> keepIds)
        {
            if (element == null)
                return false;

            if (keepIds.Contains(element.Id.Value))
                return false;

            if (element.Pinned)
                return false;

            if (element is Level || element is Grid || element is ProjectInfo || element is BasePoint)
                return false;

            if (element is View || element is ViewSheet || element is Viewport)
                return false;

            if (element is SketchPlane || element is ReferencePlane)
                return false;

            if (element is RevitLinkInstance || element is ImportInstance)
                return false;

            Category category = element.Category;
            if (category == null)
                return false;

            return category.CategoryType == CategoryType.Model;
        }

        private static void EnqueueElement(Document donorDocument, ElementId elementId, HashSet<long> keepIds, Queue<Element> queue)
        {
            if (elementId == null || elementId == ElementId.InvalidElementId)
                return;

            Element element = donorDocument.GetElement(elementId);
            EnqueueElement(keepIds, queue, element);
        }

        private static void EnqueueElement(HashSet<long> keepIds, Queue<Element> queue, Element element)
        {
            if (element == null)
                return;

            if (keepIds.Add(element.Id.Value))
                queue.Enqueue(element);
        }

        private static Element SafeGetElementByUniqueId(Document document, string uniqueId)
        {
            if (document == null || string.IsNullOrWhiteSpace(uniqueId))
                return null;

            try
            {
                return document.GetElement(uniqueId);
            }
            catch
            {
                return null;
            }
        }

        private class PayloadRootSeed
        {
            public string UniqueId { get; set; }
            public string RepoGuid { get; set; }
        }
    }
}
