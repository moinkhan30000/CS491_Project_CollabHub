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
    public enum DiffMergeMode
    {
        Resolution,
        ViewOnly,
        HistoricalViewer
    }

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
        
        public DiffMergeMode CurrentMode { get; private set; } = DiffMergeMode.Resolution;

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
            if (CurrentMode == DiffMergeMode.ViewOnly)
            {
                StartMergeButton.IsEnabled = false;
                StartMergeButton.Visibility = Visibility.Collapsed;
                return;
            }

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
            PreviewStateService.Is3WayMerge = false;
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
            
            // Set mode to Resolution so user can select/apply changes
            SetMode(DiffMergeMode.Resolution);
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

        public void LoadHistoricalMergeResult(HistoricalMergeResult result, string projectId, string commitId)
        {
            if (result == null) { Clear(); return; }

            CurrentMode = DiffMergeMode.HistoricalViewer;
            _currentProjectId = projectId;
            _currentTargetCommitId = commitId;
            _isMergeMode = false;
            PreviewStateService.Is3WayMerge = true;
            BaseCommitText.Text = result.ParentCommitId ?? "-";
            TargetCommitText.Text = result.ParentCommitId2 ?? "-";

            _currentConflicts = new List<Conflict>(result.Conflicts ?? new List<Conflict>());
            _currentChanges = new List<Change>(); // Historically we only care about conflicts for the viewer
            
            ConflictCountText.Text = _currentConflicts.Count.ToString();
            AddedCountText.Text = "-";
            ModifiedCountText.Text = "-";
            DeletedCountText.Text = "-";

            if (_currentConflicts.Count > 0)
            {
                ConflictBanner.Visibility = Visibility.Visible;
                ConflictBannerText.Text = $"Historical view: {_currentConflicts.Count} conflict(s) resolved in this merge.";
            }
            else
            {
                ConflictBanner.Visibility = Visibility.Collapsed;
            }

            var items = BuildChangeItems(_currentChanges, _currentConflicts);
            
            // Auto-select radio buttons based on resolutions
            if (result.Resolutions != null)
            {
                foreach (var item in items)
                {
                    if (item.IsConflict)
                    {
                        var resolution = result.Resolutions.FirstOrDefault(r => r.ElementId == item.ConflictId);
                        if (resolution != null)
                        {
                            item.Resolution = resolution.ResolutionType; // "keep_local" or "accept_remote"
                        }
                    }
                }
            }

            ChangesListView.ItemsSource = items;
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
            if (result.AutoMergedChanges != null)
            {
                _currentChanges.AddRange(result.AutoMergedChanges);
            }

            // P1 fix: source-only changes (not in conflicts, auto-merge, or target) must be applied too
            _currentChanges.AddRange(GetSourceOnlyChanges(result));

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
            CurrentMode = DiffMergeMode.Resolution;
            
            SetPreviewMode(false);
            UpdateMergeButtonVisibility();
        }

        private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChangesListView.ItemsSource == null) return;
            
            var selectedItem = FilterComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null) return;
            
            string filter = selectedItem.Content.ToString();
            ApplyFilter(filter);
        }

        private void ApplyFilter(string filter)
        {
            if (_currentChanges == null) return;
            
            var filteredItems = BuildChangeItems(_currentChanges, _currentConflicts);
            
            switch (filter)
            {
                case "Conflicts Only":
                    filteredItems = filteredItems.Where(item => item.IsConflict).ToList();
                    break;
                case "Spatial Collisions":
                    filteredItems = filteredItems.Where(item => 
                        _currentConflicts.Any(c => 
                            (c.ElementId == item.ElementId || c.ElementId == item.RepoGuid) && 
                            c.ConflictType == "spatial_collision")).ToList();
                    break;
                case "Parameter Conflicts":
                    filteredItems = filteredItems.Where(item => 
                        _currentConflicts.Any(c => 
                            (c.ElementId == item.ElementId || c.ElementId == item.RepoGuid) && 
                            c.ConflictType == "parameter_conflict")).ToList();
                    break;
                case "Unresolved Only":
                    filteredItems = filteredItems.Where(item => 
                        _currentConflicts.Any(c => 
                            (c.ElementId == item.ElementId || c.ElementId == item.RepoGuid) && 
                            !PreviewStateService.ConflictResolutions.ContainsKey(c.ElementId ?? c.ElementId))).ToList();
                    break;
                case "All Changes":
                default:
                    // No filtering
                    break;
            }
            
            ChangesListView.ItemsSource = filteredItems;
        }

        private void BatchActionsMenu_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button?.ContextMenu != null)
            {
                button.ContextMenu.IsOpen = true;
            }
        }

        private void BatchResolveSpatialKeepBoth_Click(object sender, RoutedEventArgs e)
        {
            foreach (var conflict in _currentConflicts.Where(c => c.ConflictType == "spatial_collision"))
            {
                PreviewStateService.ConflictResolutions[conflict.ElementId] = "keep_both";
            }
            RefreshChangeItems();
            MessageBox.Show("All spatial collisions resolved to 'Keep Both'.", "Batch Resolution", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BatchAcceptRemote_Click(object sender, RoutedEventArgs e)
        {
            foreach (var conflict in _currentConflicts)
            {
                PreviewStateService.ConflictResolutions[conflict.ElementId] = "accept_remote";
            }
            RefreshChangeItems();
            MessageBox.Show("All conflicts resolved to 'Accept Remote'.", "Batch Resolution", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BatchRejectDeletions_Click(object sender, RoutedEventArgs e)
        {
            var deletionConflicts = _currentConflicts.Where(c => 
                c.ConflictType == "delete_modified" && 
                ((c.LocalChange?.ContainsKey("ChangeType") == true && c.LocalChange["ChangeType"]?.ToString() == "deleted") || 
                 (c.RemoteChange?.ContainsKey("ChangeType") == true && c.RemoteChange["ChangeType"]?.ToString() == "deleted"))).ToList();
            
            foreach (var conflict in deletionConflicts)
            {
                PreviewStateService.ConflictResolutions[conflict.ElementId] = "keep_local";
            }
            RefreshChangeItems();
            MessageBox.Show("All deletion conflicts resolved to 'Keep Local'.", "Batch Resolution", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BatchSelectConflicts_Click(object sender, RoutedEventArgs e)
        {
            var changeItems = ChangesListView.ItemsSource as List<ChangeItem>;
            if (changeItems == null) return;
            
            foreach (var item in changeItems.Where(item => item.IsConflict))
            {
                item.IsSelected = true;
            }
            ChangesListView.Items.Refresh();
        }

        private void RefreshChangeItems()
        {
            ChangesListView.ItemsSource = BuildChangeItems(_currentChanges, _currentConflicts);
            ApplyFilter((FilterComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "All Changes");
        }

        private void HighlightCollision_Click(object sender, RoutedEventArgs e)
        {
            if (_previewIndex < 0 || _previewIndex >= _currentChanges.Count) return;
            
            var currentChange = _currentChanges[_previewIndex];
            var conflict = _currentConflicts.FirstOrDefault(c => 
                c.ElementId == currentChange.ElementId || c.ElementId == currentChange.RepoGuid);
            
            if (conflict != null && conflict.ConflictType == "spatial_collision")
            {
                try
                {
                    _updateHandler.Queue(new DiffMergeUpdateRequest
                    {
                        AllChanges = _currentChanges,
                        CurrentIndex = _previewIndex,
                        IncludedStates = GetIncludedStates(),
                        ConflictResolutions = PreviewStateService.ConflictResolutions,
                        HighlightCollision = true,
                        ZoomToElement = true,
                        CurrentChange = _currentChanges[_previewIndex]
                    });
                    _updateExternalEvent.Raise();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to highlight collision: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("No spatial collision to highlight for this change.", "Highlight Collision", MessageBoxButton.OK, MessageBoxImage.Information);
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
                            bool shouldAccept = true;
                            if (item.IsConflict)
                            {
                                string conflictId = item.ConflictId ?? item.ElementId;
                                if (PreviewStateService.ConflictResolutions.TryGetValue(conflictId, out string res))
                                {
                                    if (res == "keep_local")
                                    {
                                        shouldAccept = false;
                                    }
                                }
                            }

                            if (shouldAccept)
                            {
                                if (!string.IsNullOrEmpty(item.TrackingKey))
                                    acceptedKeys.Add(item.TrackingKey);
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

        public void AutoFinalizeCleanMerge(Merge3WayResult result, string projectId, string targetCommitId, string modelId)
        {
            try
            {
                var acceptedKeys = new List<string>();
                var changesToApply = new List<Change>(result.TargetChanges ?? new List<Change>());
                if (result.AutoMergedChanges != null) changesToApply.AddRange(result.AutoMergedChanges);

                // P1 fix: include source-only changes in auto-finalize path too
                changesToApply.AddRange(GetSourceOnlyChanges(result));

                if (changesToApply.Count > 0)
                {
                    foreach (var change in changesToApply)
                    {
                        string key = PreviewStateService.GetChangeTrackingKey(change);
                        if (!string.IsNullOrEmpty(key)) acceptedKeys.Add(key);
                    }
                }

                _finalizeHandler.Queue(new DiffMergeFinalizeRequest
                {
                    IsCancelled = false,
                    OriginalRequest = new DiffMergeApplyRequest
                    {
                        ProjectId = projectId,
                        TargetCommitId = targetCommitId,
                        ModelId = modelId,
                        Changes = changesToApply
                    },
                    AcceptedChangeKeys = acceptedKeys
                });
                _finalizeExternalEvent.Raise();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to auto-finalize merge: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if item == null)
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
                if i < items.Count)
                    includedStates.Add(items[i].IsSelected);
                else
                    includedStates.Add(true);
            }

            // Build conflict resolutions copy
            var conflictRes = new Dictionary<string, string>(PreviewStateService.ConflictResolutions);

            // Send update request
            _updateHandler.Queue(new DiffMergeUpdateRequest
            {
                AllChanges = _currentChanges,
                IncludedStates = includedStates,
                ConflictResolutions = conflictRes,
                ZoomToElement = zoomTo,
                CurrentIndex = _previewIndex
            });
            _updateExternalEvent.Raise();
        }

        private void SetMode(DiffMergeMode mode)
        {
            CurrentMode = mode;
            switch (mode)
            {
                case DiffMergeMode.Resolution:
                    VisualDiffPanel.Visibility = Visibility.Visible;
                    HistoryPanel.Visibility = Visibility.Collapsed;
                    ConflictPanel.Visibility = Visibility.Collapsed;
                    break;
                case DiffMergeMode.ViewOnly:
                    VisualDiffPanel.Visibility = Visibility.Visible;
                    HistoryPanel.Visibility = Visibility.Collapsed;
                    ConflictPanel.Visibility = Visibility.Collapsed;
                    break;
                case DiffMergeMode.HistoricalViewer:
                    VisualDiffPanel.Visibility = Visibility.Collapsed;
                    HistoryPanel.Visibility = Visibility.Visible;
                    ConflictPanel.Visibility = Visibility.Visible;
                    break;
            }

            UpdateMergeButtonVisibility();
        }

        private void SetPreviewMode(bool isPreview)
        {
            _isPreviewing = isPreview;
            if (isPreview)
            {
                // When entering preview mode, ensure all changes are considered included
                var allIncluded = Enumerable.Repeat(true, _currentChanges.Count).ToList();
                var allConflicts = new Dictionary<string, string>(PreviewStateService.ConflictResolutions);
                
                _updateHandler.Queue(new DiffMergeUpdateRequest
                {
                    AllChanges = _currentChanges,
                    IncludedStates = allIncluded,
                    ConflictResolutions = allConflicts,
                    ZoomToElement = false,
                    CurrentIndex = -1
                });
            }
            else
            {
                // Revert to normal mode: clear any temporary additions/deletions
                PreviewStateService.Clear();
                _updateHandler.Queue(new DiffMergeUpdateRequest
                {
                    AllChanges = _currentChanges,
                    IncludedStates = GetIncludedStates(),
                    ConflictResolutions = PreviewStateService.ConflictResolutions,
                    ZoomToElement = false,
                    CurrentIndex = -1
                });
            }
            _updateExternalEvent.Raise();
        }

        private List<bool> GetIncludedStates()
        {
            return _currentChanges.Select(c => true).ToList();
        }

        private (int added, int modified, int deleted) Summarize(List<Change> changes)
        {
            int added = 0, modified = 0, deleted = 0;
            if (changes != null)
            {
                foreach (var change in changes)
                {
                    if (change.ChangeType == "added") added++;
                    else if (change.ChangeType == "modified") modified++;
                    else if (change.ChangeType == "deleted") deleted++;
                }
            }
            return (added, modified, deleted);
        }

        private List<Change> GetSourceOnlyChanges(Merge3WayResult result)
        {
            var sourceOnly = new List<Change>();
            var conflictElementIds = new HashSet<string>(_currentConflicts.Select(c => c.ElementId));
            var targetChangeElementIds = new HashSet<string>(_currentChanges.Select(c => c.ElementId));
            var autoMergedChangeElementIds = new HashSet<string>(result.AutoMergedChanges?.Select(c => c.ElementId) ?? Enumerable.Empty<string>());

            // Include changes that are in source but not in conflicts/target
            foreach (var change in result.SourceChanges ?? Enumerable.Empty<Change>())
            {
                if (!conflictElementIds.Contains(change.ElementId) && !targetChangeElementIds.Contains(change.ElementId))
                {
                    sourceOnly.Add(change);
                }
            }

            return sourceOnly;
        }

        private static string BuildConflictDiffText(Change sourceChange, Change targetChange, string conflictType)
        {
            var sb = new System.Text.StringBuilder();

            // Spatial collision: two distinct elements overlapping — show identity of each
            if (conflictType == "spatial_collision")
            {
                sb.AppendLine("SPATIAL COLLISION: Two elements occupy overlapping space.");
                sb.AppendLine();
                if (sourceChange != null)
                {
                    sb.AppendLine($"Ours:   {sourceChange.Category}: {sourceChange.Type}");
                    if (sourceChange.NewData != null && sourceChange.NewData.TryGetValue("location", out object srcLoc) && srcLoc != null)
                        sb.AppendLine($"        Location: {FormatLocationData(srcLoc)}");
                }
                if (targetChange != null)
                {
                    sb.AppendLine($"Theirs: {targetChange.Category}: {targetChange.Type}");
                    if (targetChange.NewData != null && targetChange.NewData.TryGetValue("location", out object tgtLoc) && tgtLoc != null)
                        sb.AppendLine($"        Location: {FormatLocationData(tgtLoc)}");
                }
                return sb.ToString().Trim();
            }

            // delete_modified: one side deleted, the other modified
            if (conflictType == "delete_modified")
            {
                bool oursDeleted = sourceChange?.ChangeType == "deleted";
                sb.AppendLine(oursDeleted
                    ? "Ours: DELETED  |  Theirs: modified"
                    : "Ours: modified  |  Theirs: DELETED");
                sb.AppendLine();
            }

            // Parameter diff table
            var sourceParams = new Dictionary<string, ParameterChange>(StringComparer.OrdinalIgnoreCase);
            var targetParams = new Dictionary<string, ParameterChange>(StringComparer.OrdinalIgnoreCase);

            if (sourceChange?.ParameterChanges != null)
                foreach (var pc in sourceChange.ParameterChanges)
                    sourceParams[pc.Name] = pc;

            if (targetChange?.ParameterChanges != null)
                foreach (var pc in targetChange.ParameterChanges)
                    targetParams[pc.Name] = pc;

            var allParamNames = new HashSet<string>(sourceParams.Keys, StringComparer.OrdinalIgnoreCase);
            allParamNames.UnionWith(targetParams.Keys);

            bool hasParamDiffs = false;
            var paramSection = new System.Text.StringBuilder();
            foreach (string paramName in allParamNames.OrderBy(n => n))
            {
                bool inSource = sourceParams.TryGetValue(paramName, out ParameterChange srcPc);
                bool inTarget = targetParams.TryGetValue(paramName, out ParameterChange tgtPc);

                string oursNew   = inSource ? (srcPc.NewValue?.ToString() ?? "(null)") : "(unchanged)";
                string theirsNew = inTarget ? (tgtPc.NewValue?.ToString() ?? "(null)") : "(unchanged)";

                if (string.Equals(oursNew, theirsNew, StringComparison.OrdinalIgnoreCase)) continue;

                hasParamDiffs = true;
                paramSection.AppendLine($"  {paramName}:");
                paramSection.AppendLine($"    Ours:   {oursNew}");
                paramSection.AppendLine($"    Theirs: {theirsNew}");
            }

            if (hasParamDiffs)
            {
                sb.AppendLine("Conflicting Parameters:");
                sb.Append(paramSection);
            }

            // Location
            bool srcLocChanged = sourceChange?.LocationChanged == true;
            bool tgtLocChanged = targetChange?.LocationChanged == true;
            if (srcLocChanged || tgtLocChanged)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("Location:");
                if (srcLocChanged && !tgtLocChanged)
                    sb.AppendLine("  Ours: moved  |  Theirs: unchanged");
                else if (!srcLocChanged && tgtLocChanged)
                    sb.AppendLine("  Ours: unchanged  |  Theirs: moved");
                else
                    sb.AppendLine("  Both branches moved this element");
            }

            // Geometry
            bool srcGeomChanged = sourceChange?.GeometryChanged == true;
            bool tgtGeomChanged = targetChange?.GeometryChanged == true;
            if (srcGeomChanged || tgtGeomChanged)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine("Geometry:");
                if (srcGeomChanged && !tgtGeomChanged)
                    sb.AppendLine("  Ours: changed  |  Theirs: unchanged");
                else if (!srcGeomChanged && tgtGeomChanged)
                    sb.AppendLine("  Ours: unchanged  |  Theirs: changed");
                else
                    sb.AppendLine("  Both branches changed geometry");
            }

            string result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "No differing parameters detected." : result;
        }

        private static string FormatLocationData(object locationObj)
        {
            try
            {
                var locJson = Newtonsoft.Json.Linq.JObject.FromObject(locationObj);
                string locType = locJson["type"]?.ToString();
                if (locType == "point")
                {
                    double x = locJson["point"]?["x"]?.Value<double>() ?? 0;
                    double y = locJson["point"]?["y"]?.Value<double>() ?? 0;
                    double z = locJson["point"]?["z"]?.Value<double>() ?? 0;
                    return $"({x:F2}, {y:F2}, {z:F2})";
                }
                if (locType == "curve")
                {
                    double sx = locJson["startPoint"]?["x"]?.Value<double>() ?? 0;
                    double sy = locJson["startPoint"]?["y"]?.Value<double>() ?? 0;
                    double ex2 = locJson["endPoint"]?["x"]?.Value<double>() ?? 0;
                    double ey2 = locJson["endPoint"]?["y"]?.Value<double>() ?? 0;
                    return $"({sx:F2},{sy:F2}) to ({ex2:F2},{ey2:F2})";
                }
            }
            catch { }
            return "(unavailable)";
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

                // P4 fix: build two-column diff text from both source and target changes
                string conflictId = item.ConflictId ?? item.ElementId;
                Change sourceChange = null;
                Change targetChange = null;

                if (item.ConflictType == "spatial_collision"
                    && conflictId != null
                    && conflictId.Contains("|"))
                {
                    // Spatial collision compound id: "srcElementId|tgtElementId"
                    var parts = conflictId.Split('|');
                    string srcId = parts[0];
                    string tgtId = parts.Length > 1 ? parts[1] : null;
                    sourceChange = PreviewStateService.SourceChanges
                        ?.FirstOrDefault(c => (c.RepoGuid ?? c.ElementId) == srcId);
                    targetChange = PreviewStateService.TargetChanges
                        ?.FirstOrDefault(c => (c.RepoGuid ?? c.ElementId) == tgtId);
                }
                else
                {
                    sourceChange = PreviewStateService.SourceChanges
                        ?.FirstOrDefault(c =>
                            (!string.IsNullOrEmpty(c.RepoGuid) && c.RepoGuid == item.RepoGuid) ||
                            c.ElementId == item.ElementId ||
                            (c.RepoGuid ?? c.ElementId) == conflictId);
                    targetChange = PreviewStateService.TargetChanges
                        ?.FirstOrDefault(c =>
                            (!string.IsNullOrEmpty(c.RepoGuid) && c.RepoGuid == item.RepoGuid) ||
                            c.ElementId == item.ElementId ||
                            (c.RepoGuid ?? c.ElementId) == conflictId);
                }

                PreviewElementDetails.Text = (sourceChange != null || targetChange != null)
                    ? BuildConflictDiffText(sourceChange, targetChange, item.ConflictType)
                    : item.Description + "\n\n" + item.Details;

                // Show "Keep Both" only for spatial collisions
                ConflictKeepBoth.Visibility = item.ConflictType == "spatial_collision" 
                    ? Visibility.Visible : Visibility.Collapsed;

                // Restore saved resolution
                string savedRes = null;
                PreviewStateService.ConflictResolutions.TryGetValue(item.ConflictId ?? item.ElementId, out savedRes);

                ConflictKeepOurs.Checked -= ConflictResolution_Changed;
                ConflictKeepTheirs.Checked -= ConflictResolution_Changed;
                ConflictKeepBoth.Checked -= ConflictResolution_Changed;

                ConflictKeepOurs.IsChecked = savedRes != "accept_remote" && savedRes != "keep_both";
                ConflictKeepTheirs.IsChecked = savedRes == "accept_remote";
                ConflictKeepBoth.IsChecked = savedRes == "keep_both";

                ConflictKeepOurs.Checked += ConflictResolution_Changed;
                ConflictKeepTheirs.Checked += ConflictResolution_Changed;
                ConflictKeepBoth.Checked += ConflictResolution_Changed;
            }
            else
            {
                PreviewConflictPanel.Visibility = Visibility.Collapsed;
                PreviewElementDetails.Text = item.Description + "\n\n" + item.Details;
            }
        }
    }
}
