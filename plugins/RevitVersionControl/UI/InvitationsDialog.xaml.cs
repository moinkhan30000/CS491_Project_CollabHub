using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitVersionControl.Services;
using Microsoft.Win32;
using System.Threading.Tasks;

namespace RevitVersionControl.UI
{
    public partial class InvitationsDialog : Window
    {
        /// <summary>
        /// Path to the downloaded file (if user accepted an invitation)
        /// </summary>
        public string DownloadedFilePath { get; private set; }
        public string AcceptedProjectId { get; private set; }
        public string AcceptedBaseCommitId { get; private set; }
        public string AcceptedModelId { get; private set; }

        public InvitationsDialog()
        {
            InitializeComponent();
            LoadInvites();
        }

        private async void LoadInvites()
        {
            try
            {
                var invites = await ApiClient.Instance.GetPendingInvitesAsync();
                InvitesList.ItemsSource = invites;
                
                if (invites.Count == 0)
                {
                   // Maybe show "No invites" message or something
                }
            }
            catch (Exception ex) 
            {
                MessageBox.Show($"Failed to load invites: {ex.Message}");
            }
        }

        private async void Accept_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int inviteId)
            {
                // Get invitation data from the button's DataContext
                dynamic invite = btn.DataContext;
                string projectName = invite?.ProjectName ?? "Project";
                string fileExt = invite?.FileExtension ?? ".rvt";
                AcceptedProjectId = invite?.ProjectId;
                
                // Remove leading dot if present for filter, add it back for default extension
                string extNoDot = fileExt.TrimStart('.');
                string filterName = extNoDot.ToUpper() == "RVT" ? "Revit Project" : 
                                    extNoDot.ToUpper() == "RFA" ? "Revit Family" : "Revit File";
                
                // Prompt user for save location
                var saveDialog = new SaveFileDialog
                {
                    Filter = $"{filterName} (*.{extNoDot})|*.{extNoDot}|All Files (*.*)|*.*",
                    Title = "Save Project As",
                    FileName = projectName,
                    DefaultExt = fileExt
                };

                if (saveDialog.ShowDialog() == true)
                {
                    await Respond(inviteId, "ACTIVE", saveDialog.FileName);
                }
            }
        }

        private async void Decline_Click(object sender, RoutedEventArgs e)
        {
             if (sender is Button btn && btn.Tag is int inviteId)
            {
                if (MessageBox.Show("Are you sure you want to decline this invitation?", "Confirm", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    await Respond(inviteId, "DECLINED", null);
                }
            }
        }

        private async Task Respond(int inviteId, string status, string savePath)
        {
             try
            {
                string result = await ApiClient.Instance.RespondToInviteAsync(inviteId, status, savePath);
                
                if (status == "ACTIVE" && !string.IsNullOrEmpty(savePath))
                {
                     // Result is the path if successful
                     if (result == savePath)
                     {
                         DownloadedFilePath = savePath;
                         await TrackAcceptedProjectAsync(savePath);
                         
                         // Ask user if they want to open the project now
                         var openNow = MessageBox.Show(
                             $"Project saved to:\n{savePath}\n\nDo you want to open it now?", 
                             "Success", 
                             MessageBoxButton.YesNo, 
                             MessageBoxImage.Question);
                         
                         if (openNow == MessageBoxResult.Yes)
                         {
                             DialogResult = true; // This will close the dialog
                             Close();
                         }
                         else
                         {
                             DownloadedFilePath = null; // User chose not to open
                             LoadInvites(); // Refresh list
                         }
                     }
                     else 
                     {
                         MessageBox.Show($"Error: {result}");
                     }
                }
                else
                {
                     LoadInvites();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
            }
        }

        private async Task TrackAcceptedProjectAsync(string savePath)
        {
            if (string.IsNullOrWhiteSpace(AcceptedProjectId) || string.IsNullOrWhiteSpace(savePath))
                return;

            try
            {
                var baseCommit = await ApiClient.Instance.GetBaseModelCommitAsync(AcceptedProjectId);
                if (baseCommit != null)
                {
                    AcceptedBaseCommitId = baseCommit.CommitId;
                    AcceptedModelId = baseCommit.ModelId;
                    DocumentSyncStateService.SaveState(
                        savePath,
                        AcceptedProjectId,
                        baseCommit.ModelId,
                        baseCommit.CommitId);

                    var baseSnapshot = await ApiClient.Instance.GetSnapshotAsync(
                        AcceptedProjectId,
                        baseCommit.CommitId);
                    if (baseSnapshot != null)
                    {
                        baseSnapshot.ProjectId = AcceptedProjectId;
                        baseSnapshot.ModelId = string.IsNullOrWhiteSpace(baseCommit.ModelId)
                            ? savePath
                            : baseCommit.ModelId;

                        SnapshotCacheService.SaveSnapshot(
                            AcceptedProjectId,
                            baseSnapshot.ModelId,
                            baseCommit.CommitId,
                            baseSnapshot);
                    }
                }
            }
            catch
            {
                // Ignore local tracking failures during invite acceptance.
            }
        }
    }
}

