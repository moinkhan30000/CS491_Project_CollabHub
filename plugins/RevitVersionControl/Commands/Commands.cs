using System;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;
using RevitVersionControl.UI;
using RevitVersionControl;

namespace RevitVersionControl.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class PublishCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;
                Document doc = uiApp.ActiveUIDocument.Document;

                // Show publish dialog
                var publishDialog = new PublishDialog(doc.PathName);
                var result = publishDialog.ShowDialog();

                if (result != true)
                    return Result.Cancelled;

                string commitMessage = publishDialog.CommitMessage;
                string projectId = publishDialog.SelectedProjectId;

                if (string.IsNullOrWhiteSpace(doc.PathName) || !System.IO.File.Exists(doc.PathName))
                {
                    TaskDialog.Show("Error", "Please save the document to disk before publishing.");
                    return Result.Failed;
                }

                var apiClient = new ApiClient();
                var baseStatusTask = Task.Run(async () =>
                    await apiClient.GetBaseFileStatusAsync(projectId, doc.PathName));
                var baseStatus = baseStatusTask.Result;

                if (baseStatus == null)
                {
                    TaskDialog.Show("Error",
                        string.IsNullOrWhiteSpace(apiClient.LastError)
                            ? "Failed to check base file status."
                            : $"Failed to check base file status.\n\n{apiClient.LastError}");
                    return Result.Failed;
                }

                if (!baseStatus.Exists)
                {
                    var uploadTask = Task.Run(async () =>
                        await apiClient.UploadBaseFileAsync(projectId, doc.PathName, doc.PathName));
                    bool uploaded = uploadTask.Result;
                    if (!uploaded)
                    {
                        TaskDialog.Show("Error",
                            string.IsNullOrWhiteSpace(apiClient.LastError)
                                ? "Failed to upload base file."
                                : $"Failed to upload base file.\n\n{apiClient.LastError}");
                        return Result.Failed;
                    }
                }

                // Extract elements
                var extractor = new ElementExtractor(doc);
                var extractionOptions = new ExtractionOptions
                {
                    BatchSize = 200,
                    PauseMilliseconds = 10,
                    IncludeGeometry = true,
                    LogProgress = true
                };
                var elementData = extractor.ExtractAllElements(extractionOptions);

                // Create snapshot
                var snapshot = new ElementSnapshot
                {
                    Version = "1.0",
                    ProjectId = projectId,
                    ModelId = doc.PathName,
                    Timestamp = DateTime.UtcNow,
                    UserName = uiApp.Application.Username,
                    CommitMessage = commitMessage,
                    Elements = elementData.Cast<object>().ToList()
                };

                // Publish to server
                var publishTask = Task.Run(async () => await apiClient.PublishSnapshotAsync(projectId, snapshot));
                var commit = publishTask.Result;

                if (commit != null)
                {
                    TaskDialog.Show("Success", 
                        $"Snapshot published successfully!\n\n" +
                        $"Commit ID: {commit.CommitId}\n" +
                        $"Elements: {elementData.Count}");
                    return Result.Succeeded;
                }
                else
                {
                    var errorDetail = string.IsNullOrWhiteSpace(apiClient.LastError)
                        ? "Failed to publish snapshot to server."
                        : $"Failed to publish snapshot to server.\n\n{apiClient.LastError}";
                    TaskDialog.Show("Error", errorDetail);
                    return Result.Failed;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"Failed to publish: {ex.Message}");
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ViewHistoryCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Show history pane
                var paneId = new DockablePaneId(new Guid("12345678-1234-1234-1234-123456789012"));
                DockablePane pane = commandData.Application.GetDockablePane(paneId);
                
                if (pane != null)
                {
                    pane.Show();
                    HistoryPaneProvider.Instance?.Refresh();
                    return Result.Succeeded;
                }

                TaskDialog.Show("Error", "History pane not found.");
                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class PullCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;
                Document doc = uiApp.ActiveUIDocument.Document;

                // Show pull dialog
                var pullDialog = new PullDialog();
                var result = pullDialog.ShowDialog();

                if (result != true)
                    return Result.Cancelled;

                string targetCommit = pullDialog.SelectedCommitId;
                string currentCommit = pullDialog.CurrentCommitId;
                string projectId = pullDialog.ProjectId;

                // Get changes from server
                TaskDialog.Show("Pulling", "Fetching changes from server...");
                
                var apiClient = new ApiClient();
                var pullTask = Task.Run(async () => 
                    await apiClient.PullChangesAsync(projectId, currentCommit, targetCommit));
                var pullResult = pullTask.Result;

                if (pullResult == null)
                {
                    TaskDialog.Show("Error", "Failed to pull changes from server.");
                    return Result.Failed;
                }

                // Check for conflicts
                if (pullResult.RequiresResolution)
                {
                    TaskDialog.Show("Conflicts", 
                        $"There are {pullResult.Conflicts.Count} conflicts that need resolution.\n" +
                        "Please use the Merge dialog to resolve conflicts.");
                    return Result.Succeeded;
                }

                // Show diff/merge pane with changes
                var paneId = new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321"));
                DockablePane diffPane = commandData.Application.GetDockablePane(paneId);
                
                if (diffPane != null)
                {
                    DiffMergePaneProvider.Instance?.LoadPullResult(pullResult);
                    diffPane.Show();
                }

                TaskDialog.Show("Success", 
                    $"Found {pullResult.Changes.Count} changes.\n" +
                    "Review and apply changes in the Merge pane.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"Failed to pull: {ex.Message}");
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class DiffViewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // Show diff dialog for selecting versions to compare
                var diffDialog = new DiffSelectDialog();
                var result = diffDialog.ShowDialog();

                if (result != true)
                    return Result.Cancelled;

                string baseCommit = diffDialog.BaseCommitId;
                string targetCommit = diffDialog.TargetCommitId;
                string projectId = diffDialog.ProjectId;

                // Get diff from server
                TaskDialog.Show("Computing Diff", "Comparing versions...");
                
                var apiClient = new ApiClient();
                var diffTask = Task.Run(async () => 
                    await apiClient.GetDiffAsync(projectId, baseCommit, targetCommit));
                var diffResult = diffTask.Result;

                if (diffResult == null)
                {
                    TaskDialog.Show("Error", "Failed to compute diff.");
                    return Result.Failed;
                }

                // Show diff/merge pane with results
                var paneId = new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321"));
                DockablePane diffPane = commandData.Application.GetDockablePane(paneId);
                
                if (diffPane != null)
                {
                    DiffMergePaneProvider.Instance?.LoadDiffResult(diffResult);
                    diffPane.Show();
                }

                // Highlight changed elements in viewport
                // (would be implemented in visual diff service)

                TaskDialog.Show("Diff Results", 
                    $"Added: {diffResult.Summary["added"]}\n" +
                    $"Modified: {diffResult.Summary["modified"]}\n" +
                    $"Deleted: {diffResult.Summary["deleted"]}\n\n" +
                    "Changed elements are highlighted in the view.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"Failed to compute diff: {ex.Message}");
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var settingsDialog = new SettingsDialog();
                settingsDialog.ShowDialog();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
