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
                    string activeBranch = DocumentSyncStateService.GetStatusForProject(
                        null, projectId, false)?.State?.CurrentBranchName ?? "main";
                    
                    int index = allBranches.FindIndex(b => string.Equals(b.Name, activeBranch, StringComparison.OrdinalIgnoreCase));
                    BranchComboBox.SelectedIndex = index >= 0 ? index : 0;
                }
            }
            catch
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
                        RawTimestamp = commit.Timestamp,
                        ChangedElements = commit.ChangedElements,
                        IsActive = (commit.CommitId == currentCommitId)
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
                    
                    string branchToMerge = dialog.SelectedBranchToMerge;
                    if (!string.IsNullOrEmpty(branchToMerge))
                    {
                        var branches = BranchComboBox.ItemsSource as List<Branch>;
                        var targetBranchObj = branches?.FirstOrDefault(x => string.Equals(x.Name, branchToMerge, StringComparison.OrdinalIgnoreCase));
                        
                        if (targetBranchObj != null && !string.IsNullOrWhiteSpace(targetBranchObj.HeadCommitId))
                        {
                            try
                            {
                                // Fetch the diff between current commit and the merge target
                                var diffResult = await _apiClient.GetDiffAsync(project.ProjectId, currentCommitId, targetBranchObj.HeadCommitId);
                                
                                if (diffResult == null || diffResult.Changes == null || diffResult.Changes.Count == 0)
                                {
                                    MessageBox.Show("No differences found between your current commit and the target branch. Nothing to merge.", "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else
                                {
                                    // Get 3-way merge analysis first (required for conflict detection)
                                    Merge3WayResult mergeResult = null;
                                    try
                                    {
                                        mergeResult = await _apiClient.Merge3WayAsync(project.ProjectId, currentCommitId, targetBranchObj.HeadCommitId);
                                    }
                                    catch (Exception mergeEx)
                                    {
                                        MessageBox.Show($"3-way merge analysis failed:\n{mergeEx.Message}\n\nCannot proceed with merge without conflict detection.", "Merge Blocked", MessageBoxButton.OK, MessageBoxImage.Error);
                                        return;
                                    }

                                    if (mergeResult == null)
                                    {
                                        MessageBox.Show("3-way merge analysis returned no data. Cannot proceed.", "Merge Blocked", MessageBoxButton.OK, MessageBoxImage.Error);
                                        return;
                                    }

                                    // Pass both diff and 3-way result to the DiffViewer
                                    DiffViewerPaneProvider.Instance?.LoadDiffForMerge(diffResult, project.ProjectId, currentCommitId, targetBranchObj.HeadCommitId, branchToMerge, mergeResult);

                                    bool hasConflicts = mergeResult.HasConflicts;
                                    int conflictCount = mergeResult.Conflicts?.Count ?? 0;

                                    string message;
                                    if (hasConflicts)
                                    {
                                        message = $"⚠️ {conflictCount} CONFLICT(S) detected!\n\n" +
                                            $"Found {diffResult.Changes.Count} total change(s) between your branch and '{branchToMerge}'.\n\n" +
                                            "The Diff Viewer has been opened with all changes listed.\n" +
                                            "Conflicting elements are marked — you must pick ONE side per conflict.\n" +
                                            "Then click 'Apply Selected Changes' to apply and create a merge commit.";
                                    }
                                    else
                                    {
                                        message = $"Found {diffResult.Changes.Count} change(s) to merge from '{branchToMerge}'.\n\n" +
                                            "The Diff Viewer has been opened with all changes listed.\n" +
                                            "Review the changes, adjust selections if needed, then click 'Apply Selected Changes'.";
                                    }

                                    MessageBox.Show(message, "Merge Analysis", MessageBoxButton.OK, 
                                        hasConflicts ? MessageBoxImage.Warning : MessageBoxImage.Information);
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show($"Failed to initiate merge: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
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

        private async void ViewMergeDecisions_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem == null) return;

            var contextMenu = menuItem.Parent as ContextMenu;
            if (contextMenu == null) return;

            var listViewItem = contextMenu.PlacementTarget as ListViewItem;
            if (listViewItem == null) return;

            var commitItem = listViewItem.Content as CommitItem;
            if (commitItem == null || string.IsNullOrEmpty(commitItem.CommitId)) return;

            if (ProjectComboBox.SelectedItem is Project project)
            {
                try
                {
                    System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                    var decisions = await _apiClient.GetHistoricalMergeDecisionsAsync(project.ProjectId, commitItem.CommitId);
                    
                    if (decisions == null || string.IsNullOrEmpty(decisions.ParentCommitId2))
                    {
                        MessageBox.Show("This commit is not a merge commit.", "Historical Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    DiffMergePaneProvider.Instance?.LoadHistoricalMergeResult(decisions, project.ProjectId, commitItem.CommitId);
                    
                    // Show the DiffMergePane pane
                    var paneId = new Autodesk.Revit.UI.DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321"));
                    var diffPane = Autodesk.Revit.UI.RevitCommandId.LookupCommandId("CustomCtrl_%CustomCtrl_%CollabHub%Version Control%DiffMergePane");
                    // Can't show dockable pane directly from UI thread easily unless we have an ExternalEvent or it's already shown
                    // but we can ask the user to open it
                    MessageBox.Show("Merge decisions loaded.\n\nPlease open the 'Changes & Merge' pane from the Version Control tab in the Ribbon to view them.", "Historical Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to load merge decisions: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }
            }
        }

        private async void CompareCommits_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = CommitListView.SelectedItems.Cast<CommitItem>().ToList();
            if (selectedItems.Count != 2)
            {
                MessageBox.Show("Please select exactly 2 commits to compare.\n(Hold Ctrl or Shift while clicking to select multiple).", "Compare Commits", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var commitA = selectedItems[0];
            var commitB = selectedItems[1];

            if (string.IsNullOrEmpty(commitA.CommitId) || string.IsNullOrEmpty(commitB.CommitId))
                return;

            // The older commit should be the base, the newer should be the target
            var baseCommit = commitA.RawTimestamp < commitB.RawTimestamp ? commitA : commitB;
            var targetCommit = commitA.RawTimestamp < commitB.RawTimestamp ? commitB : commitA;

            var project = ProjectComboBox.SelectedItem as Project;
            if (project == null) return;

            try
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                var diffResult = await _apiClient.GetDiffAsync(project.ProjectId, baseCommit.CommitId, targetCommit.CommitId);
                
                if (diffResult != null)
                {
                    DiffMergePaneProvider.Instance?.LoadDiffResult(diffResult);
                    MessageBox.Show($"Loaded comparison between {baseCommit.CommitId.Substring(0, 8)} and {targetCommit.CommitId.Substring(0, 8)}.\n\nPlease open the 'Changes & Merge' pane from the Version Control tab in the Ribbon to view and apply the changes.", "Compare Commits", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Failed to retrieve comparison.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error comparing commits: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
        }

        private class CommitItem
        {
            public string Message { get; set; }
            public string CommitId { get; set; }
            public string BranchName { get; set; }
            public string Author { get; set; }
            public string Timestamp { get; set; }
            public DateTime RawTimestamp { get; set; }
            public int ChangedElements { get; set; }
            public bool IsActive { get; set; }
        }
    }
}
