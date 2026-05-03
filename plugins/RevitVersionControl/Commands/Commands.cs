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
                    ApiClient.Instance.Logout();
                    Application.SetLoggedInState(false);
                    HistoryPaneProvider.Instance?.Clear();
                    DiffMergePaneProvider.Instance?.Clear();
                    DiffViewerPaneProvider.Instance?.Clear();
                    TaskDialog.Show("Logged Out", "You have been logged out successfully.");
                }
                else
                {
                    var loginDialog = new LoginDialog();
                    if (loginDialog.ShowDialog() == true)
                    {
                        Application.SetLoggedInState(true);
                        HistoryPaneProvider.Instance?.ReloadProjects();
                        DiffViewerPaneProvider.Instance?.ReloadProjects();
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
                    Application.SetLoggedInState(true);
                    HistoryPaneProvider.Instance?.ReloadProjects();
                    DiffViewerPaneProvider.Instance?.ReloadProjects();
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
                bool hasUnsavedChanges = doc.IsModified;

                var publishDialog = new PublishDialog(doc.PathName, hasUnsavedChanges);
                var result = publishDialog.ShowDialog();

                if (result != true)
                    return Result.Cancelled;

                string commitMessage = publishDialog.CommitMessage;
                string projectId = publishDialog.SelectedProjectId;
                string branchName = publishDialog.SelectedBranchName;
                var apiClient = ApiClient.Instance;
                var trackedState = DocumentSyncStateService.GetStateForProject(doc.PathName, projectId);
                bool canUseTrackedState = trackedState != null
                                          && string.Equals(trackedState.ProjectId, projectId, StringComparison.OrdinalIgnoreCase);
                string modelId = canUseTrackedState && !string.IsNullOrWhiteSpace(trackedState.ModelId)
                    ? trackedState.ModelId
                    : doc.PathName;
                ElementSnapshot baselineSnapshot = null;
                if (canUseTrackedState && !string.IsNullOrWhiteSpace(trackedState.CurrentCommitId))
                {
                    baselineSnapshot = SnapshotCacheService.GetSnapshot(projectId, modelId, trackedState.CurrentCommitId);
                    if (baselineSnapshot == null)
                    {
                        var baselineTask = Task.Run(async () =>
                            await apiClient.GetSnapshotAsync(projectId, trackedState.CurrentCommitId));
                        baselineSnapshot = baselineTask.GetAwaiter().GetResult();
                        if (baselineSnapshot != null)
                        {
                            baselineSnapshot.ProjectId = projectId;
                            baselineSnapshot.ModelId = modelId;
                            SnapshotCacheService.SaveSnapshot(projectId, modelId, trackedState.CurrentCommitId, baselineSnapshot);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(doc.PathName) || !System.IO.File.Exists(doc.PathName))
                {
                    TaskDialog.Show("Error", "Please save the document to disk before publishing.");
                    return Result.Failed;
                }

                if (hasUnsavedChanges && PayloadSupportService.HasUnsavedPayloadBackedAdditions(doc, baselineSnapshot))
                {
                    TaskDialog.Show("Save Required", PayloadSupportService.SaveRequiredMessage);
                    return Result.Failed;
                }

                var baseStatusTask = Task.Run(async () =>
                    await apiClient.GetBaseFileStatusAsync(projectId, modelId));
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
                        await apiClient.UploadBaseFileAsync(projectId, modelId, doc.PathName));
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

                try
                {
                    var knownRepoGuids = RepoGuidService.BuildKnownRepoGuidMap(baselineSnapshot);
                    using (var guidTransaction = new Transaction(doc, "Ensure Repo GUIDs"))
                    {
                        guidTransaction.Start();
                        RepoGuidService.EnsureRepoGuids(doc, knownRepoGuids);
                        guidTransaction.Commit();
                    }
                }
                catch
                {
                }

                if (!string.IsNullOrWhiteSpace(branchName))
                {
                    try
                    {
                        var branchTask = Task.Run(async () => await apiClient.GetBranchesAsync(projectId));
                        var branches = branchTask.GetAwaiter().GetResult();
                        if (!branches.Any(b => string.Equals(b.Name, branchName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var createBranchTask = Task.Run(async () => await apiClient.CreateBranchAsync(projectId, branchName, canUseTrackedState ? trackedState.CurrentCommitId : null));
                            createBranchTask.GetAwaiter().GetResult();
                        }
                    }
                    catch { }
                }

                var extractor = new ElementExtractor(doc);
                var extractionOptions = new ExtractionOptions
                {
                    BatchSize = 200,
                    PauseMilliseconds = 10,
                    IncludeGeometry = true,
                    LogProgress = true
                };
                var elementData = extractor.ExtractAllElements(extractionOptions);

                var extractedElements = elementData.Cast<object>().ToList();
                var snapshot = new ElementSnapshot
                {
                    Version = "1.0",
                    ProjectId = projectId,
                    ModelId = modelId,
                    Timestamp = DateTime.UtcNow,
                    UserName = uiApp.Application.Username,
                    CommitMessage = commitMessage,
                    Elements = extractedElements,
                    ParentCommit = canUseTrackedState ? trackedState.CurrentCommitId : null,
                    ParentCommit2 = canUseTrackedState ? trackedState.MergeParentCommitId : null
                };

                Commit commit = null;
                bool usedPackagePublish = false;
                string packageFallbackReason = null;
                bool requiresPayloadBackedPublish = false;

                if (canUseTrackedState && !string.IsNullOrWhiteSpace(trackedState.CurrentCommitId))
                {
                    if (baselineSnapshot != null)
                    {
                        var localDiffEngine = new LocalDiffEngine();
                        var changes = localDiffEngine.ComputeDiff(baselineSnapshot.Elements, snapshot.Elements);

                        if (changes.Count == 0)
                        {
                            TaskDialog.Show("Up to Date", "No changes detected. Snapshot is already up to date.");
                            return Result.Succeeded;
                        }

                        var payloadPreparation = PayloadSupportService.PreparePayloadBackedChanges(
                            doc,
                            projectId,
                            changes,
                            hasUnsavedChanges);
                        if (!payloadPreparation.Success)
                        {
                            TaskDialog.Show("Error", payloadPreparation.ErrorMessage);
                            return Result.Failed;
                        }
                        requiresPayloadBackedPublish = payloadPreparation.PayloadRefs != null
                                                       && payloadPreparation.PayloadRefs.Count > 0;

                        var package = new CommitPackage
                        {
                            ModelId = modelId,
                            CommitMessage = commitMessage,
                            ParentCommit = trackedState.CurrentCommitId,
                            ParentCommit2 = trackedState.MergeParentCommitId,
                            Changes = changes,
                            ElementCount = extractedElements.Count,
                            PayloadRefs = payloadPreparation.PayloadRefs,
                            CheckpointSnapshot = null,
                            BranchName = branchName
                        };

                        var packageTask = Task.Run(async () => await ApiClient.Instance.PublishPackageAsync(projectId, package));
                        commit = packageTask.GetAwaiter().GetResult();
                        if (commit == null
                            && !string.IsNullOrWhiteSpace(apiClient.LastError)
                            && apiClient.LastError.Contains("Checkpoint required", StringComparison.OrdinalIgnoreCase))
                        {
                            package.CheckpointSnapshot = snapshot;
                            packageTask = Task.Run(async () => await ApiClient.Instance.PublishPackageAsync(projectId, package));
                            commit = packageTask.GetAwaiter().GetResult();
                        }

                        if (commit != null)
                        {
                            usedPackagePublish = true;
                        }
                        else if (!string.IsNullOrWhiteSpace(apiClient.LastError) &&
                                 apiClient.LastError.Contains("No changes detected", StringComparison.OrdinalIgnoreCase))
                        {
                            TaskDialog.Show("Up to Date", "No changes detected. Snapshot is already up to date.");
                            return Result.Succeeded;
                        }
                        else
                        {
                            packageFallbackReason = apiClient.LastError;
                        }
                    }
                }

                if (commit == null)
                {
                    if (requiresPayloadBackedPublish)
                    {
                        string payloadFallbackError = string.IsNullOrWhiteSpace(packageFallbackReason)
                            ? "Payload-backed additions require successful package publish. Full snapshot fallback is disabled for this publish."
                            : "Payload-backed additions require successful package publish. Full snapshot fallback is disabled for this publish.\n\n"
                              + packageFallbackReason;
                        TaskDialog.Show("Error", payloadFallbackError);
                        return Result.Failed;
                    }

                    var publishTask = Task.Run(async () => await ApiClient.Instance.PublishSnapshotAsync(projectId, snapshot, branchName));
                    commit = publishTask.GetAwaiter().GetResult();
                }

                if (commit != null)
                {
                    DocumentSyncStateService.SaveState(doc.PathName, projectId, modelId, commit.CommitId, branchName, null);
                    SnapshotCacheService.SaveSnapshot(projectId, modelId, commit.CommitId, snapshot);

                    string modeText = usedPackagePublish
                        ? "Delta package published successfully!"
                        : "Snapshot published successfully!";
                    if (!string.IsNullOrWhiteSpace(packageFallbackReason))
                    {
                        modeText += "\n\nPackage publish was unavailable, so the add-in fell back to a full snapshot.";
                    }

                    string shortCommitId = commit.CommitId?.Length > 8
                        ? commit.CommitId.Substring(0, 8)
                        : commit.CommitId;

                    TaskDialog.Show("Success", 
                        $"{modeText}\n\n" +
                        $"Commit: {shortCommitId}\n" +
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
                        ? "Failed to publish changes to server."
                        : $"Failed to publish changes to server.\n\n{apiClient.LastError}";
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

                // Roadmap §9.16 — clear any active diff session before pulling so stale ghosts/overrides don't pollute the model.
                try
                {
                    DiffViewService.ClearActiveDiff(uiApp);
                    DiffViewerPaneProvider.Instance?.Clear();
                }
                catch { }

                var pullDialog = new PullDialog(doc.PathName, doc.IsModified);
                var result = pullDialog.ShowDialog();

                if (result != true)
                    return Result.Cancelled;

                string targetCommit = pullDialog.SelectedCommitId;
                string currentCommit = pullDialog.CurrentCommitId;
                string projectId = pullDialog.ProjectId;
                string trackedModelId = pullDialog.SelectedModelId
                                        ?? DocumentSyncStateService.GetStateForProject(doc.PathName, projectId)?.ModelId
                                        ?? doc.PathName;
                
                string targetBranchName = pullDialog.SelectedBranchName;

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
                    // Auto-open the existing Diff Viewer so user can see and choose changes
                    try
                    {
                        var dvPaneId = new DockablePaneId(DiffViewerPaneProvider.PaneGuid);
                        DockablePane dvPane = commandData.Application.GetDockablePane(dvPaneId);
                        if (dvPane != null)
                        {
                            DiffViewerPaneProvider.Instance?.ReloadProjects();
                            dvPane.Show();
                        }
                    }
                    catch { }

                    TaskDialog.Show("Conflicts",
                        $"There are {pullResult.Conflicts.Count} conflicts that need resolution.\n\n" +
                        "The Diff Viewer has been opened. Select Base and Target commits, click Compare, " +
                        "then use the checkboxes to select which changes to apply and click 'Apply Selected Changes'.");
                    return Result.Succeeded;
                }

                // Show the existing Diff Viewer for non-conflict pulls too
                try
                {
                    var dvPaneId2 = new DockablePaneId(DiffViewerPaneProvider.PaneGuid);
                    DockablePane dvPane2 = commandData.Application.GetDockablePane(dvPaneId2);
                    if (dvPane2 != null)
                    {
                        DiffViewerPaneProvider.Instance?.ReloadProjects();
                        dvPane2.Show();
                    }
                }
                catch { }

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

                bool success = PullService.ExecutePullApply(
                    uiApp,
                    projectId,
                    trackedModelId,
                    targetCommit,
                    targetBranchName,
                    pullResult,
                    silentMode: false);

                return success ? Result.Succeeded : Result.Failed;
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
            {
                TaskDialog.Show("Authentication Required", "Please log in to use this feature.");
                return Result.Cancelled;
            }

            try
            {
                // The View Diff button is now just a launcher for the Commit Diff Viewer pane,
                // which holds its own project / branch / base / target pickers and the Compare button.
                var paneId = new DockablePaneId(DiffViewerPaneProvider.PaneGuid);
                DockablePane pane = commandData.Application.GetDockablePane(paneId);
                if (pane != null)
                {
                    DiffViewerPaneProvider.Instance?.ReloadProjects();
                    pane.Show();
                    return Result.Succeeded;
                }

                TaskDialog.Show("Error", "Commit Diff Viewer pane is not registered. Try restarting Revit.");
                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"Failed to open Commit Diff Viewer: {ex.Message}");
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
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

    [Transaction(TransactionMode.Manual)]
    public class DiffMergeCommand : IExternalCommand
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
                var paneId = new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321"));
                DockablePane pane = commandData.Application.GetDockablePane(paneId);
                if (pane != null)
                {
                    pane.Show();
                    return Result.Succeeded;
                }

                TaskDialog.Show("Error", "Changes & Merge pane is not registered. Try restarting Revit.");
                return Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show("Error", $"Failed to open Changes & Merge pane: {ex.Message}");
                return Result.Failed;
            }
        }
    }
}

