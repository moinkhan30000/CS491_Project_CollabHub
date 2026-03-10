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
    public class LoginCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (ApiClient.Instance.IsLoggedIn)
                {
                    // Logout flow
                    ApiClient.Instance.Logout();
                    Application.SetLoggedInState(false);
                    HistoryPaneProvider.Instance?.Clear();
                    DiffMergePaneProvider.Instance?.Clear();
                    TaskDialog.Show("Logged Out", "You have been logged out successfully.");
                }
                else
                {
                    // Login flow
                    var loginDialog = new LoginDialog();
                    if (loginDialog.ShowDialog() == true)
                    {
                        // Dialog sets result true only if login/register succeeded
                        Application.SetLoggedInState(true);
                        HistoryPaneProvider.Instance?.ReloadProjects();
                        TaskDialog.Show("Success", "Logged in successfully!");
                    }
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class RegisterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (ApiClient.Instance.IsLoggedIn)
                {
                    TaskDialog.Show("Info", "You are already logged in.");
                    return Result.Succeeded;
                }

                var registerDialog = new RegisterDialog();
                if (registerDialog.ShowDialog() == true)
                {
                    // Register dialog handles auto-login on success
                    Application.SetLoggedInState(true);
                    HistoryPaneProvider.Instance?.ReloadProjects();
                    TaskDialog.Show("Success", "Account created and logged in!");
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class PublishCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!ApiClient.Instance.IsLoggedIn)
            {
                TaskDialog.Show("Authentication Required", "Please log in to use this feature.");
                return Result.Cancelled;
            }

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

                var apiClient = ApiClient.Instance;
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
                var publishTask = Task.Run(async () => await ApiClient.Instance.PublishSnapshotAsync(projectId, snapshot));
                // Note: Blocking call is not ideal but standard for simple Revit commands.
                var commit = publishTask.GetAwaiter().GetResult();

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
                    if (!string.IsNullOrWhiteSpace(apiClient.LastError) &&
                        apiClient.LastError.Contains("No changes detected"))
                    {
                        TaskDialog.Show("Up to Date", "No changes detected. Snapshot is already up to date.");
                        return Result.Succeeded;
                    }

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
            if (!ApiClient.Instance.IsLoggedIn) 
                return Result.Cancelled;

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
            if (!ApiClient.Instance.IsLoggedIn)
                return Result.Cancelled;

            try
            {
                UIApplication uiApp = commandData.Application;
                Document doc = uiApp.ActiveUIDocument.Document;

                var pullDialog = new PullDialog();
                var result = pullDialog.ShowDialog();

                if (result != true)
                    return Result.Cancelled;

                string targetCommit = pullDialog.SelectedCommitId;
                string currentCommit = pullDialog.CurrentCommitId;
                string projectId = pullDialog.ProjectId;

                // Use wait cursor instead of blocking TaskDialog
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
                    return Result.Failed;
                }

                if (pullResult.RequiresResolution)
                {
                    TaskDialog.Show("Conflicts",
                        $"There are {pullResult.Conflicts.Count} conflicts that need resolution.\n" +
                        "Please use the Merge dialog to resolve conflicts.");
                    return Result.Succeeded;
                }

                // Show changes in diff/merge pane
                var paneId = new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321"));
                DockablePane diffPane = commandData.Application.GetDockablePane(paneId);

                if (diffPane != null)
                {
                    DiffMergePaneProvider.Instance?.LoadPullResult(pullResult);
                    diffPane.Show();
                }

                int totalChanges = pullResult.Changes.Count;
                int addedCount = pullResult.Changes.Count(c => c.ChangeType == "added");
                int modifiedCount = pullResult.Changes.Count(c => c.ChangeType == "modified");
                int deletedCount = pullResult.Changes.Count(c => c.ChangeType == "deleted");

                TaskDialogCommonButtons buttons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                TaskDialog confirmDialog = new TaskDialog("Apply Changes?")
                {
                    MainInstruction = "Apply Remote Changes",
                    MainContent = $"Found {totalChanges} changes:\n" +
                                 $"  • Added: {addedCount}\n" +
                                 $"  • Modified: {modifiedCount}\n" +
                                 $"  • Deleted: {deletedCount}\n\n" +
                                 $"Do you want to apply these changes to your model?",
                    CommonButtons = buttons
                };

                TaskDialogResult applyResult = confirmDialog.Show();

                if (applyResult != TaskDialogResult.Yes)
                {
                    TaskDialog.Show("Cancelled", "Pull cancelled. No changes were applied.");
                    return Result.Succeeded;
                }

                // Use wait cursor for apply as well
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                ElementApplier.ApplyResult applyResponse = null;
                try
                {
                    var applier = new ElementApplier(doc);
                    applyResponse = applier.ApplyChanges(pullResult.Changes);
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }

                if (applyResponse.Success)
                {
                    string resultMessage = $"Changes Applied Successfully!\n\n" +
                                          $"Applied: {applyResponse.AppliedCount}\n" +
                                          $"Skipped: {applyResponse.SkippedCount}";

                    if (applyResponse.Errors.Count > 0)
                    {
                        resultMessage += $"\n\nWarnings/Errors: {applyResponse.Errors.Count}\n";
                        resultMessage += string.Join("\n", applyResponse.Errors.Take(5));
                        if (applyResponse.Errors.Count > 5)
                            resultMessage += $"\n... and {applyResponse.Errors.Count - 5} more";
                    }

                    TaskDialog.Show("Success", resultMessage);

                    if (applyResponse.Errors.Count > 0)
                    {
                        System.Diagnostics.Debug.WriteLine("\n=== APPLY CHANGES DETAILED LOG ===");
                        foreach (var error in applyResponse.Errors)
                            System.Diagnostics.Debug.WriteLine(error);
                        System.Diagnostics.Debug.WriteLine("=================================\n");
                    }

                    return Result.Succeeded;
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
                    return Result.Failed;
                }
            }
            catch (Exception ex)
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
                message = ex.Message;
                TaskDialog.Show("Error", $"Pull failed: {ex.Message}");
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class DiffViewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!ApiClient.Instance.IsLoggedIn)
                return Result.Cancelled;

            try
            {
                var diffDialog = new DiffSelectDialog();
                var result = diffDialog.ShowDialog();

                if (result != true)
                    return Result.Cancelled;

                string baseCommit = diffDialog.BaseCommitId;
                string targetCommit = diffDialog.TargetCommitId;
                string projectId = diffDialog.ProjectId;

                if (baseCommit == targetCommit)
                {
                    TaskDialog.Show("No Differences", "Base and target commits are the same.");
                    return Result.Succeeded;
                }

                // Use wait cursor instead of blocking TaskDialog
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                DiffResult diffResult = null;
                try
                {
                    var diffTask = Task.Run(async () =>
                        await ApiClient.Instance.GetDiffAsync(projectId, baseCommit, targetCommit));
                    diffResult = diffTask.GetAwaiter().GetResult();
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }

                if (diffResult == null)
                {
                    var errorDetail = string.IsNullOrWhiteSpace(ApiClient.Instance.LastError)
                        ? "Failed to compute diff."
                        : $"Failed to compute diff.\n\n{ApiClient.Instance.LastError}";
                    TaskDialog.Show("Error", errorDetail);
                    return Result.Failed;
                }

                if (diffResult.Summary != null &&
                    diffResult.Summary.TryGetValue("total", out int totalChanges) &&
                    totalChanges == 0)
                {
                    TaskDialog.Show("No Differences", "No changes found between the selected commits.");
                    return Result.Succeeded;
                }

                // Load result into pane and show it
                var paneId = new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321"));
                DockablePane diffPane = commandData.Application.GetDockablePane(paneId);

                if (diffPane != null)
                {
                    DiffMergePaneProvider.Instance?.LoadDiffResult(diffResult);
                    diffPane.Show();
                }

                TaskDialog.Show("Diff Results",
                    $"Added: {diffResult.Summary["added"]}\n" +
                    $"Modified: {diffResult.Summary["modified"]}\n" +
                    $"Deleted: {diffResult.Summary["deleted"]}\n\n" +
                    "Results loaded in the Changes & Merge pane.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
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
            // Settings might be allowed even if logged out? Or maybe only logged in.
            // Let's restrict it for safety.
            if (!ApiClient.Instance.IsLoggedIn)
                return Result.Cancelled;

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
