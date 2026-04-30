using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    internal class DiffMergeZoomHandler : IExternalEventHandler
    {
        private ElementId _targetId = ElementId.InvalidElementId;
        private string _repoGuid;
        private string _uniqueId;

        /// <summary>
        /// Zoom by known Revit ElementId (for temp/ghost elements).
        /// </summary>
        public void ZoomTo(ElementId id)
        {
            _targetId = id;
            _repoGuid = null;
            _uniqueId = null;
        }

        /// <summary>
        /// Zoom by RepoGuid + UniqueId (for existing model elements).
        /// The handler will resolve the ElementId on the Revit thread.
        /// </summary>
        public void ZoomTo(string repoGuid, string uniqueId)
        {
            _targetId = ElementId.InvalidElementId;
            _repoGuid = repoGuid;
            _uniqueId = uniqueId;
        }

        public void Execute(UIApplication app)
        {
            var id = _targetId;
            string repoGuid = _repoGuid;
            string uniqueId = _uniqueId;

            _targetId = ElementId.InvalidElementId;
            _repoGuid = null;
            _uniqueId = null;

            var uidoc = app?.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null) return;

            // If we don't have a direct ElementId, resolve it from RepoGuid/UniqueId
            if (id == null || id == ElementId.InvalidElementId)
            {
                if (!string.IsNullOrEmpty(repoGuid) || !string.IsNullOrEmpty(uniqueId))
                {
                    Element el = RepoGuidService.FindElement(doc, repoGuid, uniqueId);
                    
                    // Fallback: try numeric parse
                    if (el == null && !string.IsNullOrEmpty(uniqueId))
                    {
                        if (long.TryParse(uniqueId, out long numId))
                        {
                            try { el = doc.GetElement(new ElementId(numId)); } catch { }
                        }
                    }

                    if (el != null)
                        id = el.Id;
                }
            }

            if (id == null || id == ElementId.InvalidElementId) return;

            try
            {
                uidoc.ShowElements(id);
                uidoc.Selection.SetElementIds(new List<ElementId> { id });
            }
            catch { }
        }

        public string GetName() => "Diff Merge Zoom Handler";
    }
}
