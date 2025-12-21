using System;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class PublishDialog : Window
    {
        public string CommitMessage { get; private set; }
        public string SelectedProjectId { get; private set; }
        public string ModelPath { get; }

        private readonly ApiClient _apiClient = new ApiClient();

        public PublishDialog(string modelPath)
        {
            InitializeComponent();
            ModelPath = modelPath;
            Loaded += PublishDialog_Loaded;
            ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
        }

        private async void PublishDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadProjectsAsync();
        }

        private async Task LoadProjectsAsync()
        {
            try
            {
                ProjectComboBox.IsEnabled = false;
                var projects = await _apiClient.GetProjectsAsync();
                ProjectComboBox.ItemsSource = projects;
                ProjectComboBox.DisplayMemberPath = "Name";
                if (projects.Count > 0)
                {
                    ProjectComboBox.SelectedIndex = 0;
                    await UpdateBaseFileStatusAsync(projects[0].ProjectId);
                }
                else
                {
                    MessageBox.Show("No projects found on the server.", "Info",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load projects: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ProjectComboBox.IsEnabled = true;
            }
        }

        private async void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem is Project project)
            {
                await UpdateBaseFileStatusAsync(project.ProjectId);
            }
        }

        private async Task UpdateBaseFileStatusAsync(string projectId)
        {
            BaseFileStatusText.Text = "Base file: checking...";
            try
            {
                if (string.IsNullOrWhiteSpace(ModelPath))
                {
                    BaseFileStatusText.Text = "Base file: document not saved";
                    return;
                }

                var status = await _apiClient.GetBaseFileStatusAsync(projectId, ModelPath);
                if (status == null)
                {
                    BaseFileStatusText.Text = "Base file: status unavailable";
                    return;
                }

                BaseFileStatusText.Text = status.Exists
                    ? "Base file: present"
                    : "Base file: missing (will upload on publish)";
            }
            catch
            {
                BaseFileStatusText.Text = "Base file: status unavailable";
            }
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
            var selectedProject = ProjectComboBox.SelectedItem as Project;
            SelectedProjectId = selectedProject?.ProjectId;

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
