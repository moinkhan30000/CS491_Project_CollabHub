using System;
using System.Windows;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class LoginDialog : Window
    {
        public LoginDialog()
        {
            InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string email = EmailTextBox.Text.Trim();
                string password = PasswordBox.Password;

                if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                {
                    MessageBox.Show("Please enter both email and password.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LoginButton.IsEnabled = false;
                LoginButton.Content = "Logging in...";

                bool success = await ApiClient.Instance.LoginAsync(email, password);

                if (success)
                {
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show("Invalid email or password.", "Login Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Login error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoginButton.IsEnabled = true;
                LoginButton.Content = "Login";
            }
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            // Open Register Dialog
            var registerDialog = new RegisterDialog();
            bool? result = registerDialog.ShowDialog();

            // If registration was successful (result == true), the user is already logged in (auto-login).
            // So we should close the login dialog as well with success.
            if (result == true)
            {
                DialogResult = true;
                Close();
            }
        }
    }
}
