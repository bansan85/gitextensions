using GitExtensions.Extensibility.Git;
using GitUIPluginInterfaces;

namespace GitUI.UserControls;

/// <summary>
///  Merges a freshly built (detached) file status tree into the live tree, so that unchanged nodes
///  keep their identity - and with it their expansion state, selection and scroll position.
/// </summary>
internal static class FileStatusTreeMerger
{
    private const string KeySeparator = "\0";

    internal enum NodeKind
    {
        Group,
        Folder,
        File
    }

    /// <summary>
    ///  Identifies a node among its siblings across rebuilds.
    /// </summary>
    internal readonly record struct NodeKey(NodeKind Kind, string Value);

    /// <summary>
    ///  One node of a <see cref="Compute(IEnumerable{TreeNode})">tree signature</see>.
    /// </summary>
    internal readonly record struct SignatureEntry(int Depth, NodeKey Key, string? Text);

    internal sealed class MergeResult
    {
        public int Removed { get; internal set; }

        public int Inserted { get; internal set; }

        public int Updated { get; internal set; }

        public int Retained { get; internal set; }

        /// <summary>
        ///  Gets the roots of the inserted subtrees (their icons have to be loaded).
        /// </summary>
        public List<TreeNode> InsertedNodes { get; } = [];

        /// <summary>
        ///  Gets the matched nodes whose appearance changed.
        /// </summary>
        public List<TreeNode> UpdatedNodes { get; } = [];

        /// <summary>
        ///  Gets the nodes which were replaced by a new node at the same position.
        /// </summary>
        public List<(TreeNode Old, TreeNode New)> ReplacedNodes { get; } = [];
    }

    /// <summary>
    ///  Gets the signature of the given tree: the ordered keys (and texts) of all its nodes.
    /// </summary>
    /// <remarks>
    ///  The icon index is deliberately not part of the signature: icons are completed asynchronously on the live nodes.
    ///  For the same reason the text of submodules (which gets the submodule status appended asynchronously) is ignored.
    /// </remarks>
    internal static List<SignatureEntry> Compute(IEnumerable<TreeNode> roots)
    {
        List<SignatureEntry> signature = [];
        foreach (TreeNode root in roots)
        {
            Append(root, depth: 0);
        }

        return signature;

        void Append(TreeNode node, int depth)
        {
            NodeKey key = TryGetKey(node, out NodeKey nodeKey) ? nodeKey : new NodeKey(NodeKind.Group, string.Empty);
            signature.Add(new SignatureEntry(depth, key, IsSubmodule(node) ? null : node.Text));
            foreach (TreeNode child in node.Nodes)
            {
                Append(child, depth + 1);
            }
        }
    }

    /// <summary>
    ///  Describes the first difference between two signatures, or <see langword="null"/> if they are equal.
    /// </summary>
    internal static string? FindDifference(IReadOnlyList<SignatureEntry> expected, IReadOnlyList<SignatureEntry> actual)
    {
        int count = Math.Min(expected.Count, actual.Count);
        for (int index = 0; index < count; ++index)
        {
            if (expected[index] != actual[index])
            {
                return $"node #{index}: expected {expected[index]} but was {actual[index]}";
            }
        }

        return expected.Count == actual.Count
            ? null
            : $"expected {expected.Count} nodes but was {actual.Count}";
    }

    /// <summary>
    ///  Finds out whether the tree cannot be merged unambiguously.
    /// </summary>
    /// <returns>A description of the problem, or <see langword="null"/> if the tree can be merged.</returns>
    internal static string? FindProblem(IReadOnlyCollection<TreeNode> roots)
    {
        return FindProblemInSiblings(roots);

        static string? FindProblemInSiblings(IReadOnlyCollection<TreeNode> siblings)
        {
            HashSet<NodeKey> keys = new(siblings.Count);
            foreach (TreeNode node in siblings)
            {
                if (node.Tag is TreeNode[])
                {
                    return $"placeholder node \"{node.Text}\"";
                }

                if (!TryGetKey(node, out NodeKey key))
                {
                    return $"node \"{node.Text}\" has no key";
                }

                if (!keys.Add(key))
                {
                    return $"duplicate key {key}";
                }

                if (FindProblemInSiblings([.. node.Nodes.Cast<TreeNode>()]) is string problem)
                {
                    return problem;
                }
            }

            return null;
        }
    }

    /// <summary>
    ///  Merges the <paramref name="desired"/> nodes into <paramref name="live"/>.
    /// </summary>
    /// <param name="live">The collection of the live tree.</param>
    /// <param name="desired">The nodes of the freshly built tree; they are moved into <paramref name="live"/> if they are new.</param>
    /// <param name="hasSameAppearance">Tells whether two items of the same node are displayed alike (apart from the file icon).</param>
    internal static MergeResult Merge(TreeNodeCollection live, IReadOnlyList<TreeNode> desired, Func<GitItemStatus, GitItemStatus, bool> hasSameAppearance)
    {
        MergeResult result = new();
        MergeChildren(live, [.. desired], result, hasSameAppearance);
        return result;
    }

    internal static bool TryGetKey(TreeNode node, out NodeKey key)
    {
        switch (node.Tag)
        {
            case FileStatusItem fileStatusItem:
                GitItemStatus item = fileStatusItem.Item;
                key = new NodeKey(NodeKind.File, string.Join(KeySeparator, item.Name, item.OldName, (int)item.Staged));
                return true;

            case RelativePath path:
                key = new NodeKey(NodeKind.Folder, path.Value);
                return true;

            case FileStatusList.GroupKey groupKey:
                key = new NodeKey(NodeKind.Group, groupKey.Value);
                return true;

            case GitRevision revision:
                key = new NodeKey(NodeKind.Group, revision.ObjectId.ToString());
                return true;

            default:
                key = default;
                return false;
        }
    }

    private static void CollectExpandedStates(TreeNode node, Dictionary<string, bool> states)
    {
        if (node.Tag is RelativePath path && node.Nodes.Count > 0)
        {
            states[path.Value] = node.IsExpanded;
        }

        foreach (TreeNode child in node.Nodes)
        {
            CollectExpandedStates(child, states);
        }
    }

    private static void ExpandInserted(TreeNode node, IReadOnlyDictionary<string, bool> replacedStates)
    {
        if (node.Nodes.Count == 0)
        {
            return;
        }

        // A folder which replaces a folder with another display shape (for example when a sibling splits a chain of single-child
        // folders) keeps the state of the replaced one; every other new folder is shown expanded like on the first display.
        bool expand = node.Tag is not RelativePath path || !replacedStates.TryGetValue(path.Value, out bool wasExpanded) || wasExpanded;
        if (expand)
        {
            node.Expand();
        }
        else
        {
            node.Collapse();
        }

        foreach (TreeNode child in node.Nodes)
        {
            ExpandInserted(child, replacedStates);
        }
    }

    private static bool IsSubmodule(TreeNode node)
        => node.Tag is FileStatusItem { Item.IsSubmodule: true };

    private static void MergeChildren(TreeNodeCollection live, TreeNode[] desired, MergeResult result, Func<GitItemStatus, GitItemStatus, bool> hasSameAppearance)
    {
        Dictionary<NodeKey, TreeNode> desiredByKey = [];
        foreach (TreeNode node in desired)
        {
            desiredByKey.Add(GetKey(node), node);
        }

        // Remove what is gone. The states of the removed folders are kept for new folders which replace them.
        Dictionary<string, bool> replacedStates = [];
        foreach (TreeNode liveNode in live.Cast<TreeNode>().ToArray())
        {
            if (TryGetKey(liveNode, out NodeKey liveKey) && desiredByKey.ContainsKey(liveKey))
            {
                continue;
            }

            CollectExpandedStates(liveNode, replacedStates);
            liveNode.Remove();
            ++result.Removed;
        }

        HashSet<NodeKey> liveKeys = [.. live.Cast<TreeNode>().Select(node => TryGetKey(node, out NodeKey liveKey) ? liveKey : default)];

        // Both collections are ordered alike, so the remaining live nodes are a subsequence of the desired nodes.
        for (int index = 0; index < desired.Length; ++index)
        {
            TreeNode desiredNode = desired[index];
            NodeKey key = GetKey(desiredNode);

            if (index < live.Count && TryGetKey(live[index], out NodeKey liveKey) && liveKey == key)
            {
                MergeNode(live, index, desiredNode, result, hasSameAppearance);
                continue;
            }

            if (liveKeys.Contains(key))
            {
                throw new InvalidOperationException($"The order of the nodes differs: {key} is not at position {index}.");
            }

            desiredNode.Remove();
            live.Insert(index, desiredNode);
            ExpandInserted(desiredNode, replacedStates);
            result.InsertedNodes.Add(desiredNode);
            ++result.Inserted;
        }

        return;

        static NodeKey GetKey(TreeNode node)
            => TryGetKey(node, out NodeKey key)
                ? key
                : throw new InvalidOperationException($"The node \"{node.Text}\" has no key.");
    }

    private static void MergeNode(TreeNodeCollection live, int index, TreeNode desiredNode, MergeResult result, Func<GitItemStatus, GitItemStatus, bool> hasSameAppearance)
    {
        TreeNode liveNode = live[index];

        if (desiredNode.Tag is FileStatusItem desiredItem)
        {
            if (liveNode.Tag is not FileStatusItem liveItem)
            {
                throw new InvalidOperationException($"The node \"{liveNode.Text}\" does not hold a file status item.");
            }

            if (desiredItem.Item.IsSubmodule)
            {
                // The status of a submodule is completed asynchronously on the node which was created for it.
                desiredNode.Remove();
                live.RemoveAt(index);
                live.Insert(index, desiredNode);
                result.ReplacedNodes.Add((liveNode, desiredNode));
                result.InsertedNodes.Add(desiredNode);
                ++result.Updated;
                return;
            }

            // The tag always has to point to the current item: callers select by item reference.
            liveNode.Tag = desiredItem;

            if (liveNode.Text == desiredNode.Text && hasSameAppearance(liveItem.Item, desiredItem.Item))
            {
                ++result.Retained;
                return;
            }

            liveNode.Text = desiredNode.Text;
            liveNode.ImageIndex = desiredNode.ImageIndex;
            liveNode.SelectedImageIndex = desiredNode.SelectedImageIndex;
            result.UpdatedNodes.Add(liveNode);
            ++result.Updated;
            return;
        }

        if (liveNode.Text != desiredNode.Text || liveNode.Tag?.Equals(desiredNode.Tag) is not true)
        {
            liveNode.Text = desiredNode.Text;
            liveNode.Tag = desiredNode.Tag;
            liveNode.ImageIndex = desiredNode.ImageIndex;
            liveNode.SelectedImageIndex = desiredNode.SelectedImageIndex;
        }

        ++result.Retained;
        MergeChildren(liveNode.Nodes, [.. desiredNode.Nodes.Cast<TreeNode>()], result, hasSameAppearance);
    }
}
