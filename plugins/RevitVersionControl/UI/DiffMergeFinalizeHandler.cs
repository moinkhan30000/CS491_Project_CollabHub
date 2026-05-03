using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    internal class DiffMergeFinalizeRequest
    {
        public bool IsCancelled { get; set; }
        public DiffMergeApplyRequest OriginalRequest { get; set; }
        public List<string> AcceptedChangeKeys { get; set; }
    }

    internal class DiffMergeFinalizeHandler : IExternalEventHandler
    {
        private DiffMergeFinalizeRequest _pendingRequest;

        public void Queue(DiffMergeFinalizeRequest request)
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

            using (Transaction trans = new Transaction(doc, "Finalize CollabHub Merge"))
            {
                trans.Start();

                try
                {
                    // --- 1. Switch back to original view and delete temporary view ---
                    if (PreviewStateService.TempViewId != ElementId.InvalidElementId)
                    {
                        if (PreviewStateService.OriginalViewId != ElementId.InvalidElementId)
                        {
                            try
                            {
                                uidoc.ActiveView = doc.GetElement(PreviewStateService.OriginalViewId) as View;
                            }
                            catch { }
                        }

                        try
                        {
                            doc.Delete(PreviewStateService.TempViewId);
                        }
                        catch { }
                    }

                    if (request.IsCancelled)
                    {
                        // --- CANCELLED: Delete all temporary added elements ---
                        foreach (var tempId in PreviewStateService.TempAddedElements.Values)
                        {
                            try { doc.Delete(tempId); } catch { }
                        }

                        // Delete ghost elements for deleted changes
                        foreach (var ghostId in PreviewStateService.TempGhostElements.Values)
                        {
                            try { doc.Delete(ghostId); } catch { }
                        }

                        PreviewStateService.Clear();
                        trans.Commit();

                        TaskDialog.Show("Merge Cancelled", "Merge preview has been cancelled.\nAll temporary elements have been removed.");
                    }
                    else if (request.OriginalRequest != null)
                    {
                        // --- CONFIRMED: Process accepted/rejected changes ---
                        var acceptedKeys = new HashSet<string>(request.AcceptedChangeKeys ?? new List<string>());
                        var changes = request.OriginalRequest.Changes ?? new List<Change>();

                        int addedAccepted = 0, addedRejected = 0, addedDeleteFailed = 0;
                        int modAccepted = 0, delAccepted = 0;

                        // Process "added" changes: reject = delete temp element, accept = keep it
                        foreach (var change in changes.Where(c => c.ChangeType == "added"))
                        {
                            string trackingKey = PreviewStateService.GetChangeTrackingKey(change);
                            bool accepted = trackingKey != null && acceptedKeys.Contains(trackingKey);

                            if (!accepted)
                            {
                                addedRejected++;
                                // Delete from TempAddedElements
                                if (trackingKey != null &&
                                    PreviewStateService.TempAddedElements.TryGetValue(trackingKey, out ElementId tempId))
                                {
                                    try
                                    {
                                        doc.Delete(tempId);
                                    }
                                    catch
                                    {
                                        addedDeleteFailed++;
                                    }
                                }
                            }
                            else
                            {
                                addedAccepted++;
                                // Clear graphic overrides from accepted elements so they look normal
                                if (trackingKey != null &&
                                    PreviewStateService.TempAddedElements.TryGetValue(trackingKey, out ElementId acceptedId))
                                {
                                    try
                                    {
                                        var originalView = doc.GetElement(PreviewStateService.OriginalViewId) as View;
                                        if (originalView != null)
                                            originalView.SetElementOverrides(acceptedId, new OverrideGraphicSettings());
                                    }
                                    catch { }
                                }
                            }
                        }

                        // Process "modified" changes: apply directly
                        foreach (var change in changes.Where(c => c.ChangeType == "modified"))
                        {
                            string trackingKey = PreviewStateService.GetChangeTrackingKey(change);
                            bool accepted = trackingKey != null && acceptedKeys.Contains(trackingKey);
                            if (!accepted) continue;

                            modAccepted++;
                            try
                            {
                                Element el = RepoGuidService.FindElement(doc, change.RepoGuid, change.ElementId);
                                if (el == null && !string.IsNullOrEmpty(change.ElementId))
                                {
                                    if (long.TryParse(change.ElementId, out long numId))
                                    {
                                        try { el = doc.GetElement(new ElementId(numId)); } catch { }
                                    }
                                }
                                if (el == null) continue;

                                var newData = change.NewData != null
                                    ? Newtonsoft.Json.Linq.JObject.FromObject(change.NewData)
                                    : null;
                                if (newData == null) continue;

                                if (change.LocationChanged)
                                    ApplyLocationChange(doc, el, newData);

                                if (change.ParameterChanges != null)
                                {
                                    foreach (var pc in change.ParameterChanges)
                                    {
                                        try
                                        {
                                            Parameter param = el.LookupParameter(pc.Name);
                                            if (param == null || param.IsReadOnly || pc.NewValue == null) continue;
                                            ApplyParameterValue(param, pc.NewValue, pc.ElementName, doc);
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch { }
                        }

                        // Process "deleted" changes: delete the element
                        foreach (var change in changes.Where(c => c.ChangeType == "deleted"))
                        {
                            string trackingKey = PreviewStateService.GetChangeTrackingKey(change);
                            bool accepted = trackingKey != null && acceptedKeys.Contains(trackingKey);
                            if (!accepted) continue;

                            delAccepted++;
                            try
                            {
                                Element el = RepoGuidService.FindElement(doc, change.RepoGuid, change.ElementId);
                                if (el != null)
                                {
                                    if (el.Pinned) el.Pinned = false;
                                    doc.Delete(el.Id);
                                }
                            }
                            catch { }
                        }

                        // --- 2. Update state ---
                        string projectId = request.OriginalRequest.ProjectId;
                        string targetCommitId = request.OriginalRequest.TargetCommitId;
                        if (!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(targetCommitId))
                        {
                            var currentState = DocumentSyncStateService.GetStateForProject(doc.PathName, projectId);
                            string modelId = !string.IsNullOrWhiteSpace(request.OriginalRequest.ModelId)
                                ? request.OriginalRequest.ModelId
                                : currentState?.ModelId ?? doc.PathName;

                            string currentCommitId = currentState?.CurrentCommitId ?? targetCommitId;
                            string currentBranch = currentState?.CurrentBranchName ?? "main";

                            DocumentSyncStateService.SaveState(
                                doc.PathName, projectId, modelId,
                                currentCommitId, currentBranch, targetCommitId);
                        }

                        // --- 3. Always clean up ghost elements ---
                        foreach (var ghostId in PreviewStateService.TempGhostElements.Values)
                        {
                            try { doc.Delete(ghostId); } catch { }
                        }

                        PreviewStateService.Clear();
                        trans.Commit();

                        TaskDialog.Show("Merge Complete",
                            $"Added: {addedAccepted} accepted, {addedRejected} rejected" +
                            (addedDeleteFailed > 0 ? $" ({addedDeleteFailed} delete failed)" : "") +
                            $"\nModified: {modAccepted} applied" +
                            $"\nDeleted: {delAccepted} applied" +
                            $"\nAccepted keys: {acceptedKeys.Count}" +
                            $"\nTotal changes: {changes.Count}" +
                            "\n\nPlease click Publish to create the merge commit.");
                    }
                    else
                    {
                        PreviewStateService.Clear();
                        trans.Commit();
                    }
                }
                catch (Exception ex)
                {
                    PreviewStateService.Clear();
                    if (trans.HasStarted()) trans.RollBack();
                    TaskDialog.Show("Merge Error", $"Failed to finalize merge:\n{ex.Message}");
                }
            }

            DiffMergePaneProvider.Instance?.Clear();
            HistoryPaneProvider.Instance?.Refresh();
        }

        private static void ApplyLocationChange(Document doc, Element element, Newtonsoft.Json.Linq.JObject newData)
        {
            var locationData = newData["location"] as Newtonsoft.Json.Linq.JObject;
            if (locationData == null) return;

            string locationType = locationData["type"]?.ToString();
            Location location = element.Location;
            if (location == null) return;

            if (location is LocationPoint locPoint && locationType == "point")
            {
                var point = locationData["point"] as Newtonsoft.Json.Linq.JObject;
                if (point == null) return;

                double px = 0, py = 0, pz = 0;
                double.TryParse(point["x"]?.ToString(), out px);
                double.TryParse(point["y"]?.ToString(), out py);
                double.TryParse(point["z"]?.ToString(), out pz);
                locPoint.Point = new XYZ(px, py, pz);

                if (locationData["rotation"] != null &&
                    double.TryParse(locationData["rotation"].ToString(), out double rotation))
                {
                    double diff = rotation - locPoint.Rotation;
                    if (Math.Abs(diff) > 0.0001)
                    {
                        Line axis = Line.CreateBound(locPoint.Point, locPoint.Point + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, element.Id, axis, diff);
                    }
                }
            }
            else if (location is LocationCurve locCurve && locationType == "curve")
            {
                var startPt = locationData["startPoint"] as Newtonsoft.Json.Linq.JObject;
                var endPt = locationData["endPoint"] as Newtonsoft.Json.Linq.JObject;
                if (startPt == null || endPt == null) return;

                double sx = 0, sy = 0, sz = 0, ex = 0, ey = 0, ez = 0;
                double.TryParse(startPt["x"]?.ToString(), out sx);
                double.TryParse(startPt["y"]?.ToString(), out sy);
                double.TryParse(startPt["z"]?.ToString(), out sz);
                double.TryParse(endPt["x"]?.ToString(), out ex);
                double.TryParse(endPt["y"]?.ToString(), out ey);
                double.TryParse(endPt["z"]?.ToString(), out ez);
                locCurve.Curve = Line.CreateBound(new XYZ(sx, sy, sz), new XYZ(ex, ey, ez));
            }
        }

        private static void ApplyParameterValue(Parameter param, object newValue, string elementName, Document doc)
        {
            switch (param.StorageType)
            {
                case StorageType.Double:
                    if (double.TryParse(newValue.ToString(), out double d))
                        param.Set(d);
                    break;
                case StorageType.Integer:
                    if (int.TryParse(newValue.ToString(), out int i))
                        param.Set(i);
                    break;
                case StorageType.String:
                    param.Set(newValue.ToString());
                    break;
                case StorageType.ElementId:
                    if (!string.IsNullOrEmpty(elementName))
                    {
                        var match = new FilteredElementCollector(doc)
                            .OfClass(typeof(ElementType))
                            .Cast<ElementType>()
                            .FirstOrDefault(t =>
                                (t.FamilyName + " : " + t.Name) == elementName ||
                                t.Name == elementName);
                        if (match != null)
                            param.Set(match.Id);
                    }
                    else if (int.TryParse(newValue.ToString(), out int eid))
                    {
                        param.Set(new ElementId(eid));
                    }
                    break;
            }
        }

        public string GetName() => "Diff Merge Finalize Handler";
    }
}
