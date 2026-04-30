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
        private readonly BranchSwitchEventHandler _switchHandler;
        private readonly Autodesk.Revit.UI.ExternalEvent _switchEvent;

        public HistoryPane()
        {
            _switchHandler = new BranchSwitchEventHandler();
            _switchEvent = Autodesk.Revit.UI.ExternalEvent.Create(_switchHandler);
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

                var hint = DocumentSyncStateService.GetProjectHint(projectId);
                string currentCommitId = hint?.CurrentCommitId;

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
                        ChangedElements = commit.ChangedElements,
                        IsActive = (commit.CommitId == currentCommitId),
                        ParentCommit = commit.ParentCommit
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

        private async void ManageBranchesButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem is Project project)
            {
                var hint = DocumentSyncStateService.GetProjectHint(project.ProjectId);
                string currentCommitId = hint?.CurrentCommitId;
                
                var dialog = new BranchManagerDialog(project.ProjectId, project.Name, currentCommitId);
                if (dialog.ShowDialog() == true)
                {
                    string targetBranch = dialog.SelectedBranchToSwitch;
                    if (!string.IsNullOrEmpty(targetBranch))
                    {
                        await LoadBranchesAsync(project.ProjectId);
                        var branches = BranchComboBox.ItemsSource as List<Branch>;
                        var b = branches?.FirstOrDefault(x => string.Equals(x.Name, targetBranch, StringComparison.OrdinalIgnoreCase));
                        
                        if (b != null)
                        {
                            if (!string.IsNullOrWhiteSpace(b.HeadCommitId) && b.HeadCommitId != currentCommitId)
                            {
                                var result = MessageBox.Show($"You are about to pull the latest commit from '{targetBranch}' to switch branches. Do you want to proceed?", "Switch Branch", MessageBoxButton.YesNo, MessageBoxImage.Question);
                                if (result == MessageBoxResult.Yes)
                                {
                                    if (_switchEvent != null)
                                    {
                                        _switchHandler.Queue(new BranchSwitchRequest
                                        {
                                            ProjectId = project.ProjectId,
                                            TargetBranch = targetBranch,
                                            TargetCommitId = b.HeadCommitId,
                                            CurrentCommitId = currentCommitId
                                        });
                                        _switchEvent.Raise();
                                    }
                                }
                                else
                                {
                                    // User cancelled the pull, do not switch the tracking branch.
                                    return;
                                }
                            }
                            else
                            {
                                // Safe to switch tracking locally without pulling.
                                if (hint != null && !string.IsNullOrWhiteSpace(hint.LastKnownDocumentPath))
                                {
                                    DocumentSyncStateService.SaveState(hint.LastKnownDocumentPath, project.ProjectId, hint.ModelId, hint.CurrentCommitId, targetBranch);
                                    CurrentTrackedBranchText.Text = $"Active Branch: {targetBranch}";
                                }
                            }
                            
                            BranchComboBox.SelectedItem = b;
                            await LoadCommitsAsync(project.ProjectId);
                        }
                    }
                }
                else
                {
                    await LoadBranchesAsync(project.ProjectId);
                }
            }
        }

        private void NetworkGraphButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem is Project project)
            {
                var hint = DocumentSyncStateService.GetProjectHint(project.ProjectId);
                string currentCommitId = hint?.CurrentCommitId;
                
                var dialog = new NetworkGraphWindow(project.ProjectId, project.Name, currentCommitId);
                dialog.ShowDialog();
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
            public bool IsActive { get; set; }
            public string ParentCommit { get; set; }
        }

        private void CommitListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateCompareButtonState();
        }

        private void UpdateCompareButtonState()
        {
            var selected = GetSelectedCommitItems();
            if (selected.Count != 2)
            {
                CompareCommitsButton.IsEnabled = false;
                CompareCommitsButton.ToolTip = "Select exactly two commits on the same branch to compare.";
                return;
            }

            string a = selected[0].BranchName;
            string b = selected[1].BranchName;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)
                || string.Equals(a, "-", StringComparison.Ordinal)
                || string.Equals(b, "-", StringComparison.Ordinal)
                || !string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                CompareCommitsButton.IsEnabled = false;
                CompareCommitsButton.ToolTip = "Cross-branch compare is not supported in v1.";
                return;
            }

            CompareCommitsButton.IsEnabled = true;
            CompareCommitsButton.ToolTip = "Compare the two selected commits.";
        }

        private List<CommitItem> GetSelectedCommitItems()
        {
            var items = new List<CommitItem>();
            foreach (var item in CommitListView.SelectedItems)
            {
                if (item is CommitItem ci && !string.IsNullOrEmpty(ci.CommitId))
                    items.Add(ci);
            }
            return items;
        }

        private async void CompareCommitsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProjectComboBox.SelectedItem is Project project)) return;

            var selected = GetSelectedCommitItems();
            if (selected.Count != 2)
            {
                MessageBox.Show("Please select exactly two commits to compare.", "Compare Commits",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Order: older = base, newer = target.
            CommitItem older = selected[0];
            CommitItem newer = selected[1];
            if (DateTime.TryParse(older.Timestamp, out var aTs) && DateTime.TryParse(newer.Timestamp, out var bTs))
            {
                if (aTs > bTs) { var tmp = older; older = newer; newer = tmp; }
            }

            await OpenDiffViewAsync(project.ProjectId, older.CommitId, newer.CommitId, swapped: false);
        }

        private async void CompareToParent_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProjectComboBox.SelectedItem is Project project)) return;

            var ci = CommitListView.SelectedItem as CommitItem;
            if (ci == null || string.IsNullOrEmpty(ci.CommitId))
            {
                MessageBox.Show("Select a commit row first.", "Compare to parent",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrEmpty(ci.ParentCommit))
            {
                MessageBox.Show("This commit has no parent (root commit). Use 'Compare to...' instead.",
                    "Compare to parent", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await OpenDiffViewAsync(project.ProjectId, ci.ParentCommit, ci.CommitId, swapped: false);
        }

        private async void CompareToOther_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProjectComboBox.SelectedItem is Project project)) return;

            var ci = CommitListView.SelectedItem as CommitItem;
            if (ci == null || string.IsNullOrEmpty(ci.CommitId))
            {
                MessageBox.Show("Select a commit row first.", "Compare to...",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Build the candidate list = commits on the same branch, excluding the selected one.
            var allItems = CommitListView.ItemsSource as IEnumerable<CommitItem>;
            if (allItems == null)
            {
                MessageBox.Show("No commits available.", "Compare to...",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sameBranchCommits = allItems
                .Where(x => !string.IsNullOrEmpty(x.CommitId)
                            && !string.Equals(x.CommitId, ci.CommitId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(x.BranchName ?? string.Empty, ci.BranchName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                .Select(x => new Commit
                {
                    CommitId = x.CommitId,
                    Message = x.Message,
                    BranchName = x.BranchName,
                    Timestamp = DateTime.TryParse(x.Timestamp, out var ts) ? ts : DateTime.MinValue,
                    Author = x.Author
                })
                .ToList();

            if (sameBranchCommits.Count == 0)
            {
                MessageBox.Show("No other commits on the same branch to compare against.", "Compare to...",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var picker = new ComparePickerDialog(sameBranchCommits, ci.CommitId);
            if (picker.ShowDialog() != true || string.IsNullOrEmpty(picker.SelectedCommitId))
                return;

            // Determine ordering by timestamp.
            DateTime ciTs = DateTime.TryParse(ci.Timestamp, out var t1) ? t1 : DateTime.MinValue;
            DateTime pickedTs = sameBranchCommits.FirstOrDefault(c => string.Equals(c.CommitId, picker.SelectedCommitId, StringComparison.OrdinalIgnoreCase))?.Timestamp ?? DateTime.MinValue;

            string baseId = ci.CommitId;
            string targetId = picker.SelectedCommitId;
            bool swapped = false;
            if (pickedTs > ciTs)
            {
                baseId = ci.CommitId;
                targetId = picker.SelectedCommitId;
            }
            else
            {
                baseId = picker.SelectedCommitId;
                targetId = ci.CommitId;
                swapped = true;
            }

            await OpenDiffViewAsync(project.ProjectId, baseId, targetId, swapped);
        }

        private async Task OpenDiffViewAsync(string projectId, string baseCommitId, string targetCommitId, bool swapped)
        {
            if (string.IsNullOrWhiteSpace(projectId))
            {
                MessageBox.Show("Select a project first.", "Compare Commits",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.Equals(baseCommitId, targetCommitId, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Base and target are the same commit — nothing to diff.", "Compare Commits",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            CompareCommitsButton.IsEnabled = false;
            try
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                var (diff, baseSnapshot, error) = await DiffViewService.FetchDiffAsync(projectId, baseCommitId, targetCommitId);

                System.Windows.Input.Mouse.OverrideCursor = null;

                if (!string.IsNullOrEmpty(error) || diff == null)
                {
                    MessageBox.Show(error ?? "Failed to fetch diff.", "Compare Commits",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                int total = diff.Summary != null && diff.Summary.TryGetValue("total", out var t) ? t : (diff.Changes?.Count ?? 0);
                if (total == 0)
                {
                    MessageBox.Show("No differences between these commits.", "Compare Commits",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                if (total > DiffViewService.MaxChangesWarn && total <= DiffViewService.MaxChangesHardCap)
                {
                    var confirm = MessageBox.Show(
                        $"This diff contains {total} changes. Building the diff view may take some time. Continue?",
                        "Large Diff",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;
                }

                if (DiffViewerExternalEvent.Instance == null || DiffViewerExternalEvent.Event == null)
                {
                    MessageBox.Show("Diff viewer is not registered. Restart Revit.", "Compare Commits",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var sessionId = Guid.NewGuid();
                var request = new DiffViewerRequest
                {
                    Operation = DiffViewerOperation.Build,
                    BuildRequest = new DiffViewBuildRequest
                    {
                        ProjectId = projectId,
                        BaseCommitId = baseCommitId,
                        TargetCommitId = targetCommitId,
                        Diff = diff,
                        BaseSnapshot = baseSnapshot,
                        SessionId = sessionId,
                        OrderSwapped = swapped
                    },
                    OnBuildComplete = result =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (!result.Success)
                            {
                                MessageBox.Show(result.Message ?? "Failed to build diff view.", "Compare Commits",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                                return;
                            }

                            DiffViewerPaneProvider.Instance?.Show(result);
                        });
                    }
                };

                DiffViewerExternalEvent.Instance.Queue(request);
                DiffViewerExternalEvent.Event.Raise();
            }
            catch (Exception ex)
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
                MessageBox.Show($"Compare failed: {ex.Message}", "Compare Commits",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateCompareButtonState();
            }
        }
    }
}
