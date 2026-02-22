using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace RevitVersionControl.Services
{
    /// <summary>
    /// ElementApplier - Applies pulled changes to the Revit document
    /// 
    /// Takes Change objects from the server and applies them to the user's open Revit model:
    /// - Deletes elements marked as "deleted"
    /// - Modifies parameters and locations for "modified" elements
    /// - Logs "added" elements (complex to create, may need manual review)
    /// 
    /// All changes happen within a single Revit Transaction (safe with rollback)
    /// </summary>
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

        /// <summary>
        /// Result of applying changes
        /// </summary>
        public class ApplyResult
        {
            public bool Success { get; set; }
            public int AppliedCount { get; set; }
            public int SkippedCount { get; set; }
            public int ErrorCount { get; set; }
            public List<string> Errors { get; set; }
            public string Summary { get; set; }
        }

        /// <summary>
        /// Main method: Apply all changes to the document
        /// 
        /// This method:
        /// 1. Wraps all operations in a single Revit Transaction
        /// 2. Iterates through all changes
        /// 3. For each change, applies the appropriate modification
        /// 4. If any error occurs, rolls back the entire transaction
        /// 5. Returns result summary
        /// </summary>
        public ApplyResult ApplyChanges(List<Change> changes)
        {
            _appliedCount = 0;
            _skippedCount = 0;
            _errors = new List<string>();

            // Validate input
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

            // Create a single transaction for all changes
            using (Transaction trans = new Transaction(_document, "Apply Remote Changes"))
            {
                trans.Start();

                try
                {
                    // Process each change
                    foreach (var change in changes)
                    {
                        if (change.ChangeType == "deleted")
                        {
                            ApplyDelete(change);
                        }
                        else if (change.ChangeType == "modified")
                        {
                            ApplyModified(change);
                        }
                        else if (change.ChangeType == "added")
                        {
                            ApplyAdd(change);
                        }
                    }

                    // If we got here, commit the transaction
                    trans.Commit();

                    return new ApplyResult
                    {
                        Success = true,
                        AppliedCount = _appliedCount,
                        SkippedCount = _skippedCount,
                        ErrorCount = _errors.Count,
                        Errors = _errors,
                        Summary = $"Successfully applied {_appliedCount} changes. " +
                                 $"Skipped {_skippedCount}. " +
                                 $"Errors: {_errors.Count}"
                    };
                }
                catch (Exception ex)
                {
                    // If anything goes wrong, rollback everything
                    trans.RollBack();
                    _errors.Add($"Transaction failed: {ex.Message}");

                    return new ApplyResult
                    {
                        Success = false,
                        AppliedCount = _appliedCount,
                        SkippedCount = _skippedCount,
                        ErrorCount = _errors.Count,
                        Errors = _errors,
                        Summary = $"Failed to apply changes. Transaction rolled back. Error: {ex.Message}"
                    };
                }
            }
        }

        /// <summary>
        /// Apply DELETED change - Remove element from document
        /// </summary>
        private void ApplyDelete(Change change)
        {
            try
            {
                // Try to find element by UniqueId
                Element element = null;
                try
                {
                    element = _document.GetElement(change.ElementId);
                }
                catch
                {
                    // GetElement might throw if UniqueId is invalid
                    element = null;
                }

                // Check if we found it and it's not pinned
                if (element != null && !element.Pinned)
                {
                    _document.Delete(element.Id);
                    _appliedCount++;
                    return;
                }

                // Could not delete (not found or pinned)
                _skippedCount++;
                if (element == null)
                {
                    _errors.Add($"Delete: Element {change.ElementId} not found in document");
                }
                else if (element.Pinned)
                {
                    _errors.Add($"Delete: Element {change.ElementId} is pinned (cannot delete pinned elements)");
                }
            }
            catch (Exception ex)
            {
                _skippedCount++;
                _errors.Add($"Delete failed for {change.ElementId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply MODIFIED change - Update parameters and/or location
        /// </summary>
        private void ApplyModified(Change change)
        {
            try
            {
                // Find the element to modify
                Element element = null;
                try
                {
                    element = _document.GetElement(change.ElementId);
                }
                catch
                {
                    element = null;
                }

                if (element == null)
                {
                    _skippedCount++;
                    _errors.Add($"Modified: Element {change.ElementId} not found in document");
                    return;
                }

                bool modified = false;

                // Apply parameter changes
                if (change.ParameterChanges != null && change.ParameterChanges.Count > 0)
                {
                    modified |= ApplyParameterChanges(element, change.ParameterChanges);
                }

                // Apply location changes
                if (change.LocationChanged && change.NewData != null)
                {
                    modified |= ApplyLocationChange(element, change.NewData);
                }

                if (modified)
                {
                    _appliedCount++;
                }
                else
                {
                    // No changes were actually applied (maybe parameters were already the same?)
                    _skippedCount++;
                }
            }
            catch (Exception ex)
            {
                _skippedCount++;
                _errors.Add($"Modified failed for {change.ElementId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply ADDED change - Log for manual creation
        /// 
        /// Note: Creating new elements in Revit is complex because:
        /// - Requires correct family, type, and category
        /// - Cannot assign UniqueId to newly created elements
        /// - Requires proper parameters and constraints
        /// - May need special factories for different element types
        /// 
        /// For now, we log these and the user may need to create them manually
        /// or implement a custom element factory for specific types.
        /// </summary>
        private void ApplyAdd(Change change)
        {
            try
            {
                // For safety and simplicity, we don't auto-create new elements
                // Log what should be added so user can review
                
                string details = "";
                if (change.NewData != null && change.NewData.ContainsKey("parameters"))
                {
                    var paramsDict = change.NewData["parameters"] as IDictionary<string, object>;
                    if (paramsDict != null && paramsDict.Count > 0)
                    {
                        details = string.Join(", ", paramsDict.Keys.Take(3).Select(k => $"{k}"));
                    }
                }

                _skippedCount++;
                _errors.Add(
                    $"Added: {change.Category} ({change.Type}) - Element ID: {change.ElementId}. " +
                    $"New elements cannot be auto-created. Manual creation required or use custom factory. " +
                    $"Parameters: {details}"
                );
            }
            catch (Exception ex)
            {
                _skippedCount++;
                _errors.Add($"Add processing failed for {change.ElementId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply parameter changes to an element
        /// 
        /// For each parameter change, tries to:
        /// 1. Find the parameter by name
        /// 2. Check if it's writable (not read-only)
        /// 3. Set the value based on storage type (Double, Integer, String, ElementId)
        /// </summary>
        private bool ApplyParameterChanges(Element element, List<ParameterChange> paramChanges)
        {
            bool changed = false;

            foreach (var paramChange in paramChanges)
            {
                try
                {
                    // Try to find the parameter
                    Parameter param = element.LookupParameter(paramChange.Name);

                    // Skip if not found or read-only
                    if (param == null)
                    {
                        continue;  // Parameter doesn't exist in this element
                    }

                    if (param.IsReadOnly)
                    {
                        continue;  // Cannot modify read-only parameters
                    }

                    // Handle different storage types
                    if (paramChange.NewValue == null)
                    {
                        // Setting to null/empty (not typically supported in Revit)
                        continue;
                    }

                    bool paramChanged = false;

                    // Based on storage type, set the appropriate value
                    switch (param.StorageType)
                    {
                        case StorageType.Double:
                            if (double.TryParse(paramChange.NewValue.ToString(), out double dblValue))
                            {
                                param.Set(dblValue);
                                paramChanged = true;
                            }
                            break;

                        case StorageType.Integer:
                            if (int.TryParse(paramChange.NewValue.ToString(), out int intValue))
                            {
                                param.Set(intValue);
                                paramChanged = true;
                            }
                            break;

                        case StorageType.String:
                            param.Set(paramChange.NewValue.ToString());
                            paramChanged = true;
                            break;

                        case StorageType.ElementId:
                            // ElementId values are trickier; try to parse as int
                            if (int.TryParse(paramChange.NewValue.ToString(), out int elemIdValue))
                            {
                                param.Set(new ElementId(elemIdValue));
                                paramChanged = true;
                            }
                            break;
                    }

                    if (paramChanged)
                    {
                        changed = true;
                    }
                }
                catch (Exception ex)
                {
                    // Log but continue processing other parameters
                    _errors.Add(
                        $"Parameter change failed for '{paramChange.Name}' = '{paramChange.NewValue}': {ex.Message}"
                    );
                }
            }

            return changed;
        }

        /// <summary>
        /// Apply location change - Move element to new position/rotation
        /// 
        /// Handles different location types:
        /// - LocationPoint: Single point + optional rotation
        /// - LocationCurve: Wall or line-based element (start + end points)
        /// - Transform: RVT links (complex, may need special handling)
        /// </summary>
        private bool ApplyLocationChange(Element element, Dictionary<string, object> newData)
        {
            try
            {
                // Extract location data from newData
                if (!newData.ContainsKey("location") || newData["location"] == null)
                {
                    return false;
                }

                var locationData = newData["location"] as JObject;
                if (locationData == null)
                {
                    return false;
                }

                string locationType = locationData["type"]?.ToString();
                Location location = element.Location;

                if (location == null)
                {
                    return false;
                }

                // Handle point location (furniture, equipment, etc.)
                if (location is LocationPoint locPoint && locationType == "point")
                {
                    var point = locationData["point"] as JObject;
                    if (point != null)
                    {
                        double x = point["x"]?.Value<double>() ?? 0;
                        double y = point["y"]?.Value<double>() ?? 0;
                        double z = point["z"]?.Value<double>() ?? 0;

                        var newPoint = new XYZ(x, y, z);
                        locPoint.Point = newPoint;

                        // Also apply rotation if present
                        if (locationData["rotation"] != null &&
                            double.TryParse(locationData["rotation"].ToString(), out double rotation))
                        {
                            double currentRotation = locPoint.Rotation;
                            double rotationDifference = rotation - currentRotation;

                            if (Math.Abs(rotationDifference) > 0.0001)
                            {
                                Line axis = Line.CreateBound(locPoint.Point, locPoint.Point + XYZ.BasisZ);
                                ElementTransformUtils.RotateElement(_document, element.Id, axis, rotationDifference);
                            }
                        }

                        return true;
                    }
                }

                // Handle curve location (walls, lines, etc.)
                if (location is LocationCurve locCurve && locationType == "curve")
                {
                    var startPt = locationData["startPoint"] as JObject;
                    var endPt = locationData["endPoint"] as JObject;

                    if (startPt != null && endPt != null)
                    {
                        double x1 = startPt["x"]?.Value<double>() ?? 0;
                        double y1 = startPt["y"]?.Value<double>() ?? 0;
                        double z1 = startPt["z"]?.Value<double>() ?? 0;

                        double x2 = endPt["x"]?.Value<double>() ?? 0;
                        double y2 = endPt["y"]?.Value<double>() ?? 0;
                        double z2 = endPt["z"]?.Value<double>() ?? 0;

                        try
                        {
                            var newStart = new XYZ(x1, y1, z1);
                            var newEnd = new XYZ(x2, y2, z2);

                            // Create new line from points
                            Line newLine = Line.CreateBound(newStart, newEnd);

                            // Replace the curve
                            locCurve.Curve = newLine;
                            return true;
                        }
                        catch (Exception ex)
                        {
                            _errors.Add($"Failed to create new curve for {element.Id}: {ex.Message}");
                            return false;
                        }
                    }
                }

                // Transform location (RVT links) - complex, skip for now
                if (locationType == "transform")
                {
                    _errors.Add($"Transform location changes not yet supported for {element.Id}");
                    return false;
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