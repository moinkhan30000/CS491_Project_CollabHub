using System;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;
using RevitVersionControl.UI;
using RevitVersionControl.Services;
    
namespace RevitVersionControl
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Application : IExternalApplication
    {
        private const string TabName = "Version Control";   
        private const string PanelName = "Revit VC";

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
                try { application.CreateRibbonTab(TabName); } catch { }

                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
                LoginButton = AddPushButton(panel, "Login", "Login",
                    typeof(Commands.LoginCommand), "LoginCommand.png", "Login to CollabHub");

                RegisterButton = AddPushButton(panel, "Register", "Register",
                    typeof(Commands.RegisterCommand), "RegisterCommand.png", "Create a new CollabHub account");

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

                var initBtn = AddPushButton(panel, "Init", "Initialize\nProject",
                    typeof(Commands.InitProjectCommand), "InitCommand.png", "Initialize version control for this project");
                RestrictedButtons.Add(initBtn);

                var inviteBtn = AddPushButton(panel, "Invite", "Invite\nCollaborators",
                    typeof(Commands.InviteCommand), "InviteCommand.png", "Invite other users to this project");
                RestrictedButtons.Add(inviteBtn);

                var invitesBtn = AddPushButton(panel, "Invites", "My\nInvitations",
                    typeof(Commands.InvitationsCommand), "InvitationsCommand.png", "Manage your project invitations");
                RestrictedButtons.Add(invitesBtn);

                SetLoggedInState(false);

                try { RegisterDockablePanes(application); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Warning: Dockable panes failed to load: {ex.Message}");
                }

                try { DiffViewerExternalEvent.Register(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Warning: DiffViewerExternalEvent registration failed: {ex.Message}");
                }

                // Auto-purge stale diff artifacts (red ghosts, the Diff_… view) when a document opens.
                // Diff sessions are transient by design but DirectShapes are real model elements that
                // would otherwise survive across save/close/open cycles.
                try
                {
                    application.ControlledApplication.DocumentOpened += OnDocumentOpened_AutoCleanDiff;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Warning: DocumentOpened subscription failed: {ex.Message}");
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
            try { application.ControlledApplication.DocumentOpened -= OnDocumentOpened_AutoCleanDiff; }
            catch { }
            return Result.Succeeded;
        }

        private void OnDocumentOpened_AutoCleanDiff(object sender, Autodesk.Revit.DB.Events.DocumentOpenedEventArgs e)
        {
            try { Services.DiffViewService.AutoCleanArtifacts(e.Document); }
            catch { /* never block document open */ }
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

            try
            {
                application.RegisterDockablePane(
                    new DockablePaneId(DiffViewerPaneProvider.PaneGuid),
                    "Commit Diff Viewer",
                    new DiffViewerPaneProvider()
                );
            }
            catch { }
        }

        private BitmapImage LoadImage(string _imageName)
        {
            return null;
        }
    }

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

    public class DiffViewerPaneProvider : IDockablePaneProvider
    {
        public static readonly Guid PaneGuid = new Guid("ABCDEF12-3456-7890-ABCD-EF1234567890");
        public static DiffViewerPaneProvider Instance { get; private set; }
        private DiffViewerPane _pane;

        public DiffViewerPaneProvider()
        {
            Instance = this;
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            _pane = new DiffViewerPane();
            data.FrameworkElement = _pane;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }

        public void Show(Services.DiffViewBuildResult result) => _pane?.LoadResult(result);
        public void ReloadProjects() => _pane?.ReloadProjects();
        public void Clear() => _pane?.Clear(resetPickers: true);
        public void LoadDiffForMerge(DiffResult diffResult, string projectId, string baseCommitId, string targetCommitId, string targetBranchName, Merge3WayResult merge3Way) =>
            _pane?.LoadDiffForMerge(diffResult, projectId, baseCommitId, targetCommitId, targetBranchName, merge3Way);
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

        public void LoadPullResult(
            object result,
            string projectId = null,
            string currentCommitId = null,
            string targetCommitId = null,
            string modelId = null) =>
            _diffMergePane?.LoadPullResult(result as PullResult, projectId, currentCommitId, targetCommitId, modelId);
        public void LoadDiffResult(object result) => _diffMergePane?.LoadDiffResult(result as DiffResult);

        public void LoadMerge3WayResult(Merge3WayResult result, string projectId, string sourceCommitId, string targetCommitId, string modelId) =>
            _diffMergePane?.LoadMerge3WayResult(result, projectId, sourceCommitId, targetCommitId, modelId);

        public void LoadHistoricalMergeResult(HistoricalMergeResult result, string projectId, string commitId) =>
            _diffMergePane?.LoadHistoricalMergeResult(result, projectId, commitId);

        public void SetMode(DiffMergeMode mode) => _diffMergePane?.SetMode(mode);

        public void AutoFinalizeCleanMerge(Merge3WayResult result, string projectId, string targetCommitId, string modelId) =>
            _diffMergePane?.AutoFinalizeCleanMerge(result, projectId, targetCommitId, modelId);

        public void Clear() => _diffMergePane?.Clear();
    }
}
