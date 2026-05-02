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
                    TaskDialog.Show("Conflicts",
                        $"There are {pullResult.Conflicts.Count} conflicts that need resolution.\n" +
                        "Please use the Pull menu or Merge dialog to resolve conflicts manually.");
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

                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                ElementApplier.ApplyResult applyResponse = null;
                try
                {
                    if (!PayloadSupportService.EnsurePayloadsAvailable(projectId, pullResult.Changes, out string payloadError))
                    {
                        TaskDialog.Show("Error", payloadError);
                        return;
                    }

                    var applier = new ElementApplier(doc, projectId);
                    applyResponse = applier.ApplyChanges(pullResult.Changes);
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }

                if (applyResponse.Success)
                {
                    DocumentSyncStateService.SaveState(doc.PathName, projectId, trackedModelId, targetCommit, targetBranchName);
                    
                    try
                    {
                        var extractor = new ElementExtractor(doc);
                        var extractionOptions = new ExtractionOptions
                        {
                            BatchSize = 200,
                            PauseMilliseconds = 10,
                            IncludeGeometry = true,
                            LogProgress = true
                        };
                        var currentElements = extractor.ExtractAllElements(extractionOptions);
                        var cachedSnapshot = new ElementSnapshot
                        {
                            Version = "1.0",
                            ProjectId = projectId,
                            ModelId = trackedModelId,
                            Timestamp = DateTime.UtcNow,
                            UserName = app.Application.Username,
                            CommitMessage = $"Cached after pull to {targetCommit}",
                            Elements = currentElements.Cast<object>().ToList(),
                            ParentCommit = targetCommit
                        };
                        SnapshotCacheService.SaveSnapshot(projectId, trackedModelId, targetCommit, cachedSnapshot);
                    }
                    catch { }

                    bool hasIssues = applyResponse.Errors.Count > 0
                                     || applyResponse.Warnings.Count > 0
                                     || applyResponse.UnsupportedElements.Count > 0
                                     || applyResponse.IgnoredAutogenerated.Count > 0;

                    string title = hasIssues ? "Completed with issues" : "Success";
                    TaskDialog.Show(title, ApplyResultMessageBuilder.Build(applyResponse));

                    // Refresh HistoryPane
                    HistoryPaneProvider.Instance?.Refresh();
                }
                else
                {
                    string errorMessage = "Failed to apply changes:\n\n";
                    if (applyResponse.Errors.Count > 0)
                    {
                        errorMessage += string.Join("\n", applyResponse.Errors.Take(5));
                        if (applyResponse.Errors.Count > 5)
                            errorMessage += $"\n\n... and {applyResponse.Errors.Count - 5} more errors";
                    }
                    else
                    {
                        errorMessage += "Unknown error occurred.";
                    }
                    TaskDialog.Show("Error", errorMessage);
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
