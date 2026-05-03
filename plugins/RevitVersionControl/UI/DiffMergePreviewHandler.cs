using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    internal class DiffMergePreviewHandler : IExternalEventHandler
    {
        private DiffMergeApplyRequest _pendingRequest;

        public void Queue(DiffMergeApplyRequest request)
        {
            _pendingRequest = request;
        }

        public void Execute(UIApplication app)
        {
            var request = _pendingRequest;
            _pendingRequest = null;
            if (request == null || request.Changes == null) return;

            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return;

            PreviewStateService.Clear();
            PreviewStateService.OriginalViewId = uidoc.ActiveView.Id;

            // Diagnostic counters
            int addedCreated = 0, addedFailed = 0;
            int ghostCreated = 0, ghostFailed = 0;
            int colorApplied = 0, colorFailed = 0;
            string solidFillStatus = "NOT FOUND";
            List<string> failReasons = new List<string>();

            using (Transaction trans = new Transaction(doc, "CollabHub Merge Preview"))
            {
                trans.Start();

                try
                {
                    // --- 1. Get or create a 3D preview view ---
                    View3D previewView = null;

                    // Try to find existing {3D} view
                    View3D source3D = new FilteredElementCollector(doc)
                        .OfClass(typeof(View3D))
                        .Cast<View3D>()
                        .FirstOrDefault(v => !v.IsTemplate && v.Name.Contains("{3D}"));

                    if (source3D == null)
                    {
                        source3D = new FilteredElementCollector(doc)
                            .OfClass(typeof(View3D))
                            .Cast<View3D>()
                            .FirstOrDefault(v => !v.IsTemplate);
                    }

                    if (source3D != null)
                    {
                        try
                        {
                            ElementId newViewId = source3D.Duplicate(ViewDuplicateOption.Duplicate);
                            previewView = doc.GetElement(newViewId) as View3D;
                        }
                        catch { }
                    }

                    // Fallback: create a new 3D view
                    if (previewView == null)
                    {
                        var viewFamilyTypeId = new FilteredElementCollector(doc)
                            .OfClass(typeof(ViewFamilyType))
                            .Cast<ViewFamilyType>()
                            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional)?.Id;

                        if (viewFamilyTypeId != null)
                        {
                            try { previewView = View3D.CreateIsometric(doc, viewFamilyTypeId); }
                            catch { }
                        }
                    }

                    if (previewView == null)
                    {
                        trans.RollBack();
                        TaskDialog.Show("Preview Error", "Could not create a 3D preview view.");
                        return;
                    }

                    previewView.Name = "CollabHub Merge Preview - " + Guid.NewGuid().ToString().Substring(0, 4);
                    PreviewStateService.TempViewId = previewView.Id;

                    // Remove View Template and set display style
                    try { previewView.ViewTemplateId = ElementId.InvalidElementId; } catch { }
                    try { previewView.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                    try { previewView.DetailLevel = ViewDetailLevel.Fine; } catch { }

                    // --- 2. Find solid fill pattern ---
                    FillPatternElement solidFill = FindSolidFillPattern(doc);
                    solidFillStatus = solidFill != null ? $"FOUND: {solidFill.Name}" : "NOT FOUND (colors may not show)";

                    // --- 3. Create temporary elements for ADDED changes ---
                    var addedChanges = request.Changes.Where(c => c.ChangeType == "added").ToList();
                    foreach (var change in addedChanges)
                    {
                        try
                        {
                            string trackingKey = PreviewStateService.GetChangeTrackingKey(change);
                            if (string.IsNullOrEmpty(trackingKey)) { addedFailed++; failReasons.Add("ADD: No tracking key"); continue; }
                            if (change.NewData == null) { addedFailed++; failReasons.Add("ADD: NewData is null"); continue; }

                            var newData = Newtonsoft.Json.Linq.JObject.FromObject(change.NewData);
                            var creator = new ElementCreator(doc);
                            CreationResult result = creator.Create(newData);

                            if (result?.Element != null)
                            {
                                PreviewStateService.TempAddedElements[trackingKey] = result.Element.Id;
                                addedCreated++;

                                string repoGuid = change.RepoGuid ?? newData["repoGuid"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(repoGuid))
                                {
                                    try { RepoGuidService.SetRepoGuid(result.Element, repoGuid); } catch { }
                                }
                            }
                            else
                            {
                                addedFailed++;
                                failReasons.Add($"ADD: Create failed for {change.Category}/{change.Type}: {result?.Reason}");
                            }
                        }
                        catch (Exception ex)
                        {
                            addedFailed++;
                            failReasons.Add($"ADD exception: {ex.Message}");
                        }
                    }

                    // --- 4. Create GHOST elements for DELETED changes ---
                    var deletedChanges = request.Changes.Where(c => c.ChangeType == "deleted").ToList();
                    foreach (var change in deletedChanges)
                    {
                        try
                        {
                            string trackingKey = PreviewStateService.GetChangeTrackingKey(change);
                            if (string.IsNullOrEmpty(trackingKey)) { ghostFailed++; continue; }

                            if (change.OldData != null)
                            {
                                var oldData = Newtonsoft.Json.Linq.JObject.FromObject(change.OldData);
                                var creator = new ElementCreator(doc);
                                CreationResult result = creator.Create(oldData);

                                if (result?.Element != null)
                                {
                                    PreviewStateService.TempGhostElements[trackingKey] = result.Element.Id;
                                    ghostCreated++;
                                    continue;
                                }
                            }

                            // Fallback: DirectShape box
                            if (CreateGhostDirectShape(doc, change, trackingKey))
                                ghostCreated++;
                            else
                                ghostFailed++;
                        }
                        catch { ghostFailed++; }
                    }

                    // --- 4.5 Create REMOTE GHOST elements for MODIFIED conflicts ---
                    var modifiedConflicts = request.Changes.Where(c => c.ChangeType == "modified" && PreviewStateService.ActiveConflicts != null && PreviewStateService.ActiveConflicts.Any(conf => conf.ElementId == c.ElementId || conf.ElementId == c.RepoGuid)).ToList();
                    foreach (var change in modifiedConflicts)
                    {
                        try
                        {
                            string trackingKey = PreviewStateService.GetChangeTrackingKey(change);
                            if (string.IsNullOrEmpty(trackingKey)) continue;
                            
                            string remoteKey = trackingKey + "_remote";
                            if (CreateGhostDirectShape(doc, change, remoteKey, useNewData: true))
                                ghostCreated++;
                            else
                                ghostFailed++;
                        }
                        catch { ghostFailed++; }
                    }

                    // --- 5. Apply Graphic Overrides ---
                    Color colorAdded = new Color(0, 200, 0);        // Green
                    Color colorModified = new Color(255, 165, 0);   // Orange
                    Color colorDeleted = new Color(255, 0, 0);      // Red
                    Color colorConflict = new Color(255, 0, 255);   // Magenta

                    foreach (var change in request.Changes)
                    {
                        ElementId targetId = ElementId.InvalidElementId;
                        Color color = null;
                        int transparency = 0;
                        string trackingKey = PreviewStateService.GetChangeTrackingKey(change);

                        if (change.ChangeType == "added")
                        {
                            if (trackingKey != null && PreviewStateService.TempAddedElements.TryGetValue(trackingKey, out ElementId id))
                            {
                                targetId = id;
                                color = colorAdded;
                            }
                        }
                        else if (change.ChangeType == "modified")
                        {
                            // Try RepoGuid lookup
                            Element el = null;
                            if (!string.IsNullOrEmpty(change.RepoGuid))
                            {
                                el = RepoGuidService.FindElement(doc, change.RepoGuid, null);
                            }
                            // Try UniqueId lookup
                            if (el == null && !string.IsNullOrEmpty(change.ElementId))
                            {
                                try { el = doc.GetElement(change.ElementId); } catch { }
                            }
                            // Try numeric ID
                            if (el == null && !string.IsNullOrEmpty(change.ElementId))
                            {
                                if (long.TryParse(change.ElementId, out long numId))
                                {
                                    try { el = doc.GetElement(new ElementId(numId)); } catch { }
                                }
                            }
                            if (el != null)
                            {
                                targetId = el.Id;
                                color = colorModified;
                                
                                string remoteKey = trackingKey + "_remote";
                                if (PreviewStateService.TempGhostElements.TryGetValue(remoteKey, out ElementId remoteGhostId))
                                {
                                    try
                                    {
                                        OverrideGraphicSettings ogsRemote = new OverrideGraphicSettings();
                                        ogsRemote.SetProjectionLineColor(colorConflict);
                                        ogsRemote.SetProjectionLineWeight(5);
                                        ogsRemote.SetSurfaceForegroundPatternColor(colorConflict);
                                        ogsRemote.SetSurfaceBackgroundPatternColor(colorConflict);
                                        ogsRemote.SetCutForegroundPatternColor(colorConflict);
                                        ogsRemote.SetCutBackgroundPatternColor(colorConflict);
                                        if (solidFill != null)
                                        {
                                            ogsRemote.SetSurfaceForegroundPatternId(solidFill.Id);
                                            ogsRemote.SetSurfaceBackgroundPatternId(solidFill.Id);
                                            ogsRemote.SetCutForegroundPatternId(solidFill.Id);
                                            ogsRemote.SetCutBackgroundPatternId(solidFill.Id);
                                        }
                                        ogsRemote.SetSurfaceTransparency(50);
                                        previewView.SetElementOverrides(remoteGhostId, ogsRemote);
                                        colorApplied++;
                                        transparency = 50; // Make the real element 50% transparent too so both can be seen
                                    }
                                    catch { }
                                }
                            }
                        }
                        else if (change.ChangeType == "deleted")
                        {
                            if (trackingKey != null && PreviewStateService.TempGhostElements.TryGetValue(trackingKey, out ElementId ghostId))
                            {
                                targetId = ghostId;
                                color = colorDeleted;
                                transparency = 70;
                            }
                            else
                            {
                                // Try to find existing element and color it red
                                Element el = null;
                                if (!string.IsNullOrEmpty(change.RepoGuid))
                                    el = RepoGuidService.FindElement(doc, change.RepoGuid, null);
                                if (el == null && !string.IsNullOrEmpty(change.ElementId))
                                {
                                    try { el = doc.GetElement(change.ElementId); } catch { }
                                }
                                if (el != null)
                                {
                                    targetId = el.Id;
                                    color = colorDeleted;
                                }
                            }
                        }

                        if (targetId != ElementId.InvalidElementId && color != null)
                        {
                            try
                            {
                                OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                                ogs.SetProjectionLineColor(color);
                                ogs.SetProjectionLineWeight(5);
                                ogs.SetSurfaceForegroundPatternColor(color);
                                ogs.SetSurfaceBackgroundPatternColor(color);
                                ogs.SetCutForegroundPatternColor(color);
                                ogs.SetCutBackgroundPatternColor(color);

                                if (solidFill != null)
                                {
                                    ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                                    ogs.SetSurfaceBackgroundPatternId(solidFill.Id);
                                    ogs.SetCutForegroundPatternId(solidFill.Id);
                                    ogs.SetCutBackgroundPatternId(solidFill.Id);
                                }

                                if (transparency > 0)
                                    ogs.SetSurfaceTransparency(transparency);

                                previewView.SetElementOverrides(targetId, ogs);
                                colorApplied++;
                            }
                            catch (Exception ex)
                            {
                                colorFailed++;
                                failReasons.Add($"COLOR: {change.ChangeType} {change.ElementId}: {ex.Message}");
                            }
                        }
                        else
                        {
                            colorFailed++;
                            if (change.ChangeType == "modified" || change.ChangeType == "deleted")
                            {
                                failReasons.Add($"FIND: {change.ChangeType} {change.Category}/{change.Type} id={change.ElementId} repo={change.RepoGuid} → NOT FOUND");
                            }
                        }
                    }

                    trans.Commit();
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted()) trans.RollBack();
                    TaskDialog.Show("Preview Error", $"Failed to create merge preview:\n{ex.Message}\n\n{ex.StackTrace}");
                    return;
                }
            }

            // Switch to the preview view
            if (PreviewStateService.TempViewId != ElementId.InvalidElementId)
            {
                try
                {
                    uidoc.ActiveView = doc.GetElement(PreviewStateService.TempViewId) as View;
                }
                catch { }
            }

            // Build diagnostic summary
            string diagnostics = $"Solid Fill: {solidFillStatus}\n" +
                                 $"Added: {addedCreated} created, {addedFailed} failed\n" +
                                 $"Ghosts: {ghostCreated} created, {ghostFailed} failed\n" +
                                 $"Colors: {colorApplied} applied, {colorFailed} failed";

            if (failReasons.Count > 0)
            {
                diagnostics += "\n\nDetails:\n" + string.Join("\n", failReasons.Take(10));
            }

            TaskDialog.Show("Merge Preview", diagnostics +
                "\n\nGreen = Added | Orange = Modified | Red = Deleted\n" +
                "Use the Changes & Merge pane to review and finalize.");
        }

        /// <summary>
        /// Find a solid fill pattern using multiple strategies.
        /// </summary>
        private static FillPatternElement FindSolidFillPattern(Document doc)
        {
            var allPatterns = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .ToList();

            // Strategy 1: IsSolidFill
            foreach (var fp in allPatterns)
            {
                try
                {
                    var pattern = fp.GetFillPattern();
                    if (pattern != null && pattern.IsSolidFill) return fp;
                }
                catch { }
            }

            // Strategy 2: Name-based
            foreach (var fp in allPatterns)
            {
                string name = fp.Name ?? "";
                if (name.Equals("Solid fill", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("<Solid fill>", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Solid", StringComparison.OrdinalIgnoreCase))
                    return fp;
            }

            // Strategy 3: Any pattern containing "solid"
            foreach (var fp in allPatterns)
            {
                if ((fp.Name ?? "").IndexOf("solid", StringComparison.OrdinalIgnoreCase) >= 0)
                    return fp;
            }

            // Strategy 4: Any drafting pattern
            foreach (var fp in allPatterns)
            {
                try
                {
                    var pattern = fp.GetFillPattern();
                    if (pattern != null && pattern.Target == FillPatternTarget.Drafting)
                        return fp;
                }
                catch { }
            }

            // Strategy 5: Just return the first available pattern
            return allPatterns.FirstOrDefault();
        }

        /// <summary>
        /// Creates a DirectShape box at the element's last known location.
        /// </summary>
        private static bool CreateGhostDirectShape(Document doc, Change change, string trackingKey, bool useNewData = false)
        {
            try
            {
                XYZ min = null;
                XYZ max = null;

                var dataToExtract = useNewData ? change.NewData : change.OldData;

                // Try to extract exact BoundingBox from JSON data first
                if (dataToExtract != null && dataToExtract.ContainsKey("geometry") && dataToExtract["geometry"] is Newtonsoft.Json.Linq.JObject geom)
                {
                    if (geom.ContainsKey("boundingBox") && geom["boundingBox"] is Newtonsoft.Json.Linq.JObject bbox)
                    {
                        var minJ = bbox["min"] as Newtonsoft.Json.Linq.JObject;
                        var maxJ = bbox["max"] as Newtonsoft.Json.Linq.JObject;
                        if (minJ != null && maxJ != null)
                        {
                            min = new XYZ(minJ["x"].Value<double>(), minJ["y"].Value<double>(), minJ["z"].Value<double>());
                            max = new XYZ(maxJ["x"].Value<double>(), maxJ["y"].Value<double>(), maxJ["z"].Value<double>());
                        }
                    }
                }

                // If BoundingBox not found in JSON, use the real element if NOT using NewData
                if ((min == null || max == null) && !useNewData)
                {
                    Element realElement = null;
                    if (!string.IsNullOrEmpty(change.RepoGuid))
                        realElement = RepoGuidService.FindElement(doc, change.RepoGuid, change.ElementId);
                    
                    if (realElement == null && !string.IsNullOrEmpty(change.ElementId))
                    {
                        try { realElement = doc.GetElement(change.ElementId); } catch { }
                    }

                    if (realElement != null)
                    {
                        BoundingBoxXYZ bbox = realElement.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            min = bbox.Min;
                            max = bbox.Max;
                        }
                    }
                }

                // Fallback to Location if real element not found or has no bbox
                if (min == null || max == null)
                {
                    XYZ location = ExtractLocation(dataToExtract);
                    if (location == null) return false;

                    double half = 1.0; // Slightly larger fallback box
                    min = new XYZ(location.X - half, location.Y - half, location.Z - half);
                    max = new XYZ(location.X + half, location.Y + half, location.Z + half);
                }

                var points = new List<XYZ>
                {
                    new XYZ(min.X, min.Y, min.Z),
                    new XYZ(max.X, min.Y, min.Z),
                    new XYZ(max.X, max.Y, min.Z),
                    new XYZ(min.X, max.Y, min.Z),
                    new XYZ(min.X, min.Y, max.Z),
                    new XYZ(max.X, min.Y, max.Z),
                    new XYZ(max.X, max.Y, max.Z),
                    new XYZ(min.X, max.Y, max.Z),
                };

                var builder = new TessellatedShapeBuilder();
                builder.OpenConnectedFaceSet(true);
                builder.AddFace(new TessellatedFace(new List<XYZ> { points[0], points[1], points[2], points[3] }, ElementId.InvalidElementId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { points[4], points[7], points[6], points[5] }, ElementId.InvalidElementId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { points[0], points[4], points[5], points[1] }, ElementId.InvalidElementId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { points[2], points[6], points[7], points[3] }, ElementId.InvalidElementId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { points[0], points[3], points[7], points[4] }, ElementId.InvalidElementId));
                builder.AddFace(new TessellatedFace(new List<XYZ> { points[1], points[5], points[6], points[2] }, ElementId.InvalidElementId));
                builder.CloseConnectedFaceSet();
                builder.Build();

                var result = builder.GetBuildResult();
                if (result.Outcome == TessellatedShapeBuilderOutcome.Nothing) return false;

                var geomObjects = result.GetGeometricalObjects();
                if (geomObjects == null || geomObjects.Count == 0) return false;

                var catId = new ElementId(BuiltInCategory.OST_GenericModel);
                var ds = DirectShape.CreateElement(doc, catId);
                ds.SetShape(geomObjects.ToList());
                ds.Name = "CollabHub Ghost - " + (change.Category ?? "Element");

                PreviewStateService.TempGhostElements[trackingKey] = ds.Id;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Extract XYZ location from element data dictionary.
        /// </summary>
        private static XYZ ExtractLocation(Dictionary<string, object> data)
        {
            if (data == null) return null;
            if (!data.ContainsKey("location")) return null;

            try
            {
                var locObj = Newtonsoft.Json.Linq.JObject.FromObject(data["location"]);
                string locType = locObj["type"]?.ToString();

                if (locType == "point")
                {
                    var pt = locObj["point"] as Newtonsoft.Json.Linq.JObject;
                    if (pt == null) return null;
                    double.TryParse(pt["x"]?.ToString(), out double x);
                    double.TryParse(pt["y"]?.ToString(), out double y);
                    double.TryParse(pt["z"]?.ToString(), out double z);
                    return new XYZ(x, y, z);
                }
                else if (locType == "curve")
                {
                    var sp = locObj["startPoint"] as Newtonsoft.Json.Linq.JObject;
                    var ep = locObj["endPoint"] as Newtonsoft.Json.Linq.JObject;
                    if (sp == null || ep == null) return null;
                    double.TryParse(sp["x"]?.ToString(), out double sx);
                    double.TryParse(sp["y"]?.ToString(), out double sy);
                    double.TryParse(sp["z"]?.ToString(), out double sz);
                    double.TryParse(ep["x"]?.ToString(), out double ex);
                    double.TryParse(ep["y"]?.ToString(), out double ey);
                    double.TryParse(ep["z"]?.ToString(), out double ez);
                    return new XYZ((sx + ex) / 2, (sy + ey) / 2, (sz + ez) / 2);
                }
            }
            catch { }
            return null;
        }

        public string GetName() => "Diff Merge Preview Handler";
    }
}
