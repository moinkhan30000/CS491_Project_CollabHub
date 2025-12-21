using System.Windows;

namespace RevitVersionControl.UI
{
    public partial class InitProjectDialog : Window
    {
        public string ProjectName { get; private set; }

        public InitProjectDialog(string defaultName = "")
        {
            InitializeComponent();
            ProjectNameInput.Text = defaultName;
        }

        private void InitButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ProjectNameInput.Text))
            {
                MessageBox.Show("Please enter a project name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ProjectName = ProjectNameInput.Text.Trim();
            DialogResult = true;
            Close();
        }
    }
}
