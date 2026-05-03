using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using RevitVersionControl.Services;
using Autodesk.Revit.UI;

namespace RevitVersionControl.UI
{
    public partial class DiffMergePane : Page
    {
        private List<Change> _currentChanges = new List<Change>();
        private string _currentProjectId;
        private string _currentTargetCommitId;
        private string _currentModelId;
        private readonly DiffMergeApplyHandler _applyHandler;
        private readonly ExternalEvent _applyExternalEvent;
        
        private readonly DiffMergePreviewHandler _previewHandler;
        private readonly ExternalEvent _previewExternalEvent;
        
        private readonly DiffMergeFinalizeHandler _finalizeHandler;
        private readonly ExternalEvent _finalizeExternalEvent;

        private readonly DiffMergeZoomHandler _zoomHandler;
        private readonly ExternalEvent _zoomExternalEvent;

        private readonly DiffMergeUpdateHandler _updateHandler;
        private readonly ExternalEvent _updateExternalEvent;

        private int _previewIndex = 0;
        private bool _isPreviewing = false;
        private bool _isMergeMode = false;
        private List<Conflict> _currentConflicts = new List<Conflict>();

        public DiffMergePane()
        {
            _applyHandler = new DiffMergeApplyHandler();
            _applyExternalEvent = ExternalEvent.Create(_applyHandler);
            
            _previewHandler = new DiffMergePreviewHandler();
            _previewExternalEvent = ExternalEvent.Create(_previewHandler);
            
            _finalizeHandler = new DiffMergeFinalizeHandler();
            _finalizeExternalEvent = ExternalEvent.Create(_finalizeHandler);
            
            _zoomHandler = new DiffMergeZoomHandler();
            _zoomExternalEvent = ExternalEvent.Create(_zoomHandler);

            _updateHandler = new DiffMergeUpdateHandler();
            _updateExternalEvent = ExternalEvent.Create(_updateHandler);
            
            InitializeComponent();
        }

        /// <summary>Show/hide the Start Visual Merge button based on merge mode.</summary>
        private void UpdateMergeButtonVisibility()
        {
            if (_isMergeMode && _currentChanges.Count > 0)
            {
                StartMergeButton.IsEnabled = true;
                StartMergeButton.Visibility = Visibility.Visible;
            }
            else
            {
                StartMergeButton.IsEnabled = false;
                StartMergeButton.Visibility = Visibility.Collapsed;
            }
        }

        public void LoadDiffResult(DiffResult diffResult)
        {
            if (diffResult == null) { Clear(); return; }

            _currentProjectId = null;
            _isMergeMode = false;
            _currentConflicts.Clear();
            BaseCommitText.Text = diffResult.BaseVersion ?? "-";
            TargetCommitText.Text = diffResult.TargetVersion ?? "-";

            int added = diffResult.Summary != null && diffResult.Summary.ContainsKey("added") ? diffResult.Summary["added"] : 0;
            int modified = diffResult.Summary != null && diffResult.Summary.ContainsKey("modified") ? diffResult.Summary["modified"] : 0;
            int deleted = diffResult.Summary != null && diffResult.Summary.ContainsKey("deleted") ? diffResult.Summary["deleted"] : 0;

            AddedCountText.Text = added.ToString();
            ModifiedCountText.Text = modified.ToString();
            DeletedCountText.Text = deleted.ToString();
            ConflictCountText.Text = "0";
            ConflictBanner.Visibility = Visibility.Collapsed;

            _currentChanges = diffResult.Changes ?? new List<Change>();
            ChangesListView.ItemsSource = BuildChangeItems(_currentChanges, _currentConflicts);
            UpdateMergeButtonVisibility();
        }

        public void LoadPullResult(PullResult pullResult, string projectId = null, string currentCommitId = null, string targetCommitId = null, string modelId = null)
        {
            if (pullResult == null) { Clear(); return; }

            _currentProjectId = projectId;
            _currentTargetCommitId = targetCommitId;
            _currentModelId = modelId;
            _isMergeMode = !string.IsNullOrWhiteSpace(projectId);
            _currentConflicts = new List<Conflict>();
            PreviewStateService.Is3WayMerge = false;
            BaseCommitText.Text = currentCommitId ?? "-";
            TargetCommitText.Text = targetCommitId ?? "-";

            var summary = Summarize(pullResult.Changes);
            AddedCountText.Text = summary.added.ToString();
            ModifiedCountText.Text = summary.modified.ToString();
            DeletedCountText.Text = summary.deleted.ToString();
            ConflictCountText.Text = "0";
            ConflictBanner.Visibility = Visibility.Collapsed;

            _currentChanges = new List<Change>(pullResult.Changes ?? new List<Change>());
            ChangesListView.ItemsSource = BuildChangeItems(_currentChanges, _currentConflicts);
            UpdateMergeButtonVisibility();
        }

        /// <summary>
        /// Load a 3-way merge result — this is the primary merge flow.
        /// </summary>
        public void LoadMerge3WayResult(Merge3WayResult result, string projectId, string sourceCommitId, string targetCommitId, string modelId)
        {
            if (result == null) { Clear(); return; }

            _currentProjectId = projectId;
            _currentTargetCommitId = targetCommitId;
            _currentModelId = modelId;
            _isMergeMode = true;

            string shortSrc = !string.IsNullOrEmpty(sourceCommitId) && sourceCommitId.Length > 8 ? sourceCommitId.Substring(0, 8) : sourceCommitId ?? "-";
            string shortTgt = !string.IsNullOrEmpty(targetCommitId) && targetCommitId.Length > 8 ? targetCommitId.Substring(0, 8) : targetCommitId ?? "-";
            BaseCommitText.Text = shortSrc;
            TargetCommitText.Text = shortTgt;

            // Store 3-way merge data in PreviewStateService for the preview handler
            PreviewStateService.Is3WayMerge = true;
            // IMPORTANT: Copy lists to avoid shared-reference mutation when PreviewStateService.Clear() runs
            PreviewStateService.SourceChanges = new List<Change>(result.SourceChanges ?? new List<Change>());
            PreviewStateService.TargetChanges = new List<Change>(result.TargetChanges ?? new List<Change>());
            PreviewStateService.ActiveConflicts = new List<Conflict>(result.Conflicts ?? new List<Conflict>());

            _currentConflicts = new List<Conflict>(result.Conflicts ?? new List<Conflict>());
            _currentChanges = new List<Change>(result.TargetChanges ?? new List<Change>());

            var summary = Summarize(_currentChanges);
            AddedCountText.Text = summary.added.ToString();
            ModifiedCountText.Text = summary.modified.ToString();
            DeletedCountText.Text = summary.deleted.ToString();
            ConflictCountText.Text = _currentConflicts.Count.ToString();

            if (_currentConflicts.Count > 0)
            {
                ConflictBanner.Visibility = Visibility.Visible;
                ConflictBannerText.Text = $"{_currentConflicts.Count} conflict(s) found. Open Visual Merge to resolve.";
            }
            else
            {
                ConflictBanner.Visibility = Visibility.Collapsed;
            }

            ChangesListView.ItemsSource = BuildChangeItems(_currentChanges, _currentConflicts);
            UpdateMergeButtonVisibility();
        }

        public void Clear()
        {
            BaseCommitText.Text = "-";
            TargetCommitText.Text = "-";
            AddedCountText.Text = "0";
            ModifiedCountText.Text = "0";
            DeletedCountText.Text = "0";
            ConflictCountText.Text = "0";
            ConflictBanner.Visibility = Visibility.Collapsed;
            ChangesListView.ItemsSource = null;
            _currentChanges = new List<Change>();
            _currentConflicts = new List<Conflict>();
            _currentProjectId = null;
            _currentTargetCommitId = null;
            _currentModelId = null;
            _isMergeMode = false;
            
            SetPreviewMode(false);
            UpdateMergeButtonVisibility();
        }

        private void SetPreviewMode(bool isPreviewing)
        {
            _isPreviewing = isPreviewing;
            ListModeGrid.Visibility = isPreviewing ? Visibility.Collapsed : Visibility.Visible;
            ListActionPanel.Visibility = isPreviewing ? Visibility.Collapsed : Visibility.Visible;
            
            PreviewModeGrid.Visibility = isPreviewing ? Visibility.Visible : Visibility.Collapsed;
            PreviewNavPanel.Visibility = isPreviewing ? Visibility.Visible : Visibility.Collapsed;
            PreviewActionPanel.Visibility = isPreviewing ? Visibility.Visible : Visibility.Collapsed;
            
            if (isPreviewing)
            {
                _previewIndex = 0;
                UpdatePreviewCard();
            }
        }

        private void StartVisualMerge_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChanges == null || _currentChanges.Count == 0)
            {
                MessageBox.Show("No changes to preview.", "Visual Merge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_isMergeMode)
            {
                MessageBox.Show("Visual merge is only available when merging branches.", "Visual Merge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _previewHandler.Queue(new DiffMergeApplyRequest
                {
                    ProjectId = _currentProjectId,
                    TargetCommitId = _currentTargetCommitId,
                    ModelId = _currentModelId,
                    Changes = _currentChanges
                });
                _previewExternalEvent.Raise();
                SetPreviewMode(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start visual merge: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CancelPreview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _finalizeHandler.Queue(new DiffMergeFinalizeRequest { IsCancelled = true });
                _finalizeExternalEvent.Raise();
                SetPreviewMode(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to cancel preview: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void FinalizeMerge_Click(object sender, RoutedEventArgs e)
        {
            // Default unresolved conflicts to "keep_local"
            if (_currentConflicts != null)
            {
                foreach (var conflict in _currentConflicts)
                {
                    if (!PreviewStateService.ConflictResolutions.ContainsKey(conflict.ElementId))
                        PreviewStateService.ConflictResolutions[conflict.ElementId] = "keep_local";
                }
            }

            try
            {
                var items = ChangesListView.ItemsSource as List<ChangeItem>;
                var acceptedKeys = new List<string>();
                
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        if (item.IsSelected)
                        {
                            var change = _currentChanges.FirstOrDefault(c => c.ElementId == item.ElementId);
                            if (change != null)
                            {
                                string key = PreviewStateService.GetChangeTrackingKey(change);
                                if (!string.IsNullOrEmpty(key)) acceptedKeys.Add(key);
                            }
                        }
                    }
                }

                _finalizeHandler.Queue(new DiffMergeFinalizeRequest
                {
                    IsCancelled = false,
                    OriginalRequest = new DiffMergeApplyRequest
                    {
                        ProjectId = _currentProjectId,
                        TargetCommitId = _currentTargetCommitId,
                        ModelId = _currentModelId,
                        Changes = new List<Change>(_currentChanges)  // Copy to avoid reference mutation
                    },
                    AcceptedChangeKeys = acceptedKeys
                });
                _finalizeExternalEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to finalize merge: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PrevChange_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChanges == null || _currentChanges.Count == 0) return;
            _previewIndex = (_previewIndex - 1 + _currentChanges.Count) % _currentChanges.Count;
            UpdatePreviewCard();
            SendVisualUpdate(zoomTo: true);
        }

        private void NextChange_Click(object sender, RoutedEventArgs e)
        {
            if (_currentChanges == null || _currentChanges.Count == 0) return;
            _previewIndex = (_previewIndex + 1) % _currentChanges.Count;
            UpdatePreviewCard();
            SendVisualUpdate(zoomTo: true);
        }

        private void ZoomElement_Click(object sender, RoutedEventArgs e)
        {
            if (_isPreviewing)
            {
                SendVisualUpdate(zoomTo: true);
            }
            else
            {
                ZoomToSelectedListElement();
            }
        }

        private void ZoomToCurrentPreviewElement()
        {
            var items = ChangesListView.ItemsSource as List<ChangeItem>;
            if (items == null || _previewIndex < 0 || _previewIndex >= items.Count) return;

            var item = items[_previewIndex];
            ZoomToChangeItem(item);
        }

        private void ZoomToSelectedListElement()
        {
            var item = ChangesListView.SelectedItem as ChangeItem;
            if (item == null)
            {
                MessageBox.Show("Please select a change to zoom to.", "Zoom", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ZoomToChangeItem(item);
        }

        private void ZoomToChangeItem(ChangeItem item)
        {
            if (item == null) return;

            var change = _currentChanges?.FirstOrDefault(c => c.ElementId == item.ElementId);
            if (change == null) return;

            string key = PreviewStateService.GetChangeTrackingKey(change);
            Autodesk.Revit.DB.ElementId targetId = Autodesk.Revit.DB.ElementId.InvalidElementId;

            // Check temp added elements first (newly created during preview)
            if (key != null && PreviewStateService.TempAddedElements.TryGetValue(key, out Autodesk.Revit.DB.ElementId tempId))
            {
                targetId = tempId;
            }
            // Check ghost elements for deleted changes
            else if (key != null && PreviewStateService.TempGhostElements.TryGetValue(key, out Autodesk.Revit.DB.ElementId ghostId))
            {
                targetId = ghostId;
            }

            if (targetId != Autodesk.Revit.DB.ElementId.InvalidElementId)
            {
                // We have a direct ElementId — use it
                _zoomHandler.ZoomTo(targetId);
                _zoomExternalEvent.Raise();
            }
            else
            {
                // For modified/existing elements: pass RepoGuid + ElementId (UniqueId)
                // to the zoom handler so it can resolve on the Revit thread
                _zoomHandler.ZoomTo(change.RepoGuid, change.ElementId);
                _zoomExternalEvent.Raise();
            }
        }

        private void PreviewAcceptCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            var items = ChangesListView.ItemsSource as List<ChangeItem>;
            if (items != null && _previewIndex >= 0 && _previewIndex < items.Count)
            {
                items[_previewIndex].IsSelected = PreviewAcceptCheckbox.IsChecked == true;
            }
            // Update visuals in real-time
            SendVisualUpdate(zoomTo: false);
        }

        private void ConflictResolution_Changed(object sender, RoutedEventArgs e)
        {
            var items = ChangesListView.ItemsSource as List<ChangeItem>;
            if (items == null || _previewIndex < 0 || _previewIndex >= items.Count) return;

            var item = items[_previewIndex];
            if (!item.IsConflict) return;

            string resolution = "keep_local";
            if (ConflictKeepTheirs.IsChecked == true) resolution = "accept_remote";
            else if (ConflictKeepBoth.IsChecked == true) resolution = "keep_both";

            PreviewStateService.ConflictResolutions[item.ConflictId ?? item.ElementId] = resolution;
            // Update visuals in real-time
            SendVisualUpdate(zoomTo: false);
        }

        /// <summary>
        /// Sends the current UI state to the DiffMergeUpdateHandler so it can
        /// refresh all graphic overrides on the Revit thread in real-time.
        /// </summary>
        private void SendVisualUpdate(bool zoomTo)
        {
            if (!_isPreviewing || _currentChanges == null || _currentChanges.Count == 0) return;

            var items = ChangesListView.ItemsSource as List<ChangeItem>;
            if (items == null) return;

            // Build included states list (parallel to _currentChanges)
            var includedStates = new List<bool>();
            for (int i = 0; i < _currentChanges.Count; i++)
            {
                if (i < items.Count)
                    includedStates.Add(items[i].IsSelected);
                else
                    includedStates.Add(true);
            }

            // Build conflict resolutions copy
            var conflictRes = new Dictionary<string, string>(PreviewStateService.ConflictResolutions);

            Change currentChange = null;
            if (_previewIndex >= 0 && _previewIndex < _currentChanges.Count)
                currentChange = _currentChanges[_previewIndex];

            _updateHandler.Queue(new DiffMergeUpdateRequest
            {
                AllChanges = new List<Change>(_currentChanges),
                CurrentIndex = _previewIndex,
                CurrentChange = currentChange,
                IncludedStates = includedStates,
                ConflictResolutions = conflictRes,
                ZoomToElement = zoomTo,
            });
            _updateExternalEvent.Raise();
        }

        private void UpdatePreviewCard()
        {
            var items = ChangesListView.ItemsSource as List<ChangeItem>;
            if (items == null || _previewIndex < 0 || _previewIndex >= items.Count) return;

            var item = items[_previewIndex];
            
            PreviewChangeCounter.Text = $"Reviewing Change {_previewIndex + 1} of {items.Count}";
            PreviewStatusText.Text = item.ChangeType;
            PreviewStatusBadge.Background = item.StatusColor;
            PreviewElementType.Text = item.ElementType;
            PreviewElementDetails.Text = item.Description + "\n\n" + item.Details;
            
            // Unsubscribe to avoid triggering event
            PreviewAcceptCheckbox.Checked -= PreviewAcceptCheckbox_Changed;
            PreviewAcceptCheckbox.Unchecked -= PreviewAcceptCheckbox_Changed;
            PreviewAcceptCheckbox.IsChecked = item.IsSelected;
            PreviewAcceptCheckbox.Checked += PreviewAcceptCheckbox_Changed;
            PreviewAcceptCheckbox.Unchecked += PreviewAcceptCheckbox_Changed;

            // Show/hide conflict resolution panel
            if (item.IsConflict)
            {
                PreviewConflictPanel.Visibility = Visibility.Visible;
                PreviewConflictDescription.Text = item.ConflictDescription ?? "This element was modified on both branches.";

                // Show "Keep Both" only for spatial collisions
                ConflictKeepBoth.Visibility = item.ConflictType == "spatial_collision" 
                    ? Visibility.Visible : Visibility.Collapsed;

                // Restore saved resolution
                string savedRes = null;
                PreviewStateService.ConflictResolutions.TryGetValue(item.ConflictId ?? item.ElementId, out savedRes);

                ConflictKeepOurs.IsChecked = savedRes != "accept_remote" && savedRes != "keep_both";
                ConflictKeepTheirs.IsChecked = savedRes == "accept_remote";
                ConflictKeepBoth.IsChecked = savedRes == "keep_both";
            }
            else
            {
                PreviewConflictPanel.Visibility = Visibility.Collapsed;
            }
        }

        private List<ChangeItem> BuildChangeItems(List<Change> changes, List<Conflict> conflicts)
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

            // Build conflict lookup by both repoGuid and elementId
            var conflictMap = new Dictionary<string, Conflict>();
            if (conflicts != null)
            {
                foreach (var c in conflicts)
                {
                    if (c.ConflictType == "spatial_collision" && c.ElementId != null && c.ElementId.Contains("|"))
                    {
                        foreach (var part in c.ElementId.Split('|'))
                        {
                            if (!conflictMap.ContainsKey(part))
                                conflictMap[part] = c;
                        }
                    }
                    else if (c.ElementId != null)
                    {
                        conflictMap[c.ElementId] = c;
                    }
                }
            }

            foreach (var change in changes)
            {
                string identity = change.RepoGuid ?? change.ElementId;
                Conflict conflict = null;
                if (!conflictMap.TryGetValue(identity, out conflict))
                    conflictMap.TryGetValue(change.ElementId ?? "", out conflict);

                var item = new ChangeItem
                {
                    ChangeType = conflict != null ? "CNFL" : GetShortChangeType(change.ChangeType),
                    StatusColor = conflict != null 
                        ? new SolidColorBrush(Color.FromRgb(255, 0, 255))  // Magenta
                        : GetStatusColor(change.ChangeType),
                    ElementType = $"{change.Category}: {change.Type}",
                    Description = conflict != null ? conflict.Description : BuildDescription(change),
                    Details = BuildDetails(change),
                    ElementId = change.ElementId,
                    IsSelected = true,
                    IsConflict = conflict != null,
                    ConflictType = conflict?.ConflictType,
                    ConflictId = conflict?.ElementId,
                    ConflictDescription = conflict?.Description
                };

                items.Add(item);
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

        public class ChangeItem : INotifyPropertyChanged
        {
            private bool _isSelected;

            public string ChangeType { get; set; }
            public Brush StatusColor { get; set; }
            public string ElementType { get; set; }
            public string Description { get; set; }
            public string Details { get; set; }
            public string ElementId { get; set; }

            // Conflict fields
            public bool IsConflict { get; set; }
            public string ConflictType { get; set; }
            public string ConflictId { get; set; }
            public string ConflictDescription { get; set; }

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
    }
}
