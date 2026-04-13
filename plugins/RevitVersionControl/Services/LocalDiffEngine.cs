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

            var baseEntries = ToElementEntries(baseElements);
            var targetEntries = ToElementEntries(targetElements);
            var matchedBase = new HashSet<string>(StringComparer.Ordinal);
            var matchedTarget = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pair in MatchEntries(baseEntries, targetEntries, matchedBase, matchedTarget))
            {
                var change = CompareElements(pair.Base, pair.Target);
                if (change != null)
                    changes.Add(change);
            }

            var addedEntries = targetEntries
                .Where(entry => !matchedTarget.Contains(entry.TrackingKey))
                .OrderBy(entry => entry.ElementId, StringComparer.Ordinal);
            foreach (var entry in addedEntries)
            {
                changes.Add(new Change
                {
                    ChangeType = "added",
                    ElementId = entry.ElementId,
                    RepoGuid = entry.RepoGuid,
                    Category = entry.Element["category"]?.ToString() ?? "Unknown",
                    Type = entry.Element["type"]?.ToString() ?? "Unknown",
                    ParameterChanges = new List<ParameterChange>(),
                    GeometryChanged = false,
                    LocationChanged = false,
                    OldData = null,
                    NewData = ToDictionary(entry.Element),
                });
            }

            var deletedEntries = baseEntries
                .Where(entry => !matchedBase.Contains(entry.TrackingKey))
                .OrderBy(entry => entry.ElementId, StringComparer.Ordinal);
            foreach (var entry in deletedEntries)
            {
                changes.Add(new Change
                {
                    ChangeType = "deleted",
                    ElementId = entry.ElementId,
                    RepoGuid = entry.RepoGuid,
                    Category = entry.Element["category"]?.ToString() ?? "Unknown",
                    Type = entry.Element["type"]?.ToString() ?? "Unknown",
                    ParameterChanges = new List<ParameterChange>(),
                    GeometryChanged = false,
                    LocationChanged = false,
                    OldData = ToDictionary(entry.Element),
                    NewData = null,
                });
            }

            return changes;
        }

        private static Change CompareElements(ElementEntry baseElement, ElementEntry targetElement)
        {
            var parameterChanges = CompareParameters(
                baseElement.Element["parameters"] as JObject,
                targetElement.Element["parameters"] as JObject);
            bool geometryChanged = CompareGeometry(
                baseElement.Element["geometry"] as JObject,
                targetElement.Element["geometry"] as JObject);
            bool locationChanged = !JToken.DeepEquals(baseElement.Element["location"], targetElement.Element["location"]);

            if (parameterChanges.Count == 0 && !geometryChanged && !locationChanged)
                return null;

            return new Change
            {
                ChangeType = "modified",
                ElementId = baseElement.ElementId,
                RepoGuid = baseElement.RepoGuid ?? targetElement.RepoGuid,
                Category = baseElement.Element["category"]?.ToString() ?? "Unknown",
                Type = baseElement.Element["type"]?.ToString() ?? "Unknown",
                ParameterChanges = parameterChanges,
                GeometryChanged = geometryChanged,
                LocationChanged = locationChanged,
                OldData = ToDictionary(baseElement.Element),
                NewData = ToDictionary(targetElement.Element),
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

        private static IEnumerable<(ElementEntry Base, ElementEntry Target)> MatchEntries(
            List<ElementEntry> baseEntries,
            List<ElementEntry> targetEntries,
            HashSet<string> matchedBase,
            HashSet<string> matchedTarget)
        {
            var pairs = new List<(ElementEntry Base, ElementEntry Target)>();

            var baseByRepoGuid = baseEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RepoGuid))
                .ToDictionary(entry => entry.RepoGuid, entry => entry, StringComparer.OrdinalIgnoreCase);
            var targetByRepoGuid = targetEntries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.RepoGuid))
                .ToDictionary(entry => entry.RepoGuid, entry => entry, StringComparer.OrdinalIgnoreCase);

            foreach (string repoGuid in baseByRepoGuid.Keys.Intersect(targetByRepoGuid.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var baseEntry = baseByRepoGuid[repoGuid];
                var targetEntry = targetByRepoGuid[repoGuid];
                matchedBase.Add(baseEntry.TrackingKey);
                matchedTarget.Add(targetEntry.TrackingKey);
                pairs.Add((baseEntry, targetEntry));
            }

            var baseById = baseEntries
                .Where(entry => !matchedBase.Contains(entry.TrackingKey))
                .ToDictionary(entry => entry.ElementId, entry => entry, StringComparer.Ordinal);
            var targetById = targetEntries
                .Where(entry => !matchedTarget.Contains(entry.TrackingKey))
                .ToDictionary(entry => entry.ElementId, entry => entry, StringComparer.Ordinal);

            foreach (string elementId in baseById.Keys.Intersect(targetById.Keys, StringComparer.Ordinal))
            {
                var baseEntry = baseById[elementId];
                var targetEntry = targetById[elementId];
                matchedBase.Add(baseEntry.TrackingKey);
                matchedTarget.Add(targetEntry.TrackingKey);
                pairs.Add((baseEntry, targetEntry));
            }

            return pairs;
        }

        private static List<ElementEntry> ToElementEntries(IEnumerable<object> elements)
        {
            var result = new List<ElementEntry>();

            foreach (object element in elements ?? Enumerable.Empty<object>())
            {
                JObject obj = element as JObject ?? JObject.FromObject(element);
                string id = obj["id"]?.ToString();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string repoGuid = obj["repoGuid"]?.ToString();
                result.Add(new ElementEntry
                {
                    Element = obj,
                    ElementId = id,
                    RepoGuid = repoGuid,
                    TrackingKey = !string.IsNullOrWhiteSpace(repoGuid) ? repoGuid : "id:" + id
                });
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

        private class ElementEntry
        {
            public JObject Element { get; set; }
            public string ElementId { get; set; }
            public string RepoGuid { get; set; }
            public string TrackingKey { get; set; }
        }
    }
}
