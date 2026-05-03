using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitVersionControl.Services;

namespace RevitVersionControl.UI
{
    public partial class NetworkGraphWindow : Window
    {
        private readonly string _projectId;
        private readonly string _projectName;
        private readonly string _activeCommitId;
        private readonly ApiClient _apiClient = ApiClient.Instance;

        public NetworkGraphWindow(string projectId, string projectName, string activeCommitId)
        {
            InitializeComponent();
            _projectId = projectId;
            _projectName = projectName;
            _activeCommitId = activeCommitId;
            HeaderText.Text = $"Network Graph: {_projectName}";
            Loaded += NetworkGraphWindow_Loaded;
        }

        private async void NetworkGraphWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var commits = await _apiClient.GetCommitsAsync(_projectId, limit: 1000);
                var latestCommit = await _apiClient.GetLatestCommitAsync(_projectId);
                var rootCommit = await _apiClient.GetProjectRootCommitAsync(_projectId);
                
                if (latestCommit != null && commits.TrueForAll(c => c.CommitId != latestCommit.CommitId))
                    commits.Insert(0, latestCommit);
                if (rootCommit != null && commits.TrueForAll(c => c.CommitId != rootCommit.CommitId))
                    commits.Add(rootCommit);

                // Deduplicate and sort chronologically (oldest first, so left to right)
                commits = commits
                    .GroupBy(c => c.CommitId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(c => c.Timestamp)
                    .ToList();

                DrawGraph(commits);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load graph: {ex.Message}");
            }
        }

        private void DrawGraph(List<Commit> commits)
        {
            GraphCanvas.Children.Clear();
            if (commits.Count == 0) return;

            // 1. Assign Lanes to Branches
            var branchLanes = new Dictionary<string, int>();
            int currentLane = 0;
            
            // Try to assign 'main' to lane 0
            if (commits.Any(c => string.Equals(c.BranchName, "main", StringComparison.OrdinalIgnoreCase)))
            {
                branchLanes["main"] = currentLane++;
            }

            foreach (var c in commits)
            {
                string bName = string.IsNullOrEmpty(c.BranchName) ? "main" : c.BranchName;
                if (!branchLanes.ContainsKey(bName))
                {
                    branchLanes[bName] = currentLane++;
                }
            }

            double xSpacing = 120;
            double ySpacing = 80;
            double startX = 50;
            double startY = 50;

            // 2. Assign coordinates
            var nodeCoords = new Dictionary<string, Point>();
            for (int i = 0; i < commits.Count; i++)
            {
                var c = commits[i];
                string bName = string.IsNullOrEmpty(c.BranchName) ? "main" : c.BranchName;
                int lane = branchLanes[bName];
                
                double x = startX + (i * xSpacing);
                double y = startY + (lane * ySpacing);
                nodeCoords[c.CommitId] = new Point(x, y);
            }

            // Calculate canvas size
            GraphCanvas.Width = startX + (commits.Count * xSpacing) + 100;
            GraphCanvas.Height = startY + (currentLane * ySpacing) + 100;

            var laneColors = new Brush[] { 
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")),
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50")),
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800")),
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E91E63")),
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9C27B0"))
            };

            // 3. Draw Edges
            foreach (var c in commits)
            {
                if (!string.IsNullOrEmpty(c.ParentCommit) && nodeCoords.ContainsKey(c.ParentCommit))
                {
                    var parentPt = nodeCoords[c.ParentCommit];
                    var childPt = nodeCoords[c.CommitId];

                    string bName = string.IsNullOrEmpty(c.BranchName) ? "main" : c.BranchName;
                    int lane = branchLanes[bName];
                    var brush = laneColors[lane % laneColors.Length];

                    var line = new Line
                    {
                        X1 = parentPt.X,
                        Y1 = parentPt.Y,
                        X2 = childPt.X,
                        Y2 = childPt.Y,
                        Stroke = brush,
                        StrokeThickness = 3,
                        Opacity = 0.7
                    };
                    GraphCanvas.Children.Add(line);
                }

                if (!string.IsNullOrEmpty(c.ParentCommit2) && nodeCoords.ContainsKey(c.ParentCommit2))
                {
                    var parentPt2 = nodeCoords[c.ParentCommit2];
                    var childPt = nodeCoords[c.CommitId];

                    // Find the branch of the second parent to color the line
                    var parent2Commit = commits.FirstOrDefault(x => x.CommitId == c.ParentCommit2);
                    string bName2 = parent2Commit != null && !string.IsNullOrEmpty(parent2Commit.BranchName) ? parent2Commit.BranchName : "main";
                    int lane2 = branchLanes.ContainsKey(bName2) ? branchLanes[bName2] : 0;
                    var brush2 = laneColors[lane2 % laneColors.Length];

                    var line2 = new Line
                    {
                        X1 = parentPt2.X,
                        Y1 = parentPt2.Y,
                        X2 = childPt.X,
                        Y2 = childPt.Y,
                        Stroke = brush2,
                        StrokeThickness = 2,
                        StrokeDashArray = new DoubleCollection { 4, 2 },
                        Opacity = 0.6
                    };
                    GraphCanvas.Children.Add(line2);
                }
            }

            // 4. Draw Nodes
            foreach (var c in commits)
            {
                var pt = nodeCoords[c.CommitId];
                string bName = string.IsNullOrEmpty(c.BranchName) ? "main" : c.BranchName;
                int lane = branchLanes[bName];
                var brush = laneColors[lane % laneColors.Length];

                bool isActive = (c.CommitId == _activeCommitId);
                
                var ellipse = new Ellipse
                {
                    Width = isActive ? 24 : 16,
                    Height = isActive ? 24 : 16,
                    Fill = brush,
                    Stroke = isActive ? Brushes.White : Brushes.Transparent,
                    StrokeThickness = isActive ? 3 : 0,
                    ToolTip = $"Commit: {c.Message}\nBranch: {bName}\nAuthor: {c.GetAuthorName()}\nDate: {c.Timestamp.ToString("g")}"
                };

                Canvas.SetLeft(ellipse, pt.X - (ellipse.Width / 2));
                Canvas.SetTop(ellipse, pt.Y - (ellipse.Height / 2));
                GraphCanvas.Children.Add(ellipse);

                var txt = new TextBlock
                {
                    Text = c.Message.Length > 15 ? c.Message.Substring(0, 15) + "..." : c.Message,
                    Foreground = Brushes.LightGray,
                    FontSize = 10,
                    ToolTip = c.Message
                };
                Canvas.SetLeft(txt, pt.X - 30);
                Canvas.SetTop(txt, pt.Y + 15);
                GraphCanvas.Children.Add(txt);
                
                if (isActive)
                {
                    var activeTxt = new TextBlock
                    {
                        Text = "★ ACTIVE",
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold,
                        FontSize = 10
                    };
                    Canvas.SetLeft(activeTxt, pt.X - 25);
                    Canvas.SetTop(activeTxt, pt.Y - 25);
                    GraphCanvas.Children.Add(activeTxt);
                }
            }
        }
    }
}
