using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitVersionControl.Services
{
    public class LocalDiffEngine
    {
        public List<Change> ComputeDiff(IEnumerable<object> baseElements, IEnumerable<object> targetElements)
        {
            var changes = new List<Change>();

            var baseDict = ToElementMap(baseElements);
            var targetDict = ToElementMap(targetElements);

            var addedIds = targetDict.Keys.Except(baseDict.Keys).OrderBy(id => id, StringComparer.Ordinal);
            foreach (string elementId in addedIds)
            {
                JObject element = targetDict[elementId];
                changes.Add(new Change
                {
                    ChangeType = "added",
                    ElementId = elementId,
                    Category = element["category"]?.ToString() ?? "Unknown",
                    Type = element["type"]?.ToString() ?? "Unknown",
                    ParameterChanges = new List<ParameterChange>(),
                    GeometryChanged = false,
                    LocationChanged = false,
                    OldData = null,
                    NewData = ToDictionary(element),
                });
            }

            var deletedIds = baseDict.Keys.Except(targetDict.Keys).OrderBy(id => id, StringComparer.Ordinal);
            foreach (string elementId in deletedIds)
            {
                JObject element = baseDict[elementId];
                changes.Add(new Change
                {
                    ChangeType = "deleted",
                    ElementId = elementId,
                    Category = element["category"]?.ToString() ?? "Unknown",
                    Type = element["type"]?.ToString() ?? "Unknown",
                    ParameterChanges = new List<ParameterChange>(),
                    GeometryChanged = false,
                    LocationChanged = false,
                    OldData = ToDictionary(element),
                    NewData = null,
                });
            }

            var commonIds = baseDict.Keys.Intersect(targetDict.Keys).OrderBy(id => id, StringComparer.Ordinal);
            foreach (string elementId in commonIds)
            {
                JObject baseElement = baseDict[elementId];
                JObject targetElement = targetDict[elementId];
                var change = CompareElements(baseElement, targetElement);
                if (change != null)
                    changes.Add(change);
            }

            return changes;
        }

        private static Change CompareElements(JObject baseElement, JObject targetElement)
        {
            var parameterChanges = CompareParameters(
                baseElement["parameters"] as JObject,
                targetElement["parameters"] as JObject);
            bool geometryChanged = CompareGeometry(
                baseElement["geometry"] as JObject,
                targetElement["geometry"] as JObject);
            bool locationChanged = !JToken.DeepEquals(baseElement["location"], targetElement["location"]);

            if (parameterChanges.Count == 0 && !geometryChanged && !locationChanged)
                return null;

            return new Change
            {
                ChangeType = "modified",
                ElementId = baseElement["id"]?.ToString(),
                Category = baseElement["category"]?.ToString() ?? "Unknown",
                Type = baseElement["type"]?.ToString() ?? "Unknown",
                ParameterChanges = parameterChanges,
                GeometryChanged = geometryChanged,
                LocationChanged = locationChanged,
                OldData = ToDictionary(baseElement),
                NewData = ToDictionary(targetElement),
            };
        }

        private static List<ParameterChange> CompareParameters(JObject baseParams, JObject targetParams)
        {
            var changes = new List<ParameterChange>();
            var parameterNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            if (baseParams != null)
            {
                foreach (var property in baseParams.Properties())
                    parameterNames.Add(property.Name);
            }

            if (targetParams != null)
            {
                foreach (var property in targetParams.Properties())
                    parameterNames.Add(property.Name);
            }

            foreach (string name in parameterNames)
            {
                var baseParam = baseParams?[name] as JObject;
                var targetParam = targetParams?[name] as JObject;

                if (baseParam == null && targetParam == null)
                    continue;

                JToken baseValue = baseParam?["value"];
                JToken targetValue = targetParam?["value"];
                if (JToken.DeepEquals(baseValue, targetValue))
                    continue;

                changes.Add(new ParameterChange
                {
                    Name = name,
                    OldValue = baseValue?.ToObject<object>(),
                    NewValue = targetValue?.ToObject<object>(),
                    Type = targetParam?["type"]?.ToString()
                        ?? baseParam?["type"]?.ToString()
                        ?? "Unknown",
                    ElementName = targetParam?["elementName"]?.ToString()
                        ?? baseParam?["elementName"]?.ToString(),
                });
            }

            return changes;
        }

        private static bool CompareGeometry(JObject baseGeometry, JObject targetGeometry)
        {
            if (baseGeometry == null && targetGeometry == null)
                return false;

            if (baseGeometry == null || targetGeometry == null)
                return true;

            string baseHash = baseGeometry["geometryHash"]?.ToString();
            string targetHash = targetGeometry["geometryHash"]?.ToString();
            return !string.Equals(baseHash, targetHash, StringComparison.Ordinal);
        }

        private static Dictionary<string, JObject> ToElementMap(IEnumerable<object> elements)
        {
            var result = new Dictionary<string, JObject>(StringComparer.Ordinal);

            foreach (object element in elements ?? Enumerable.Empty<object>())
            {
                JObject obj = element as JObject ?? JObject.FromObject(element);
                string id = obj["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                    result[id] = obj;
            }

            return result;
        }

        private static Dictionary<string, object> ToDictionary(JObject value)
        {
            if (value == null)
                return null;

            return JsonConvert.DeserializeObject<Dictionary<string, object>>(
                JsonConvert.SerializeObject(value));
        }
    }
}
