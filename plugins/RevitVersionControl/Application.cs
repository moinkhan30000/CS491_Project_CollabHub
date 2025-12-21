using System;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using Autodesk.Revit.Attributes;

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

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // Create ribbon tab
                try
                {
                    application.CreateRibbonTab(TabName);
                }
                catch
                {
                    // Tab already exists
                }

                // Create ribbon panel
                RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);

                // Add buttons
                AddPushButton(panel, "Publish", "Publish\nSnapshot", 
                    typeof(Commands.PublishCommand), "PublishCommand.png", 
                    "Publish current model snapshot to server");

                AddPushButton(panel, "History", "View\nHistory", 
                    typeof(Commands.ViewHistoryCommand), "HistoryCommand.png", 
                    "View commit history and branches");

                AddPushButton(panel, "Pull", "Pull\nChanges", 
                    typeof(Commands.PullCommand), "PullCommand.png", 
                    "Pull changes from remote");

                AddPushButton(panel, "Diff", "View\nDiff", 
                    typeof(Commands.DiffViewCommand), "DiffCommand.png", 
                    "View differences between versions");

                panel.AddSeparator();

                AddPushButton(panel, "Settings", "Settings", 
                    typeof(Commands.SettingsCommand), "SettingsCommand.png", 
                    "Configure version control settings");

                // Register dockable panes
                RegisterDockablePanes(application);

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

        private void AddPushButton(RibbonPanel panel, string name, string text, 
            Type commandType, string _imageName, string tooltip)
        {
            PushButtonData buttonData = new PushButtonData(
                name,
                text,
                typeof(Application).Assembly.Location,
                commandType.FullName
            );

            PushButton button = panel.AddItem(buttonData) as PushButton;
            button.ToolTip = tooltip;

            // Set icon (would load actual image in production)
            // button.LargeImage = LoadImage(imageName);
        }

        private void RegisterDockablePanes(UIControlledApplication application)
        {
            // Register History Pane
            application.RegisterDockablePane(
                new DockablePaneId(new Guid("12345678-1234-1234-1234-123456789012")),
                "Version History",
                new HistoryPaneProvider()
            );

            // Register Diff/Merge Pane
            application.RegisterDockablePane(
                new DockablePaneId(new Guid("87654321-4321-4321-4321-210987654321")),
                "Changes & Merge",
                new DiffMergePaneProvider()
            );
        }

        private BitmapImage LoadImage(string _imageName)
        {
            // Load embedded resource image
            // Implementation would load from resources
            return null;
        }
    }

    // Dockable Pane Providers
    public class HistoryPaneProvider : IDockablePaneProvider
    {
        public static UI.HistoryPane Instance { get; private set; }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            Instance = new UI.HistoryPane();
            data.FrameworkElement = Instance;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Tabbed
            };
        }
    }

    public class DiffMergePaneProvider : IDockablePaneProvider
    {
        public static UI.DiffMergePane Instance { get; private set; }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            Instance = new UI.DiffMergePane();
            data.FrameworkElement = Instance;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }
    }
}
