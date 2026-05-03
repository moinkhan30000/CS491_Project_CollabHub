using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    /// <summary>
    /// Handles real-time visual updates during merge preview:
    /// - Highlighting the currently reviewed element
    /// - Toggling element visibility when Include is checked/unchecked
    /// - Updating conflict resolution visuals
    /// - Highlighting spatial collisions
    /// </summary>
    internal class DiffMergeUpdateHandler : IExternalEventHandler
    {
        private DiffMergeUpdateRequest _pendingRequest;

        public void Queue(DiffMergeUpdateRequest request)
        {
            _pendingRequest = request;
        }

        public void Execute(UIApplication app)
        {
            var request = _pendingRequest;
            _pendingRequest = null;
            if (request == null) return;

            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return;

            var viewId = PreviewStateService.TempViewId;
            if (viewId == ElementId.InvalidElementId) return;

            var previewView = doc.GetElement(viewId) as View;
            if (previewView == null) return;

            // Find solid fill pattern (cached approach)
            FillPatternElement solidFill = FindSolidFill(doc);

            using (Transaction trans = new Transaction(doc, "CollabHub Update Preview"))
            {
                trans.Start();
                try
                {
                    // Re-apply overrides for ALL tracked elements based on current state
                    RefreshAllOverrides(doc, previewView, solidFill, request);

                    // Highlight spatial collisions if requested
                    if (request.HighlightCollision)
                    {
                        HighlightSpatialCollisions(doc, previewView, request);
                    }

                    // Zoom to current element if requested
                    if (request.ZoomToElement)
                    {
                        ElementId zoomTarget = ResolveElementId(doc, request.CurrentChange);
                        if (zoomTarget != ElementId.InvalidElementId)
                        {
                            trans.Commit();
                            try
                            {
                                uidoc.ShowElements(zoomTarget);
                                uidoc.Selection.SetElementIds(new List<ElementId> { zoomTarget });
                            }
                            catch { }
                            return;
                        }
                    }

                    trans.Commit();
                }
                catch
                {
                    if (trans.HasStarted()) trans.RollBack();
                }
            }
        }

        private void RefreshAllOverrides(Document doc, View view, FillPatternElement solidFill,
                                          DiffMergeUpdateRequest request)
        {
            // Colors
            Color colorAdded    = new Color(0, 200, 0);      // Green
            Color colorModified = new Color(255, 165, 0);    // Orange
            Color colorDeleted  = new Color(255, 0, 0);      // Red
            Color colorExcluded = new Color(140, 140, 140);  // Gray
            Color colorHighlight = new Color(0, 255, 255);   // Cyan (current element)
            Color colorKeepOurs  = new Color(0, 120, 215);   // Blue
            Color colorKeepTheirs = new Color(180, 0, 230);  // Purple

            if (request.AllChanges == null) return;

            for (int i = 0; i < request.AllChanges.Count; i++)
            {
                var change = request.AllChanges[i];
                ElementId elemId = ResolveElementId(doc, change);
                if (elemId == ElementId.InvalidElementId) continue;

                bool isCurrent = (i == request.CurrentIndex);
                bool isIncluded = request.IncludedStates != null &&
                                  i < request.IncludedStates.Count &&
                                  request.IncludedStates[i];

                // Determine the base color
                Color baseColor;
                int transparency = 0;

                if (!isIncluded)
                {
                    // Excluded: gray + very transparent
                    baseColor = colorExcluded;
                    transparency = 80;
                }
                else
                {
                    // Check if this is a conflicted element with a resolution
                    string conflictRes = null;
                    if (request.ConflictResolutions != null)
                    {
                        string conflictId = change.RepoGuid ?? change.ElementId;
                        request.ConflictResolutions.TryGetValue(conflictId, out conflictRes);
                        if (conflictRes == null)
                            request.ConflictResolutions.TryGetValue(change.ElementId ?? "", out conflictRes);
                    }

                    if (conflictRes == "keep_local" || conflictRes == "accept_remote")
                    {
                        baseColor = conflictRes == "keep_local" ? colorKeepOurs : colorKeepTheirs;
                    }
                    else
                    {
                        // Normal included color based on change type
                        switch (change.ChangeType)
                        {
                            case "added":    baseColor = colorAdded; break;
                            case "modified": baseColor = colorModified; break;
                            case "deleted":  baseColor = colorDeleted; transparency = 70; break;
                            default:         baseColor = colorModified; break;
                        }
                    }
                }

                // Apply override
                OverrideGraphicSettings ogs = new OverrideGraphicSettings();

                if (isCurrent)
                {
                    // Current element: use cyan/highlight outline + base fill
                    ogs.SetProjectionLineColor(colorHighlight);
                    ogs.SetProjectionLineWeight(8);
                    ogs.SetSurfaceForegroundPatternColor(baseColor);
                    ogs.SetSurfaceBackgroundPatternColor(baseColor);
                    ogs.SetCutForegroundPatternColor(baseColor);
                    ogs.SetCutBackgroundPatternColor(baseColor);
                }
                else
                {
                    // Non-current: base color everywhere
                    ogs.SetProjectionLineColor(baseColor);
                    ogs.SetProjectionLineWeight(3);
                    ogs.SetSurfaceForegroundPatternColor(baseColor);
                    ogs.SetSurfaceBackgroundPatternColor(baseColor);
                    ogs.SetCutForegroundPatternColor(baseColor);
                    ogs.SetCutBackgroundPatternColor(baseColor);
                }

                if (solidFill != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                    ogs.SetSurfaceBackgroundPatternId(solidFill.Id);
                    ogs.SetCutForegroundPatternId(solidFill.Id);
                    ogs.SetCutBackgroundPatternId(solidFill.Id);
                }

                if (transparency > 0)
                    ogs.SetSurfaceTransparency(transparency);

                try { view.SetElementOverrides(elemId, ogs); } catch { }
            }
        }

        /// <summary>
        /// Resolve a Change to its Revit ElementId in the current model.
        /// Checks temp added, ghost, RepoGuid, UniqueId, and numeric ID.
        /// </summary>
        private static ElementId ResolveElementId(Document doc, Change change)
        {
            if (change == null) return ElementId.InvalidElementId;

            string key = PreviewStateService.GetChangeTrackingKey(change);

            // Temp added elements
            if (key != null && PreviewStateService.TempAddedElements.TryGetValue(key, out ElementId addedId))
                return addedId;

            // Ghost elements
            if (key != null && PreviewStateService.TempGhostElements.TryGetValue(key, out ElementId ghostId))
                return ghostId;

            // Existing elements: RepoGuid lookup
            if (!string.IsNullOrEmpty(change.RepoGuid))
            {
                Element el = RepoGuidService.FindElement(doc, change.RepoGuid, null);
                if (el != null) return el.Id;
            }

            // UniqueId lookup
            if (!string.IsNullOrEmpty(change.ElementId))
            {
                try
                {
                    Element el = doc.GetElement(change.ElementId);
                    if (el != null) return el.Id;
                }
                catch { }
            }

            // Numeric ID
            if (!string.IsNullOrEmpty(change.ElementId) && long.TryParse(change.ElementId, out long numId))
            {
                try
                {
                    Element el = doc.GetElement(new ElementId(numId));
                    if (el != null) return el.Id;
                }
                catch { }
            }

            return ElementId.InvalidElementId;
        }

        private static FillPatternElement FindSolidFill(Document doc)
        {
            var patterns = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .ToList();

            foreach (var fp in patterns)
            {
                try
                {
                    var p = fp.GetFillPattern();
                    if (p != null && p.IsSolidFill) return fp;
                }
                catch { }
            }

            return patterns.FirstOrDefault(fp =>
                (fp.Name ?? "").IndexOf("solid", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? patterns.FirstOrDefault();
        }

        private void HighlightSpatialCollisions(Document doc, View view, DiffMergeUpdateRequest request)
        {
            if (request.CurrentChange == null) return;

            // Find conflicts for the current change
            var currentConflicts = PreviewStateService.ActiveConflicts.Where(c =>
                c.ElementId == request.CurrentChange.ElementId ||
                c.ElementId == request.CurrentChange.RepoGuid).ToList();

            var spatialConflicts = currentConflicts.Where(c => c.ConflictType == "spatial_collision").ToList();
            if (!spatialConflicts.Any()) return;

            // For spatial conflicts, highlight both colliding elements
            foreach (var conflict in spatialConflicts)
            {
                // Parse the conflict.ElementId to get both element IDs
                // Format: "elem1_id+elem2_id"
                var parts = conflict.ElementId.Split('+');
                if (parts.Length != 2) continue;

                var elem1Id = parts[0];
                var elem2Id = parts[1];

                // Find the elements in the model
                ElementId id1 = ResolveElementIdByString(doc, elem1Id);
                ElementId id2 = ResolveElementIdByString(doc, elem2Id);

                if (id1 != ElementId.InvalidElementId && id2 != ElementId.InvalidElementId)
                {
                    // Create collision highlight - semi-transparent red overlay
                    Color collisionColor = new Color(255, 0, 0); // Red
                    OverrideGraphicSettings collisionOgs = new OverrideGraphicSettings();
                    collisionOgs.SetProjectionLineColor(collisionColor);
                    collisionOgs.SetProjectionLineWeight(5);
                    collisionOgs.SetSurfaceForegroundPatternColor(collisionColor);
                    collisionOgs.SetSurfaceBackgroundPatternColor(collisionColor);
                    collisionOgs.SetSurfaceTransparency(50); // Semi-transparent

                    FillPatternElement solidFill = FindSolidFill(doc);
                    if (solidFill != null)
                    {
                        collisionOgs.SetSurfaceForegroundPatternId(solidFill.Id);
                        collisionOgs.SetSurfaceBackgroundPatternId(solidFill.Id);
                    }

                    try { view.SetElementOverrides(id1, collisionOgs); } catch { }
                    try { view.SetElementOverrides(id2, collisionOgs); } catch { }
                }
            }
        }

        private static ElementId ResolveElementIdByString(Document doc, string elementId)
        {
            if (string.IsNullOrEmpty(elementId)) return ElementId.InvalidElementId;

            // Check temp elements first
            if (PreviewStateService.TempAddedElements.TryGetValue("id:" + elementId, out ElementId tempId))
                return tempId;
            if (PreviewStateService.TempGhostElements.TryGetValue("id:" + elementId, out tempId))
                return tempId;

            // Try UniqueId
            try
            {
                Element el = doc.GetElement(elementId);
                if (el != null) return el.Id;
            }
            catch { }

            // Try numeric ID
            if (long.TryParse(elementId, out long numId))
            {
                try
                {
                    Element el = doc.GetElement(new ElementId(numId));
                    if (el != null) return el.Id;
                }
                catch { }
            }

            return ElementId.InvalidElementId;
        }

        public string GetName() => "Diff Merge Update Handler";
    }

    internal class DiffMergeUpdateRequest
    {
        /// <summary>All changes being previewed.</summary>
        public List<Change> AllChanges { get; set; }

        /// <summary>Index of the currently reviewed change.</summary>
        public int CurrentIndex { get; set; }

        /// <summary>The currently reviewed Change object (for zoom).</summary>
        public Change CurrentChange { get; set; }

        /// <summary>Included state for each change (parallel to AllChanges).</summary>
        public List<bool> IncludedStates { get; set; }

        /// <summary>Conflict resolution map: elementId → "keep_local"|"accept_remote"|"keep_both"</summary>
        public Dictionary<string, string> ConflictResolutions { get; set; }

        /// <summary>Whether to zoom to the current element.</summary>
        public bool ZoomToElement { get; set; }

        /// <summary>Whether to highlight spatial collisions.</summary>
        public bool HighlightCollision { get; set; }
    }
}
