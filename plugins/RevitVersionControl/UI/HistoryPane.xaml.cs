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
        private bool _suppressBranchSwitch;

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
                _suppressBranchSwitch = true;
                BranchComboBox.IsEnabled = false;
                var branches = await _apiClient.GetBranchesAsync(projectId);

                BranchComboBox.ItemsSource = branches;
                BranchComboBox.DisplayMemberPath = "Name";
                if (branches.Count > 0)
                {
                    string activeBranch = DocumentSyncStateService.GetStatusForProject(
                        null, projectId, false)?.State?.CurrentBranchName ?? "main";
                    
                    int index = branches.FindIndex(b => string.Equals(b.Name, activeBranch, StringComparison.OrdinalIgnoreCase));
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
                _suppressBranchSwitch = false;
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
                if (selectedBranch != null)
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
            if (_suppressBranchSwitch) return;
            if (!(ProjectComboBox.SelectedItem is Project project)) return;
            if (!(BranchComboBox.SelectedItem is Branch selectedBranch)) return;

            var hint = DocumentSyncStateService.GetProjectHint(project.ProjectId);
            string currentCommitId = hint?.CurrentCommitId;
            string currentBranchName = hint?.CurrentBranchName ?? "main";

            // If user selected the branch they're already on, just refresh commits
            if (string.Equals(selectedBranch.Name, currentBranchName, StringComparison.OrdinalIgnoreCase))
            {
                await LoadCommitsAsync(project.ProjectId);
                return;
            }

            // Different branch selected — trigger a branch switch
            if (!string.IsNullOrWhiteSpace(selectedBranch.HeadCommitId) && selectedBranch.HeadCommitId != currentCommitId)
            {
                var result = MessageBox.Show(
                    $"Switch to branch '{selectedBranch.Name}'?\n\nThis will pull the latest commit from that branch.",
                    "Switch Branch", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _switchHandler.Queue(new BranchSwitchRequest
                    {
                        ProjectId = project.ProjectId,
                        TargetBranch = selectedBranch.Name,
                        TargetCommitId = selectedBranch.HeadCommitId,
                        CurrentCommitId = currentCommitId
                    });
                    _switchEvent.Raise();
                }
                else
                {
                    // User cancelled — revert dropdown to current branch
                    _suppressBranchSwitch = true;
                    var branches = BranchComboBox.ItemsSource as List<Branch>;
                    int idx = branches?.FindIndex(b => string.Equals(b.Name, currentBranchName, StringComparison.OrdinalIgnoreCase)) ?? -1;
                    BranchComboBox.SelectedIndex = idx >= 0 ? idx : 0;
                    _suppressBranchSwitch = false;
                }
            }
            else
            {
                // Same commit (e.g., new branch from current position) — just update tracking
                if (hint != null && !string.IsNullOrWhiteSpace(hint.LastKnownDocumentPath))
                {
                    DocumentSyncStateService.SaveState(hint.LastKnownDocumentPath, project.ProjectId, hint.ModelId, hint.CurrentCommitId, selectedBranch.Name);
                    CurrentTrackedBranchText.Text = $"Active Branch: {selectedBranch.Name}";
                }
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

        private async void CreateBranchFromCommit_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            if (menuItem == null) return;

            var contextMenu = menuItem.Parent as ContextMenu;
            if (contextMenu == null) return;

            var listViewItem = contextMenu.PlacementTarget as ListViewItem;
            if (listViewItem == null) return;

            var commitItem = listViewItem.Content as CommitItem;
            if (commitItem == null || string.IsNullOrEmpty(commitItem.CommitId)) return;

            if (!(ProjectComboBox.SelectedItem is Project project)) return;

            // Prompt for branch name
            string shortCommitId = commitItem.CommitId.Length > 8 ? commitItem.CommitId.Substring(0, 8) : commitItem.CommitId;
            var inputWindow = new Window
            {
                Title = "Create Branch",
                Width = 340,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            var label = new System.Windows.Controls.TextBlock
            {
                Text = $"Create branch from commit {shortCommitId}:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            System.Windows.Controls.Grid.SetRow(label, 0);
            grid.Children.Add(label);

            var textBox = new System.Windows.Controls.TextBox
            {
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 15)
            };
            System.Windows.Controls.Grid.SetRow(textBox, 1);
            grid.Children.Add(textBox);

            var buttonPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            System.Windows.Controls.Grid.SetRow(buttonPanel, 2);

            var createBtn = new System.Windows.Controls.Button
            {
                Content = "Create & Switch",
                Width = 110,
                Height = 28,
                Margin = new Thickness(0, 0, 10, 0),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#007ACC")),
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold,
                IsDefault = true
            };
            createBtn.Click += (s, ev) =>
            {
                if (string.IsNullOrWhiteSpace(textBox.Text))
                {
                    MessageBox.Show("Please enter a branch name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                inputWindow.DialogResult = true;
                inputWindow.Close();
            };

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 28,
                IsCancel = true
            };
            cancelBtn.Click += (s, ev) => { inputWindow.DialogResult = false; inputWindow.Close(); };

            buttonPanel.Children.Add(createBtn);
            buttonPanel.Children.Add(cancelBtn);
            grid.Children.Add(buttonPanel);
            inputWindow.Content = grid;
            inputWindow.Loaded += (s, ev) => textBox.Focus();

            if (inputWindow.ShowDialog() != true) return;

            string newBranchName = textBox.Text.Trim();

            try
            {
                // Check if branch already exists
                var existingBranches = await _apiClient.GetBranchesAsync(project.ProjectId);
                if (existingBranches.Any(b => string.Equals(b.Name, newBranchName, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show($"Branch '{newBranchName}' already exists. Please choose a different name.",
                        "Branch Exists", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Create branch from the selected commit
                await _apiClient.CreateBranchAsync(project.ProjectId, newBranchName, commitItem.CommitId);

                // Reload branches and switch to the new one
                await LoadBranchesAsync(project.ProjectId);
                var branches = BranchComboBox.ItemsSource as List<Branch>;
                var newBranch = branches?.FirstOrDefault(b => string.Equals(b.Name, newBranchName, StringComparison.OrdinalIgnoreCase));

                if (newBranch != null)
                {
                    var hint = DocumentSyncStateService.GetProjectHint(project.ProjectId);
                    string currentCommitId = hint?.CurrentCommitId;

                    if (!string.IsNullOrWhiteSpace(newBranch.HeadCommitId) && newBranch.HeadCommitId != currentCommitId)
                    {
                        // Different commit — need to pull
                        _switchHandler.Queue(new BranchSwitchRequest
                        {
                            ProjectId = project.ProjectId,
                            TargetBranch = newBranchName,
                            TargetCommitId = newBranch.HeadCommitId,
                            CurrentCommitId = currentCommitId
                        });
                        _switchEvent.Raise();
                    }
                    else
                    {
                        // Same commit — just update tracking
                        if (hint != null && !string.IsNullOrWhiteSpace(hint.LastKnownDocumentPath))
                        {
                            DocumentSyncStateService.SaveState(hint.LastKnownDocumentPath, project.ProjectId, hint.ModelId, hint.CurrentCommitId, newBranchName);
                            CurrentTrackedBranchText.Text = $"Active Branch: {newBranchName}";
                        }
                    }

                    _suppressBranchSwitch = true;
                    BranchComboBox.SelectedItem = newBranch;
                    _suppressBranchSwitch = false;
                    await LoadCommitsAsync(project.ProjectId);
                }

                MessageBox.Show($"Branch '{newBranchName}' created from commit {shortCommitId}!", "Success",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create branch: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
