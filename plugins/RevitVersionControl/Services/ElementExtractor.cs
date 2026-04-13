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
using Newtonsoft.Json;
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

    public class ElementExtractor
    {
        private readonly Document _document;

        public ElementExtractor(Document document)
        {
            _document = document;
        }

        public List<JObject> ExtractAllElements()
        {
            return ExtractAllElements(new ExtractionOptions());
        }

        public List<JObject> ExtractAllElements(ExtractionOptions options)
        {
            var elements = new List<JObject>();
            var extractionOptions = options ?? new ExtractionOptions();
            int processed = 0;

            FilteredElementCollector collector = new FilteredElementCollector(_document)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent();

            if (extractionOptions.LogProgress)
            {
                LogProgress($"Extraction started. Element count: {collector.GetElementCount()}");
            }

            foreach (Element element in collector)
            {
                if (ShouldSkipElement(element))
                    continue;

                try
                {
                    if (extractionOptions.LogProgress)
                    {
                        LogProgress($"Inspecting {DescribeElement(element)}");
                    }

                    var elementData = ExtractElement(element, extractionOptions.IncludeGeometry);
                    if (elementData != null)
                    {
                        elements.Add(elementData);
                    }
                }
                catch (Exception ex)
                {
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

        public JObject ExtractElement(Element element, bool includeGeometry)
        {
            if (element == null)
                return null;

            if (element is RevitLinkInstance linkInstance)
            {
                return ExtractLinkElement(linkInstance);
            }

            if (IsMinimalElement(element))
            {
                return ExtractMinimalElement(element);
            }

            var parameters = ExtractParameters(element);
            var location = ExtractLocation(element);
            if (location != null && !location.HasValues)
                location = null;

            string repoGuid = RepoGuidService.GetRepoGuid(element);
            var elementData = new JObject
            {
                ["id"] = element.UniqueId,
                ["category"] = element.Category?.Name ?? "Unknown",
                ["type"] = GetElementTypeName(element),
                ["parameters"] = parameters,
                ["location"] = location,
                ["geometry"] = includeGeometry ? ExtractGeometry(element, location) : null
            };

            if (!string.IsNullOrWhiteSpace(repoGuid))
                elementData["repoGuid"] = repoGuid;

            var typeInfo = GetElementTypeInfo(element);
            if (typeInfo.HasValue)
            {
                elementData["familyName"] = typeInfo.Value.FamilyName;
                elementData["typeName"] = typeInfo.Value.TypeName;
            }

            if (element.WorksetId != null && element.WorksetId != WorksetId.InvalidWorksetId)
                elementData["worksetId"] = element.WorksetId.ToString();

            if (element.LevelId != null && element.LevelId != ElementId.InvalidElementId)
                elementData["levelId"] = element.LevelId.ToString();

            var phaseCreated = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (phaseCreated != null && phaseCreated.HasValue)
                elementData["phaseCreated"] = GetPhaseName(phaseCreated.AsElementId());

            var phaseDemolished = element.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
            if (phaseDemolished != null && phaseDemolished.HasValue)
                elementData["phaseDemolished"] = GetPhaseName(phaseDemolished.AsElementId());

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

                    if (param.StorageType == StorageType.ElementId)
                    {
                        string referencedName = GetElementIdParamName(param.AsElementId());
                        if (referencedName != null)
                            paramData["elementName"] = referencedName;
                    }

                    parameters[param.Definition.Name] = paramData;
                }
                catch
                {
                    // Ignore unreadable parameters.
                }
            }

            return parameters;
        }

        private string GetElementIdParamName(ElementId elementId)
        {
            if (elementId == null || elementId == ElementId.InvalidElementId)
                return null;

            try
            {
                Element referenced = _document.GetElement(elementId);
                if (referenced == null)
                    return null;

                if (referenced is ElementType elementType)
                    return elementType.FamilyName + " : " + elementType.Name;

                if (!string.IsNullOrEmpty(referenced.Name))
                    return referenced.Name;
            }
            catch
            {
                return null;
            }

            return null;
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
                        ["min"] = new JObject { ["x"] = bbox.Min.X, ["y"] = bbox.Min.Y, ["z"] = bbox.Min.Z },
                        ["max"] = new JObject { ["x"] = bbox.Max.X, ["y"] = bbox.Max.Y, ["z"] = bbox.Max.Z }
                    };
                }

                geometryData["geometryHash"] = ComputeGeometryHash(element, bbox, locationData);
            }
            catch
            {
                // Ignore geometry extraction errors.
            }

            return geometryData.HasValues ? geometryData : null;
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
                    locationData["point"] = new JObject { ["x"] = point.X, ["y"] = point.Y, ["z"] = point.Z };
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
                // Ignore location extraction errors.
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
                        return elemType.FamilyName + ": " + elemType.Name;
                }
            }
            catch
            {
                // Ignore type lookup failures.
            }

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
                        return (elemType.FamilyName, elemType.Name);
                }
            }
            catch
            {
                // Ignore type lookup failures.
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
                builder.Append(JsonConvert.SerializeObject(locationData));
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
            if (element is View || element is ViewSheet || element is ScheduleSheetInstance)
                return true;
            if (element is ElementType)
                return true;
            if (element.Category?.Name?.Contains("Sketch") == true)
                return true;
            if (element.Category == null)
                return true;

            string category = element.Category.Name ?? string.Empty;
            if (SyncCategoryRules.IsAutoGeneratedOrInternalCategory(category))
                return true;

            return false;
        }

        private bool IsMinimalElement(Element element)
        {
            if (element == null)
                return false;
            if (element is Material)
                return true;

            string category = element.Category?.Name ?? string.Empty;
            if (category == "RVT Links" || category == "Materials")
                return true;
            if (SyncCategoryRules.IsAutoGeneratedOrInternalCategory(category))
                return true;

            string typeName = element.GetType().Name ?? string.Empty;
            if (category.Length == 0 && (typeName == "Element" || typeName == "Family" || typeName == "GraphicsStyle"))
                return true;

            return false;
        }

        private JObject ExtractMinimalElement(Element element)
        {
            var data = new JObject
            {
                ["id"] = element.UniqueId,
                ["category"] = element.Category?.Name ?? "Unknown",
                ["type"] = element.GetType().Name ?? "Unknown",
                ["name"] = element.Name ?? "Unknown",
                ["parameters"] = new JObject(),
                ["location"] = null,
                ["geometry"] = null
            };

            string repoGuid = RepoGuidService.GetRepoGuid(element);
            if (!string.IsNullOrWhiteSpace(repoGuid))
                data["repoGuid"] = repoGuid;

            return data;
        }

        private JObject ExtractLinkElement(RevitLinkInstance linkInstance)
        {
            JObject location = null;

            try
            {
                Transform transform = linkInstance.GetTotalTransform();
                if (transform != null)
                {
                    location = new JObject
                    {
                        ["type"] = "transform",
                        ["origin"] = new JObject
                        {
                            ["x"] = transform.Origin.X,
                            ["y"] = transform.Origin.Y,
                            ["z"] = transform.Origin.Z
                        },
                        ["basisX"] = new JObject
                        {
                            ["x"] = transform.BasisX.X,
                            ["y"] = transform.BasisX.Y,
                            ["z"] = transform.BasisX.Z
                        },
                        ["basisY"] = new JObject
                        {
                            ["x"] = transform.BasisY.X,
                            ["y"] = transform.BasisY.Y,
                            ["z"] = transform.BasisY.Z
                        },
                        ["basisZ"] = new JObject
                        {
                            ["x"] = transform.BasisZ.X,
                            ["y"] = transform.BasisZ.Y,
                            ["z"] = transform.BasisZ.Z
                        }
                    };
                }
            }
            catch
            {
                location = null;
            }

            var data = new JObject
            {
                ["id"] = linkInstance.UniqueId,
                ["category"] = linkInstance.Category?.Name ?? "RVT Links",
                ["type"] = linkInstance.GetType().Name ?? "RevitLinkInstance",
                ["name"] = linkInstance.Name ?? "Link",
                ["parameters"] = new JObject(),
                ["location"] = location,
                ["geometry"] = null
            };

            string repoGuid = RepoGuidService.GetRepoGuid(linkInstance);
            if (!string.IsNullOrWhiteSpace(repoGuid))
                data["repoGuid"] = repoGuid;

            return data;
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
                // Ignore logging failures.
            }
        }

        private static string DescribeElement(Element element)
        {
            if (element == null)
                return "null element";

            string category = element.Category?.Name ?? "UnknownCategory";
            string typeName = element.GetType().Name ?? "UnknownType";
            string name = element.Name ?? "UnknownName";
            string uniqueId = element.UniqueId ?? "UnknownId";
            return $"Id={element.Id.Value} UniqueId={uniqueId} Category={category} Type={typeName} Name={name}";
        }
    }
}
