using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitVersionControl.Services
{
    public class ElementLookup
    {
        private readonly Document _doc;
        private Dictionary<string, Element> _repoGuidIndex;
        private Dictionary<string, Element> _uniqueIdIndex;

        public ElementLookup(Document doc)
        {
            _doc = doc;
        }

        private void EnsureIndex()
        {
            if (_repoGuidIndex != null && _uniqueIdIndex != null)
                return;

            _repoGuidIndex = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
            _uniqueIdIndex = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);

            foreach (Element element in new FilteredElementCollector(_doc).WhereElementIsNotElementType())
            {
                if (element == null) continue;

                if (!string.IsNullOrEmpty(element.UniqueId) && !_uniqueIdIndex.ContainsKey(element.UniqueId))
                    _uniqueIdIndex[element.UniqueId] = element;

                string repoGuid = null;
                try { repoGuid = RepoGuidService.GetRepoGuid(element); } catch { }
                if (!string.IsNullOrWhiteSpace(repoGuid) && !_repoGuidIndex.ContainsKey(repoGuid))
                    _repoGuidIndex[repoGuid] = element;
            }
        }

        public Element Resolve(string repoGuid, string uniqueId, string elementIdInt)
        {
            EnsureIndex();

            if (!string.IsNullOrWhiteSpace(repoGuid)
                && _repoGuidIndex.TryGetValue(repoGuid, out var byRepo))
                return byRepo;

            if (!string.IsNullOrWhiteSpace(uniqueId)
                && _uniqueIdIndex.TryGetValue(uniqueId, out var byUnique))
                return byUnique;

            if (!string.IsNullOrWhiteSpace(elementIdInt))
            {
                if (long.TryParse(elementIdInt, out long parsed))
                {
                    try
                    {
                        Element byId = _doc.GetElement(new ElementId(parsed));
                        if (byId != null) return byId;
                    }
                    catch { }
                }
            }

            return null;
        }

        public Element ResolveChange(Change change)
        {
            if (change == null) return null;
            return Resolve(change.RepoGuid, change.ElementId, change.ElementId);
        }
    }
}
