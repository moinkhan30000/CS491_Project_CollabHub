using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public class PullDialog : Window
    {
        public string SelectedCommitId { get; private set; }
        public string CurrentCommitId { get; private set; }
        public string ProjectId { get; private set; }
        public string SelectedModelId { get; private set; }
        public string SelectedBranchName { get; private set; }

        private ComboBox ProjectComboBox;
        private ComboBox CurrentCommitComboBox;
        private ComboBox TargetBranchComboBox;
        private ComboBox TargetCommitComboBox;
        private TextBlock CurrentVersionStatusText;
        private readonly ApiClient _apiClient = ApiClient.Instance;
        private readonly string _documentPath;
        private readonly bool _hasUnsavedChanges;
        private DocumentSyncStatus _syncStatus;

        public PullDialog(string documentPath, bool hasUnsavedChanges)
        {
            _documentPath = documentPath;
            _hasUnsavedChanges = hasUnsavedChanges;
            _syncStatus = DocumentSyncStateService.GetStatus(documentPath, hasUnsavedChanges);
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Title = "Pull Changes";
            this.Width = 420;
            this.Height = 360;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var mainPanel = new StackPanel { Margin = new Thickness(10) };

            mainPanel.Children.Add(new TextBlock { Text = "Project:", Margin = new Thickness(0, 0, 0, 5) });
            ProjectComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
            mainPanel.Children.Add(ProjectComboBox);

            mainPanel.Children.Add(new TextBlock { Text = "Current Commit:", Margin = new Thickness(0, 0, 0, 5) });
            CurrentCommitComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(CurrentCommitComboBox);
            CurrentVersionStatusText = new TextBlock
            {
                Margin = new Thickness(0, -10, 0, 15),
                Text = _syncStatus.Summary,
                TextWrapping = TextWrapping.Wrap,
            };
            mainPanel.Children.Add(CurrentVersionStatusText);

            mainPanel.Children.Add(new TextBlock { Text = "Target Branch:", Margin = new Thickness(0, 0, 0, 5) });
            TargetBranchComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            TargetBranchComboBox.SelectionChanged += TargetBranchComboBox_SelectionChanged;
            mainPanel.Children.Add(TargetBranchComboBox);

            mainPanel.Children.Add(new TextBlock { Text = "Target Commit (Manual Override):", Margin = new Thickness(0, 0, 0, 5) });
            TargetCommitComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(TargetCommitComboBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var pullButton = new Button
            {
                Content = "Pull",
                Width = 80,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            pullButton.Click += PullButton_Click;
            buttonPanel.Children.Add(pullButton);

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 80,
                IsCancel = true
            };
            cancelButton.Click += CancelButton_Click;
            buttonPanel.Children.Add(cancelButton);

            mainPanel.Children.Add(buttonPanel);
            this.Content = mainPanel;
            Loaded += PullDialog_Loaded;
        }

        private async void PullDialog_Loaded(object sender, RoutedEventArgs e)
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
                await LoadCommitsAsync(project.ProjectId);
                await LoadBranchesAsync(project.ProjectId);
            }
        }

        private async Task LoadBranchesAsync(string projectId)
        {
            try
            {
                TargetBranchComboBox.ItemsSource = null;
                TargetBranchComboBox.SelectedItem = null;
                var branches = await _apiClient.GetBranchesAsync(projectId);
                
                var branchItems = branches.Select(b => new BranchItem { Name = b.Name, HeadCommitId = b.HeadCommitId }).ToList();
                TargetBranchComboBox.ItemsSource = branchItems;
                TargetBranchComboBox.DisplayMemberPath = "Name";

                if (branchItems.Count > 0)
                {
                    // Select default branch or current branch
                    string trackedBranch = _syncStatus.State?.CurrentBranchName ?? "main";
                    var currentBranch = branchItems.FirstOrDefault(b => b.Name.Equals(trackedBranch, StringComparison.OrdinalIgnoreCase)) ?? branchItems.First();
                    TargetBranchComboBox.SelectedItem = currentBranch;
                }
            }
            catch { }
        }

        private void TargetBranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetBranchComboBox.SelectedItem is BranchItem branchItem && TargetCommitComboBox.ItemsSource != null)
            {
                var commits = (List<CommitItem>)TargetCommitComboBox.ItemsSource;
                var headCommit = commits.Find(c => c.CommitId == branchItem.HeadCommitId);
                if (headCommit != null)
                {
                    TargetCommitComboBox.SelectedItem = headCommit;
                }
            }
        }

        private async Task LoadCommitsAsync(string projectId)
        {
            try
            {
                _syncStatus = DocumentSyncStateService.GetStatusForProject(_documentPath, projectId, _hasUnsavedChanges);
                CurrentCommitComboBox.ItemsSource = null;
                TargetCommitComboBox.ItemsSource = null;
                CurrentCommitComboBox.SelectedItem = null;
                TargetCommitComboBox.SelectedItem = null;
                CurrentCommitComboBox.IsEnabled = true;
                CurrentVersionStatusText.Foreground = System.Windows.SystemColors.ControlTextBrush;

                var commits = await _apiClient.GetCommitsAsync(projectId, limit: 1000);
                var latestCommit = await _apiClient.GetLatestCommitAsync(projectId);
                var rootCommit = await _apiClient.GetProjectRootCommitAsync(projectId);
                if (latestCommit != null && commits.TrueForAll(c => c.CommitId != latestCommit.CommitId))
                    commits.Insert(0, latestCommit);
                if (rootCommit != null && commits.TrueForAll(c => c.CommitId != rootCommit.CommitId))
                    commits.Add(rootCommit);

                commits = commits
                    .GroupBy(c => c.CommitId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(c => c.Timestamp)
                    .ToList();

                var commitItems = new List<CommitItem>();
                foreach (var commit in commits)
                {
                    commitItems.Add(new CommitItem
                    {
                        CommitId = commit.CommitId,
                        ModelId = commit.ModelId,
                        DisplayText = $"{commit.Message} ({commit.CommitId})"
                    });
                }

                CurrentCommitComboBox.ItemsSource = commitItems;
                TargetCommitComboBox.ItemsSource = commitItems;
                CurrentCommitComboBox.DisplayMemberPath = "DisplayText";
                TargetCommitComboBox.DisplayMemberPath = "DisplayText";

                CommitItem trackedCurrent = null;
                if (_syncStatus.State?.ProjectId == projectId
                    && !string.IsNullOrWhiteSpace(_syncStatus.State.CurrentCommitId))
                {
                    trackedCurrent = commitItems.Find(c => c.CommitId == _syncStatus.State.CurrentCommitId);
                    if (trackedCurrent != null)
                    {
                        CurrentCommitComboBox.SelectedItem = trackedCurrent;
                        CurrentCommitComboBox.IsEnabled = false;
                    }
                }

                if (trackedCurrent == null
                    && rootCommit != null
                    && DocumentSyncStateService.WasAcceptedDocumentForProject(_documentPath, projectId))
                {
                    trackedCurrent = commitItems.Find(c => c.CommitId == rootCommit.CommitId);
                    if (trackedCurrent != null)
                    {
                        DocumentSyncStateService.SaveState(
                            _documentPath,
                            projectId,
                            rootCommit.ModelId ?? _documentPath,
                            rootCommit.CommitId);
                        _syncStatus = DocumentSyncStateService.GetStatusForProject(_documentPath, projectId, _hasUnsavedChanges);
                        CurrentCommitComboBox.SelectedItem = trackedCurrent;
                        CurrentCommitComboBox.IsEnabled = false;
                    }
                }

                if (CurrentCommitComboBox.SelectedItem == null)
                {
                    CurrentVersionStatusText.Text =
                        _syncStatus.HasTrackedCommit && _syncStatus.State?.ProjectId == projectId
                            ? "Current synced version: tracked commit was not found on the server. Select the current commit manually."
                            : "Current synced version: unknown for this document. Select the current commit manually.";
                }
                else
                {
                    CurrentVersionStatusText.Text = BuildSyncStateText(commitItems, trackedCurrent);
                }

                CurrentVersionStatusText.Foreground = BuildSyncStateBrush(commitItems, trackedCurrent);
                SelectDefaultTargetCommit(commitItems, trackedCurrent);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load commits: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string BuildSyncStateText(List<CommitItem> commitItems, CommitItem trackedCurrent)
        {
            if (trackedCurrent == null)
                return "Current synced version: unknown for this document. Select the current commit manually.";

            string commitText = trackedCurrent.CommitId.Length > 8
                ? trackedCurrent.CommitId.Substring(0, 8)
                : trackedCurrent.CommitId;

            bool remoteAhead = commitItems.Count > 0 && commitItems[0].CommitId != trackedCurrent.CommitId;
            bool localChanges = _syncStatus.HasLocalChanges;

            if (localChanges && remoteAhead)
                return $"Current synced version: {commitText} (local changes detected; remote has newer commits)";

            if (localChanges)
                return _syncStatus.Summary;

            if (remoteAhead)
                return $"Current synced version: {commitText} (clean locally; remote has newer commits)";

            return $"Current synced version: {commitText} (clean and up to date)";
        }

        private System.Windows.Media.Brush BuildSyncStateBrush(List<CommitItem> commitItems, CommitItem trackedCurrent)
        {
            if (trackedCurrent == null)
                return System.Windows.SystemColors.ControlTextBrush;

            bool remoteAhead = commitItems.Count > 0 && commitItems[0].CommitId != trackedCurrent.CommitId;
            bool localChanges = _syncStatus.HasLocalChanges;

            if (localChanges && remoteAhead)
                return System.Windows.Media.Brushes.DarkRed;

            if (localChanges)
                return System.Windows.Media.Brushes.DarkOrange;

            if (remoteAhead)
                return System.Windows.Media.Brushes.DarkBlue;

            return System.Windows.Media.Brushes.DarkGreen;
        }

        private void SelectDefaultTargetCommit(List<CommitItem> commitItems, CommitItem trackedCurrent)
        {
            if (commitItems == null || commitItems.Count == 0)
                return;

            if (trackedCurrent == null)
            {
                TargetCommitComboBox.SelectedIndex = 0;
                return;
            }

            if (TargetBranchComboBox.SelectedItem is BranchItem currentBranch && !string.IsNullOrWhiteSpace(currentBranch.HeadCommitId))
            {
                var branchTarget = commitItems.Find(c => c.CommitId == currentBranch.HeadCommitId);
                if (branchTarget != null && branchTarget.CommitId != trackedCurrent.CommitId)
                {
                    TargetCommitComboBox.SelectedItem = branchTarget;
                    return;
                }
            }

            var suggestedTarget = commitItems.Find(c => c.CommitId != trackedCurrent.CommitId);
            TargetCommitComboBox.SelectedItem = suggestedTarget ?? trackedCurrent;
        }

        private void PullButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a project.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CurrentCommitComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select the current commit.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TargetCommitComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a target commit.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var selectedProject = ProjectComboBox.SelectedItem as Project;
            ProjectId = selectedProject?.ProjectId;

            if (CurrentCommitComboBox.SelectedItem is CommitItem currentItem)
            {
                CurrentCommitId = currentItem.CommitId;
            }

            if (TargetBranchComboBox.SelectedItem is BranchItem branchItem)
            {
                SelectedBranchName = branchItem.Name;
            }

            if (TargetCommitComboBox.SelectedItem is CommitItem targetItem)
            {
                SelectedCommitId = targetItem.CommitId;
                SelectedModelId = targetItem.ModelId;
            }

            if (CurrentCommitId == SelectedCommitId)
            {
                MessageBox.Show("This document is already at the selected target commit.", "Up to Date",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private class CommitItem
        {
            public string CommitId { get; set; }
            public string ModelId { get; set; }
            public string DisplayText { get; set; }
        }

        private class BranchItem
        {
            public string Name { get; set; }
            public string HeadCommitId { get; set; }
        }
    }
}
