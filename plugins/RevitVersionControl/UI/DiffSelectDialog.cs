using System;
using System.Windows;
using System.Windows.Controls;

namespace RevitVersionControl.UI
{
    public class DiffSelectDialog : Window
    {
        public string BaseCommitId { get; private set; }
        public string TargetCommitId { get; private set; }
        public string ProjectId { get; private set; }

        private ComboBox ProjectComboBox;
        private ComboBox BaseCommitComboBox;
        private ComboBox TargetCommitComboBox;

        public DiffSelectDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Title = "Select Commits to Compare";
            this.Width = 400;
            this.Height = 300;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var mainPanel = new StackPanel { Margin = new Thickness(10) };

            // Project selection
            mainPanel.Children.Add(new TextBlock { Text = "Project:", Margin = new Thickness(0, 0, 0, 5) });
            ProjectComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            LoadProjects();
            mainPanel.Children.Add(ProjectComboBox);

            // Base commit selection
            mainPanel.Children.Add(new TextBlock { Text = "Base Commit:", Margin = new Thickness(0, 0, 0, 5) });
            BaseCommitComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(BaseCommitComboBox);

            // Target commit selection
            mainPanel.Children.Add(new TextBlock { Text = "Target Commit:", Margin = new Thickness(0, 0, 0, 5) });
            TargetCommitComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(TargetCommitComboBox);

            // Buttons
            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            
            var compareButton = new Button 
            { 
                Content = "Compare", 
                Width = 80, 
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            compareButton.Click += CompareButton_Click;
            buttonPanel.Children.Add(compareButton);

            var cancelButton = new Button 
            { 
                Content = "Cancel", 
                Width = 80,
                IsCancel = true
            };
            cancelButton.Click += CancelButton_Click;
            buttonPanel.Children.Add(cancelButton);

            mainPanel.Children.Add(buttonPanel);

            this.Content = mainPanel;
        }

        private void LoadProjects()
        {
            // Load projects from API
            // For now, add dummy data
            ProjectComboBox.Items.Add(new { Name = "Office Building", ProjectId = "project-1" });
            ProjectComboBox.Items.Add(new { Name = "Residential Complex", ProjectId = "project-2" });
            ProjectComboBox.SelectedIndex = 0;
        }

        private void CompareButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a project.", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (BaseCommitComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a base commit.", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (TargetCommitComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a target commit.", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            dynamic selectedProject = ProjectComboBox.SelectedItem;
            ProjectId = selectedProject.ProjectId;
            BaseCommitId = BaseCommitComboBox.SelectedItem.ToString();
            TargetCommitId = TargetCommitComboBox.SelectedItem.ToString();

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
