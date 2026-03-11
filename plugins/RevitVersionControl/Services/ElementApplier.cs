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
        private int _appliedCount = 0;
        private int _skippedCount = 0;
        private List<string> _errors = new List<string>();

        public ElementApplier(Document document)
        {
            _document = document;
        }

        public class ApplyResult
        {
            public bool Success { get; set; }
            public int AppliedCount { get; set; }
            public int SkippedCount { get; set; }
            public int ErrorCount { get; set; }
            public List<string> Errors { get; set; }
            public string Summary { get; set; }
        }

        public ApplyResult ApplyChanges(List<Change> changes)
        {
            _appliedCount = 0;
            _skippedCount = 0;
            _errors = new List<string>();

            if (changes == null || changes.Count == 0)
            {
                return new ApplyResult
                {
                    Success = true,
                    AppliedCount = 0,
                    SkippedCount = 0,
                    ErrorCount = 0,
                    Errors = new List<string>(),
                    Summary = "No changes to apply."
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
                        Success = true,
                        AppliedCount = _appliedCount,
                        SkippedCount = _skippedCount,
                        ErrorCount = _errors.Count,
                        Errors = _errors,
                        Summary = $"Applied {_appliedCount}, Skipped {_skippedCount}, Errors {_errors.Count}"
                    };
                }
                catch (Exception ex)
                {
                    trans.RollBack();
                    _errors.Add($"Transaction failed: {ex.Message}");
                    return new ApplyResult
                    {
                        Success = false,
                        AppliedCount = _appliedCount,
                        SkippedCount = _skippedCount,
                        ErrorCount = _errors.Count,
                        Errors = _errors,
                        Summary = $"Failed. Rolled back. Error: {ex.Message}"
                    };
                }
            }
        }

        private void ApplyDelete(Change change)
        {
            try
            {
                Element element = null;
                try { element = _document.GetElement(change.ElementId); } catch { }

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
                string category = newData["category"]?.ToString() ?? "";
                string familyName = newData["familyName"]?.ToString() ?? "";
                string typeName = newData["typeName"]?.ToString() ?? "";
                var locationData = newData["location"] as JObject;

                // Route to correct creation method by category
                Element created = null;

                if (category == "Walls")
                    created = CreateWall(newData, locationData);
                else if (category == "Floors")
                    created = CreateFloor(newData, locationData);
                else
                    created = CreateFamilyInstance(familyName, typeName, locationData, newData);

                if (created != null)
                {
                    // Apply parameters after creation
                    var parameters = newData["parameters"] as JObject;
                    if (parameters != null)
                        ApplyParametersFromJObject(created, parameters);

                    _appliedCount++;
                }
                else
                {
                    _skippedCount++;
                    _errors.Add($"Add: Could not create {category} '{familyName}:{typeName}' ({change.ElementId})");
                }
            }
            catch (Exception ex)
            {
                _skippedCount++;
                _errors.Add($"Add failed for {change.ElementId}: {ex.Message}");
            }
        }

        private Element CreateWall(JObject newData, JObject locationData)
        {
            if (locationData == null) return null;

            var startPt = locationData["startPoint"] as JObject;
            var endPt = locationData["endPoint"] as JObject;
            if (startPt == null || endPt == null) return null;

            var start = new XYZ(
                startPt["x"]?.Value<double>() ?? 0,
                startPt["y"]?.Value<double>() ?? 0,
                startPt["z"]?.Value<double>() ?? 0);

            var end = new XYZ(
                endPt["x"]?.Value<double>() ?? 0,
                endPt["y"]?.Value<double>() ?? 0,
                endPt["z"]?.Value<double>() ?? 0);

            if (start.DistanceTo(end) < 0.01) return null;

            // Find wall type by typeName
            string typeName = newData["typeName"]?.ToString() ?? "";
            WallType wallType = new FilteredElementCollector(_document)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(wt => wt.Name == typeName)
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(WallType))
                    .Cast<WallType>()
                    .FirstOrDefault();

            if (wallType == null) return null;

            // Find level
            Level level = GetNearestLevel(start.Z);
            if (level == null) return null;

            Line line = Line.CreateBound(start, end);
            return Wall.Create(_document, line, wallType.Id, level.Id, 10.0, 0, false, false);
        }

        private Element CreateFloor(JObject newData, JObject locationData)
        {
            var geometry = newData["geometry"] as JObject;
            var bbox = geometry?["boundingBox"] as JObject;
            if (bbox == null) return null;

            var min = bbox["min"] as JObject;
            var max = bbox["max"] as JObject;
            if (min == null || max == null) return null;

            double x1 = min["x"]?.Value<double>() ?? 0;
            double y1 = min["y"]?.Value<double>() ?? 0;
            double z  = min["z"]?.Value<double>() ?? 0;
            double x2 = max["x"]?.Value<double>() ?? 0;
            double y2 = max["y"]?.Value<double>() ?? 0;

            string typeName = newData["typeName"]?.ToString() ?? "";
            FloorType floorType = new FilteredElementCollector(_document)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault(ft => ft.Name == typeName)
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(FloorType))
                    .Cast<FloorType>()
                    .FirstOrDefault();

            if (floorType == null) return null;

            Level level = GetNearestLevel(z);
            if (level == null) return null;

            var curveLoop = new CurveLoop();
            curveLoop.Append(Line.CreateBound(new XYZ(x1, y1, z), new XYZ(x2, y1, z)));
            curveLoop.Append(Line.CreateBound(new XYZ(x2, y1, z), new XYZ(x2, y2, z)));
            curveLoop.Append(Line.CreateBound(new XYZ(x2, y2, z), new XYZ(x1, y2, z)));
            curveLoop.Append(Line.CreateBound(new XYZ(x1, y2, z), new XYZ(x1, y1, z)));

            return Floor.Create(_document, new List<CurveLoop> { curveLoop }, floorType.Id, level.Id);
        }

        private Element CreateFamilyInstance(string familyName, string typeName, JObject locationData, JObject newData)
        {
            // Find family symbol by familyName + typeName
            FamilySymbol symbol = new FilteredElementCollector(_document)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                    fs.FamilyName == familyName && fs.Name == typeName)
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs => fs.FamilyName == familyName);

            if (symbol == null) return null;

            if (!symbol.IsActive)
                symbol.Activate();

            Level level = null;
            XYZ location = XYZ.Zero;

            if (locationData != null)
            {
                string locType = locationData["type"]?.ToString();
                if (locType == "point")
                {
                    var pt = locationData["point"] as JObject;
                    if (pt != null)
                    {
                        location = new XYZ(
                            pt["x"]?.Value<double>() ?? 0,
                            pt["y"]?.Value<double>() ?? 0,
                            pt["z"]?.Value<double>() ?? 0);
                    }
                }
                level = GetNearestLevel(location.Z);
            }

            level = level ?? new FilteredElementCollector(_document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault();

            if (level == null) return null;

            return _document.Create.NewFamilyInstance(
                location, symbol, level, StructuralType.NonStructural);
        }

        private Level GetNearestLevel(double elevation)
        {
            return new FilteredElementCollector(_document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - elevation))
                .FirstOrDefault();
        }

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
                            if (int.TryParse(paramChange.NewValue.ToString(), out int eid))
                            { param.Set(new ElementId(eid)); changed = true; }
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