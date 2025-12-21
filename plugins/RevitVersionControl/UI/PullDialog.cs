using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public class PullDialog : Window
    {
        public string SelectedCommitId { get; private set; }
        public string CurrentCommitId { get; private set; }
        public string ProjectId { get; private set; }

        private ComboBox ProjectComboBox;
        private ComboBox CurrentCommitComboBox;
        private ComboBox TargetCommitComboBox;
        private readonly ApiClient _apiClient = ApiClient.Instance;

        public PullDialog()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.Title = "Pull Changes";
            this.Width = 420;
            this.Height = 320;
            this.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var mainPanel = new StackPanel { Margin = new Thickness(10) };

            mainPanel.Children.Add(new TextBlock { Text = "Project:", Margin = new Thickness(0, 0, 0, 5) });
            ProjectComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            ProjectComboBox.SelectionChanged += ProjectComboBox_SelectionChanged;
            mainPanel.Children.Add(ProjectComboBox);

            mainPanel.Children.Add(new TextBlock { Text = "Current Commit:", Margin = new Thickness(0, 0, 0, 5) });
            CurrentCommitComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(CurrentCommitComboBox);

            mainPanel.Children.Add(new TextBlock { Text = "Target Commit:", Margin = new Thickness(0, 0, 0, 5) });
            TargetCommitComboBox = new ComboBox { Margin = new Thickness(0, 0, 0, 15) };
            mainPanel.Children.Add(TargetCommitComboBox);

            var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

            var pullButton = new Button
            {
                Content = "Pull",
                Width = 80,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            pullButton.Click += PullButton_Click;
            buttonPanel.Children.Add(pullButton);

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
            Loaded += PullDialog_Loaded;
        }

        private async void PullDialog_Loaded(object sender, RoutedEventArgs e)
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
                CurrentCommitComboBox.ItemsSource = null;
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

                CurrentCommitComboBox.ItemsSource = commitItems;
                TargetCommitComboBox.ItemsSource = commitItems;
                CurrentCommitComboBox.DisplayMemberPath = "DisplayText";
                TargetCommitComboBox.DisplayMemberPath = "DisplayText";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load commits: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PullButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a project.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (CurrentCommitComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select the current commit.", "Validation Error",
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

            if (CurrentCommitComboBox.SelectedItem is CommitItem currentItem)
            {
                CurrentCommitId = currentItem.CommitId;
            }

            if (TargetCommitComboBox.SelectedItem is CommitItem targetItem)
            {
                SelectedCommitId = targetItem.CommitId;
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
