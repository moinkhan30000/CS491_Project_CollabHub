using System;

namespace RevitVersionControl.UI
{
    public class PullDialog
    {
        public string SelectedCommitId { get; set; }
        public string CurrentCommitId { get; set; }
        public string ProjectId { get; set; }

        public bool? ShowDialog()
        {
            return true;
        }
    }
}
