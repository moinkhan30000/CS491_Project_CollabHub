using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
    public class ExtractionOptions
    {
        public int BatchSize { get; set; } = 200;
        public int PauseMilliseconds { get; set; } = 10;
        public bool IncludeGeometry { get; set; } = true;
        public bool LogProgress { get; set; } = true;
    }

    /// <summary>
    /// Extracts element-level data from Revit model
    /// Converts elements to JSON-serializable format
    /// </summary>
    public class ElementExtractor
    {
        private readonly Document _document;

        public ElementExtractor(Document document)
        {
            _document = document;
        }

        /// <summary>
        /// Extract all elements from the current model
        /// </summary>
        public List<JObject> ExtractAllElements()
        {
            return ExtractAllElements(new ExtractionOptions());
        }

        /// <summary>
        /// Extract all elements with configurable throttling/logging options
        /// </summary>
        public List<JObject> ExtractAllElements(ExtractionOptions options)
        {
            var elements = new List<JObject>();
            var extractionOptions = options ?? new ExtractionOptions();
            int processed = 0;

            // Get all model elements (excluding views, sheets, etc.)
            FilteredElementCollector collector = new FilteredElementCollector(_document)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            if (extractionOptions.LogProgress)
            {
                LogProgress($"Extraction started. Element count: {collector.GetElementCount()}");
            }

            foreach (Element element in collector)
            {
                // Skip elements we don't want to track
                if (ShouldSkipElement(element))
                    continue;

                try
                {
                    var elementData = ExtractElement(element, extractionOptions.IncludeGeometry);
                    if (elementData != null)
                    {
                        elements.Add(elementData);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue processing
                    Console.WriteLine($"Failed to extract element {element.Id}: {ex.Message}");
                    if (extractionOptions.LogProgress)
                    {
                        LogProgress($"Failed element {element.Id} ({element.UniqueId}): {ex.Message}");
                    }
                }

                processed++;
                if (extractionOptions.BatchSize > 0 && processed % extractionOptions.BatchSize == 0)
                {
                    if (extractionOptions.LogProgress)
                    {
                        LogProgress($"Processed {processed} elements. Last: {element.Id} ({element.UniqueId})");
                    }

                    if (extractionOptions.PauseMilliseconds > 0)
                    {
                        Thread.Sleep(extractionOptions.PauseMilliseconds);
                    }
                }
            }

            if (extractionOptions.LogProgress)
            {
                LogProgress($"Extraction completed. Extracted {elements.Count} elements.");
            }

            return elements;
        }

        /// <summary>
        /// Extract data from a single element
        /// </summary>
        public JObject ExtractElement(Element element, bool includeGeometry)
        {
            if (element == null)
                return null;

            var parameters = ExtractParameters(element);
            var location = ExtractLocation(element);
            if (location != null && !location.HasValues)
            {
                location = null;
            }

            var elementData = new JObject
            {
                ["id"] = element.UniqueId,
                ["category"] = element.Category?.Name ?? "Unknown",
                ["type"] = GetElementTypeName(element),
                ["parameters"] = parameters,
                ["location"] = location,
                ["geometry"] = includeGeometry ? ExtractGeometry(element, location) : null
            };

            var typeInfo = GetElementTypeInfo(element);
            if (typeInfo.HasValue)
            {
                elementData["familyName"] = typeInfo.Value.FamilyName;
                elementData["typeName"] = typeInfo.Value.TypeName;
            }

            // Add optional properties
            if (element.WorksetId != null && element.WorksetId != WorksetId.InvalidWorksetId)
            {
                elementData["worksetId"] = element.WorksetId.ToString();
            }

            if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId)
            {
                elementData["levelId"] = element.LevelId.ToString();
            }

            // Phase information
            var phaseCreated = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phaseCreated != null && phaseCreated.HasValue)
            {
                elementData["phaseCreated"] = GetPhaseName(phaseCreated.AsElementId());
            }

            var phaseDemolished = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
            if (phaseDemolished != null && phaseDemolished.HasValue)
            {
                elementData["phaseDemolished"] = GetPhaseName(phaseDemolished.AsElementId());
            }

            return elementData;
        }

        private JObject ExtractParameters(Element element)
        {
            var parameters = new JObject();

            var orderedParams = element.Parameters
                .Cast<Parameter>()
                .Where(p => p != null)
                .OrderBy(p => p.Definition?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            foreach (Parameter param in orderedParams)
            {
                try
                {
                    if (!param.HasValue)
                        continue;

                    var paramData = new JObject
                    {
                        ["value"] = JToken.FromObject(GetParameterValue(param)),
                        ["type"] = param.Definition.GetDataType().TypeId ?? "Unknown",
                        ["isReadOnly"] = param.IsReadOnly,
                        ["storageType"] = param.StorageType.ToString()
                    };

                    parameters[param.Definition.Name] = paramData;
                }
                catch
                {
                    // Skip parameters that can't be read
                }
            }

            return parameters;
        }

        private object GetParameterValue(Parameter param)
        {
            switch (param.StorageType)
            {
                case StorageType.String:
                    return param.AsString() ?? "";
                
                case StorageType.Integer:
                    return param.AsInteger();
                
                case StorageType.Double:
                    return NormalizeDouble(param.AsDouble());
                
                case StorageType.ElementId:
                    var elemId = param.AsElementId();
                    return elemId != null ? elemId.ToString() : "";
                
                default:
                    return param.AsValueString() ?? "";
            }
        }

        private JObject ExtractGeometry(Element element, JObject locationData)
        {
            var geometryData = new JObject();

            try
            {
                BoundingBoxXYZ bbox = element.get_BoundingBox(null);
                if (bbox != null)
                {
                    geometryData["boundingBox"] = new JObject
                    {
                        ["min"] = new JObject
                        {
                            ["x"] = bbox.Min.X,
                            ["y"] = bbox.Min.Y,
                            ["z"] = bbox.Min.Z
                        },
                        ["max"] = new JObject
                        {
                            ["x"] = bbox.Max.X,
                            ["y"] = bbox.Max.Y,
                            ["z"] = bbox.Max.Z
                        }
                    };
                }

                // Compute geometry hash for change detection
                geometryData["geometryHash"] = ComputeGeometryHash(element, bbox, locationData);
            }
            catch
            {
                // Geometry extraction failed
            }

            if (!geometryData.HasValues)
            {
                return null;
            }

            return geometryData;
        }

        private JObject ExtractLocation(Element element)
        {
            var locationData = new JObject();

            try
            {
                Location location = element.Location;

                if (location is LocationPoint locationPoint)
                {
                    XYZ point = locationPoint.Point;
                    locationData["type"] = "point";
                    locationData["point"] = new JObject
                    {
                        ["x"] = point.X,
                        ["y"] = point.Y,
                        ["z"] = point.Z
                    };
                    locationData["rotation"] = locationPoint.Rotation;
                }
                else if (location is LocationCurve locationCurve)
                {
                    Curve curve = locationCurve.Curve;
                    locationData["type"] = "curve";
                    locationData["startPoint"] = new JObject
                    {
                        ["x"] = curve.GetEndPoint(0).X,
                        ["y"] = curve.GetEndPoint(0).Y,
                        ["z"] = curve.GetEndPoint(0).Z
                    };
                    locationData["endPoint"] = new JObject
                    {
                        ["x"] = curve.GetEndPoint(1).X,
                        ["y"] = curve.GetEndPoint(1).Y,
                        ["z"] = curve.GetEndPoint(1).Z
                    };
                }
            }
            catch
            {
                // Location extraction failed
            }

            return locationData;
        }

        private string GetElementTypeName(Element element)
        {
            try
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    ElementType elemType = _document.GetElement(typeId) as ElementType;
                    if (elemType != null)
                    {
                        return elemType.FamilyName + ": " + elemType.Name;
                    }
                }
            }
            catch { }

            return element.Name ?? "Unknown";
        }

        private (string FamilyName, string TypeName)? GetElementTypeInfo(Element element)
        {
            try
            {
                ElementId typeId = element.GetTypeId();
                if (typeId != ElementId.InvalidElementId)
                {
                    ElementType elemType = _document.GetElement(typeId) as ElementType;
                    if (elemType != null)
                    {
                        return (elemType.FamilyName, elemType.Name);
                    }
                }
            }
            catch
            {
                // Ignore failures
            }

            return null;
        }

        private string GetPhaseName(ElementId phaseId)
        {
            if (phaseId == null || phaseId == ElementId.InvalidElementId)
                return null;

            try
            {
                Phase phase = _document.GetElement(phaseId) as Phase;
                return phase?.Name;
            }
            catch
            {
                return null;
            }
        }

        private string ComputeGeometryHash(Element element, BoundingBoxXYZ bbox, JObject locationData)
        {
            var builder = new StringBuilder();
            builder.Append(element.Category?.Name ?? "Unknown");
            builder.Append("|");
            builder.Append(GetElementTypeName(element));
            builder.Append("|");

            if (bbox != null)
            {
                builder.Append("bbox:");
                builder.Append(FormatDouble(bbox.Min.X)).Append(",");
                builder.Append(FormatDouble(bbox.Min.Y)).Append(",");
                builder.Append(FormatDouble(bbox.Min.Z)).Append("|");
                builder.Append(FormatDouble(bbox.Max.X)).Append(",");
                builder.Append(FormatDouble(bbox.Max.Y)).Append(",");
                builder.Append(FormatDouble(bbox.Max.Z)).Append("|");
            }

            if (locationData != null)
            {
                builder.Append("loc:");
                builder.Append(locationData.ToString(Newtonsoft.Json.Formatting.None));
            }

            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
                byte[] hash = sha256.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }

        private static double NormalizeDouble(double value)
        {
            return Math.Round(value, 6);
        }

        private static string FormatDouble(double value)
        {
            return NormalizeDouble(value).ToString("R", CultureInfo.InvariantCulture);
        }

        private bool ShouldSkipElement(Element element)
        {
            // Skip views, sheets, schedules, etc.
            if (element is View || element is ViewSheet || element is ScheduleSheetInstance)
                return true;

            // Skip element types
            if (element is ElementType)
                return true;

            // Skip sketch elements
            if (element.Category?.Name?.Contains("Sketch") == true)
                return true;

            return false;
        }

        private static void LogProgress(string message)
        {
            try
            {
                var baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevitVersionControl");
                Directory.CreateDirectory(baseDir);
                var path = Path.Combine(baseDir, "extractor.log");
                File.AppendAllText(path, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
            }
            catch
            {
                // Ignore logging failures
            }
        }
    }
}
