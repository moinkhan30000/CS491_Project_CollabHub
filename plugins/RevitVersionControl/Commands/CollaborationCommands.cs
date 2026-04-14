using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;
using RevitVersionControl.UI;
using System.Threading.Tasks;

namespace RevitVersionControl.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class InitProjectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!ApiClient.Instance.IsLoggedIn)
            {
                TaskDialog.Show("Error", "Please login first.");
                return Result.Cancelled;
            }

            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || string.IsNullOrEmpty(doc.PathName))
            {
                TaskDialog.Show("Error", "Please save the project locally before initializing.");
                return Result.Failed;
            }

            var dialog = new InitProjectDialog(doc.Title); // Suggest current filename as name
            if (dialog.ShowDialog() == true)
            {
                string projectName = dialog.ProjectName;
                string filePath = doc.PathName;

                try
                {
                    using (var guidTransaction = new Transaction(doc, "Assign Repo GUIDs"))
                    {
                        guidTransaction.Start();
                        RepoGuidService.EnsureRepoGuids(doc);
                        guidTransaction.Commit();
                    }
                }
                catch
                {
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
                var initialSnapshot = new ElementSnapshot
                {
                    Version = "1.0",
                    ModelId = filePath,
                    Timestamp = DateTime.UtcNow,
                    UserName = commandData.Application.Application.Username,
                    CommitMessage = "Initial Base Snapshot",
                    Elements = elementData.ConvertAll(e => (object)e)
                };

                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    var task = Task.Run(() => ApiClient.Instance.InitProjectAsync(projectName, filePath, initialSnapshot));

                    if (task.Wait(TimeSpan.FromSeconds(120)))
                    {
                        var resultProject = task.Result;
                        if (resultProject != null)
                        {
                            System.Windows.Input.Mouse.OverrideCursor = null;
                            if (!string.IsNullOrWhiteSpace(resultProject.BaseCommitId))
                            {
                                initialSnapshot.ProjectId = resultProject.ProjectId;
                                initialSnapshot.ModelId = resultProject.ModelId ?? filePath;
                                DocumentSyncStateService.SaveState(
                                    filePath,
                                    resultProject.ProjectId,
                                    resultProject.ModelId ?? filePath,
                                    resultProject.BaseCommitId);
                                SnapshotCacheService.SaveSnapshot(
                                    resultProject.ProjectId,
                                    resultProject.ModelId ?? filePath,
                                    resultProject.BaseCommitId,
                                    initialSnapshot);
                                TaskDialog.Show("Success", $"Project '{resultProject.Name}' initialized and base snapshot published!");
                            }
                            else
                            {
                                TaskDialog.Show("Partial Success",
                                    $"Project '{resultProject.Name}' was initialized, but the initial base snapshot could not be published.\n\n" +
                                    "Current-version tracking will be incomplete until the first successful publish.");
                            }
                            HistoryPaneProvider.Instance?.ReloadProjects();
                            return Result.Succeeded;
                        }
                        else
                        {
                            throw new Exception("Server returned null project.");
                        }
                    }
                    else
                    {
                        throw new TimeoutException("Operation timed out after 120 seconds. Server might be processing a large file.");
                    }
                }
                catch (AggregateException ae)
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                    string errorMsg = "";
                    foreach(var e in ae.InnerExceptions) errorMsg += e.Message + "\n";
                    TaskDialog.Show("Error", $"Connection Failed:\n{errorMsg}");
                    return Result.Failed;
                }
                catch (Exception ex)
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                    TaskDialog.Show("Error", $"Detailed Error:\n{ex.Message}");
                    return Result.Failed;
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }
            }
            return Result.Cancelled;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class InviteCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!ApiClient.Instance.IsLoggedIn)
            {
                TaskDialog.Show("Error", "Please login first.");
                return Result.Cancelled;
            }

            var dialog = new CollaboratorsDialog();
            dialog.ShowDialog();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class InvitationsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
             if (!ApiClient.Instance.IsLoggedIn)
            {
                TaskDialog.Show("Error", "Please login first.");
                return Result.Cancelled;
            }

            var dialog = new InvitationsDialog();
            var dialogResult = dialog.ShowDialog();
            
            if (dialogResult == true && !string.IsNullOrEmpty(dialog.DownloadedFilePath))
            {
                try
                {
                    string filePath = dialog.DownloadedFilePath;
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                    var openOptions = new OpenOptions();

                    var openedDocument = commandData.Application.OpenAndActivateDocument(modelPath, openOptions, false);
                    PersistAcceptedTracking(dialog, openedDocument?.Document?.PathName ?? filePath);
                    
                    TaskDialog.Show("Success", $"Project opened: {System.IO.Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Error", $"Failed to open document:\n{ex.Message}\n\nYou can manually open the file from: {dialog.DownloadedFilePath}");
                }
            }
            
            return Result.Succeeded;
        }

        private static void PersistAcceptedTracking(InvitationsDialog dialog, string openedDocumentPath)
        {
            if (dialog == null
                || string.IsNullOrWhiteSpace(dialog.AcceptedProjectId)
                || string.IsNullOrWhiteSpace(openedDocumentPath))
            {
                return;
            }

            try
            {
                string commitId = dialog.AcceptedBaseCommitId;
                string modelId = dialog.AcceptedModelId;

                if (string.IsNullOrWhiteSpace(commitId))
                {
                    var baseCommitTask = Task.Run(() =>
                        ApiClient.Instance.GetBaseModelCommitAsync(dialog.AcceptedProjectId));

                    if (!baseCommitTask.Wait(TimeSpan.FromSeconds(30)))
                        return;

                    var baseCommit = baseCommitTask.Result;
                    if (baseCommit == null)
                        return;

                    commitId = baseCommit.CommitId;
                    modelId = baseCommit.ModelId;
                }

                string canonicalModelId = string.IsNullOrWhiteSpace(modelId)
                    ? openedDocumentPath
                    : modelId;

                DocumentSyncStateService.SaveState(
                    openedDocumentPath,
                    dialog.AcceptedProjectId,
                    canonicalModelId,
                    commitId);

                if (SnapshotCacheService.GetSnapshot(dialog.AcceptedProjectId, canonicalModelId, commitId) == null)
                {
                    var snapshotTask = Task.Run(() =>
                        ApiClient.Instance.GetSnapshotAsync(dialog.AcceptedProjectId, commitId));

                    if (!snapshotTask.Wait(TimeSpan.FromSeconds(60)))
                        return;

                    var snapshot = snapshotTask.Result;
                    if (snapshot == null)
                        return;

                    snapshot.ProjectId = dialog.AcceptedProjectId;
                    snapshot.ModelId = canonicalModelId;
                    SnapshotCacheService.SaveSnapshot(
                        dialog.AcceptedProjectId,
                        canonicalModelId,
                        commitId,
                        snapshot);
                }
            }
            catch
            {
            }
        }
    }
}
