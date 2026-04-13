using System;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using RevitVersionControl.UI;
using RevitVersionControl.Services;
    
namespace RevitVersionControl
{
    /// <summary>
    /// Main application entry point for Revit add-in
    /// Registers ribbon UI and commands
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Application : IExternalApplication
    {
        private const string TabName = "Version Control";   
        private const string PanelName = "Revit VC";

        // Static references to UI elements to control visibility
        public static System.Collections.Generic.List<PushButton> RestrictedButtons { get; private set; } = new System.Collections.Generic.List<PushButton>();
        public static PushButton LoginButton { get; private set; }
        public static PushButton RegisterButton { get; private set; }

        public static void SetLoggedInState(bool isLoggedIn)
        {
            if (LoginButton != null)
            {
                LoginButton.ItemText = isLoggedIn ? "Logout" : "Login";
                LoginButton.ToolTip = isLoggedIn ? "Logout from CollabHub" : "Login to CollabHub";
            }

            if (RegisterButton != null)
                RegisterButton.Visible = !isLoggedIn;

            foreach (var btn in RestrictedButtons)
            {
                btn.Visible = isLoggedIn;
                btn.Enabled = isLoggedIn;
            }
        }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create ribbon tab
                try { application.CreateRibbonTab(TabName); } catch { }

                // Create ribbon panel
                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);

                // Add buttons
                
                // Login/Logout Button (Always Visible)
                LoginButton = AddPushButton(panel, "Login", "Login",
                    typeof(Commands.LoginCommand), "LoginCommand.png", "Login to CollabHub");

                // Register Button (Visible only when logged out)
                RegisterButton = AddPushButton(panel, "Register", "Register",
                    typeof(Commands.RegisterCommand), "RegisterCommand.png", "Create a new CollabHub account");

                // Restricted Buttons (Initially Hidden)
                var publishBtn = AddPushButton(panel, "Publish", "Publish\nSnapshot",
                    typeof(Commands.PublishCommand), "PublishCommand.png", "Publish current model snapshot to server");
                RestrictedButtons.Add(publishBtn);

                var historyBtn = AddPushButton(panel, "History", "View\nHistory",
                    typeof(Commands.ViewHistoryCommand), "HistoryCommand.png", "View commit history and branches");
                RestrictedButtons.Add(historyBtn);

                var pullBtn = AddPushButton(panel, "Pull", "Pull\nChanges",
                    typeof(Commands.PullCommand), "PullCommand.png", "Pull changes from remote");
                RestrictedButtons.Add(pullBtn);

                var diffBtn = AddPushButton(panel, "Diff", "View\nDiff",
                    typeof(Commands.DiffViewCommand), "DiffCommand.png", "View differences between versions");
                RestrictedButtons.Add(diffBtn);

                panel.AddSeparator();

                var settingsBtn = AddPushButton(panel, "Settings", "Settings",
                    typeof(Commands.SettingsCommand), "SettingsCommand.png", "Configure version control settings");
                RestrictedButtons.Add(settingsBtn);

                panel.AddSeparator();

                // Collaboration Buttons
                var initBtn = AddPushButton(panel, "Init", "Initialize\nProject",
                    typeof(Commands.InitProjectCommand), "InitCommand.png", "Initialize version control for this project");
                RestrictedButtons.Add(initBtn);

                var inviteBtn = AddPushButton(panel, "Invite", "Invite\nCollaborators",
                    typeof(Commands.InviteCommand), "InviteCommand.png", "Invite other users to this project");
                RestrictedButtons.Add(inviteBtn);

                var invitesBtn = AddPushButton(panel, "Invites", "My\nInvitations",
                    typeof(Commands.InvitationsCommand), "InvitationsCommand.png", "Manage your project invitations");
                RestrictedButtons.Add(invitesBtn);

                // Initialize state
                SetLoggedInState(false);

                // Register dockable panes (skip if UI classes don't exist)
                try { RegisterDockablePanes(application); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Warning: Dockable panes failed to load: {ex.Message}");
                    // Continue - dockable panes are optional
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Failed to initialize add-in: {ex.Message}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // Cleanup resources
            return Result.Succeeded;
        }

        private PushButton AddPushButton(RibbonPanel panel, string name, string text,
            Type commandType, string _imageName, string tooltip)
        {
            PushButtonData buttonData = new PushButtonData(
                name, text,
                typeof(Application).Assembly.Location,
                commandType.FullName);

            PushButton button = panel.AddItem(buttonData) as PushButton;
            button.ToolTip = tooltip;
            
            return button;
        }

        private void RegisterDockablePanes(UIControlledApplication application)
        {
            // These will only register if the UI classes exist
            // If they don't, the exception is caught in OnStartup
            
            try
            {
                application.RegisterDockablePane(
                    new DockablePaneId(new Guid("12345678-1234-1234-1234-123456789012")),
                    "Version History",
                    new HistoryPaneProvider()
                );
            }
            catch { }

            try
            {
                application.RegisterDockablePane(
                    new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321")),
                    "Changes & Merge",
                    new DiffMergePaneProvider()
                );
            }
            catch { }
        }

        private BitmapImage LoadImage(string _imageName)
        {
            // Load embedded resource image
            // Implementation would load from resources
            return null;
        }
    }

    // Dockable Pane Providers - Stub implementations with all required methods
    public class HistoryPaneProvider : IDockablePaneProvider
    {
        public static HistoryPaneProvider Instance { get; private set; } = new HistoryPaneProvider();
        private HistoryPane _historyPane;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            _historyPane = new HistoryPane();
            data.FrameworkElement = _historyPane;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Tabbed };
        }

        public void Clear() => _historyPane?.Clear();
        public void ReloadProjects() => _historyPane?.ReloadProjects();
        public void Refresh() => _historyPane?.Refresh();
    }

    public class DiffMergePaneProvider : IDockablePaneProvider
    {
        public static DiffMergePaneProvider Instance { get; private set; }
        private DiffMergePane _diffMergePane;

        public DiffMergePaneProvider()
        {
            Instance = this;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            _diffMergePane = new DiffMergePane();
            data.FrameworkElement = _diffMergePane;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }

        public void LoadPullResult(object result, string projectId = null) =>
            _diffMergePane?.LoadPullResult(result as PullResult, projectId);
        public void LoadDiffResult(object result) => _diffMergePane?.LoadDiffResult(result as DiffResult);
        public void Clear() => _diffMergePane?.Clear();
    }
}
