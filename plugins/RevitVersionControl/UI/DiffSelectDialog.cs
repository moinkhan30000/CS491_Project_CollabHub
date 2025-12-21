using System;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;
using RevitVersionControl.Services;

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
        private readonly ApiClient _apiClient = ApiClient.Instance;

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
            ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
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
            Loaded += DiffSelectDialog_Loaded;
        }

        private async void DiffSelectDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadProjectsAsync();
        }

        private async Task LoadProjectsAsync()
        {
            try
            {
                ProjectComboBox.IsEnabled = false;
                var projects = await _apiClient.GetProjectsAsync();
                ProjectComboBox.ItemsSource = projects;
                ProjectComboBox.DisplayMemberPath = "Name";
                if (projects.Count > 0)
                {
                    ProjectComboBox.SelectedIndex = 0;
                }
                else
                {
                    MessageBox.Show("No projects found on the server.", "Info",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load projects: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ProjectComboBox.IsEnabled = true;
            }
        }

        private async void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem is Project project)
            {
                await LoadCommitsAsync(project.ProjectId);
            }
        }

        private async Task LoadCommitsAsync(string projectId)
        {
            try
            {
                BaseCommitComboBox.ItemsSource = null;
                TargetCommitComboBox.ItemsSource = null;

                var commits = await _apiClient.GetCommitsAsync(projectId);
                var commitItems = new List<CommitItem>();
                foreach (var commit in commits)
                {
                    commitItems.Add(new CommitItem
                    {
                        CommitId = commit.CommitId,
                        DisplayText = $"{commit.Message} ({commit.CommitId})"
                    });
                }

                BaseCommitComboBox.ItemsSource = commitItems;
                TargetCommitComboBox.ItemsSource = commitItems;
                BaseCommitComboBox.DisplayMemberPath = "DisplayText";
                TargetCommitComboBox.DisplayMemberPath = "DisplayText";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load commits: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
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

            var selectedProject = ProjectComboBox.SelectedItem as Project;
            ProjectId = selectedProject?.ProjectId;

            if (BaseCommitComboBox.SelectedItem is CommitItem baseItem)
            {
                BaseCommitId = baseItem.CommitId;
            }

            if (TargetCommitComboBox.SelectedItem is CommitItem targetItem)
            {
                TargetCommitId = targetItem.CommitId;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private class CommitItem
        {
            public string CommitId { get; set; }
            public string DisplayText { get; set; }
        }
    }
}
