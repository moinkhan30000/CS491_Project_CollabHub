using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace RevitVersionControl.Services
{
    public class ElementApplier
    {
        private readonly Document _document;
        private readonly ElementCreator _creator;
        private int _appliedCount;
        private int _skippedCount;
        private List<string> _errors;
        private List<string> _unsupportedElements;

        public ElementApplier(Document document)
        {
            _document = document;
            _creator  = new ElementCreator(document);
        }

        public class ApplyResult
        {
            public bool Success { get; set; }
            public int AppliedCount { get; set; }
            public int SkippedCount { get; set; }
            public int ErrorCount { get; set; }
            public List<string> Errors { get; set; }
            public List<string> UnsupportedElements { get; set; }
            public string Summary { get; set; }
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Returns family names required by incoming "added" changes that are
        /// not loaded in the document. Call this before ApplyChanges to warn
        /// the user proactively.
        /// </summary>
        public List<string> GetMissingFamilies(List<Change> changes) =>
            _creator.GetMissingFamilies(changes);

        /// <summary>
        /// Apply a list of changes (added / modified / deleted) to the document
        /// inside a single transaction.
        /// </summary>
        public ApplyResult ApplyChanges(List<Change> changes)
        {
            _appliedCount      = 0;
            _skippedCount      = 0;
            _errors            = new List<string>();
            _unsupportedElements = new List<string>();

            if (changes == null || changes.Count == 0)
            {
                return new ApplyResult
                {
                    Success            = true,
                    AppliedCount       = 0,
                    SkippedCount       = 0,
                    ErrorCount         = 0,
                    Errors             = new List<string>(),
                    UnsupportedElements = new List<string>(),
                    Summary            = "No changes to apply."
                };
            }

            using (Transaction trans = new Transaction(_document, "Apply Remote Changes"))
            {
                trans.Start();
                try
                {
                    foreach (var change in changes)
                    {
                        if (change.ChangeType == "deleted")
                            ApplyDelete(change);
                        else if (change.ChangeType == "modified")
                            ApplyModified(change);
                        else if (change.ChangeType == "added")
                            ApplyAdd(change);
                    }

                    trans.Commit();

                    return new ApplyResult
                    {
                        Success             = true,
                        AppliedCount        = _appliedCount,
                        SkippedCount        = _skippedCount,
                        ErrorCount          = _errors.Count,
                        Errors              = _errors,
                        UnsupportedElements = _unsupportedElements,
                        Summary = $"Applied {_appliedCount}, Skipped {_skippedCount}, " +
                                  $"Unsupported {_unsupportedElements.Count}, Errors {_errors.Count}"
                    };
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    _errors.Add($"Transaction failed: {ex.Message}");
                    return new ApplyResult
                    {
                        Success             = false,
                        AppliedCount        = _appliedCount,
                        SkippedCount        = _skippedCount,
                        ErrorCount          = _errors.Count,
                        Errors              = _errors,
                        UnsupportedElements = _unsupportedElements,
                        Summary = $"Failed. Rolled back. Error: {ex.Message}"
                    };
                }
            }
        }

        // -----------------------------------------------------------------------
        // Private apply methods
        // -----------------------------------------------------------------------

        private void ApplyDelete(Change change)
        {
            try
            {
                Element element = _document.GetElement(change.ElementId);

                if (element != null && !element.Pinned)
                {
                    _document.Delete(element.Id);
                    _appliedCount++;
                }
                else
                {
                    _skippedCount++;
                    _errors.Add(element == null
                        ? $"Delete: Element {change.ElementId} not found"
                        : $"Delete: Element {change.ElementId} is pinned");
                }
            }
            catch (Exception ex)
            {
                _skippedCount++;
                _errors.Add($"Delete failed for {change.ElementId}: {ex.Message}");
            }
        }

        private void ApplyModified(Change change)
        {
            try
            {
                Element element = null;
                try { element = _document.GetElement(change.ElementId); } catch { }

                if (element == null)
                {
                    _skippedCount++;
                    _errors.Add($"Modified: Element {change.ElementId} not found");
                    return;
                }

                bool modified = false;

                if (change.ParameterChanges != null && change.ParameterChanges.Count > 0)
                    modified |= ApplyParameterChanges(element, change.ParameterChanges);

                if (change.LocationChanged && change.NewData != null)
                    modified |= ApplyLocationChange(element, change.NewData);

                if (modified) _appliedCount++;
                else _skippedCount++;
            }
            catch (Exception ex)
            {
                _skippedCount++;
                _errors.Add($"Modified failed for {change.ElementId}: {ex.Message}");
            }
        }

        private void ApplyAdd(Change change)
        {
            try
            {
                if (change.NewData == null)
                {
                    _skippedCount++;
                    _errors.Add($"Add: No data for element {change.ElementId}");
                    return;
                }

                var newData = JObject.FromObject(change.NewData);

                // Delegate all creation logic to ElementCreator
                CreationResult result = _creator.Create(newData);

                if (result.IsUnsupported)
                {
                    _skippedCount++;
                    _unsupportedElements.Add($"[{change.ElementId}] {result.Reason}");
                    return;
                }

                if (result.Element == null)
                {
                    _skippedCount++;
                    _errors.Add($"Add failed for {change.ElementId}: {result.Reason}");
                    return;
                }

                // Apply parameters to the newly created element
                var parameters = newData["parameters"] as JObject;
                if (parameters != null)
                    ApplyParametersFromJObject(result.Element, parameters);

                // Surface placement warnings (e.g. hosted fallback) without failing
                if (!string.IsNullOrEmpty(result.Reason))
                    _errors.Add($"Add warning for {change.ElementId}: {result.Reason}");

                _appliedCount++;
            }
            catch (Exception ex)
            {
                _skippedCount++;
                _errors.Add($"Add failed for {change.ElementId}: {ex.Message}");
            }
        }

        // -----------------------------------------------------------------------
        // Parameter helpers (unchanged)
        // -----------------------------------------------------------------------

        private bool ApplyParameterChanges(Element element, List<ParameterChange> paramChanges)
        {
            bool changed = false;
            foreach (var paramChange in paramChanges)
            {
                try
                {
                    Parameter param = element.LookupParameter(paramChange.Name);
                    if (param == null || param.IsReadOnly || paramChange.NewValue == null) continue;

                    switch (param.StorageType)
                    {
                        case StorageType.Double:
                            if (double.TryParse(paramChange.NewValue.ToString(), out double d))
                            { param.Set(d); changed = true; }
                            break;

                        case StorageType.Integer:
                            if (int.TryParse(paramChange.NewValue.ToString(), out int i))
                            { param.Set(i); changed = true; }
                            break;

                        case StorageType.String:
                            param.Set(paramChange.NewValue.ToString());
                            changed = true;
                            break;

                        case StorageType.ElementId:
                            // Prefer name-based lookup over raw integer ID.
                            // The integer ID is local to each Revit file and will be
                            // different on the receiving user's model. elementName is
                            // the human-readable name stored during extraction.
                            if (!string.IsNullOrEmpty(paramChange.ElementName))
                            {
                                ElementId resolvedId = ResolveElementIdByName(paramChange.ElementName);
                                if (resolvedId != null)
                                {
                                    param.Set(resolvedId);
                                    changed = true;
                                    break;
                                }
                                // Name lookup failed — log and skip rather than
                                // applying the wrong integer ID from the source model.
                                _errors.Add(
                                    $"Parameter '{paramChange.Name}': " +
                                    $"could not find element named '{paramChange.ElementName}' in this model.");
                            }
                            else if (int.TryParse(paramChange.NewValue.ToString(), out int eid))
                            {
                                // Fallback for old data that has no elementName stored.
                                param.Set(new ElementId(eid));
                                changed = true;
                            }
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _errors.Add($"Parameter '{paramChange.Name}': {ex.Message}");
                }
            }
            return changed;
        }

        /// <summary>
        /// Resolves a stored element name back to a local ElementId.
        /// Searches ElementTypes first (WallType, FloorType, FamilySymbol, etc.)
        /// then falls back to named elements (Level, Phase, Material).
        /// The name format from extraction is "FamilyName : TypeName" for types
        /// and plain Name for everything else.
        /// </summary>
        private ElementId ResolveElementIdByName(string elementName)
        {
            if (string.IsNullOrEmpty(elementName))
                return null;

            try
            {
                // ElementType — covers WallType, FloorType, FamilySymbol, RoofType etc.
                // Stored format: "FamilyName : TypeName"
                var typeMatch = new FilteredElementCollector(_document)
                    .OfClass(typeof(ElementType))
                    .Cast<ElementType>()
                    .FirstOrDefault(t =>
                        (t.FamilyName + " : " + t.Name) == elementName ||
                        t.Name == elementName);

                if (typeMatch != null)
                    return typeMatch.Id;

                // Named elements — Level, Phase, Material, etc.
                var namedMatch = new FilteredElementCollector(_document)
                    .WhereElementIsNotElementType()
                    .Cast<Element>()
                    .FirstOrDefault(e =>
                        !string.IsNullOrEmpty(e.Name) && e.Name == elementName);

                if (namedMatch != null)
                    return namedMatch.Id;
            }
            catch { }

            return null;
        }

        private void ApplyParametersFromJObject(Element element, JObject parameters)
        {
            foreach (var prop in parameters.Properties())
            {
                try
                {
                    var paramData = prop.Value as JObject;
                    if (paramData == null) continue;

                    Parameter param = element.LookupParameter(prop.Name);
                    if (param == null || param.IsReadOnly) continue;

                    var value = paramData["value"];
                    if (value == null) continue;

                    switch (param.StorageType)
                    {
                        case StorageType.Double:
                            if (double.TryParse(value.ToString(), out double d))
                                param.Set(d);
                            break;
                        case StorageType.Integer:
                            if (int.TryParse(value.ToString(), out int i))
                                param.Set(i);
                            break;
                        case StorageType.String:
                            param.Set(value.ToString());
                            break;
                        case StorageType.ElementId:
                            if (int.TryParse(value.ToString(), out int eid))
                                param.Set(new ElementId(eid));
                            break;
                    }
                }
                catch { }
            }
        }

        private bool ApplyLocationChange(Element element, Dictionary<string, object> newData)
        {
            try
            {
                if (!newData.ContainsKey("location") || newData["location"] == null) return false;

                var locationData = newData["location"] as JObject;
                if (locationData == null) return false;

                string locationType = locationData["type"]?.ToString();
                Location location = element.Location;
                if (location == null) return false;

                if (location is LocationPoint locPoint && locationType == "point")
                {
                    var point = locationData["point"] as JObject;
                    if (point == null) return false;

                    locPoint.Point = new XYZ(
                        point["x"]?.Value<double>() ?? 0,
                        point["y"]?.Value<double>() ?? 0,
                        point["z"]?.Value<double>() ?? 0);

                    if (locationData["rotation"] != null &&
                        double.TryParse(locationData["rotation"].ToString(), out double rotation))
                    {
                        double diff = rotation - locPoint.Rotation;
                        if (Math.Abs(diff) > 0.0001)
                        {
                            Line axis = Line.CreateBound(locPoint.Point, locPoint.Point + XYZ.BasisZ);
                            ElementTransformUtils.RotateElement(_document, element.Id, axis, diff);
                        }
                    }
                    return true;
                }

                if (location is LocationCurve locCurve && locationType == "curve")
                {
                    var startPt = locationData["startPoint"] as JObject;
                    var endPt = locationData["endPoint"] as JObject;
                    if (startPt == null || endPt == null) return false;

                    locCurve.Curve = Line.CreateBound(
                        new XYZ(startPt["x"]?.Value<double>() ?? 0, startPt["y"]?.Value<double>() ?? 0, startPt["z"]?.Value<double>() ?? 0),
                        new XYZ(endPt["x"]?.Value<double>() ?? 0, endPt["y"]?.Value<double>() ?? 0, endPt["z"]?.Value<double>() ?? 0));
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _errors.Add($"Location change failed: {ex.Message}");
                return false;
            }
        }
    }
}