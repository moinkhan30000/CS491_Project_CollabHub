using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Threading.Tasks;
using RevitVersionControl.Services;
using Autodesk.Revit.UI;

namespace RevitVersionControl.UI
{
    public partial class DiffMergePane : Page
    {
        private List<Change> _currentChanges = new List<Change>();
        private List<ConflictItem> _currentConflicts = new List<ConflictItem>();
        
        private string _currentProjectId;
        private string _currentTargetCommitId;
        private string _currentLocalCommitId;
        private string _currentModelId;
        
        private readonly DiffMergeApplyHandler _applyHandler;
        private readonly ExternalEvent _applyExternalEvent;

        public DiffMergePane()
        {
            _applyHandler = new DiffMergeApplyHandler();
            _applyExternalEvent = ExternalEvent.Create(_applyHandler);
            InitializeComponent();
            Clear();
        }

        public void LoadDiffResult(DiffResult diffResult)
        {
            if (diffResult == null) { Clear(); return; }

            _currentProjectId = null;
            BaseCommitText.Text = diffResult.BaseVersion ?? "-";
            TargetCommitText.Text = diffResult.TargetVersion ?? "-";

            int added = diffResult.Summary != null && diffResult.Summary.ContainsKey("added") ? diffResult.Summary["added"] : 0;
            int modified = diffResult.Summary != null && diffResult.Summary.ContainsKey("modified") ? diffResult.Summary["modified"] : 0;
            int deleted = diffResult.Summary != null && diffResult.Summary.ContainsKey("deleted") ? diffResult.Summary["deleted"] : 0;

            AddedCountText.Text = added.ToString();
            ModifiedCountText.Text = modified.ToString();
            DeletedCountText.Text = deleted.ToString();

            _currentChanges = diffResult.Changes ?? new List<Change>();
            ChangesListView.ItemsSource = BuildChangeItems(_currentChanges);

            ConflictsPanel.Visibility = Visibility.Collapsed;
            _currentConflicts.Clear();
            ApplyButton.Content = "Apply Selected";
        }

        public async void LoadPullResult(PullResult pullResult, string projectId = null, string currentCommitId = null, string targetCommitId = null, string modelId = null)
        {
            if (pullResult == null) { Clear(); return; }

            _currentProjectId = projectId;
            _currentLocalCommitId = currentCommitId;
            _currentTargetCommitId = targetCommitId;
            _currentModelId = modelId;
            
            BaseCommitText.Text = currentCommitId ?? "-";
            TargetCommitText.Text = targetCommitId ?? "-";

            // If the backend demands a Merge Resolution
            if (pullResult.RequiresResolution)
            {
                // CASE A: Hard Conflicts exist - Show the UI
                if (pullResult.Conflicts != null && pullResult.Conflicts.Count > 0)
                {
                    ConflictsPanel.Visibility = Visibility.Visible;
                    _currentConflicts = pullResult.Conflicts.Select(c => new ConflictItem(c)).ToList();
                    ConflictsListView.ItemsSource = _currentConflicts;
                    ApplyButton.Content = "Resolve & Merge";
                    
                    _currentChanges = new List<Change>(); // Clear normal view
                    ChangesListView.ItemsSource = null;
                }
                // CASE B: Safe Divergence - No conflicts, so we AUTO-MERGE instantly!
                else
                {
                    ConflictsPanel.Visibility = Visibility.Collapsed;
                    _currentConflicts.Clear();
                    
                    ApplyButton.Content = "Auto-merging...";
                    ApplyButton.IsEnabled = false;

                    // Automatically trigger a merge with zero conflict resolutions
                    var mergeReq = new MergeRequest
                    {
                        BaseCommit = null, 
                        SourceCommit = _currentTargetCommitId, 
                        TargetCommit = _currentLocalCommitId, 
                        Resolutions = new List<Resolution>(),
                        Message = $"Auto-Merge {_currentTargetCommitId} into {_currentLocalCommitId}"
                    };

                    var mergeResultTask = Task.Run(async () => await ApiClient.Instance.MergeAsync(_currentProjectId, mergeReq));
                    var mergeResult = await mergeResultTask;

                    if (mergeResult != null && mergeResult.Status == "success")
                    {
                        // Successfully created the Squash Commit! Pivot and pull it natively.
                        _currentTargetCommitId = mergeResult.MergeCommitId;
                        TargetCommitText.Text = _currentTargetCommitId;
                        
                        var fetchTask = Task.Run(async () => await ApiClient.Instance.PullChangesAsync(_currentProjectId, _currentLocalCommitId, _currentTargetCommitId));
                        var freshPull = await fetchTask;

                        if (freshPull != null && !freshPull.RequiresResolution)
                        {
                            ApplyButton.Content = "Apply Safe Merge";
                            ApplyButton.IsEnabled = true;
                            
                            _currentChanges = freshPull.Changes ?? new List<Change>();
                            ChangesListView.ItemsSource = BuildChangeItems(_currentChanges);
                        }
                    }
                }
            }
            else
            {
                // Normal Fast-Forward or Rollback Pull
                ConflictsPanel.Visibility = Visibility.Collapsed;
                _currentConflicts.Clear();
                ApplyButton.Content = "Apply Selected";

                var summary = Summarize(pullResult.Changes);
                AddedCountText.Text = summary.added.ToString();
                ModifiedCountText.Text = summary.modified.ToString();
                DeletedCountText.Text = summary.deleted.ToString();

                _currentChanges = pullResult.Changes ?? new List<Change>();
                ChangesListView.ItemsSource = BuildChangeItems(_currentChanges);
            }
        }

        public void Clear()
        {
            BaseCommitText.Text = "-";
            TargetCommitText.Text = "-";
            AddedCountText.Text = "0";
            ModifiedCountText.Text = "0";
            DeletedCountText.Text = "0";
            
            ChangesListView.ItemsSource = null;
            ConflictsListView.ItemsSource = null;
            
            ConflictsPanel.Visibility = Visibility.Collapsed;
            ApplyButton.Content = "Apply Selected";

            _currentChanges.Clear();
            _currentConflicts.Clear();
            _currentProjectId = null;
            _currentTargetCommitId = null;
            _currentLocalCommitId = null;
            _currentModelId = null;
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            var items = ChangesListView.ItemsSource as List<ChangeItem>;
            if (items == null) return;
            foreach (var item in items)
                item.IsSelected = true;
            ChangesListView.Items.Refresh();
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            var items = ChangesListView.ItemsSource as List<ChangeItem>;
            if (items == null) return;
            foreach (var item in items)
                item.IsSelected = false;
            ChangesListView.Items.Refresh();
        }

        private void Highlight_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Highlight in View is not yet implemented.", "Info",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void ApplySelected_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // ============================================
                // SCENARIO 1: MERGE RESOLUTION REQUIRED
                // ============================================
                if (_currentConflicts.Any())
                {
                    IsEnabled = false; // Block UI during network calls
                    
                    var mergeReq = new MergeRequest
                    {
                        BaseCommit = null, 
                        SourceCommit = _currentTargetCommitId, 
                        TargetCommit = _currentLocalCommitId, 
                        Resolutions = _currentConflicts.Select(c => new Resolution
                        {
                            ElementId = c.ElementId,
                            ResolutionType = c.KeepLocal ? "keep_local" : "accept_remote"
                        }).ToList(),
                        Message = $"Merge {_currentTargetCommitId} into {_currentLocalCommitId} (Resolved via Dock)"
                    };

                    MergeResult mergeResult = await ApiClient.Instance.MergeAsync(_currentProjectId, mergeReq);

                    if (mergeResult == null || mergeResult.Status != "success" || string.IsNullOrEmpty(mergeResult.MergeCommitId))
                    {
                        MessageBox.Show("The backend failed to construct the final merge commit.", "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        IsEnabled = true;
                        return;
                    }

                    // Success! We pivot the target to the brand new merged commit.
                    _currentTargetCommitId = mergeResult.MergeCommitId;
                    
                    // Fetch the final, conflict-free change payload
                    PullResult freshPull = await ApiClient.Instance.PullChangesAsync(_currentProjectId, _currentLocalCommitId, _currentTargetCommitId);
                    
                    if (freshPull == null || freshPull.Conflicts?.Count > 0)
                    {
                        MessageBox.Show("Failed to fetch the finalized payload after merge.", "Merge Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        IsEnabled = true;
                        return;
                    }

                    // Feed it down to the normal external applier
                    _currentChanges = freshPull.Changes ?? new List<Change>();
                    
                    // We don't return here, we fall through to Scenario 2 so Revit applies it immediately!
                }
                
                // ============================================
                // SCENARIO 2: NORMAL APPLY (OR POST-MERGE APPLY)
                // ============================================
                
                // Since this runs in WPF, we can't touch Revit directly. We queue the external handler.
                var items = ChangesListView.ItemsSource as List<ChangeItem>; // Only grab active checkboxes if normal pull.
                
                List<Change> selectedChanges = _currentChanges; // Default all (for merge post-pulls)
                
                // If it was a normal diff/pull, respect the checkboxes. If it just merged, force apply all safe items.
                if (!_currentConflicts.Any() && items != null)
                {
                    var selectedElementIds = items.Where(i => i.IsSelected).Select(i => i.ElementId).ToHashSet();
                    selectedChanges = _currentChanges.Where(c => selectedElementIds.Contains(c.ElementId)).ToList();
                }

                if (selectedChanges.Count == 0)
                {
                    MessageBox.Show("No changes to apply.", "Apply Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    IsEnabled = true;
                    return;
                }

                if (_applyExternalEvent == null)
                {
                    MessageBox.Show("The docked apply action is not available.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    IsEnabled = true;
                    return;
                }

                _applyHandler.Queue(new DiffMergeApplyRequest
                {
                    ProjectId = _currentProjectId,
                    TargetCommitId = _currentTargetCommitId,
                    ModelId = _currentModelId,
                    Changes = selectedChanges
                });
                
                _applyExternalEvent.Raise();
                
                // We wipe the pane clean here so the user visually sees the merge is fully done
                Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to queue apply request: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsEnabled = true;
            }
        }

        private List<ChangeItem> BuildChangeItems(List<Change> changes)
        {
            var items = new List<ChangeItem>();
            if (changes == null || changes.Count == 0)
            {
                items.Add(new ChangeItem
                {
                    ChangeType = "-",
                    StatusColor = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                    ElementType = "No changes",
                    Description = "No changes available",
                    Details = "",
                    IsSelected = false
                });
                return items;
            }

            foreach (var change in changes)
            {
                items.Add(new ChangeItem
                {
                    ChangeType = GetShortChangeType(change.ChangeType),
                    StatusColor = GetStatusColor(change.ChangeType),
                    ElementType = $"{change.Category}: {change.Type}",
                    Description = BuildDescription(change),
                    Details = BuildDetails(change),
                    ElementId = change.ElementId,
                    IsSelected = true // Checked by default
                });
            }

            return items;
        }

        private static (int added, int modified, int deleted) Summarize(List<Change> changes)
        {
            if (changes == null) return (0, 0, 0);
            return (
                changes.Count(c => c.ChangeType == "added"),
                changes.Count(c => c.ChangeType == "modified"),
                changes.Count(c => c.ChangeType == "deleted")
            );
        }

        private static string GetShortChangeType(string changeType)
        {
            switch (changeType)
            {
                case "added": return "ADD";
                case "modified": return "MOD";
                case "deleted": return "DEL";
                default: return changeType?.ToUpperInvariant() ?? "-";
            }
        }

        private static SolidColorBrush GetStatusColor(string changeType)
        {
            switch (changeType)
            {
                case "added": 
                    return new SolidColorBrush(Color.FromRgb(40, 167, 69));
                case "modified": 
                    return new SolidColorBrush(Color.FromRgb(255, 193, 7));
                case "deleted": 
                    return new SolidColorBrush(Color.FromRgb(220, 53, 69));
                default: 
                    return new SolidColorBrush(Color.FromRgb(160, 160, 160));
            }
        }

        private static string BuildDescription(Change change)
        {
            switch (change.ChangeType)
            {
                case "added": return "Element added";
                case "modified": return "Element modified";
                case "deleted": return "Element deleted";
                default: return "Change detected";
            }
        }

        private static string BuildDetails(Change change)
        {
            var details = new List<string>();
            if (change.ParameterChanges != null && change.ParameterChanges.Count > 0)
            {
                var preview = change.ParameterChanges.Take(3)
                    .Select(p => $"{p.Name}: {p.OldValue} -> {p.NewValue}");
                details.Add(string.Join("; ", preview));
            }
            if (change.GeometryChanged) details.Add("Geometry changed");
            if (change.LocationChanged) details.Add("Location changed");
            return string.Join(" | ", details);
        }
        
        // --- ViewModels ---

        public class ChangeItem : INotifyPropertyChanged
        {
            private bool _isSelected;

            public string ChangeType { get; set; }
            public Brush StatusColor { get; set; }
            public string ElementType { get; set; }
            public string Description { get; set; }
            public string Details { get; set; }
            public string ElementId { get; set; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        public class ConflictItem : INotifyPropertyChanged
        {
            public string ElementId { get; set; }
            public string Description { get; set; }

            private bool _keepLocal = true; 
            public bool KeepLocal
            {
                get => _keepLocal;
                set
                {
                    if (_keepLocal != value)
                    {
                        _keepLocal = value;
                        _acceptRemote = !value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepLocal)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AcceptRemote)));
                    }
                }
            }

            private bool _acceptRemote;
            public bool AcceptRemote
            {
                get => _acceptRemote;
                set
                {
                    if (_acceptRemote != value)
                    {
                        _acceptRemote = value;
                        _keepLocal = !value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AcceptRemote)));
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KeepLocal)));
                    }
                }
            }

            public ConflictItem(Conflict c)
            {
                ElementId = c.ElementId;
                Description = c.Description ?? $"Conflict on Element {c.ElementId}";
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}

