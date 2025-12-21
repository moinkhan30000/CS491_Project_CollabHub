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

                // Use a simple task dialog to inform user? No, that blocks.
                // Just use a WaitCursor.
                // Set wait cursor manually using WPF
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    // Run on a background thread to prevent UI Deadlock.
                    // The UI thread is blocked by Wait(), so the async task MUST NOT try to use it.
                    var task = Task.Run(() => ApiClient.Instance.InitProjectAsync(projectName, filePath));

                    // Wait 120 seconds
                    if (task.Wait(TimeSpan.FromSeconds(120)))
                    {
                        var resultProject = task.Result;
                        if (resultProject != null)
                        {
                            System.Windows.Input.Mouse.OverrideCursor = null;
                            TaskDialog.Show("Success", $"Project '{resultProject.Name}' initialized!");
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
            
            // If user accepted an invitation and chose to open the file
            if (dialogResult == true && !string.IsNullOrEmpty(dialog.DownloadedFilePath))
            {
                try
                {
                    string filePath = dialog.DownloadedFilePath;
                    var app = commandData.Application.Application;
                    
                    // Open the document
                    var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
                    var openOptions = new OpenOptions();
                    
                    commandData.Application.OpenAndActivateDocument(modelPath, openOptions, false);
                    
                    TaskDialog.Show("Success", $"Project opened: {System.IO.Path.GetFileName(filePath)}");
                }
                catch (Exception ex)
                {
                    TaskDialog.Show("Error", $"Failed to open document:\n{ex.Message}\n\nYou can manually open the file from: {dialog.DownloadedFilePath}");
                }
            }
            
            return Result.Succeeded;
        }
    }
}
