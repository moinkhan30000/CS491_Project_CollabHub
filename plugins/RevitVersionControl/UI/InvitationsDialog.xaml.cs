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
                // Prompt user for save location
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Revit Project (*.rvt)|*.rvt",
                    Title = "Save Project As"
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
                         MessageBox.Show($"Project saved to: {savePath}\nPlease open it in Revit.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                         LoadInvites(); // Refresh list
                         
                         // Ideally we would open the document automatically via Revit API, 
                         // but since this is a Modal Dialog context, we might need to let the user do it 
                         // or signal the Command to open it after dialog closes.
                         // For now, instructing user is safer.
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
    }
}
