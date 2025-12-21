using System;
using System.Windows;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class CollaboratorsDialog : Window
    {
        public CollaboratorsDialog()
        {
            InitializeComponent();
            LoadProjects();
        }

        private async void LoadProjects()
        {
            try
            {
                var projects = await ApiClient.Instance.GetProjectsAsync();
                ProjectComboBox.ItemsSource = projects;
                if (projects.Count > 0) ProjectComboBox.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                 MessageBox.Show($"Failed to load projects: {ex.Message}");
            }
        }

        private async void InviteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectComboBox.SelectedValue == null)
            {
                MessageBox.Show("Please select a project.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            string projectId = ProjectComboBox.SelectedValue.ToString();

            string email = EmailInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(email))
            {
                MessageBox.Show("Please enter an email address.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                bool success = await ApiClient.Instance.InviteUserAsync(projectId, email);
                if (success)
                {
                    MessageBox.Show($"Invitation sent to {email}.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    EmailInput.Clear();
                }
                else
                {
                    MessageBox.Show("Failed to invite user. Please check if the email exists.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                 MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
