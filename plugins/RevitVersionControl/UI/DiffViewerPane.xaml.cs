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
        private DiffResult _lastDiffResult;  // stored for Apply Selected

        // Merge mode context
        private bool _isMergeMode;
        private string _mergeProjectId;
        private string _mergeSourceCommitId;  // "ours"
        private string _mergeTargetCommitId;  // "theirs"
        private string _mergeTargetBranchName;
        private Merge3WayResult _merge3WayResult;

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

        /// <summary>
        /// Trigger the full Compare pipeline for merge: fetch diff + snapshot, build visual diff view,
        /// and populate the pane with proper element IDs (same as clicking Compare manually).
        /// </summary>
        public async void LoadDiffForMerge(DiffResult diffResult, string projectId, string baseCommitId, string targetCommitId, string targetBranchName, Merge3WayResult merge3Way)
        {
            if (diffResult == null) { Clear(resetPickers: false); return; }

            if (merge3Way == null)
            {
                MessageBox.Show("Cannot proceed with merge: 3-way conflict analysis is required but unavailable.", "Merge Blocked", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Store merge context
            _isMergeMode = true;
            _mergeProjectId = projectId;
            _mergeSourceCommitId = baseCommitId;
            _mergeTargetCommitId = targetCommitId;
            _mergeTargetBranchName = targetBranchName;
            _merge3WayResult = merge3Way;
            _lastDiffResult = diffResult;

            // Show a merge banner immediately
            StatusBanner.Text = $"Merge mode: fetching visual diff from '{targetBranchName}'...";
            StatusBanner.Visibility = Visibility.Visible;

            try
            {
                // Fetch the base snapshot (needed for ghost building)
                var (diff, baseSnapshot, error) = await DiffViewService.FetchDiffAsync(projectId, baseCommitId, targetCommitId);

                if (!string.IsNullOrEmpty(error) || diff == null)
                {
                    StatusBanner.Text = $"Merge mode: Could not build visual diff. Use list below to apply changes.";
                    // Fall back to populating rows from the diff result we already have
                    PopulateRowsFromDiff(diffResult, targetBranchName);
                    return;
                }

                // Use the fresh diff (it's the same data, but ensures consistency)
                _lastDiffResult = diff;

                int total = diff.Summary != null && diff.Summary.TryGetValue("total", out var t) ? t : (diff.Changes?.Count ?? 0);
                if (total == 0)
                {
                    StatusBanner.Text = "No differences found.";
                    return;
                }

                // Queue the visual diff build via the same ExternalEvent the Compare button uses
                if (DiffViewerExternalEvent.Instance == null || DiffViewerExternalEvent.Event == null)
                {
                    StatusBanner.Text = "Merge mode: Diff viewer not registered. Restart Revit.";
                    PopulateRowsFromDiff(diff, targetBranchName);
                    return;
                }

                var sessionId = Guid.NewGuid();
                DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
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
                        OrderSwapped = false
                    },
                    OnBuildComplete = result =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (result != null && result.Success)
                            {
                                LoadResult(result);
                                AnnotateConflictRows();
                                int conflictCount = _merge3WayResult?.Conflicts?.Count ?? 0;
                                string conflictInfo = conflictCount > 0 ? $" ({conflictCount} conflict(s) — pick one side per conflict)" : "";
                                StatusBanner.Text = $"Merge mode: Select changes to apply from '{targetBranchName}'{conflictInfo}";
                                StatusBanner.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                StatusBanner.Text = $"Merge mode: Visual diff failed. {result?.Message ?? ""}";
                                PopulateRowsFromDiff(diff, targetBranchName);
                            }
                        });
                    }
                });
                DiffViewerExternalEvent.Event.Raise();
            }
            catch (Exception ex)
            {
                StatusBanner.Text = $"Merge mode: Error fetching diff - {ex.Message}";
                PopulateRowsFromDiff(diffResult, targetBranchName);
            }
        }

        /// <summary>
        /// Fallback: populate rows from diff data without visual ghost elements.
        /// </summary>
        private void PopulateRowsFromDiff(DiffResult diffResult, string branchName)
        {
            _allRows.Clear();
            int addedCount = 0, modifiedCount = 0, deletedCount = 0;

            foreach (var change in diffResult.Changes ?? new List<Change>())
            {
                string ct = change.ChangeType ?? "";
                if (ct == "added") addedCount++;
                else if (ct == "modified") modifiedCount++;
                else if (ct == "deleted") deletedCount++;

                string repoGuid = change.RepoGuid ?? "";
                string shortRepo = string.IsNullOrEmpty(repoGuid) ? "-" : (repoGuid.Length > 8 ? repoGuid.Substring(0, 8) : repoGuid);

                _allRows.Add(new DiffRow
                {
                    ChangeType = ct,
                    Category = change.Category ?? "",
                    TypeName = change.Type ?? "",
                    RepoGuid = repoGuid,
                    ShortRepoGuid = shortRepo,
                    Note = $"Merge from {branchName}",
                    IsSelected = true
                });
            }

            AddedCountText.Text = $"{addedCount} added";
            ModifiedCountText.Text = $"{modifiedCount} modified";
            DeletedCountText.Text = $"{deletedCount} deleted";
            ActiveDiffText.Visibility = Visibility.Visible;

            ApplyFilter();
            UpdateSelectionCount();
        }

        public void Clear() => Clear(resetPickers: false);

        public void Clear(bool resetPickers)
        {
            _allRows.Clear();
            _diffViewId = null;
            _sessionId = Guid.Empty;
            _lastDiffResult = null;

            // Reset merge context
            _isMergeMode = false;
            _mergeProjectId = null;
            _mergeSourceCommitId = null;
            _mergeTargetCommitId = null;
            _mergeTargetBranchName = null;
            _merge3WayResult = null;

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
            UpdateSelectionCount();
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
                var allBranches = new List<Branch> { new Branch { Name = "All Branches" } };
                if (branches != null) allBranches.AddRange(branches);
                BranchComboBox.ItemsSource = allBranches;

                _suppressBranchChanged = false;

                if (allBranches.Count == 0)
                {
                    BaseCommitComboBox.ItemsSource = null;
                    TargetCommitComboBox.ItemsSource = null;
                    UpdateCompareButtonState();
                    return;
                }

                Branch toSelect = null;
                if (!string.IsNullOrEmpty(_autoSelectBranchName))
                    toSelect = allBranches.FirstOrDefault(b => string.Equals(b.Name, _autoSelectBranchName, StringComparison.OrdinalIgnoreCase));

                BranchComboBox.SelectedItem = toSelect ?? allBranches[0];
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
                // If "All Branches" is selected, show all commits.
                List<Commit> filteredCommits;
                if (branch.Name == "All Branches")
                {
                    filteredCommits = commits;
                }
                else if (!string.IsNullOrEmpty(branch.HeadCommitId))
                {
                    filteredCommits = new List<Commit>();
                    var commitMap = commits.ToDictionary(c => c.CommitId, StringComparer.OrdinalIgnoreCase);
                    string currentId = branch.HeadCommitId;
                    while (!string.IsNullOrEmpty(currentId) && commitMap.TryGetValue(currentId, out Commit currentCommit))
                    {
                        filteredCommits.Add(currentCommit);
                        currentId = currentCommit.ParentCommit;
                    }
                    // The root may not link via parent chain on every project, so include it as a tail option too.
                    if (rootCommit != null && filteredCommits.TrueForAll(c => c.CommitId != rootCommit.CommitId))
                        filteredCommits.Add(rootCommit);
                }
                else
                {
                    filteredCommits = commits.Where(c => string.Equals(c.BranchName, branch.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                }

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
                    _lastDiffResult = null;
                    return;
                }

                _lastDiffResult = diff;

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
            UpdateSelectionCount();
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

        // ===== Selection / Apply =====

        /// <summary>
        /// Annotate rows with conflict group information from the 3-way merge result.
        /// Conflicting elements get the same ConflictGroupId but different ConflictSide.
        /// </summary>
        private void AnnotateConflictRows()
        {
            if (_merge3WayResult?.Conflicts == null || _merge3WayResult.Conflicts.Count == 0) return;

            // Build a set of conflicting element IDs
            var conflictElementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _merge3WayResult.Conflicts)
            {
                if (!string.IsNullOrEmpty(c.ElementId))
                    conflictElementIds.Add(c.ElementId);
            }

            // Build sets for source vs target changes to determine "side"
            var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ch in _merge3WayResult.SourceChanges ?? new List<Change>())
            {
                string id = ch.RepoGuid ?? ch.ElementId ?? "";
                if (!string.IsNullOrEmpty(id)) sourceIds.Add(id);
            }
            foreach (var ch in _merge3WayResult.TargetChanges ?? new List<Change>())
            {
                string id = ch.RepoGuid ?? ch.ElementId ?? "";
                if (!string.IsNullOrEmpty(id)) targetIds.Add(id);
            }

            foreach (var row in _allRows)
            {
                string rowId = row.RepoGuid ?? "";
                if (string.IsNullOrEmpty(rowId)) continue;

                // Check if this row's element is in a conflict
                bool isConflict = conflictElementIds.Contains(rowId);
                if (!isConflict) continue;

                row.ConflictGroupId = rowId;

                // Determine side: if it appears in source → ours, if in target → theirs
                if (sourceIds.Contains(rowId) && targetIds.Contains(rowId))
                {
                    // Both sides have this element. The diff we display is source→target,
                    // so the row represents the "theirs" change. We need to check index:
                    // The diff between our commit and their commit shows "their" version as the target.
                    row.ConflictSide = "theirs";
                    row.Note = "⚠ CONFLICT (theirs)";
                    row.IsSelected = false; // Default: deselect "theirs", keep "ours"
                }
                else if (sourceIds.Contains(rowId))
                {
                    row.ConflictSide = "ours";
                    row.Note = "⚠ CONFLICT (ours — current)";
                    row.IsSelected = true;
                }
                else if (targetIds.Contains(rowId))
                {
                    row.ConflictSide = "theirs";
                    row.Note = "⚠ CONFLICT (theirs — incoming)";
                    row.IsSelected = false;
                }
            }

            ApplyFilter();
            UpdateSelectionCount();
        }

        private void RowCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.DataContext is DiffRow clickedRow
                && !string.IsNullOrEmpty(clickedRow.ConflictGroupId))
            {
                // Mutual exclusion: if checking this row, uncheck the other side in the same conflict group
                if (clickedRow.IsSelected)
                {
                    foreach (var other in _allRows)
                    {
                        if (other != clickedRow
                            && other.ConflictGroupId == clickedRow.ConflictGroupId
                            && other.ConflictSide != clickedRow.ConflictSide)
                        {
                            other.IsSelected = false;
                        }
                    }
                    // Refresh the list to show updated checkboxes
                    ApplyFilter();
                }
            }
            UpdateSelectionCount();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _allRows) row.IsSelected = true;
            ApplyFilter();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var row in _allRows) row.IsSelected = false;
            ApplyFilter();
        }

        private void UpdateSelectionCount()
        {
            int count = _allRows.Count(r => r.IsSelected);
            SelectionCountText.Text = $"{count} selected";
            ApplySelectedButton.IsEnabled = count > 0 && _lastDiffResult != null;
        }

        private void ApplySelected_Click(object sender, RoutedEventArgs e)
        {
            if (_lastDiffResult == null || _lastDiffResult.Changes == null)
            {
                MessageBox.Show("No diff data available to apply.", "Apply Changes",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate conflict constraints: can't select both sides
            if (_isMergeMode && _merge3WayResult?.Conflicts != null)
            {
                foreach (var conflict in _merge3WayResult.Conflicts)
                {
                    string cid = conflict.ElementId ?? "";
                    if (string.IsNullOrEmpty(cid)) continue;

                    var conflictRows = _allRows.Where(r => r.ConflictGroupId == cid && r.IsSelected).ToList();
                    var sides = conflictRows.Select(r => r.ConflictSide).Where(s => s != null).Distinct().ToList();
                    if (sides.Count > 1)
                    {
                        MessageBox.Show($"Conflict for element '{cid}': you must pick only ONE side (ours or theirs), not both.", "Conflict Error",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
            }

            // Build selected changes using index-based matching.
            var selectedChanges = new List<Change>();
            int changeCount = _lastDiffResult.Changes.Count;
            for (int i = 0; i < _allRows.Count && i < changeCount; i++)
            {
                if (_allRows[i].IsSelected)
                    selectedChanges.Add(_lastDiffResult.Changes[i]);
            }

            if (selectedChanges.Count == 0)
            {
                // Fallback: RepoGuid matching
                var selectedRepoGuids = new HashSet<string>(
                    _allRows.Where(r => r.IsSelected && !string.IsNullOrEmpty(r.RepoGuid))
                            .Select(r => r.RepoGuid), StringComparer.OrdinalIgnoreCase);

                selectedChanges = _lastDiffResult.Changes
                    .Where(c => !string.IsNullOrEmpty(c.RepoGuid) && selectedRepoGuids.Contains(c.RepoGuid))
                    .ToList();
            }

            if (selectedChanges.Count == 0)
            {
                MessageBox.Show("No matching changes found for the selected rows.", "Apply Changes",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int addCount = selectedChanges.Count(c => c.ChangeType == "added");
            int modCount = selectedChanges.Count(c => c.ChangeType == "modified");
            int delCount = selectedChanges.Count(c => c.ChangeType == "deleted");

            string mergeNote = _isMergeMode ? "\n\nA merge commit will be created after applying." : "";
            var confirm = MessageBox.Show(
                $"Apply {selectedChanges.Count} selected change(s)?\n\n" +
                $"  • Added: {addCount}\n" +
                $"  • Modified: {modCount}\n" +
                $"  • Deleted: {delCount}{mergeNote}\n\n" +
                "This will modify your active Revit model.",
                "Confirm Apply",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            if (DiffViewerExternalEvent.Instance == null || DiffViewerExternalEvent.Event == null)
            {
                MessageBox.Show("Event handler not registered. Restart Revit.", "Apply Changes",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Disable button immediately to prevent duplicate clicks
            ApplySelectedButton.IsEnabled = false;

            string projectId = _isMergeMode ? _mergeProjectId : (ProjectComboBox.SelectedItem as Project)?.ProjectId;

            // Capture merge context before it gets cleared
            bool isMerge = _isMergeMode;
            string mergeProjectId = _mergeProjectId;
            string mergeSourceCommitId = _mergeSourceCommitId;
            string mergeTargetCommitId = _mergeTargetCommitId;
            string mergeTargetBranchName = _mergeTargetBranchName;
            Merge3WayResult merge3Way = _merge3WayResult;
            ElementId diffViewId = _diffViewId;
            Guid sessionId = _sessionId;

            DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
            {
                Operation = DiffViewerOperation.ApplySelected,
                ApplyChanges = selectedChanges,
                ApplyProjectId = projectId,
                OnApplyComplete = result =>
                {
                    Dispatcher.Invoke(async () =>
                    {
                        if (result != null && result.Success)
                        {
                            // 1. Clean ghosts/bounding boxes
                            if (DiffViewerExternalEvent.Instance != null && DiffViewerExternalEvent.Event != null)
                            {
                                DiffViewerExternalEvent.Instance.Queue(new DiffViewerRequest
                                {
                                    Operation = DiffViewerOperation.Clear,
                                    DiffViewId = diffViewId,
                                    SessionId = sessionId == Guid.Empty ? (Guid?)null : sessionId,
                                    OnClearComplete = () => { }
                                });
                                DiffViewerExternalEvent.Event.Raise();
                            }

                            // 2. Create merge commit on backend if in merge mode
                            if (isMerge && !string.IsNullOrEmpty(mergeProjectId))
                            {
                                await CreateMergeCommitAsync(mergeProjectId, merge3Way?.CommonAncestorId ?? mergeSourceCommitId, mergeSourceCommitId, mergeTargetCommitId, mergeTargetBranchName, merge3Way);
                            }

                            // 3. Reset the panel
                            Clear();
                            MessageBox.Show(result.Summary ?? "Changes applied successfully.",
                                "Apply Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        else
                        {
                            ApplySelectedButton.IsEnabled = true;
                            MessageBox.Show(result?.Summary ?? "Failed to apply changes.",
                                "Apply Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    });
                }
            });
            DiffViewerExternalEvent.Event.Raise();
        }

        /// <summary>
        /// Create a merge commit on the backend after applying changes locally.
        /// </summary>
        private async Task CreateMergeCommitAsync(string projectId, string baseCommit, string sourceCommit, string targetCommit, string targetBranchName, Merge3WayResult merge3Way)
        {
            try
            {
                // Build resolution list from conflict selections
                var resolutions = new List<ConflictResolution>();
                if (merge3Way?.Conflicts != null)
                {
                    foreach (var conflict in merge3Way.Conflicts)
                    {
                        string cid = conflict.ElementId ?? "";
                        if (string.IsNullOrEmpty(cid)) continue;

                        var selectedRow = _allRows.FirstOrDefault(r => r.ConflictGroupId == cid && r.IsSelected);
                        string resolution = selectedRow?.ConflictSide == "theirs" ? "accept_remote" : "keep_local";
                        resolutions.Add(new ConflictResolution { ElementId = cid, ResolutionType = resolution });
                    }
                }

                var hint = DocumentSyncStateService.GetProjectHint(projectId);
                string currentBranchName = hint?.CurrentBranchName;

                string message = $"Merge branch '{targetBranchName}' into current";
                var mergeResult = await _apiClient.MergeCommitAsync(projectId, baseCommit, sourceCommit, targetCommit, resolutions, message, currentBranchName);

                if (mergeResult != null && mergeResult.Status == "success")
                {
                    // Update local tracking to the new merge commit
                    if (hint != null && !string.IsNullOrWhiteSpace(hint.LastKnownDocumentPath))
                    {
                        DocumentSyncStateService.SaveState(hint.LastKnownDocumentPath, projectId, hint.ModelId, mergeResult.MergeCommitId, hint.CurrentBranchName);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Changes applied locally but merge commit creation failed:\n{ex.Message}\n\nYou may need to commit manually.", "Merge Commit Warning",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
            public bool IsSelected { get; set; } = true;
            public string ConflictGroupId { get; set; }  // null = not a conflict
            public string ConflictSide { get; set; }     // "ours" or "theirs"

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
