using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitVersionControl.UI
{
    public partial class DiffMergePane : Page
    {
        public DiffMergePane()
        {
            InitializeComponent();
            LoadSampleChanges();
        }

        private void LoadSampleChanges()
        {
            // Add sample changes for demonstration
            ChangesListView.Items.Add(new
            {
                ChangeType = "ADD",
                StatusColor = new SolidColorBrush(Color.FromRgb(40, 167, 69)),
                ElementType = "Wall: Basic Wall - 200mm",
                Description = "Wall added in conference room",
                Details = "Height: 3500mm, Length: 5000mm, Fire Rating: 3 Hour"
            });

            ChangesListView.Items.Add(new
            {
                ChangeType = "MOD",
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                ElementType = "Door: Single-Flush - 900x2100mm",
                Description = "Door D101 parameters modified",
                Details = "Fire Rating changed from '1 Hour' to '2 Hour'"
            });

            ChangesListView.Items.Add(new
            {
                ChangeType = "MOD",
                StatusColor = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                ElementType = "Window: Fixed - 1200x1500mm",
                Description = "Window W101 location changed",
                Details = "Moved from (1000, 0, 1000) to (1500, 0, 1000)"
            });

            ChangesListView.Items.Add(new
            {
                ChangeType = "DEL",
                StatusColor = new SolidColorBrush(Color.FromRgb(220, 53, 69)),
                ElementType = "Wall: Interior - 100mm",
                Description = "Partition wall removed",
                Details = "Element UniqueId: 2b3c4d5e-6f7g-8h9i-0j1k-l2m3n4o5p6q7"
            });
        }

    }
}
