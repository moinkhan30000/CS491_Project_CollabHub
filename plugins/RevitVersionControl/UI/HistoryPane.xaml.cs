using System;
using System.Windows;
using System.Windows.Controls;

namespace RevitVersionControl.UI
{
    public partial class HistoryPane : Page
    {
        public HistoryPane()
        {
            InitializeComponent();
            LoadCommits();
        }

        private void LoadCommits()
        {
            // Load commits from API
            // For demo, add sample data
            CommitListView.Items.Add(new
            {
                Message = "Added conference room walls",
                CommitId = "abc123",
                Author = "John Doe",
                Timestamp = "2025-12-03 10:30",
                ChangedElements = 25
            });

            CommitListView.Items.Add(new
            {
                Message = "Updated door specifications",
                CommitId = "def456",
                Author = "Jane Smith",
                Timestamp = "2025-12-02 15:45",
                ChangedElements = 12
            });

            CommitListView.Items.Add(new
            {
                Message = "Initial model setup",
                CommitId = "ghi789",
                Author = "John Doe",
                Timestamp = "2025-12-01 09:00",
                ChangedElements = 150
            });
        }
    }
}
