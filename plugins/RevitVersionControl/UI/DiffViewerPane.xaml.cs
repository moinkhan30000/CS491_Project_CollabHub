using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class DiffViewerPane : Page
    {
        private List<DiffRow> _allRows = new List<DiffRow>();
        private ElementId _diffViewId;
        private Guid _sessionId = Guid.Empty;

        public DiffViewerPane()
        {
            InitializeComponent();
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

            BaseCommitText.Text = string.IsNullOrEmpty(result.BaseShort) ? "-" : result.BaseShort;
            TargetCommitText.Text = string.IsNullOrEmpty(result.TargetShort) ? "-" : result.TargetShort;

            AddedCountText.Text = $"{result.AddedCount} added";
            ModifiedCountText.Text = $"{result.ModifiedCount} modified";
            DeletedCountText.Text = $"{result.DeletedCount} deleted";

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

        public void Clear()
        {
            _allRows.Clear();
            _diffViewId = null;
            _sessionId = Guid.Empty;
            BaseCommitText.Text = "-";
            TargetCommitText.Text = "-";
            AddedCountText.Text = "0 added";
            ModifiedCountText.Text = "0 modified";
            DeletedCountText.Text = "0 deleted";
            StatusBanner.Visibility = Visibility.Collapsed;
            StatusBanner.Text = string.Empty;
            RowsListView.ItemsSource = null;
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
                OnClearComplete = () => Dispatcher.Invoke(Clear)
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
                OnClearComplete = () => Dispatcher.Invoke(Clear)
            });
            DiffViewerExternalEvent.Event.Raise();
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
