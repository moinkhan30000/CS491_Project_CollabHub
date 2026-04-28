using System;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;
using RevitVersionControl.UI;
using RevitVersionControl;
using System.Collections.Generic;

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
                    TaskDialog.Show("Logged Out", "You have been logged out successfully.");
                }
                else
                {
                    var loginDialog = new LoginDialog();
                    if (loginDialog.ShowDialog() == true)
                    {
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
                    ParentCommit = canUseTrackedState ? trackedState.CurrentCommitId : null
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
                    DocumentSyncStateService.SaveState(doc.PathName, projectId, modelId, commit.CommitId, branchName);
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

                // =============================================================
                // WE NOW LOAD IT INTO THE DOCKED PANE INSTEAD OF CRASHING
                // =============================================================
                
                var paneId = new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321"));
                DockablePane diffPane = commandData.Application.GetDockablePane(paneId);

                if (diffPane != null)
                {
                    // Pass the whole PullResult (including the Conflicts list) to the pane!
                    DiffMergePaneProvider.Instance?.LoadPullResult(
                        pullResult,
                        projectId,
                        currentCommit,
                        targetCommit,
                        trackedModelId);

                    diffPane.Show();

                    if (pullResult.RequiresResolution)
                    {
                        TaskDialog.Show("Conflicts Detected", 
                            $"This pull request crosses branches and resulted in {pullResult.Conflicts.Count} conflict(s).\n\n" +
                            "Please review the Changes pane to resolve them.");
                        return Result.Succeeded;
                    }
                }
                else
                {
                    TaskDialog.Show("Error", "Could not load the Changes & Merge docked pane.");
                    return Result.Failed;
                }

                // ==============================================================

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
                                 $"Do you want to apply these changes to your model immediately?",
                    CommonButtons = buttons
                };

                TaskDialogResult applyResult = confirmDialog.Show();

                if (applyResult != TaskDialogResult.Yes)
                {
                    return Result.Succeeded; // Let it sit in the pane
                }

                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                ElementApplier.ApplyResult applyResponse = null;
                try
                {
                    if (!PayloadSupportService.EnsurePayloadsAvailable(projectId, pullResult.Changes, out string payloadError))
                    {
                        TaskDialog.Show("Error", payloadError);
                        return Result.Failed;
                    }

                    var applier = new ElementApplier(doc, projectId);
                    applyResponse = applier.ApplyChanges(pullResult.Changes);
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }

                // Reverting exactly to the stable final success/logging path here
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
                            UserName = uiApp.Application.Username,
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

