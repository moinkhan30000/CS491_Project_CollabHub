using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class DiffMergePane : Page
    {
        public DiffMergePane()
        {
            InitializeComponent();
        }

        public void LoadDiffResult(DiffResult diffResult)
        {
            if (diffResult == null)
            {
                Clear();
                return;
            }

            BaseCommitText.Text = diffResult.BaseVersion ?? "-";
            TargetCommitText.Text = diffResult.TargetVersion ?? "-";

            int added = diffResult.Summary != null && diffResult.Summary.ContainsKey("added") ? diffResult.Summary["added"] : 0;
            int modified = diffResult.Summary != null && diffResult.Summary.ContainsKey("modified") ? diffResult.Summary["modified"] : 0;
            int deleted = diffResult.Summary != null && diffResult.Summary.ContainsKey("deleted") ? diffResult.Summary["deleted"] : 0;

            AddedCountText.Text = added.ToString();
            ModifiedCountText.Text = modified.ToString();
            DeletedCountText.Text = deleted.ToString();

            ChangesListView.ItemsSource = BuildChangeItems(diffResult.Changes);
        }

        public void LoadPullResult(PullResult pullResult)
        {
            if (pullResult == null)
            {
                Clear();
                return;
            }

            BaseCommitText.Text = "-";
            TargetCommitText.Text = "-";

            var summary = Summarize(pullResult.Changes);
            AddedCountText.Text = summary.added.ToString();
            ModifiedCountText.Text = summary.modified.ToString();
            DeletedCountText.Text = summary.deleted.ToString();

            ChangesListView.ItemsSource = BuildChangeItems(pullResult.Changes);
        }

        public void Clear()
        {
            BaseCommitText.Text = "-";
            TargetCommitText.Text = "-";
            AddedCountText.Text = "0";
            ModifiedCountText.Text = "0";
            DeletedCountText.Text = "0";
            ChangesListView.ItemsSource = null;
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
                    Details = ""
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
                    ElementId = change.ElementId
                });
            }

            return items;
        }

        private static (int added, int modified, int deleted) Summarize(List<Change> changes)
        {
            int added = 0;
            int modified = 0;
            int deleted = 0;

            if (changes == null)
            {
                return (0, 0, 0);
            }

            foreach (var change in changes)
            {
                switch (change.ChangeType)
                {
                    case "added":
                        added++;
                        break;
                    case "modified":
                        modified++;
                        break;
                    case "deleted":
                        deleted++;
                        break;
                }
            }

            return (added, modified, deleted);
        }

        private static string GetShortChangeType(string changeType)
        {
            switch (changeType)
            {
                case "added":
                    return "ADD";
                case "modified":
                    return "MOD";
                case "deleted":
                    return "DEL";
                default:
                    return changeType?.ToUpperInvariant() ?? "-";
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
                case "added":
                    return "Element added";
                case "modified":
                    return "Element modified";
                case "deleted":
                    return "Element deleted";
                default:
                    return "Change detected";
            }
        }

        private static string BuildDetails(Change change)
        {
            var details = new List<string>();
            if (change.ParameterChanges != null && change.ParameterChanges.Count > 0)
            {
                var preview = change.ParameterChanges.Take(3)
                    .Select(p => $"{p.Name}: {p.OldValue} → {p.NewValue}");
                details.Add(string.Join("; ", preview));
            }

            if (change.GeometryChanged)
            {
                details.Add("Geometry changed");
            }

            if (change.LocationChanged)
            {
                details.Add("Location changed");
            }

            return string.Join(" | ", details);
        }

        private class ChangeItem
        {
            public string ChangeType { get; set; }
            public Brush StatusColor { get; set; }
            public string ElementType { get; set; }
            public string Description { get; set; }
            public string Details { get; set; }
            public string ElementId { get; set; }
        }
    }
}
