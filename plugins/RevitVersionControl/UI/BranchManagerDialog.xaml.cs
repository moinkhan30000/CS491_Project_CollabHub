using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class BranchManagerDialog : Window
    {
        private readonly ApiClient _apiClient = ApiClient.Instance;
        private readonly string _projectId;
        private readonly string _currentCommitId;
        private readonly string _projectName;
        public string SelectedBranchToSwitch { get; private set; }
        public string SelectedBranchToMerge { get; private set; }

        public BranchManagerDialog(string projectId, string projectName, string currentCommitId)
        {
            InitializeComponent();
            _projectId = projectId;
            _projectName = projectName;
            _currentCommitId = currentCommitId;
            ProjectNameText.Text = projectName;
            Loaded += BranchManagerDialog_Loaded;
        }

        private async void BranchManagerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadBranchesAsync();
        }

        private async Task LoadBranchesAsync()
        {
            try
            {
                var branches = await _apiClient.GetBranchesAsync(_projectId);
                BranchListView.ItemsSource = branches;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load branches: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void NewBranchButton_Click(object sender, RoutedEventArgs e)
        {
            var branches = BranchListView.ItemsSource as List<Branch> ?? new List<Branch>();
            
            var inputDialog = new NewBranchPromptDialog(branches, _currentCommitId);
            inputDialog.Owner = this;
            if (inputDialog.ShowDialog() == true)
            {
                string branchName = inputDialog.BranchName;
                string baseCommitId = inputDialog.BaseCommitId;
                if (!string.IsNullOrWhiteSpace(branchName))
                {
                    try
                    {
                        await _apiClient.CreateBranchAsync(_projectId, branchName, baseCommitId);
                        MessageBox.Show($"Branch '{branchName}' created successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                        await LoadBranchesAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to create branch: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void SwitchButton_Click(object sender, RoutedEventArgs e)
        {
            if (BranchListView.SelectedItem is Branch selectedBranch)
            {
                SelectedBranchToSwitch = selectedBranch.Name;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Please select a branch to switch to.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void MergeButton_Click(object sender, RoutedEventArgs e)
        {
            if (BranchListView.SelectedItem is Branch selectedBranch)
            {
                string activeBranch = DocumentSyncStateService.GetStatusForProject(null, _projectId, false)?.State?.CurrentBranchName ?? "main";
                if (selectedBranch.Name.Equals(activeBranch, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("You cannot merge a branch into itself. Please select a different branch.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SelectedBranchToMerge = selectedBranch.Name;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Please select a branch to merge into your current active branch.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async void DeleteBranchButton_Click(object sender, RoutedEventArgs e)
        {
            if (BranchListView.SelectedItem is Branch selectedBranch)
            {
                if (selectedBranch.Name.Equals("main", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Cannot delete the main branch.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var msgResult = MessageBox.Show($"Are you sure you want to delete branch '{selectedBranch.Name}'?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (msgResult == MessageBoxResult.Yes)
                {
                    bool success = await _apiClient.DeleteBranchAsync(_projectId, selectedBranch.Name);
                    if (success)
                    {
                        await LoadBranchesAsync();
                    }
                    else
                    {
                        MessageBox.Show($"Failed to delete branch.\n{_apiClient.LastError}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                MessageBox.Show("Please select a branch to delete.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    public class NewBranchPromptDialog : Window
    {
        public string BranchName { get; private set; }
        public string BaseCommitId { get; private set; }
        
        private System.Windows.Controls.TextBox _textBox;

        public NewBranchPromptDialog(List<Branch> branches, string activeCommitId)
        {
            BaseCommitId = activeCommitId;

            string shortCommit = !string.IsNullOrEmpty(activeCommitId) && activeCommitId.Length > 8
                ? activeCommitId.Substring(0, 8)
                : (activeCommitId ?? "unknown");

            Title = "New Branch";
            Width = 320;
            Height = 180;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(15) };
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });

            var infoLabel = new System.Windows.Controls.TextBlock
            {
                Text = $"Branching from current commit: {shortCommit}",
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            };
            System.Windows.Controls.Grid.SetRow(infoLabel, 0);
            grid.Children.Add(infoLabel);

            var label = new System.Windows.Controls.TextBlock { Text = "New Branch Name:", FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,5) };
            System.Windows.Controls.Grid.SetRow(label, 1);
            grid.Children.Add(label);

            _textBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0,0,0,20), Height = 25, VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Controls.Grid.SetRow(_textBox, 2);
            grid.Children.Add(_textBox);

            var stack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            System.Windows.Controls.Grid.SetRow(stack, 3);
            
            var okBtn = new System.Windows.Controls.Button { Content = "Create", Width = 70, Height = 25, Margin = new Thickness(0,0,10,0), IsDefault = true, Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#007ACC")), Foreground = System.Windows.Media.Brushes.White, FontWeight = FontWeights.Bold };
            okBtn.Click += (s, e) => { 
                BranchName = _textBox.Text.Trim(); 
                if (string.IsNullOrWhiteSpace(BranchName)) { MessageBox.Show("Name required."); return; }
                
                DialogResult = true; 
                Close(); 
            };
            
            var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Width = 70, Height = 25, IsCancel = true };
            cancelBtn.Click += (s, e) => { DialogResult = false; Close(); };
            
            stack.Children.Add(okBtn);
            stack.Children.Add(cancelBtn);
            grid.Children.Add(stack);

            Content = grid;
            Loaded += (s, e) => _textBox.Focus();
        }
    }
}
