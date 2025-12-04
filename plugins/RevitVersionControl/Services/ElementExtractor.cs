using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
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
            var elements = new List<JObject>();

            // Get all model elements (excluding views, sheets, etc.)
            FilteredElementCollector collector = new FilteredElementCollector(_document)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            foreach (Element element in collector)
            {
                // Skip elements we don't want to track
                if (ShouldSkipElement(element))
                    continue;

                try
                {
                    var elementData = ExtractElement(element);
                    if (elementData != null)
                    {
                        elements.Add(elementData);
                    }
                }
                catch (Exception ex)
                {
                    // Log error but continue processing
                    Console.WriteLine($"Failed to extract element {element.Id}: {ex.Message}");
                }
            }

            return elements;
        }

        /// <summary>
        /// Extract data from a single element
        /// </summary>
        public JObject ExtractElement(Element element)
        {
            if (element == null)
                return null;

            var elementData = new JObject
            {
                ["id"] = element.UniqueId,
                ["category"] = element.Category?.Name ?? "Unknown",
                ["type"] = GetElementTypeName(element),
                ["parameters"] = ExtractParameters(element),
                ["geometry"] = ExtractGeometry(element),
                ["location"] = ExtractLocation(element)
            };

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

            foreach (Parameter param in element.Parameters)
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
                    return param.AsDouble();
                
                case StorageType.ElementId:
                    var elemId = param.AsElementId();
                    return elemId != null ? elemId.ToString() : "";
                
                default:
                    return param.AsValueString() ?? "";
            }
        }

        private JObject ExtractGeometry(Element element)
        {
            var geometryData = new JObject();

            try
            {
                Options options = new Options
                {
                    ComputeReferences = false,
                    DetailLevel = ViewDetailLevel.Coarse
                };

                GeometryElement geomElement = element.get_Geometry(options);
                if (geomElement == null)
                    return geometryData;

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
                geometryData["geometryHash"] = ComputeGeometryHash(geomElement);
            }
            catch
            {
                // Geometry extraction failed
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

        private string ComputeGeometryHash(GeometryElement geomElement)
        {
            // Simplified hash computation
            // In production, would compute actual hash of geometry data
            return Guid.NewGuid().ToString("N").Substring(0, 16);
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
    }
}
