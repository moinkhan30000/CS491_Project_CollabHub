using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
    public class DiffViewBuildRequest
    {
        public string ProjectId { get; set; }
        public string BaseCommitId { get; set; }
        public string TargetCommitId { get; set; }
        public DiffResult Diff { get; set; }
        public ElementSnapshot BaseSnapshot { get; set; }
        public Guid SessionId { get; set; }
        public bool OrderSwapped { get; set; }
    }

    public class DiffViewChangeRow
    {
        public string ChangeType { get; set; }      // added / modified / deleted
        public string Category { get; set; }
        public string TypeName { get; set; }
        public string RepoGuid { get; set; }
        public string ShortRepoGuid =>
            string.IsNullOrEmpty(RepoGuid) || RepoGuid.Length < 8
                ? RepoGuid ?? string.Empty
                : RepoGuid.Substring(0, 8);
        public ElementId LiveElementId { get; set; }
        public ElementId GhostElementId { get; set; }
        public bool ListOnly { get; set; }
        public string Note { get; set; }
    }

    public class DiffViewBuildResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public ElementId DiffViewId { get; set; }
        public Guid SessionId { get; set; }
        public List<DiffViewChangeRow> Rows { get; set; } = new List<DiffViewChangeRow>();
        public int AddedCount { get; set; }
        public int ModifiedCount { get; set; }
        public int DeletedCount { get; set; }
        public string BaseShort { get; set; }
        public string TargetShort { get; set; }
        public bool OrderSwapped { get; set; }
    }

    public static class DiffViewService
    {
        public const string DiffViewNamePrefix = "Diff: ";
        public const int MaxChangesHardCap = 20000;
        public const int MaxChangesWarn = 5000;

        public static async Task<(DiffResult diff, ElementSnapshot baseSnapshot, string error)> FetchDiffAsync(
            string projectId, string baseCommitId, string targetCommitId)
        {
            var api = ApiClient.Instance;

            var diffTask = api.GetDiffAsync(projectId, baseCommitId, targetCommitId);
            var snapshotTask = api.GetSnapshotAsync(projectId, baseCommitId);

            await Task.WhenAll(diffTask, snapshotTask).ConfigureAwait(false);

            var diff = diffTask.Result;
            var snapshot = snapshotTask.Result;

            if (diff == null)
            {
                return (null, null, string.IsNullOrWhiteSpace(api.LastError)
                    ? "Failed to fetch diff."
                    : $"Failed to fetch diff: {api.LastError}");
            }

            return (diff, snapshot, null);
        }

        public static DiffViewBuildResult Build(UIApplication uiApp, DiffViewBuildRequest request)
        {
            var result = new DiffViewBuildResult
            {
                SessionId = request.SessionId,
                BaseShort = ShortenCommitId(request.BaseCommitId),
                TargetShort = ShortenCommitId(request.TargetCommitId),
                OrderSwapped = request.OrderSwapped
            };

            try
            {
                Document doc = uiApp.ActiveUIDocument.Document;
                var changes = request.Diff?.Changes ?? new List<Change>();

                if (changes.Count > MaxChangesHardCap)
                {
                    result.Success = false;
                    result.Message = $"Too many changes ({changes.Count}) for in-viewport visualization. Use the side pane list instead.";
                    return result;
                }

                using (var tx = new Transaction(doc, "RVCS Build Diff View"))
                {
                    tx.Start();

                    // Wipe prior session ghosts before (re)building.
                    DeleteAllRvcsGhosts(doc, sessionFilter: null);

                    // Resolve / create the diff view.
                    View3D diffView = EnsureDiffView(doc, result.BaseShort, result.TargetShort);
                    if (diffView == null)
                    {
                        tx.RollBack();
                        result.Success = false;
                        result.Message = "Failed to create diff view (no 3D ViewFamilyType available).";
                        return result;
                    }
                    result.DiffViewId = diffView.Id;

                    // Material setup.
                    var addedMaterialId = DiffViewMaterialCache.GetMaterialId(doc, DiffChangeKind.Added);
                    var modifiedMaterialId = DiffViewMaterialCache.GetMaterialId(doc, DiffChangeKind.Modified);
                    var deletedMaterialId = DiffViewMaterialCache.GetMaterialId(doc, DiffChangeKind.Deleted);
                    var solidFillId = DiffViewMaterialCache.GetSolidFillPatternId(doc);

                    var lookup = new ElementLookup(doc);
                    var ghostBuilder = new GhostBuilder(doc);
                    var snapshotIndex = BuildSnapshotIndex(request.BaseSnapshot);

                    var allBoxes = new List<BoundingBoxXYZ>();

                    foreach (var change in changes)
                    {
                        var row = new DiffViewChangeRow
                        {
                            ChangeType = change.ChangeType ?? string.Empty,
                            Category = change.Category ?? string.Empty,
                            TypeName = change.Type ?? string.Empty,
                            RepoGuid = change.RepoGuid
                        };

                        switch (row.ChangeType)
                        {
                            case "added":
                                result.AddedCount++;
                                ApplyAddedChange(doc, diffView, change, lookup, ghostBuilder, snapshotIndex, addedMaterialId, deletedMaterialId, solidFillId, request.SessionId, row, allBoxes);
                                break;
                            case "modified":
                                result.ModifiedCount++;
                                ApplyModifiedChange(doc, diffView, change, lookup, modifiedMaterialId, solidFillId, row, allBoxes);
                                break;
                            case "deleted":
                                result.DeletedCount++;
                                ApplyDeletedChange(doc, diffView, change, lookup, ghostBuilder, snapshotIndex, deletedMaterialId, request.SessionId, row, allBoxes);
                                break;
                            default:
                                row.ListOnly = true;
                                row.Note = $"Unknown change type '{row.ChangeType}'.";
                                break;
                        }

                        result.Rows.Add(row);
                    }

                    UnhideCategoriesForChanges(diffView, changes, doc);
                    ApplySectionBox(diffView, allBoxes);

                    tx.Commit();
                }

                ActivateView(uiApp, result.DiffViewId);

                // Persist the LastDiffViewId so cross-session cleanup can find it.
                try
                {
                    Document doc2 = uiApp.ActiveUIDocument.Document;
                    var hint = DocumentSyncStateService.GetState(doc2.PathName);
                    if (hint != null && !string.IsNullOrWhiteSpace(hint.ProjectId))
                    {
                        DocumentSyncStateService.SaveDiffSession(
                            doc2.PathName,
                            hint.ProjectId,
                            result.DiffViewId.Value,
                            request.SessionId);
                    }
                }
                catch { }

                result.Success = true;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Failed to build diff view: {ex.Message}";
                return result;
            }
        }

        public static void ClearDiffView(UIApplication uiApp, ElementId diffViewId, Guid? sessionId = null)
        {
            try
            {
                Document doc = uiApp.ActiveUIDocument.Document;

                using (var tx = new Transaction(doc, "RVCS Clear Diff View"))
                {
                    tx.Start();

                    // Delete tagged DirectShapes belonging to this session (or all RVCS ghosts if no session given).
                    DeleteAllRvcsGhosts(doc, sessionFilter: sessionId);

                    // Delete the diff view itself if present.
                    if (diffViewId != null && diffViewId != ElementId.InvalidElementId)
                    {
                        try
                        {
                            var view = doc.GetElement(diffViewId);
                            if (view != null) doc.Delete(diffViewId);
                        }
                        catch { }
                    }

                    tx.Commit();
                }

                try
                {
                    var hint = DocumentSyncStateService.GetState(doc.PathName);
                    if (hint != null && !string.IsNullOrWhiteSpace(hint.ProjectId))
                    {
                        DocumentSyncStateService.SaveDiffSession(doc.PathName, hint.ProjectId, 0, Guid.Empty);
                    }
                }
                catch { }
            }
            catch
            {
                // Best-effort cleanup; never throw out of this hook.
            }
        }

        public static void ClearActiveDiff(UIApplication uiApp)
        {
            try
            {
                if (uiApp == null || uiApp.ActiveUIDocument == null) return;
                Document doc = uiApp.ActiveUIDocument.Document;
                var state = DocumentSyncStateService.GetState(doc.PathName);
                if (state == null) return;

                ElementId viewId = ElementId.InvalidElementId;
                if (state.LastDiffViewElementIdValue.HasValue && state.LastDiffViewElementIdValue.Value != 0)
                    viewId = new ElementId(state.LastDiffViewElementIdValue.Value);

                Guid? session = null;
                if (Guid.TryParse(state.LastDiffSessionId ?? string.Empty, out var parsed) && parsed != Guid.Empty)
                    session = parsed;

                if (viewId == ElementId.InvalidElementId && session == null)
                    return;

                ClearDiffView(uiApp, viewId, session);
            }
            catch
            {
                // Best-effort.
            }
        }

        public static void CleanOrphanedDiffArtifacts(UIApplication uiApp)
        {
            try
            {
                Document doc = uiApp.ActiveUIDocument.Document;
                using (var tx = new Transaction(doc, "RVCS Clean Orphaned Diff Artifacts"))
                {
                    tx.Start();
                    DeleteAllRvcsGhosts(doc, sessionFilter: null);
                    DeleteAllDiffViews(doc);
                    tx.Commit();
                }
            }
            catch
            {
                // Best-effort.
            }
        }

        // ===== Internal helpers =====

        private static void ApplyAddedChange(
            Document doc, View3D diffView, Change change, ElementLookup lookup,
            GhostBuilder ghostBuilder, Dictionary<string, JObject> snapshotIndex,
            ElementId addedMaterialId, ElementId deletedMaterialId, ElementId solidFillId,
            Guid sessionId, DiffViewChangeRow row, List<BoundingBoxXYZ> allBoxes)
        {
            // Try to locate the live element first.
            Element live = lookup.ResolveChange(change);
            if (live != null)
            {
                row.LiveElementId = live.Id;
                ApplyOverride(diffView, live.Id, DiffViewColors.Added, solidFillId, transparency: 0);
                CollectBoundingBox(diffView, live, allBoxes);
                return;
            }

            // No live counterpart: synthesize a green ghost from snapshot/newData if possible.
            JObject geometrySource = ResolveGeometrySource(change, snapshotIndex, useNewData: true);
            if (geometrySource == null)
            {
                row.ListOnly = true;
                row.Note = "Added element not present locally and no geometry available.";
                return;
            }

            var ghost = ghostBuilder.BuildFromSnapshotElement(geometrySource, sessionId, DiffChangeKind.Added, addedMaterialId, change.RepoGuid);
            if (ghost.Skipped)
            {
                row.ListOnly = true;
                row.Note = $"Added (ghost): {ghost.SkipReason}";
                return;
            }

            row.GhostElementId = ghost.DirectShapeId;
            ApplyOverride(diffView, ghost.DirectShapeId, DiffViewColors.Added, solidFillId, transparency: 50);
            if (ghost.BoundingBox != null) allBoxes.Add(ghost.BoundingBox);
            row.Note = "Shown as ghost (not present locally).";
        }

        private static void ApplyModifiedChange(
            Document doc, View3D diffView, Change change, ElementLookup lookup,
            ElementId modifiedMaterialId, ElementId solidFillId,
            DiffViewChangeRow row, List<BoundingBoxXYZ> allBoxes)
        {
            Element live = lookup.ResolveChange(change);
            if (live == null)
            {
                row.ListOnly = true;
                row.Note = "Modified element not found in current document.";
                return;
            }

            row.LiveElementId = live.Id;
            ApplyOverride(diffView, live.Id, DiffViewColors.Modified, solidFillId, transparency: 0);
            CollectBoundingBox(diffView, live, allBoxes);
        }

        private static void ApplyDeletedChange(
            Document doc, View3D diffView, Change change, ElementLookup lookup,
            GhostBuilder ghostBuilder, Dictionary<string, JObject> snapshotIndex,
            ElementId deletedMaterialId, Guid sessionId,
            DiffViewChangeRow row, List<BoundingBoxXYZ> allBoxes)
        {
            // 9.13 — element may still be live (its category was removed in target). Color it red instead of building a ghost.
            Element live = lookup.ResolveChange(change);
            if (live != null)
            {
                row.LiveElementId = live.Id;
                var solidFillId = DiffViewMaterialCache.GetSolidFillPatternId(doc);
                ApplyOverride(diffView, live.Id, DiffViewColors.Deleted, solidFillId, transparency: 0);
                CollectBoundingBox(diffView, live, allBoxes);
                return;
            }

            JObject geometrySource = ResolveGeometrySource(change, snapshotIndex, useNewData: false);
            if (geometrySource == null)
            {
                row.ListOnly = true;
                row.Note = "Geometry not available for ghost preview.";
                return;
            }

            var ghost = ghostBuilder.BuildFromSnapshotElement(geometrySource, sessionId, DiffChangeKind.Deleted, deletedMaterialId, change.RepoGuid);
            if (ghost.Skipped)
            {
                row.ListOnly = true;
                row.Note = $"Deleted (ghost): {ghost.SkipReason}";
                return;
            }

            row.GhostElementId = ghost.DirectShapeId;
            var solidFillId2 = DiffViewMaterialCache.GetSolidFillPatternId(doc);
            ApplyOverride(diffView, ghost.DirectShapeId, DiffViewColors.Deleted, solidFillId2, transparency: 50, dashed: true);
            if (ghost.BoundingBox != null) allBoxes.Add(ghost.BoundingBox);
        }

        private static JObject ResolveGeometrySource(Change change, Dictionary<string, JObject> snapshotIndex, bool useNewData)
        {
            // Prefer the snapshot entry (richer; full element data) when available.
            if (snapshotIndex != null && !string.IsNullOrWhiteSpace(change.RepoGuid)
                && snapshotIndex.TryGetValue(change.RepoGuid, out var snap))
            {
                return snap;
            }
            if (snapshotIndex != null && !string.IsNullOrWhiteSpace(change.ElementId)
                && snapshotIndex.TryGetValue(change.ElementId, out var snapById))
            {
                return snapById;
            }

            // Fall back to OldData / NewData payload from the diff itself.
            try
            {
                Dictionary<string, object> source = useNewData ? change.NewData : change.OldData;
                if (source == null) return null;
                return JObject.FromObject(source);
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, JObject> BuildSnapshotIndex(ElementSnapshot snapshot)
        {
            var index = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            if (snapshot?.Elements == null) return index;

            foreach (var raw in snapshot.Elements)
            {
                JObject obj;
                try { obj = raw is JObject jo ? jo : JObject.FromObject(raw); }
                catch { continue; }

                string repoGuid = obj["repoGuid"]?.ToString();
                string uniqueId = obj["uniqueId"]?.ToString();
                string elementId = obj["elementId"]?.ToString();

                if (!string.IsNullOrWhiteSpace(repoGuid)) index[repoGuid] = obj;
                if (!string.IsNullOrWhiteSpace(uniqueId) && !index.ContainsKey(uniqueId)) index[uniqueId] = obj;
                if (!string.IsNullOrWhiteSpace(elementId) && !index.ContainsKey(elementId)) index[elementId] = obj;
            }

            return index;
        }

        private static void ApplyOverride(View view, ElementId id, Color color, ElementId solidFillId, int transparency, bool dashed = false)
        {
            if (id == null || id == ElementId.InvalidElementId) return;

            try
            {
                var ogs = new OverrideGraphicSettings();

                ogs.SetProjectionLineColor(color);
                ogs.SetCutLineColor(color);

                if (solidFillId != null && solidFillId != ElementId.InvalidElementId)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFillId);
                    ogs.SetSurfaceForegroundPatternColor(color);
                    ogs.SetCutForegroundPatternId(solidFillId);
                    ogs.SetCutForegroundPatternColor(color);
                }

                if (transparency > 0)
                    ogs.SetSurfaceTransparency(transparency);

                if (dashed)
                {
                    var dashedPatternId = FindDashLinePattern(view.Document);
                    if (dashedPatternId != ElementId.InvalidElementId)
                    {
                        ogs.SetProjectionLinePatternId(dashedPatternId);
                        ogs.SetCutLinePatternId(dashedPatternId);
                    }
                }

                view.SetElementOverrides(id, ogs);
            }
            catch
            {
                // If the element can't accept overrides in this view, silently skip.
            }
        }

        private static ElementId FindDashLinePattern(Document doc)
        {
            try
            {
                var dash = new FilteredElementCollector(doc)
                    .OfClass(typeof(LinePatternElement))
                    .Cast<LinePatternElement>()
                    .FirstOrDefault(p => p.Name != null && p.Name.IndexOf("Dash", StringComparison.OrdinalIgnoreCase) >= 0);
                return dash?.Id ?? ElementId.InvalidElementId;
            }
            catch
            {
                return ElementId.InvalidElementId;
            }
        }

        private static View3D EnsureDiffView(Document doc, string baseShort, string targetShort)
        {
            string viewName = $"{DiffViewNamePrefix}{baseShort}..{targetShort}";

            View3D existing = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, viewName, StringComparison.Ordinal));

            if (existing != null)
            {
                ClearAllOverridesOnView(existing);
                return existing;
            }

            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional);

            if (vft == null) return null;

            View3D view;
            try
            {
                view = View3D.CreateIsometric(doc, vft.Id);
            }
            catch
            {
                return null;
            }

            try { view.Name = viewName; }
            catch { /* fallback: keep auto-name */ }

            // 9.10 — do not apply any view template; ensure detail is fine for solid overrides to render.
            try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
            try { view.DisplayStyle = DisplayStyle.Shading; } catch { }

            return view;
        }

        private static void ClearAllOverridesOnView(View view)
        {
            try
            {
                var defaultOgs = new OverrideGraphicSettings();
                foreach (Element element in new FilteredElementCollector(view.Document, view.Id).WhereElementIsNotElementType())
                {
                    try { view.SetElementOverrides(element.Id, defaultOgs); } catch { }
                }
            }
            catch { }
        }

        private static void DeleteAllRvcsGhosts(Document doc, Guid? sessionFilter)
        {
            var toDelete = new List<ElementId>();
            foreach (var ds in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>())
            {
                if (!GhostBuilder.IsTaggedGhost(ds, out var sid, out _, out _)) continue;
                if (sessionFilter.HasValue && sid != sessionFilter.Value) continue;
                toDelete.Add(ds.Id);
            }

            foreach (var id in toDelete)
            {
                try { doc.Delete(id); } catch { }
            }
        }

        private static void DeleteAllDiffViews(Document doc)
        {
            var toDelete = new List<ElementId>();
            foreach (var view in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>())
            {
                if (view.IsTemplate) continue;
                if (view.Name != null && view.Name.StartsWith(DiffViewNamePrefix, StringComparison.Ordinal))
                    toDelete.Add(view.Id);
            }
            foreach (var id in toDelete)
            {
                try { doc.Delete(id); } catch { }
            }
        }

        private static void UnhideCategoriesForChanges(View3D diffView, List<Change> changes, Document doc)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var categories = doc.Settings.Categories;
            foreach (var c in changes)
            {
                if (string.IsNullOrWhiteSpace(c.Category) || !seen.Add(c.Category)) continue;
                try
                {
                    Category cat = categories.get_Item(c.Category);
                    if (cat == null) continue;
                    if (diffView.GetCategoryHidden(cat.Id))
                        diffView.SetCategoryHidden(cat.Id, false);
                }
                catch { }
            }
        }

        private static void ApplySectionBox(View3D diffView, List<BoundingBoxXYZ> boxes)
        {
            if (boxes == null || boxes.Count == 0) return;
            try
            {
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                foreach (var b in boxes)
                {
                    if (b == null) continue;
                    minX = Math.Min(minX, b.Min.X); minY = Math.Min(minY, b.Min.Y); minZ = Math.Min(minZ, b.Min.Z);
                    maxX = Math.Max(maxX, b.Max.X); maxY = Math.Max(maxY, b.Max.Y); maxZ = Math.Max(maxZ, b.Max.Z);
                }
                if (maxX < minX || maxY < minY || maxZ < minZ) return;

                double dx = maxX - minX; double dy = maxY - minY; double dz = maxZ - minZ;
                double padX = Math.Max(dx * 0.10, 1.0);
                double padY = Math.Max(dy * 0.10, 1.0);
                double padZ = Math.Max(dz * 0.10, 1.0);

                var bbox = new BoundingBoxXYZ
                {
                    Min = new XYZ(minX - padX, minY - padY, minZ - padZ),
                    Max = new XYZ(maxX + padX, maxY + padY, maxZ + padZ)
                };

                diffView.IsSectionBoxActive = false;
                diffView.SetSectionBox(bbox);
            }
            catch { }
        }

        private static void CollectBoundingBox(View view, Element element, List<BoundingBoxXYZ> boxes)
        {
            try
            {
                var bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
                if (bb != null) boxes.Add(bb);
            }
            catch { }
        }

        private static void ActivateView(UIApplication uiApp, ElementId viewId)
        {
            try
            {
                if (viewId == null || viewId == ElementId.InvalidElementId) return;
                var view = uiApp.ActiveUIDocument.Document.GetElement(viewId) as View;
                if (view != null)
                    uiApp.ActiveUIDocument.ActiveView = view;
            }
            catch { }
        }

        public static string ShortenCommitId(string commitId)
        {
            if (string.IsNullOrEmpty(commitId)) return string.Empty;
            return commitId.Length > 7 ? commitId.Substring(0, 7) : commitId;
        }
    }
}
