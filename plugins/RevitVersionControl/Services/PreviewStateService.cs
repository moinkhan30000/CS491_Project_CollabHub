using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitVersionControl.Services
{
    public static class PreviewStateService
    {
        public static ElementId TempViewId { get; set; } = ElementId.InvalidElementId;
        public static ElementId OriginalViewId { get; set; } = ElementId.InvalidElementId;
        
        // Maps the incoming change RepoGuid/ElementId to the physically created temporary ElementId in the model
        public static Dictionary<string, ElementId> TempAddedElements { get; } = new Dictionary<string, ElementId>();

        // Ghost DirectShapes for deleted elements — tracks their temp IDs for cleanup
        public static Dictionary<string, ElementId> TempGhostElements { get; } = new Dictionary<string, ElementId>();

        // Conflict resolution choices:  elementId -> "keep_local" | "accept_remote" | "keep_both"
        public static Dictionary<string, string> ConflictResolutions { get; } = new Dictionary<string, string>();

        // Maps conflict elementIds to their conflicting changes (for the UI to display)
        public static List<Conflict> ActiveConflicts { get; set; } = new List<Conflict>();

        // Source (ours) and Target (theirs) changes from 3-way merge
        public static List<Change> SourceChanges { get; set; } = new List<Change>();
        public static List<Change> TargetChanges { get; set; } = new List<Change>();

        // Whether this is a 3-way merge (vs. a simple pull)
        public static bool Is3WayMerge { get; set; } = false;

        public static void Clear()
        {
            TempViewId = ElementId.InvalidElementId;
            OriginalViewId = ElementId.InvalidElementId;
            TempAddedElements.Clear();
            TempGhostElements.Clear();
            ConflictResolutions.Clear();
            ActiveConflicts.Clear();
            SourceChanges.Clear();
            TargetChanges.Clear();
            Is3WayMerge = false;
        }
        
        public static string GetChangeTrackingKey(Change change)
        {
            if (!string.IsNullOrWhiteSpace(change?.RepoGuid))
                return "repo:" + change.RepoGuid;

            if (!string.IsNullOrWhiteSpace(change?.ElementId))
                return "id:" + change.ElementId;

            return null;
        }
    }
}
