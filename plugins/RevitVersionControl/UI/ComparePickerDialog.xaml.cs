using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class ComparePickerDialog : Window
    {
        public string SelectedCommitId { get; private set; }
        public string SelectedCommitMessage { get; private set; }

        public ComparePickerDialog(IEnumerable<Commit> commits, string excludeCommitId = null)
        {
            InitializeComponent();

            var items = (commits ?? Enumerable.Empty<Commit>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.CommitId))
                .Where(c => string.IsNullOrEmpty(excludeCommitId) || !string.Equals(c.CommitId, excludeCommitId, StringComparison.OrdinalIgnoreCase))
                .Select(c => new CommitRow
                {
                    Message = string.IsNullOrEmpty(c.Message) ? "(no message)" : c.Message,
                    CommitId = c.CommitId,
                    ShortCommitId = c.CommitId.Length > 7 ? c.CommitId.Substring(0, 7) : c.CommitId,
                    Author = c.GetAuthorName(),
                    Timestamp = c.Timestamp.ToString("yyyy-MM-dd HH:mm")
                })
                .ToList();

            CommitsListView.ItemsSource = items;
        }

        private void CommitsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CommitsListView.SelectedItem is CommitRow row)
            {
                SelectedCommitId = row.CommitId;
                SelectedCommitMessage = row.Message;
                SelectedCommitText.Text = $"{row.ShortCommitId} - {row.Message}";
                OkButton.IsEnabled = true;
            }
            else
            {
                SelectedCommitId = null;
                SelectedCommitMessage = null;
                SelectedCommitText.Text = "-";
                OkButton.IsEnabled = false;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(SelectedCommitId)) return;
            DialogResult = true;
            Close();
        }

        private class CommitRow
        {
            public string Message { get; set; }
            public string CommitId { get; set; }
            public string ShortCommitId { get; set; }
            public string Author { get; set; }
            public string Timestamp { get; set; }
        }
    }
}
