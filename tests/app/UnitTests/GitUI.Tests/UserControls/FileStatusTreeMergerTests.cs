using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.UserControls;
using GitUIPluginInterfaces;

namespace GitUITests.UserControls;

[TestFixture]
public class FileStatusTreeMergerTests
{
    private static readonly GitRevision _revision = new(ObjectId.WorkTreeId);

    private TreeView _treeView = null!;

    [SetUp]
    public void SetUp()
    {
        _treeView = new TreeView();
    }

    [TearDown]
    public void TearDown()
    {
        _treeView.Dispose();
    }

    [Test]
    public void Merge_should_keep_the_instances_of_unchanged_nodes()
    {
        Fill(["a/1", "a/2", "b/1", "b/2"]);
        TreeNode[] before = Nodes();

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "a/2", "b/1", "b/2"]);

        Nodes().Should().Equal(before, (x, y) => ReferenceEquals(x, y));
        result.Removed.Should().Be(0);
        result.Inserted.Should().Be(0);
        result.Updated.Should().Be(0);
        result.Retained.Should().Be(6);
    }

    [Test]
    public void Merge_should_remove_a_vanished_file_and_nothing_else()
    {
        Fill(["a/1", "a/2", "b/1", "b/2"]);
        TreeNode folderB = _treeView.Nodes[1];
        TreeNode b1 = folderB.Nodes[0];

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "b/1", "b/2"]);

        result.Removed.Should().Be(1);
        result.Inserted.Should().Be(0);
        _treeView.Nodes[1].Should().BeSameAs(folderB);
        folderB.Nodes[0].Should().BeSameAs(b1);
        Names().Should().Equal("a/1", "b/1", "b/2");
    }

    [Test]
    public void Merge_should_remove_a_folder_which_became_empty()
    {
        Fill(["a/1", "b/1", "b/2"]);

        FileStatusTreeMerger.MergeResult result = Merge(["b/1", "b/2"]);

        result.Removed.Should().Be(1);
        _treeView.Nodes.Count.Should().Be(1);
        Names().Should().Equal("b/1", "b/2");
    }

    [Test]
    public void Merge_should_insert_a_new_file_at_its_sorted_position()
    {
        Fill(["a/1", "a/3"]);
        TreeNode a1 = _treeView.Nodes[0].Nodes[0];
        TreeNode a3 = _treeView.Nodes[0].Nodes[1];

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "a/2", "a/3"]);

        result.Inserted.Should().Be(1);
        result.InsertedNodes.Should().ContainSingle();
        _treeView.Nodes[0].Nodes[0].Should().BeSameAs(a1);
        _treeView.Nodes[0].Nodes[2].Should().BeSameAs(a3);
        Names().Should().Equal("a/1", "a/2", "a/3");
    }

    [Test]
    public void Merge_should_insert_a_new_folder_expanded()
    {
        Fill(["b/1", "b/2"]);

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "a/2", "b/1", "b/2"]);

        result.Inserted.Should().Be(1);
        _treeView.Nodes[0].IsExpanded.Should().BeTrue();
    }

    [Test]
    public void Merge_should_keep_the_state_of_the_folders_which_are_retained()
    {
        Fill(["a/1", "a/2", "b/1", "b/2"]);
        _treeView.Nodes[0].Collapse();
        _treeView.Nodes[1].Expand();

        Merge(["a/1", "a/2", "b/1"]);

        _treeView.Nodes[0].IsExpanded.Should().BeFalse();
        _treeView.Nodes[1].IsExpanded.Should().BeTrue();
    }

    [Test]
    public void Merge_should_update_the_tag_of_a_retained_file_to_the_new_item()
    {
        Fill(["a/1", "a/2"]);
        TreeNode node = _treeView.Nodes[0].Nodes[0];
        GitItemStatus oldItem = ((FileStatusItem)node.Tag!).Item;

        Merge(["a/1", "a/2"]);

        _treeView.Nodes[0].Nodes[0].Should().BeSameAs(node);
        ((FileStatusItem)node.Tag!).Item.Should().NotBeSameAs(oldItem);
        ((FileStatusItem)node.Tag!).Item.Name.Should().Be("a/1");
    }

    [Test]
    public void Merge_should_update_a_file_whose_appearance_changed_in_place()
    {
        Fill(["a/1", "a/2"]);
        TreeNode node = _treeView.Nodes[0].Nodes[0];

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "a/2"], hasSameAppearance: (_, _) => false);

        _treeView.Nodes[0].Nodes[0].Should().BeSameAs(node);
        result.Updated.Should().Be(2);
        result.UpdatedNodes.Should().HaveCount(2);
        result.Retained.Should().Be(1);
    }

    [Test]
    public void Merge_should_replace_a_submodule_node_so_that_its_status_can_complete_on_the_new_node()
    {
        Fill(["a/1", "sub/"], submodules: ["sub/"]);
        TreeNode oldNode = _treeView.Nodes[1];

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "sub/"], submodules: ["sub/"]);

        _treeView.Nodes[1].Should().NotBeSameAs(oldNode);
        result.ReplacedNodes.Should().ContainSingle().Which.Old.Should().BeSameAs(oldNode);
    }

    [Test]
    public void Merge_should_give_the_same_signature_as_a_full_rebuild([Values] bool flat, [Values] bool mergeSingleItemsWithFolder)
    {
        string[] before = ["a/1", "a/2", "a/x/1", "a/x/2", "b/1", "c/d/e/1", "c/d/e/2", "root"];
        string[] after = ["a/1", "a/x/1", "a/x/3", "b/1", "b/2", "c/d/f/1", "c/d/e/2", "c/g/1", "root2"];
        Fill(before, flat, mergeSingleItemsWithFolder);
        TreeView rebuilt = new();
        foreach (TreeNode node in Build(after, flat, mergeSingleItemsWithFolder).Nodes.Cast<TreeNode>().ToArray())
        {
            node.Remove();
            rebuilt.Nodes.Add(node);
        }

        List<FileStatusTreeMerger.SignatureEntry> expected = FileStatusTreeMerger.Compute(rebuilt.Nodes.Cast<TreeNode>());

        Merge(after, flat, mergeSingleItemsWithFolder);

        FileStatusTreeMerger.FindDifference(expected, FileStatusTreeMerger.Compute(_treeView.Nodes.Cast<TreeNode>())).Should().BeNull();
        rebuilt.Dispose();
    }

    [Test]
    public void Merge_should_keep_a_collapsed_folder_collapsed_when_a_sibling_splits_its_chain()
    {
        Fill(["a/b/1", "a/b/2"]);
        _treeView.Nodes.Count.Should().Be(1);
        _treeView.Nodes[0].Collapse();

        Merge(["a/b/1", "a/b/2", "a/c/1", "a/c/2"]);

        TreeNode folderA = _treeView.Nodes[0];
        folderA.Nodes.Count.Should().Be(2);
        folderA.IsExpanded.Should().BeTrue();
        TreeNode folderB = folderA.Nodes.Cast<TreeNode>().Single(node => ((RelativePath)node.Tag!).Value == "a/b");
        folderB.IsExpanded.Should().BeFalse();
        TreeNode folderC = folderA.Nodes.Cast<TreeNode>().Single(node => ((RelativePath)node.Tag!).Value == "a/c");
        folderC.IsExpanded.Should().BeTrue();
    }

    [Test]
    public void Merge_should_replace_a_file_displayed_merged_with_its_folder_by_the_folder_when_a_second_file_is_added()
    {
        Fill(["a/1", "a/2", "b/1"], mergeSingleItemsWithFolder: true);
        _treeView.Nodes[1].Tag.Should().BeOfType<FileStatusItem>();

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "a/2", "b/1", "b/2"], mergeSingleItemsWithFolder: true);

        result.Removed.Should().Be(1);
        result.Inserted.Should().Be(1);
        _treeView.Nodes[1].Tag.Should().BeOfType<RelativePath>();
        _treeView.Nodes[1].Nodes.Count.Should().Be(2);
        _treeView.Nodes[1].IsExpanded.Should().BeTrue();
    }

    [Test]
    public void Merge_should_replace_a_folder_by_the_file_displayed_merged_with_its_folder_when_one_file_is_left()
    {
        Fill(["a/1", "a/2", "b/1", "b/2"], mergeSingleItemsWithFolder: true);
        _treeView.Nodes[1].Collapse();

        FileStatusTreeMerger.MergeResult result = Merge(["a/1", "a/2", "b/1"], mergeSingleItemsWithFolder: true);

        result.Removed.Should().Be(1);
        result.Inserted.Should().Be(1);
        _treeView.Nodes[1].Tag.Should().BeOfType<FileStatusItem>();
        _treeView.Nodes[1].Nodes.Count.Should().Be(0);
        Names().Should().Equal("a/1", "a/2", "b/1");
    }

    [Test]
    public void Merge_should_not_let_a_recreated_folder_inherit_an_earlier_state()
    {
        Fill(["a/1", "a/2", "b/1", "b/2"]);
        _treeView.Nodes[0].Collapse();

        Merge(["b/1", "b/2"]);
        Merge(["a/1", "a/2", "b/1", "b/2"]);

        _treeView.Nodes[0].IsExpanded.Should().BeTrue();
    }

    [Test]
    public void Merge_should_throw_when_the_order_of_the_nodes_differs()
    {
        Fill(["a/1", "b/1"]);
        TreeNode[] reversed = [.. Build(["a/1", "b/1"], flat: false, mergeSingleItemsWithFolder: false).Nodes.Cast<TreeNode>().Reverse()];

        Action act = () => FileStatusTreeMerger.Merge(_treeView.Nodes, reversed, HasSameAppearance);

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void Compute_should_differ_for_different_trees()
    {
        Fill(["a/1", "a/2"]);
        List<FileStatusTreeMerger.SignatureEntry> signature = FileStatusTreeMerger.Compute(_treeView.Nodes.Cast<TreeNode>());
        using TreeView other = new();
        other.Nodes.Add(Build(["a/1", "a/3"], flat: false, mergeSingleItemsWithFolder: false).Nodes[0]);

        FileStatusTreeMerger.FindDifference(signature, FileStatusTreeMerger.Compute(other.Nodes.Cast<TreeNode>())).Should().NotBeNull();
    }

    [Test]
    public void FindProblem_should_report_a_duplicate_key()
    {
        TreeNode[] nodes = [FileNode("a"), FileNode("a")];

        FileStatusTreeMerger.FindProblem(nodes).Should().Contain("duplicate");
    }

    [Test]
    public void FindProblem_should_report_a_node_without_a_key()
    {
        TreeNode[] nodes = [FileNode("a"), new TreeNode("no tag")];

        FileStatusTreeMerger.FindProblem(nodes).Should().Contain("no key");
    }

    [Test]
    public void FindProblem_should_report_a_placeholder_node()
    {
        TreeNode[] nodes = [new TreeNode("placeholder") { Tag = Array.Empty<TreeNode>() }];

        FileStatusTreeMerger.FindProblem(nodes).Should().Contain("placeholder");
    }

    [Test]
    public void FindProblem_should_return_null_for_a_valid_tree()
    {
        TreeNode[] nodes = [.. Build(["a/1", "a/2", "b/1"], flat: false, mergeSingleItemsWithFolder: false).Nodes.Cast<TreeNode>()];

        FileStatusTreeMerger.FindProblem(nodes).Should().BeNull();
    }

    private static TreeNode Build(IEnumerable<string> names, bool flat, bool mergeSingleItemsWithFolder, IReadOnlyCollection<string>? submodules = null)
    {
        FileStatusList.StatusSorter sorter = new();
        return sorter.CreateTreeSortedByPath(
            names.Select(name => new GitItemStatus(name) { IsSubmodule = submodules?.Contains(name) is true }),
            flat,
            mergeSingleItemsWithFolder,
            createNode: status => new TreeNode(status.Name) { Tag = new FileStatusItem(firstRev: null, secondRev: _revision, status) });
    }

    private static TreeNode FileNode(string name)
        => new(name) { Tag = new FileStatusItem(firstRev: null, secondRev: _revision, new GitItemStatus(name)) };

    private static bool HasSameAppearance(GitItemStatus oldItem, GitItemStatus newItem) => true;

    private void Fill(string[] names, bool flat = false, bool mergeSingleItemsWithFolder = false, string[]? submodules = null)
    {
        foreach (TreeNode node in Build(names, flat, mergeSingleItemsWithFolder, submodules).Nodes.Cast<TreeNode>().ToArray())
        {
            node.Remove();
            _treeView.Nodes.Add(node);
        }

        _treeView.ExpandAll();
    }

    private FileStatusTreeMerger.MergeResult Merge(string[] names, bool flat = false, bool mergeSingleItemsWithFolder = false, string[]? submodules = null, Func<GitItemStatus, GitItemStatus, bool>? hasSameAppearance = null)
    {
        TreeNode[] desired = [.. Build(names, flat, mergeSingleItemsWithFolder, submodules).Nodes.Cast<TreeNode>()];
        return FileStatusTreeMerger.Merge(_treeView.Nodes, desired, hasSameAppearance ?? HasSameAppearance);
    }

    private IEnumerable<string> Names()
        => _treeView.Nodes.Cast<TreeNode>()
            .SelectMany(node => node.Items())
            .Select(node => node.Tag).OfType<FileStatusItem>()
            .Select(item => item.Item.Name);

    private TreeNode[] Nodes() => [.. _treeView.Nodes.Cast<TreeNode>().SelectMany(node => node.Items())];
}
