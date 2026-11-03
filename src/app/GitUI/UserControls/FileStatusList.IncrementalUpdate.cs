using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Automation;
using GitCommands;
using GitExtensions.Extensibility.Git;
using GitUI.UserControls;

namespace GitUI;

partial class FileStatusList
{
    internal enum UpdateOutcome
    {
        /// <summary>
        ///  Incremental updates are not enabled, the list is rebuilt as usual.
        /// </summary>
        NotAttempted,

        /// <summary>
        ///  The merge was applied and verified.
        /// </summary>
        Merged,

        /// <summary>
        ///  The merge was not attempted because the new content cannot be merged unambiguously; the list is rebuilt.
        /// </summary>
        RebuiltIneligible,

        /// <summary>
        ///  The merge was applied but its result differed from the expected tree; the list is rebuilt.
        /// </summary>
        RebuiltAfterMismatch,

        /// <summary>
        ///  The merge failed; the list is rebuilt.
        /// </summary>
        RebuiltAfterFailure
    }

    /// <summary>
    ///  The settings a tree is built with. A tree is only merged into a tree which was built with the same settings.
    /// </summary>
    private sealed record BuildParameters(
        bool FlatList,
        GroupBy? GroupBy,
        bool GroupByRevision,
        bool ShowDiffGroups,
        bool MergeSingleItemsWithFolder,
        bool ShowGroupNodes,
        int GroupCount);

    private BuildParameters? _lastBuildParameters;
    private bool _loadingClearSkipped;
    private UpdateOutcome _lastUpdateOutcome = UpdateOutcome.NotAttempted;
    private Action<TreeNodeCollection>? _afterMergeHook;

    /// <summary>
    ///  Gets or sets whether new content is merged into the displayed tree instead of replacing it,
    ///  so that expanded/collapsed folders, selection and scroll position survive a refresh.
    /// </summary>
    /// <remarks>
    ///  Correctness has priority: whenever the merge cannot be applied or verified, the tree is rebuilt completely.
    /// </remarks>
    [Description("Merge new content into the displayed tree instead of replacing it")]
    [DefaultValue(false)]
    public bool UpdateIncrementally { get; set; }

    private bool CanMergeIntoDisplayedTree
        => UpdateIncrementally
            && !_isFileTreeMode
            && _lastBuildParameters is not null
            && FileStatusListView.Nodes.Count > 0;

    private void FileStatusListLoadingUnlessUpdatedIncrementally()
    {
        // FileStatusListLoading clears the tree and the selection, which is what loses the state of the tree.
        if (CanMergeIntoDisplayedTree)
        {
            _loadingClearSkipped = true;
            return;
        }

        FileStatusListLoading();
    }

    private BuildParameters GetBuildParameters(int groupCount)
        => new(_flatList,
            _groupBy,
            GroupByRevision,
            _showDiffGroups,
            AppSettings.FileStatusMergeSingleItemWithFolder.Value,
            !_flatList || AppSettings.FileStatusShowGroupNodesInFlatList.Value,
            groupCount);

    private bool HasSameAppearance(GitItemStatus oldItem, GitItemStatus newItem)
        => GetItemImageKey(oldItem) == GetItemImageKey(newItem)
            && oldItem.IsStatusOnly == newItem.IsStatusOnly
            && oldItem.ErrorMessage == newItem.ErrorMessage
            && (oldItem == _noItemStatuses[0]) == (newItem == _noItemStatuses[0]);

    private static bool IsRecoverable(Exception ex)
        => ex is not (OperationCanceledException or OutOfMemoryException);

    /// <summary>
    ///  Leaves out entries which cannot be displayed meaningfully, so that they do not prevent the other entries from being shown.
    /// </summary>
    private static IEnumerable<GitItemStatus> RemoveInvalidEntries(IEnumerable<GitItemStatus> statuses)
    {
        HashSet<(string Name, string? OldName, StagedStatus Staged)> identities = [];
        foreach (GitItemStatus status in statuses)
        {
            if (string.IsNullOrEmpty(status.Name))
            {
                Trace.WriteLine("An entry without a name is not shown.");
                continue;
            }

            if (!identities.Add((status.Name, status.OldName, status.Staged)))
            {
                Trace.WriteLine($"A duplicate of \"{status.Name}\" is not shown.");
                continue;
            }

            yield return status;
        }
    }

    private void RestoreSelectionAfterMerge(IReadOnlyCollection<TreeNode> selectedBefore, TreeNode? focusedBefore, FileStatusTreeMerger.MergeResult result)
    {
        // Removing the selected node makes the tree select a neighbor, and the tree keeps nodes of its own selection which were removed.
        Dictionary<TreeNode, TreeNode> replacedNodes = result.ReplacedNodes.ToDictionary(pair => pair.Old, pair => pair.New);
        HashSet<TreeNode> selection = [.. selectedBefore.Select(Map).Where(IsInTree)];
        TreeNode? focused = focusedBefore is null ? null : Map(focusedBefore);
        if (selection.Count == 0)
        {
            ClearSelected();
            return;
        }

        FileStatusListView.SetSelectedNodes(selection, focused is not null && IsInTree(focused) ? focused : selection.First());
        return;

        bool IsInTree(TreeNode node) => node.TreeView == FileStatusListView;

        TreeNode Map(TreeNode node) => replacedNodes.GetValueOrDefault(node, node);
    }

    /// <summary>
    ///  Gets the nodes which are shown from the top of the tree, beginning with the top node.
    ///  A <see langword="null"/> entry stands for the very first node, which is not tracked by identity.
    /// </summary>
    private TreeNode?[] GetVisibleNodesFromTop()
    {
        TreeNode? top = FileStatusListView.TopNode;
        if (top is null)
        {
            return [];
        }

        if (top == FileStatusListView.Nodes[0])
        {
            return [null];
        }

        List<TreeNode?> nodes = [];
        for (TreeNode? node = top; node is not null && nodes.Count <= FileStatusListView.VisibleCount; node = node.NextVisibleNode)
        {
            nodes.Add(node);
        }

        return [.. nodes];
    }

    /// <summary>
    ///  Inserting and removing nodes makes the tree scroll. Scrolls back to the node which was on top before,
    ///  or to the next one which is still shown when this one was removed.
    /// </summary>
    private void RestoreScrollPosition(TreeNode?[] visibleBefore)
    {
        if (visibleBefore.Length == 0 || FileStatusListView.Nodes.Count == 0)
        {
            return;
        }

        TreeNode? top = visibleBefore[0] is null
            ? FileStatusListView.Nodes[0]
            : visibleBefore.FirstOrDefault(node => node?.TreeView == FileStatusListView && IsShownWhenScrolledTo(node));
        if (top is null || top == FileStatusListView.TopNode)
        {
            return;
        }

        try
        {
            FileStatusListView.BeginUpdate();
            FileStatusListView.TopNode = top;
            FileStatusListView.ScrollLeftMost();
        }
        finally
        {
            FileStatusListView.EndUpdate();
        }

        return;

        // TreeNode.IsVisible tells whether the node is within the client area, which is not what is needed here.
        static bool IsShownWhenScrolledTo(TreeNode node)
        {
            for (TreeNode? parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (!parent.IsExpanded)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private UpdateOutcome TryMergeNodes(List<TreeNodeInfo> nodes, BuildParameters buildParameters)
    {
        return _lastUpdateOutcome = Merge();

        UpdateOutcome Merge()
        {
            if (!UpdateIncrementally)
            {
                return UpdateOutcome.NotAttempted;
            }

            if (!CanMergeIntoDisplayedTree
                || _lastBuildParameters != buildParameters
                || nodes.Count == 0
                || nodes.Any(node => node.State == ExpandCollapseState.PartiallyExpanded))
            {
                return UpdateOutcome.RebuiltIneligible;
            }

            TreeNode[] desired = [.. nodes.Select(node => node.Node)];
            if (FileStatusTreeMerger.FindProblem(desired) is string problem)
            {
                Trace.WriteLine($"The file list is rebuilt instead of merged: {problem}");
                return UpdateOutcome.RebuiltIneligible;
            }

            List<FileStatusTreeMerger.SignatureEntry> expected = FileStatusTreeMerger.Compute(desired);
            TreeNode[] selectedBefore = [.. FileStatusListView.SelectedNodes];
            TreeNode? focusedBefore = FileStatusListView.FocusedNode;
            TreeNode?[] visibleBefore = GetVisibleNodesFromTop();
            string? difference;
            try
            {
                // Everything which changes the appearance of the tree happens while redrawing is suspended,
                // so that the tree is painted once, and only when it is in its final state.
                FileStatusListView.BeginUpdate();
                try
                {
                    FileStatusTreeMerger.MergeResult result = FileStatusTreeMerger.Merge(FileStatusListView.Nodes, desired, HasSameAppearance);
                    _afterMergeHook?.Invoke(FileStatusListView.Nodes);
                    RestoreSelectionAfterMerge(selectedBefore, focusedBefore, result);

                    difference = FileStatusTreeMerger.FindDifference(expected, FileStatusTreeMerger.Compute(FileStatusListView.Nodes.Cast<TreeNode>()));
                    if (difference is null)
                    {
                        LoadFileIcons([.. result.InsertedNodes.SelectMany(node => node.Items()), .. result.UpdatedNodes], cancellationToken: default);
                    }
                }
                finally
                {
                    FileStatusListView.EndUpdate();
                }
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                Trace.WriteLine($"The file list is rebuilt because merging failed: {ex}");
                return UpdateOutcome.RebuiltAfterFailure;
            }

            if (difference is not null)
            {
                Trace.WriteLine($"The file list is rebuilt because the merged tree differs from the expected one: {difference}");
                return UpdateOutcome.RebuiltAfterMismatch;
            }

            RestoreScrollPosition(visibleBefore);
            return UpdateOutcome.Merged;
        }
    }
}
