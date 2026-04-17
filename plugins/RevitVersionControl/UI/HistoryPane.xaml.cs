using System;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class HistoryPane : Page
    {
        private readonly ApiClient _apiClient = ApiClient.Instance;

        public HistoryPane()
        {
            InitializeComponent();
            Loaded += HistoryPane_Loaded;
        }

        private async void HistoryPane_Loaded(object sender, RoutedEventArgs e)
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
                ProjectComboBox.SelectedValuePath = "ProjectId";
                if (projects.Count > 0)
                {
                    ProjectComboBox.SelectedIndex = 0;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(_apiClient.LastError) &&
                        _apiClient.LastError.Contains("HTTP 401"))
                    {
                        CommitListView.ItemsSource = new List<CommitItem>
                        {
                            new CommitItem { Message = "Please log in to view projects.", CommitId = "", Author = "", Timestamp = "", ChangedElements = 0 }
                        };
                        return;
                    }

                    CommitListView.ItemsSource = new List<CommitItem>
                    {
                        new CommitItem { Message = "No projects found.", CommitId = "", Author = "", Timestamp = "", ChangedElements = 0 }
                    };
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

        private async Task LoadBranchesAsync(string projectId)
        {
            try
            {
                BranchComboBox.IsEnabled = false;
                var branches = await _apiClient.GetBranchesAsync(projectId);
                
                var allBranches = new List<Branch> { new Branch { Name = "All Branches" } };
                allBranches.AddRange(branches);
                
                BranchComboBox.ItemsSource = allBranches;
                BranchComboBox.DisplayMemberPath = "Name";
                
                if (allBranches.Count > 0)
                {
                    BranchComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                // Ignore silent load failures for branches
            }
            finally
            {
                BranchComboBox.IsEnabled = true;
            }
        }

        private async Task LoadCommitsAsync(string projectId)
        {
            try
            {
                CommitListView.ItemsSource = null;
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

                var selectedBranch = BranchComboBox.SelectedItem as Branch;
                if (selectedBranch != null && selectedBranch.Name != "All Branches")
                {
                    if (!string.IsNullOrEmpty(selectedBranch.HeadCommitId))
                    {
                        var branchCommits = new List<Commit>();
                        var commitMap = commits.ToDictionary(c => c.CommitId, StringComparer.OrdinalIgnoreCase);
                        
                        string currentId = selectedBranch.HeadCommitId;
                        while (!string.IsNullOrEmpty(currentId) && commitMap.TryGetValue(currentId, out Commit currentCommit))
                        {
                            branchCommits.Add(currentCommit);
                            currentId = currentCommit.ParentCommit;
                        }
                        commits = branchCommits;
                    }
                    else
                    {
                        commits = commits.Where(c => string.Equals(c.BranchName, selectedBranch.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    }
                }

                var items = new List<CommitItem>();
                foreach (var commit in commits)
                {
                    items.Add(new CommitItem
                    {
                        Message = commit.Message,
                        CommitId = commit.CommitId,
                        BranchName = string.IsNullOrEmpty(commit.BranchName) ? "-" : commit.BranchName,
                        Author = commit.GetAuthorName(),
                        Timestamp = commit.Timestamp.ToString("yyyy-MM-dd HH:mm"),
                        ChangedElements = commit.ChangedElements
                    });
                }

                if (items.Count == 0)
                {
                    items.Add(new CommitItem { Message = "No commits found.", CommitId = "", BranchName = "-", Author = "", Timestamp = "", ChangedElements = 0 });
                }

                CommitListView.ItemsSource = items;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load commits: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public async void Refresh()
        {
            if (ProjectComboBox.SelectedItem is Project project)
            {
                await LoadCommitsAsync(project.ProjectId);
            }
        }

        public async void ReloadProjects()
        {
            await LoadProjectsAsync();
        }

        public void Clear()
        {
            ProjectComboBox.ItemsSource = null;
            CommitListView.ItemsSource = new List<CommitItem>
            {
                new CommitItem { Message = "Please log in to view projects.", CommitId = "", Author = "", Timestamp = "", ChangedElements = 0 }
            };
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadProjectsAsync();
        }

        private async void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem is Project project)
            {
                var hint = DocumentSyncStateService.GetProjectHint(project.ProjectId);
                CurrentTrackedBranchText.Text = hint != null && !string.IsNullOrWhiteSpace(hint.CurrentBranchName) 
                    ? $"Active Branch: {hint.CurrentBranchName}" 
                    : "Active Branch: none";

                await LoadBranchesAsync(project.ProjectId);
                await LoadCommitsAsync(project.ProjectId);
            }
        }

        private async void BranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem is Project project)
            {
                await LoadCommitsAsync(project.ProjectId);
            }
        }

        private class CommitItem
        {
            public string Message { get; set; }
            public string CommitId { get; set; }
            public string BranchName { get; set; }
            public string Author { get; set; }
            public string Timestamp { get; set; }
            public int ChangedElements { get; set; }
        }
    }
}
