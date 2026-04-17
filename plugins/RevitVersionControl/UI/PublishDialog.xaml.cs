using System;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Linq;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class PublishDialog : Window
    {
        public string CommitMessage { get; private set; }
        public string SelectedProjectId { get; private set; }
        public string SelectedBranchName { get; private set; }
        public string ModelPath { get; }

        private readonly ApiClient _apiClient = ApiClient.Instance;
        private readonly bool _hasUnsavedChanges;
        private DocumentSyncStatus _syncStatus;

        public PublishDialog(string modelPath, bool hasUnsavedChanges)
        {
            InitializeComponent();
            ModelPath = modelPath;
            _hasUnsavedChanges = hasUnsavedChanges;
            _syncStatus = DocumentSyncStateService.GetStatus(modelPath, hasUnsavedChanges);
            Loaded += PublishDialog_Loaded;
            ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
            SaveRequirementNoteText.Text = hasUnsavedChanges
                ? "Unsaved standard edits can publish. New payload-backed additions require saving first."
                : "Standard edits can publish now. New payload-backed additions still require a saved file.";
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
                    int trackedIndex = projects.FindIndex(p => p.ProjectId == _syncStatus.State?.ProjectId);
                    ProjectComboBox.SelectedIndex = trackedIndex >= 0 ? trackedIndex : 0;
                    await UpdateBaseFileStatusAsync(((Project)ProjectComboBox.SelectedItem)?.ProjectId ?? projects[0].ProjectId);
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
                await LoadBranchesAsync(project.ProjectId);
            }
        }

        private async Task LoadBranchesAsync(string projectId)
        {
            try
            {
                BranchComboBox.IsEnabled = false;
                var branches = await _apiClient.GetBranchesAsync(projectId);
                BranchComboBox.ItemsSource = branches;
                BranchComboBox.DisplayMemberPath = "Name";
                
                string trackedBranch = _syncStatus.State?.CurrentBranchName ?? "main";
                int trackedIndex = branches.FindIndex(b => string.Equals(b.Name, trackedBranch, StringComparison.OrdinalIgnoreCase));
                
                if (trackedIndex >= 0)
                {
                    BranchComboBox.SelectedIndex = trackedIndex;
                }
                else
                {
                    BranchComboBox.Text = trackedBranch;
                }
            }
            catch
            {
            }
            finally
            {
                BranchComboBox.IsEnabled = true;
            }
        }

        private async Task UpdateBaseFileStatusAsync(string projectId)
        {
            BaseFileStatusText.Text = "Base file: checking...";
            try
            {
                _syncStatus = DocumentSyncStateService.GetStatusForProject(ModelPath, projectId, _hasUnsavedChanges);
                TrackedVersionStatusText.Text = _syncStatus.Summary;

                if (string.IsNullOrWhiteSpace(ModelPath))
                {
                    BaseFileStatusText.Text = "Base file: document not saved";
                    return;
                }

                string trackedModelId = _syncStatus.State?.ProjectId == projectId
                    ? (_syncStatus.State?.ModelId ?? ModelPath)
                    : ModelPath;

                var status = await _apiClient.GetBaseFileStatusAsync(projectId, trackedModelId);
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

            if (string.IsNullOrWhiteSpace(BranchComboBox.Text))
            {
                MessageBox.Show("Please select or enter a branch.", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string trackedBranch = _syncStatus.State?.CurrentBranchName ?? "main";
            string selectedBranch = BranchComboBox.Text.Trim();

            if (!string.Equals(selectedBranch, trackedBranch, StringComparison.OrdinalIgnoreCase))
            {
                var existingBranches = BranchComboBox.ItemsSource as System.Collections.Generic.List<Branch>;
                if (existingBranches != null && existingBranches.Any(b => string.Equals(b.Name, selectedBranch, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show($"You are currently on branch '{trackedBranch}'. To commit to the existing branch '{selectedBranch}', you must Pull it first to safely switch your local document state over to that branch.\n\n(You may only specify a different branch here if you want to branch off and create a NEW branch.)", 
                        "Git-like Branch Protection", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            CommitMessage = CommitMessageTextBox.Text;
            var selectedProject = ProjectComboBox.SelectedItem as Project;
            SelectedProjectId = selectedProject?.ProjectId;
            SelectedBranchName = BranchComboBox.Text.Trim();

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
