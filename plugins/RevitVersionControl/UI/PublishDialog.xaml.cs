using System;
using System.Windows;
using System.Windows.Controls;

namespace RevitVersionControl.UI
{
    public partial class PublishDialog : Window
    {
        public string CommitMessage { get; private set; }
        public string SelectedProjectId { get; private set; }

        public PublishDialog()
        {
            InitializeComponent();
            LoadProjects();
        }

        private void LoadProjects()
        {
            // Load projects from API
            // For now, add dummy data
            ProjectComboBox.Items.Add(new { Name = "Office Building", ProjectId = "project-1" });
            ProjectComboBox.Items.Add(new { Name = "Residential Complex", ProjectId = "project-2" });
            ProjectComboBox.SelectedIndex = 0;
        }

        private void PublishButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CommitMessageTextBox.Text))
            {
                MessageBox.Show("Please enter a commit message.", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ProjectComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a project.", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CommitMessage = CommitMessageTextBox.Text;
            dynamic selectedProject = ProjectComboBox.SelectedItem;
            SelectedProjectId = selectedProject.ProjectId;

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
