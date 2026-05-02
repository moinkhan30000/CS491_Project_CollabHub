using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RevitVersionControl.Services;
using ElementId = Autodesk.Revit.DB.ElementId;

namespace RevitVersionControl.UI
{
    public partial class DiffViewerPane : Page
    {
        private readonly ApiClient _apiClient = ApiClient.Instance;

        private List<DiffRow> _allRows = new List<DiffRow>();
        private ElementId _diffViewId;
        private Guid _sessionId = Guid.Empty;

        // Hint state used to auto-select the user's current commit when projects/branches load.
        private string _autoSelectCommitId;
        private string _autoSelectBranchName;

        // Reentrancy guards: while we're populating dropdowns programmatically we don't want
        // the SelectionChanged handlers to chain and cause double-loads.
        private bool _suppressProjectChanged;
        private bool _suppressBranchChanged;
        private bool _suppressCommitChanged;

        public DiffViewerPane()
        {
            InitializeComponent();
            Loaded += DiffViewerPane_Loaded;
        }

        // ===== Public API used by the dockable-pane provider =====

        public async void ReloadProjects()
        {
            if (_apiClient.IsLoggedIn)
                await LoadProjectsAsync();
            else
                Clear(resetPickers: true);
        }

        public void Clear() => Clear(resetPickers: false);

        public void Clear(bool resetPickers)
        {
            _allRows.Clear();
            _diffViewId = null;
            _sessionId = Guid.Empty;
            AddedCountText.Text = "0 added";
            ModifiedCountText.Text = "0 modified";
            DeletedCountText.Text = "0 deleted";
            StatusBanner.Visibility = Visibility.Collapsed;
            StatusBanner.Text = string.Empty;
            ActiveDiffText.Visibility = Visibility.Collapsed;
            ActiveDiffText.Text = string.Empty;
            RowsListView.ItemsSource = null;

            if (resetPickers)
            {
                _suppressProjectChanged = _suppressBranchChanged = _suppressCommitChanged = true;
                ProjectComboBox.ItemsSource = null;
                BranchComboBox.ItemsSource = null;
                BaseCommitComboBox.ItemsSource = null;
                TargetCommitComboBox.ItemsSource = null;
                _suppressProjectChanged = _suppressBranchChanged = _suppressCommitChanged = false;
                UpdateCompareButtonState();
            }
        }

        public void LoadResult(DiffViewBuildResult result)
        {
            if (result == null)
            {
                Clear();
                return;
            }

            _diffViewId = result.DiffViewId;
            _sessionId = result.SessionId;

            AddedCountText.Text = $"{result.AddedCount} added";
            ModifiedCountText.Text = $"{result.ModifiedCount} modified";
            DeletedCountText.Text = $"{result.DeletedCount} deleted";

            string baseShort = string.IsNullOrEmpty(result.BaseShort) ? "-" : result.BaseShort;
            string targetShort = string.IsNullOrEmpty(result.TargetShort) ? "-" : result.TargetShort;
            ActiveDiffText.Text = $"Visualizing: {baseShort} → {targetShort}";
            ActiveDiffText.Visibility = Visibility.Visible;

            if (result.OrderSwapped)
            {
                StatusBanner.Visibility = Visibility.Visible;
                StatusBanner.Text = "Order swapped — showing diff from older to newer.";
            }
            else if (!string.IsNullOrEmpty(result.Message))
            {
                StatusBanner.Visibility = Visibility.Visible;
                StatusBanner.Text = result.Message;
            }
            else
            {
                StatusBanner.Visibility = Visibility.Collapsed;
                StatusBanner.Text = string.Empty;
            }

            _allRows.Clear();
            foreach (var r in result.Rows ?? Enumerable.Empty<DiffViewChangeRow>())
            {
                _allRows.Add(new DiffRow
                {
                    ChangeType = r.ChangeType,
                    Category = r.Category,
                    TypeName = r.TypeName,
                    RepoGuid = r.RepoGuid,
                    ShortRepoGuid = r.ShortRepoGuid,
                    LiveElementIdValue = r.LiveElementId?.Value ?? 0,
                    GhostElementIdValue = r.GhostElementId?.Value ?? 0,
                    Note = r.Note ?? (r.ListOnly ? "List-only" : string.Empty),
                });
            }

            ApplyFilter();
        }

        // ===== Picker / data flow =====

        private async void DiffViewerPane_Loaded(object sender, RoutedEventArgs e)
        {
            if (_apiClient.IsLoggedIn && (ProjectComboBox.ItemsSource == null))
                await LoadProjectsAsync();
        }

        private async Task LoadProjectsAsync()
        {
            try
            {
                _suppressProjectChanged = true;
                ProjectComboBox.IsEnabled = false;

                var projects = await _apiClient.GetProjectsAsync();
                ProjectComboBox.ItemsSource = projects;

                _suppressProjectChanged = false;

                if (projects != null && projects.Count > 0)
                    ProjectComboBox.SelectedIndex = 0;
                else
                {
                    BranchComboBox.ItemsSource = null;
                    BaseCommitComboBox.ItemsSource = null;
                    TargetCommitComboBox.ItemsSource = null;
                    UpdateCompareButtonState();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load projects: {ex.Message}", "Diff Viewer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _suppressProjectChanged = false;
                ProjectComboBox.IsEnabled = true;
            }
        }

        private async void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProjectChanged) return;
            if (!(ProjectComboBox.SelectedItem is Project project)) return;

            // Pull the document's hint for this project so we can pre-select the current branch/commit.
            var hint = DocumentSyncStateService.GetProjectHint(project.ProjectId);
            _autoSelectCommitId = hint?.CurrentCommitId;
            _autoSelectBranchName = hint?.CurrentBranchName;

            await LoadBranchesAsync(project.ProjectId);
        }

        private async Task LoadBranchesAsync(string projectId)
        {
            try
            {
                _suppressBranchChanged = true;
                BranchComboBox.IsEnabled = false;

                var branches = await _apiClient.GetBranchesAsync(projectId);
                BranchComboBox.ItemsSource = branches;

                _suppressBranchChanged = false;

                if (branches == null || branches.Count == 0)
                {
                    BaseCommitComboBox.ItemsSource = null;
                    TargetCommitComboBox.ItemsSource = null;
                    UpdateCompareButtonState();
                    return;
                }

                Branch toSelect = null;
                if (!string.IsNullOrEmpty(_autoSelectBranchName))
                    toSelect = branches.FirstOrDefault(b => string.Equals(b.Name, _autoSelectBranchName, StringComparison.OrdinalIgnoreCase));

                BranchComboBox.SelectedItem = toSelect ?? branches[0];
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load branches: {ex.Message}", "Diff Viewer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _suppressBranchChanged = false;
                BranchComboBox.IsEnabled = true;
            }
        }

        private async void BranchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressBranchChanged) return;

            var project = ProjectComboBox.SelectedItem as Project;
            var branch = BranchComboBox.SelectedItem as Branch;
            if (project == null || branch == null) return;

            await LoadCommitsForBranchAsync(project.ProjectId, branch);
        }

        private async Task LoadCommitsForBranchAsync(string projectId, Branch branch)
        {
            try
            {
                _suppressCommitChanged = true;
                BaseCommitComboBox.IsEnabled = false;
                TargetCommitComboBox.IsEnabled = false;

                var commits = await _apiClient.GetCommitsAsync(projectId, limit: 1000);
                var latestCommit = await _apiClient.GetLatestCommitAsync(projectId);
                var rootCommit = await _apiClient.GetProjectRootCommitAsync(projectId);

                if (commits == null) commits = new List<Commit>();
                if (latestCommit != null && commits.TrueForAll(c => c.CommitId != latestCommit.CommitId))
                    commits.Insert(0, latestCommit);
                if (rootCommit != null && commits.TrueForAll(c => c.CommitId != rootCommit.CommitId))
                    commits.Add(rootCommit);

                commits = commits
                    .GroupBy(c => c.CommitId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderByDescending(c => c.Timestamp)
                    .ToList();

                // Filter to the selected branch's chain — same logic HistoryPane uses.
                var filteredCommits = new List<Commit>();
                if (!string.IsNullOrEmpty(branch.HeadCommitId))
                {
                    var commitMap = commits.ToDictionary(c => c.CommitId, StringComparer.OrdinalIgnoreCase);
                    string currentId = branch.HeadCommitId;
                    while (!string.IsNullOrEmpty(currentId) && commitMap.TryGetValue(currentId, out Commit currentCommit))
                    {
                        filteredCommits.Add(currentCommit);
                        currentId = currentCommit.ParentCommit;
                    }
                }
                else
                {
                    filteredCommits = commits.Where(c => string.Equals(c.BranchName, branch.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                // The root may not link via parent chain on every project, so include it as a tail option too.
                if (rootCommit != null && filteredCommits.TrueForAll(c => c.CommitId != rootCommit.CommitId))
                    filteredCommits.Add(rootCommit);

                var items = filteredCommits.Select(c => new CommitDropdownItem
                {
                    CommitId = c.CommitId,
                    Timestamp = c.Timestamp,
                    DisplayText = BuildDisplayText(c)
                }).ToList();

                BaseCommitComboBox.ItemsSource = items;
                TargetCommitComboBox.ItemsSource = items.ToList(); // separate list so the two combos don't share selection state

                _suppressCommitChanged = false;

                // Auto-select current commit as Target if it's on this branch.
                if (!string.IsNullOrEmpty(_autoSelectCommitId))
                {
                    var match = items.FirstOrDefault(i => string.Equals(i.CommitId, _autoSelectCommitId, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        var targetItems = TargetCommitComboBox.ItemsSource as IEnumerable<CommitDropdownItem>;
                        var targetMatch = targetItems?.FirstOrDefault(i => string.Equals(i.CommitId, _autoSelectCommitId, StringComparison.OrdinalIgnoreCase));
                        if (targetMatch != null) TargetCommitComboBox.SelectedItem = targetMatch;
                    }
                }

                UpdateCompareButtonState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load commits: {ex.Message}", "Diff Viewer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _suppressCommitChanged = false;
                BaseCommitComboBox.IsEnabled = true;
                TargetCommitComboBox.IsEnabled = true;
            }
        }

        private static string BuildDisplayText(Commit c)
        {
            string shortId = string.IsNullOrEmpty(c.CommitId) ? "-" : (c.CommitId.Length > 7 ? c.CommitId.Substring(0, 7) : c.CommitId);
            string msg = string.IsNullOrEmpty(c.Message) ? "(no message)" : c.Message;
            string when = c.Timestamp == default ? "" : c.Timestamp.ToString("yyyy-MM-dd HH:mm");
            return string.IsNullOrEmpty(when) ? $"{shortId}  ·  {msg}" : $"{shortId}  ·  {msg}  ·  {when}";
        }

        private void CommitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCommitChanged) return;
            UpdateCompareButtonState();
        }

        private void UpdateCompareButtonState()
        {
            bool hasProject = ProjectComboBox.SelectedItem != null;
            bool hasBranch = BranchComboBox.SelectedItem != null;
            var b = BaseCommitComboBox.SelectedItem as CommitDropdownItem;
            var t = TargetCommitComboBox.SelectedItem as CommitDropdownItem;

            bool distinct = b != null && t != null && !string.Equals(b.CommitId, t.CommitId, StringComparison.OrdinalIgnoreCase);
            CompareButton.IsEnabled = hasProject && hasBranch && distinct;

            if (!hasProject) CompareButton.ToolTip = "Pick a project first.";
            else if (!hasBranch) CompareButton.ToolTip = "Pick a branch.";
            else if (b == null || t == null) CompareButton.ToolTip = "Pick base and target commits.";
            else if (!distinct) CompareButton.ToolTip = "Base and target are the same commit.";
            else CompareButton.ToolTip = "Compare the two selected commits.";
        }

        // ===== Buttons =====

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadProjectsAsync();
        }

        private async void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProjectComboBox.SelectedItem is Project project)) return;
            if (!(BaseCommitComboBox.SelectedItem is CommitDropdownItem baseItem)) return;
            if (!(TargetCommitComboBox.SelectedItem is CommitDropdownItem targetItem)) return;

            string baseId = baseItem.CommitId;
            string targetId = targetItem.CommitId;
            bool swapped = false;

            // Auto-swap by timestamp so colors keep their semantics (older = base, newer = target).
            if (baseItem.Timestamp != default && targetItem.Timestamp != default
                && baseItem.Timestamp > targetItem.Timestamp)
            {
                var tmp = baseId; baseId = targetId; targetId = tmp;
                swapped = true;
            }

            CompareButton.IsEnabled = false;
            try
            {
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;

                var (diff, baseSnapshot, error) = await DiffViewService.FetchDiffAsync(project.ProjectId, baseId, targetId);

                System.Windows.Input.Mouse.OverrideCursor = null;

                if (!string.IsNullOrEmpty(error) || diff == null)
                {
                    MessageBox.Show(error ?? "Failed to fetch diff.", "Diff Viewer",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                int total = diff.Summary != null && diff.Summary.TryGetValue("total", out var t) ? t : (diff.Changes?.Count ?? 0);
                if (total == 0)
                {
                    MessageBox.Show("No differences between these commits.", "Diff Viewer",
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
                    MessageBox.Show("Diff viewer is not registered. Restart Revit.", "Diff Viewer",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var sessionId = Guid.NewGuid();
                DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
                {
                    Operation = DiffViewerOperation.Build,
                    BuildRequest = new DiffViewBuildRequest
                    {
                        ProjectId = project.ProjectId,
                        BaseCommitId = baseId,
                        TargetCommitId = targetId,
                        Diff = diff,
                        BaseSnapshot = baseSnapshot,
                        SessionId = sessionId,
                        OrderSwapped = swapped
                    },
                    OnBuildComplete = result =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (result == null || !result.Success)
                            {
                                MessageBox.Show(result?.Message ?? "Failed to build diff view.",
                                    "Diff Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        });
                    }
                });
                DiffViewerExternalEvent.Event.Raise();
            }
            catch (Exception ex)
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
                MessageBox.Show($"Compare failed: {ex.Message}", "Diff Viewer",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateCompareButtonState();
            }
        }

        private void ApplyFilter()
        {
            bool showAdded = FilterAdded.IsChecked == true;
            bool showModified = FilterModified.IsChecked == true;
            bool showDeleted = FilterDeleted.IsChecked == true;

            var filtered = _allRows.Where(r =>
                (r.ChangeType == "added" && showAdded) ||
                (r.ChangeType == "modified" && showModified) ||
                (r.ChangeType == "deleted" && showDeleted)).ToList();

            RowsListView.ItemsSource = filtered;
        }

        private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ZoomTo_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Tag is DiffRow row))
                return;

            long elementIdValue = row.LiveElementIdValue != 0 ? row.LiveElementIdValue : row.GhostElementIdValue;
            if (elementIdValue == 0) return;

            if (DiffViewerExternalEvent.Instance == null || DiffViewerExternalEvent.Event == null)
                return;

            DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
            {
                Operation = DiffViewerOperation.ZoomTo,
                TargetElementId = new ElementId(elementIdValue)
            });
            DiffViewerExternalEvent.Event.Raise();
        }

        private void ClearDiff_Click(object sender, RoutedEventArgs e)
        {
            if (DiffViewerExternalEvent.Instance == null || DiffViewerExternalEvent.Event == null)
            {
                MessageBox.Show("Diff viewer event handler is not registered.", "Diff Viewer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
            {
                Operation = DiffViewerOperation.Clear,
                DiffViewId = _diffViewId,
                SessionId = _sessionId == Guid.Empty ? (Guid?)null : _sessionId,
                OnClearComplete = () => Dispatcher.Invoke(() => Clear())
            });
            DiffViewerExternalEvent.Event.Raise();
        }

        private void CleanOrphans_Click(object sender, RoutedEventArgs e)
        {
            if (DiffViewerExternalEvent.Instance == null || DiffViewerExternalEvent.Event == null)
                return;

            DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
            {
                Operation = DiffViewerOperation.CleanOrphans,
                OnClearComplete = () => Dispatcher.Invoke(() => Clear())
            });
            DiffViewerExternalEvent.Event.Raise();
        }

        // ===== Models =====

        public class CommitDropdownItem
        {
            public string CommitId { get; set; }
            public DateTime Timestamp { get; set; }
            public string DisplayText { get; set; }
        }

        public class DiffRow
        {
            public string ChangeType { get; set; }
            public string Category { get; set; }
            public string TypeName { get; set; }
            public string RepoGuid { get; set; }
            public string ShortRepoGuid { get; set; }
            public string Note { get; set; }
            public long LiveElementIdValue { get; set; }
            public long GhostElementIdValue { get; set; }

            public string ShortType => ChangeType switch
            {
                "added" => "ADD",
                "modified" => "MOD",
                "deleted" => "DEL",
                _ => (ChangeType ?? "-").ToUpperInvariant()
            };

            public Brush StatusBrush
            {
                get
                {
                    return ChangeType switch
                    {
                        "added" => new SolidColorBrush(Color.FromRgb(0, 200, 83)),
                        "modified" => new SolidColorBrush(Color.FromRgb(181, 137, 0)),
                        "deleted" => new SolidColorBrush(Color.FromRgb(213, 0, 0)),
                        _ => new SolidColorBrush(Color.FromRgb(160, 160, 160))
                    };
                }
            }

            public bool HasGeometry => LiveElementIdValue != 0 || GhostElementIdValue != 0;
        }
    }
}
