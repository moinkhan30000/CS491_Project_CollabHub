using System;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public enum DiffViewerOperation
    {
        Build,
        Clear,
        CleanOrphans,
        ZoomTo
    }

    public class DiffViewerRequest
    {
        public DiffViewerOperation Operation { get; set; }
        public DiffViewBuildRequest BuildRequest { get; set; }
        public Autodesk.Revit.DB.ElementId DiffViewId { get; set; }
        public Autodesk.Revit.DB.ElementId TargetElementId { get; set; }
        public Guid? SessionId { get; set; }
        public Action<DiffViewBuildResult> OnBuildComplete { get; set; }
        public Action OnClearComplete { get; set; }
    }

    public class DiffViewerExternalEvent : IExternalEventHandler
    {
        private DiffViewerRequest _request;
        private static DiffViewerExternalEvent _instance;
        private static ExternalEvent _externalEvent;

        public static DiffViewerExternalEvent Instance => _instance;
        public static ExternalEvent Event => _externalEvent;

        public static void Register()
        {
            if (_instance != null) return;
            _instance = new DiffViewerExternalEvent();
            _externalEvent = ExternalEvent.Create(_instance);
        }

        public void Queue(DiffViewerRequest request)
        {
            _request = request;
        }

        public void Execute(UIApplication app)
        {
            var request = _request;
            _request = null;
            if (request == null) return;

            try
            {
                switch (request.Operation)
                {
                    case DiffViewerOperation.Build:
                        var result = DiffViewService.Build(app, request.BuildRequest);
                        try
                        {
                            if (result != null && result.Success)
                            {
                                ShowDiffViewerPane(app, result);
                            }
                        }
                        catch { }
                        try { request.OnBuildComplete?.Invoke(result); }
                        catch { }
                        break;

                    case DiffViewerOperation.Clear:
                        DiffViewService.ClearDiffView(app, request.DiffViewId, request.SessionId);
                        try { request.OnClearComplete?.Invoke(); } catch { }
                        break;

                    case DiffViewerOperation.CleanOrphans:
                        DiffViewService.CleanOrphanedDiffArtifacts(app);
                        try { request.OnClearComplete?.Invoke(); } catch { }
                        break;

                    case DiffViewerOperation.ZoomTo:
                        if (request.TargetElementId != null
                            && request.TargetElementId != Autodesk.Revit.DB.ElementId.InvalidElementId
                            && app.ActiveUIDocument != null)
                        {
                            try { app.ActiveUIDocument.ShowElements(request.TargetElementId); }
                            catch { }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Diff Viewer", $"Diff viewer operation failed: {ex.Message}");
            }
        }

        public string GetName() => "DiffViewerExternalEvent";

        private static void ShowDiffViewerPane(UIApplication app, DiffViewBuildResult result)
        {
            try
            {
                var paneId = new Autodesk.Revit.UI.DockablePaneId(RevitVersionControl.DiffViewerPaneProvider.PaneGuid);
                var pane = app.GetDockablePane(paneId);
                if (pane != null)
                {
                    RevitVersionControl.DiffViewerPaneProvider.Instance?.Show(result);
                    pane.Show();
                }
            }
            catch
            {
                // The pane may not be registered (older startup ordering); fail silently.
            }
        }
    }
}
