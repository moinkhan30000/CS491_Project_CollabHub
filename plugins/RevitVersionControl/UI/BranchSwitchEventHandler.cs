using System;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public class BranchSwitchRequest
    {
        public string ProjectId { get; set; }
        public string TargetBranch { get; set; }
        public string TargetCommitId { get; set; }
        public string CurrentCommitId { get; set; }
    }

    public class BranchSwitchEventHandler : IExternalEventHandler
    {
        private BranchSwitchRequest _request;

        public void Queue(BranchSwitchRequest request)
        {
            _request = request;
        }

        public void Execute(UIApplication app)
        {
            if (_request == null) return;

            try
            {
                // Auto-clear any active diff session before mutating the document (roadmap §9.16).
                try
                {
                    DiffViewService.ClearActiveDiff(app);
                    RevitVersionControl.DiffViewerPaneProvider.Instance?.Clear();
                }
                catch { }

                Document doc = app.ActiveUIDocument.Document;
                string projectId = _request.ProjectId;
                string currentCommit = _request.CurrentCommitId;
                string targetCommit = _request.TargetCommitId;
                string targetBranchName = _request.TargetBranch;
                
                string trackedModelId = DocumentSyncStateService.GetStateForProject(doc.PathName, projectId)?.ModelId ?? doc.PathName;

                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                
                PullResult pullResult = null;
                try
                {
                    var pullTask = Task.Run(async () =>
                        await ApiClient.Instance.PullChangesAsync(projectId, currentCommit, targetCommit));
                    pullResult = pullTask.GetAwaiter().GetResult();
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }

                if (pullResult == null)
                {
                    TaskDialog.Show("Error", "Failed to pull changes from server.");
                    return;
                }

                if (pullResult.RequiresResolution)
                {
                    // Auto-open the Diff Viewer so user can see conflicts and choose changes
                    try
                    {
                        var paneId = new Autodesk.Revit.UI.DockablePaneId(DiffViewerPaneProvider.PaneGuid);
                        var diffPane = app.GetDockablePane(paneId);
                        if (diffPane != null)
                        {
                            // Trigger a diff build between current and target commits
                            if (DiffViewerExternalEvent.Instance != null && DiffViewerExternalEvent.Event != null)
                            {
                                var (diff, baseSnapshot, error) = System.Threading.Tasks.Task.Run(async () =>
                                    await Services.DiffViewService.FetchDiffAsync(projectId, currentCommit, targetCommit))
                                    .GetAwaiter().GetResult();

                                if (diff != null && string.IsNullOrEmpty(error))
                                {
                                    var sessionId = Guid.NewGuid();
                                    DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
                                    {
                                        Operation = DiffViewerOperation.Build,
                                        BuildRequest = new Services.DiffViewBuildRequest
                                        {
                                            ProjectId = projectId,
                                            BaseCommitId = currentCommit,
                                            TargetCommitId = targetCommit,
                                            Diff = diff,
                                            BaseSnapshot = baseSnapshot,
                                            SessionId = sessionId,
                                            OrderSwapped = false
                                        },
                                        OnBuildComplete = result => { }
                                    });
                                    DiffViewerExternalEvent.Event.Raise();
                                }
                            }

                            diffPane.Show();
                        }
                    }
                    catch { }

                    TaskDialog.Show("Conflicts",
                        $"There are {pullResult.Conflicts.Count} conflicts that need resolution.\n\n" +
                        "The Diff Viewer has been opened. Use the checkboxes to select which changes to apply, " +
                        "then click 'Apply Selected Changes'.");
                    return;
                }
                
                int totalChanges = pullResult.Changes.Count;
                if (totalChanges == 0)
                {
                    DocumentSyncStateService.SaveState(doc.PathName, projectId, trackedModelId, targetCommit, targetBranchName);
                    TaskDialog.Show("Up to date", $"Branch '{targetBranchName}' is already up to date.");
                    // Refresh HistoryPane
                    HistoryPaneProvider.Instance?.Refresh();
                    return;
                }

                bool success = PullService.ExecutePullApply(
                    app,
                    projectId,
                    trackedModelId,
                    targetCommit,
                    targetBranchName,
                    pullResult,
                    silentMode: false);

                if (success)
                {
                    // Refresh HistoryPane
                    HistoryPaneProvider.Instance?.Refresh();
                }
            }
            catch (Exception ex)
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
                TaskDialog.Show("Error", $"Pull failed: {ex.Message}");
            }
            finally
            {
                _request = null;
            }
        }

        public string GetName() => "BranchSwitchEventHandler";
    }
}
