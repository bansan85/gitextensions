using System.ComponentModel.Design;
using GitExtensions.Extensibility.Git;
using GitUI;
using GitUI.UserControls;
using GitUIPluginInterfaces;
using NSubstitute;

namespace GitExtensions.UITests.CommandsDialogs;

[Apartment(ApartmentState.STA)]
public class FileStatusListTests
{
    // Created once for each test
    private Form _form = null!;
    private FileStatusList _fileStatusList = null!;
    private IGitUICommandsSource _uiCommandsSource = null!;

    [SetUp]
    public void SetUp()
    {
        ServiceContainer serviceContainer = GlobalServiceContainer.CreateDefaultMockServiceContainer();
        IGitModule module = Substitute.For<IGitModule>();
        GitUICommands commands = new(serviceContainer, module);
        _uiCommandsSource = Substitute.For<IGitUICommandsSource>();
        _uiCommandsSource.UICommands.Returns(x => commands);

        _form = new Form();
        _fileStatusList = new FileStatusList
        {
            Parent = _form,
            UICommandsSource = _uiCommandsSource
        };
        _form.Show(); // must be visible to be able to change the focus
    }

    [TearDown]
    public void TearDown()
    {
        _fileStatusList.Dispose();
        _form.Dispose();
    }

    [Test]
    public void SelectAll_should_expand_folders_and_focus_the_first_node()
    {
        const int folderCount = 4;
        const int filesPerFolder = 50;
        GitItemStatus[] items = Enumerable.Range(0, folderCount * filesPerFolder)
            .Select(index => new GitItemStatus($"folder{index / filesPerFolder}/subfolder/file{index:D3}"))
            .ToArray();
        _fileStatusList.SetDiffs(null, new GitRevision(ObjectId.Random()), items);
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        tree.CollapseAll();

        _fileStatusList.SelectAll();

        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(items);
        tree.Items().Where(node => node.Nodes.Count > 0).Should().OnlyContain(node => node.IsExpanded);
        tree.FocusedNode.Should().BeSameAs(tree.Nodes[0]);
        tree.Nodes[0].IsVisible.Should().BeTrue();
        tree.UpdateSuspended.Should().BeFalse();
    }

    [Test]
    public void SetDiffs_should_keep_the_nodes_and_the_state_of_the_folders_when_updating_incrementally()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1", "b/2", "c/1", "c/2");
        TreeNode folderA = tree.Nodes[0];
        TreeNode folderB = tree.Nodes[1];
        TreeNode folderC = tree.Nodes[2];
        folderA.Collapse();
        folderC.Collapse();

        SetDiffs(_fileStatusList, "a/1", "a/2", "b/2", "c/1", "c/2");

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        tree.Nodes[0].Should().BeSameAs(folderA);
        tree.Nodes[1].Should().BeSameAs(folderB);
        tree.Nodes[2].Should().BeSameAs(folderC);
        folderA.IsExpanded.Should().BeFalse();
        folderB.IsExpanded.Should().BeTrue();
        folderC.IsExpanded.Should().BeFalse();
        Names(tree).Should().Equal("a/1", "a/2", "b/2", "c/1", "c/2");
    }

    [Test]
    public void SetDiffs_should_keep_the_scroll_position_when_folders_are_removed_and_inserted_incrementally()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        string[] files = [.. Enumerable.Range(0, 40).SelectMany(folder => new[] { $"d{folder:D2}/a", $"d{folder:D2}/b" })];
        SetDiffs(_fileStatusList, files);
        TreeNode top = tree.Nodes[20];
        tree.TopNode = top;
        tree.TopNode.Should().BeSameAs(top);

        SetDiffs(_fileStatusList, [.. files.Where(file => !file.StartsWith("d10/"))]);
        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        tree.TopNode.Should().BeSameAs(top);

        SetDiffs(_fileStatusList, [.. files.Where(file => !file.StartsWith("d10/") || file == "d10/a")]);
        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        tree.TopNode.Should().BeSameAs(top);
    }

    [Test]
    public void SetDiffs_should_rebuild_the_list_when_incremental_updates_are_not_enabled()
    {
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1", "b/2");
        TreeNode folderA = tree.Nodes[0];
        folderA.Collapse();

        SetDiffs(_fileStatusList, "a/1", "a/2", "b/2");

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.NotAttempted);
        tree.Nodes[0].Should().NotBeSameAs(folderA);
    }

    [Test]
    public void SetDiffs_should_rebuild_the_list_completely_when_merging_fails()
    {
        _fileStatusList.UpdateIncrementally = true;
        FileStatusList.TestAccessor accessor = _fileStatusList.GetTestAccessor();
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1", "b/2");
        accessor.AfterMergeHook = _ => throw new InvalidOperationException("injected failure");

        SetDiffs(_fileStatusList, "a/1", "b/1", "b/2", "c/1");

        accessor.LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.RebuiltAfterFailure);
        Names(accessor.FileStatusListView).Should().Equal("a/1", "b/1", "b/2", "c/1");
        Signature(_fileStatusList).Should().Equal(ReferenceSignature("a/1", "b/1", "b/2", "c/1"));
    }

    [Test]
    public void SetDiffs_should_rebuild_the_list_completely_when_the_merged_tree_differs_from_the_expected_one()
    {
        _fileStatusList.UpdateIncrementally = true;
        FileStatusList.TestAccessor accessor = _fileStatusList.GetTestAccessor();
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1", "b/2");
        accessor.AfterMergeHook = nodes => nodes[0].Nodes[0].Remove();

        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1", "b/2", "c/1");

        accessor.LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.RebuiltAfterMismatch);
        Names(accessor.FileStatusListView).Should().Equal("a/1", "a/2", "b/1", "b/2", "c/1");
        Signature(_fileStatusList).Should().Equal(ReferenceSignature("a/1", "a/2", "b/1", "b/2", "c/1"));
    }

    [Test]
    public void SetDiffs_should_not_swallow_a_cancellation_while_merging()
    {
        _fileStatusList.UpdateIncrementally = true;
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1");
        _fileStatusList.GetTestAccessor().AfterMergeHook = _ => throw new OperationCanceledException();

        Action act = () => SetDiffs(_fileStatusList, "a/1", "b/1", "b/2");

        act.Should().Throw<OperationCanceledException>();
    }

    [Test]
    public void SetDiffs_should_show_the_other_entries_when_the_data_contains_duplicates_and_entries_without_name()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;

        SetDiffs(_fileStatusList, "a/1", "a/1", "", "a/2", "b/1");

        Names(tree).Should().Equal("a/1", "a/2", "b/1");
        SetDiffs(_fileStatusList, "a/1", "", "a/1", "b/1", "b/1", "b/2");
        Names(tree).Should().Equal("a/1", "b/1", "b/2");
        Signature(_fileStatusList).Should().Equal(ReferenceSignature("a/1", "b/1", "b/2"));
    }

    [Test]
    public void SetDiffs_should_keep_the_selection_of_a_node_which_is_retained()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1", "b/2");
        TreeNode selected = tree.Nodes[1].Nodes[1];
        tree.SelectedNode = selected;

        SetDiffs(_fileStatusList, "a/2", "b/1", "b/2", "c/1");

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        tree.SelectedNodes.Should().ContainSingle().Which.Should().BeSameAs(selected);
        _fileStatusList.SelectedGitItem!.Name.Should().Be("b/2");
    }

    [Test]
    public void SetDiffs_should_raise_SelectedIndexChanged_for_a_retained_selection_so_that_changed_data_is_shown()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1", "a/2");
        tree.SelectedNode = tree.Nodes[0].Nodes[1];
        int raised = 0;
        _fileStatusList.SelectedIndexChanged += (_, _) => ++raised;

        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1");

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        raised.Should().BeGreaterThan(0);
        _fileStatusList.SelectedGitItem!.Name.Should().Be("a/2");
    }

    [Test]
    public void SetDiffs_should_drop_the_selection_of_a_node_which_was_removed()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1", "b/2");
        tree.SelectedNode = tree.Nodes[0].Nodes[0];

        SetDiffs(_fileStatusList, "a/2", "b/1", "b/2");

        tree.SelectedNodes.All(node => node.TreeView == tree).Should().BeTrue();
        _fileStatusList.SelectedItems.Items().Select(item => item.Name).Should().NotContain("a/1");
    }

    [Test]
    public void SetDiffs_should_not_reset_the_top_node_when_updating_incrementally()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        string[] names = [.. Enumerable.Range(0, 300).Select(index => $"folder{index / 10:D2}/file{index:D3}")];
        SetDiffs(_fileStatusList, names);
        TreeNode top = tree.Nodes[20];
        tree.TopNode = top;

        SetDiffs(_fileStatusList, names[..^1]);

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        tree.TopNode.Should().BeSameAs(top);
    }

    [Test]
    public void SetDiffs_should_use_the_merge_for_a_changed_filter()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1.cs", "a/2.txt", "b/1.cs", "b/2.txt");
        TreeNode folderB = tree.Nodes[1];
        folderB.Collapse();

        _fileStatusList.SetFilter(@"\.cs$");

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        Names(tree).Should().Equal("a/1.cs", "b/1.cs");
        tree.Nodes[1].Should().BeSameAs(folderB);
        folderB.IsExpanded.Should().BeFalse();
    }

    [Test]
    public void SetDiffs_should_rebuild_the_list_when_the_display_mode_changed()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1");
        TreeNode folderA = tree.Nodes[0];
        _fileStatusList.GetTestAccessor().SetFlatList(true);

        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1");

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.RebuiltIneligible);
        tree.Nodes.Cast<TreeNode>().Should().NotContain(folderA);
        Signature(_fileStatusList).Should().Equal(ReferenceSignatureFlat("a/1", "a/2", "b/1"));
    }

    [Test, Explicit("Timing measurement for SC-004")]
    public void SetDiffs_timing_of_an_incremental_update_versus_a_rebuild()
    {
        string[] names = [.. Enumerable.Range(0, 5000).Select(index => $"folder{index / 20:D3}/sub/file{index:D4}.cs")];
        foreach (bool incremental in new[] { false, true })
        {
            _fileStatusList.UpdateIncrementally = incremental;
            SetDiffs(_fileStatusList, names);
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int run = 1; run <= 5; ++run)
            {
                SetDiffs(_fileStatusList, names[..^run]);
            }

            TestContext.Out.WriteLine($"incremental={incremental}: {stopwatch.ElapsedMilliseconds / 5} ms per refresh after removing one file ({_fileStatusList.GetTestAccessor().LastUpdateOutcome})");
        }
    }

    [Test]
    public void SetDiffs_should_use_the_merge_for_a_flat_list()
    {
        _fileStatusList.UpdateIncrementally = true;
        MultiSelectTreeView tree = _fileStatusList.GetTestAccessor().FileStatusListView;
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1");
        _fileStatusList.GetTestAccessor().SetFlatList(true);
        SetDiffs(_fileStatusList, "a/1", "a/2", "b/1");
        TreeNode[] before = [.. tree.Nodes.Cast<TreeNode>()];

        SetDiffs(_fileStatusList, "a/1", "b/1");

        _fileStatusList.GetTestAccessor().LastUpdateOutcome.Should().Be(FileStatusList.UpdateOutcome.Merged);
        tree.Nodes.Cast<TreeNode>().Should().Equal(before[0], before[2]);
    }

    [Test]
    public void ItemSelections()
    {
        FileStatusList.TestAccessor accessor = _fileStatusList.GetTestAccessor();

        GitItemStatus itemNotInList = new(name: "not in list");
        GitItemStatus item0 = new(name: "z.0");
        GitItemStatus item1 = new(name: "x.1");
        GitItemStatus item2 = new(name: "y.2");
        List<GitItemStatus> items = [item0, item1, item2];

        // alphabetical order
        GitItemStatus itemAt0 = item1;
        GitItemStatus itemAt1 = item2;
        GitItemStatus itemAt2 = item0;
        GitRevision firstRev = new(ObjectId.Random());
        GitRevision secondRev = new(ObjectId.Random());
        _fileStatusList.SetDiffs(firstRev: firstRev, secondRev: secondRev, items: items);

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(0);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt0);
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt0);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt0 });

        _fileStatusList.SelectedGitItem.Should().BeSameAs(_fileStatusList.SelectedItem!.Item);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(_fileStatusList.SelectedItems.Items());

        // SelectedIndex

        _fileStatusList.SelectedGitItems = [itemAt1];

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt1);
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt1);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1 });

        _fileStatusList.SelectedItems = [];

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1); // unchanged
        _fileStatusList.SelectedItem.Should().BeNull(); // empty
        _fileStatusList.SelectedGitItem.Should().BeNull(); // empty
        _fileStatusList.SelectedItems.Items().Should().BeEmpty();

        _fileStatusList.SelectedGitItems = [itemAt2];
        _fileStatusList.SelectedGitItems = [itemNotInList]; // clears the selection

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(2); // unchanged
        _fileStatusList.SelectedItem.Should().BeNull(); // empty
        _fileStatusList.SelectedGitItem.Should().BeNull(); // empty
        _fileStatusList.SelectedItems.Items().Should().BeEmpty();

        _fileStatusList.SelectedGitItems = [itemAt1];

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt1);
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt1);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1 });

        // SelectedGitItem

        _fileStatusList.SelectedGitItem = itemAt1;

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt1);
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt1);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1 });

        _fileStatusList.SelectedGitItem = null;

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1); // unchanged
        _fileStatusList.SelectedItem.Should().BeNull(); // empty
        _fileStatusList.SelectedGitItem.Should().BeNull(); // empty
        _fileStatusList.SelectedItems.Items().Should().BeEmpty();

        _fileStatusList.SelectedGitItem = itemAt2;
        _fileStatusList.SelectedGitItem = itemNotInList; // clears the selection

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(2); // unchanged
        _fileStatusList.SelectedItem.Should().BeNull(); // empty
        _fileStatusList.SelectedGitItem.Should().BeNull(); // empty
        _fileStatusList.SelectedItems.Items().Should().BeEmpty();

        _fileStatusList.SelectedGitItem = itemAt1;

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt1);
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt1);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1 });

        // SelectedItems.Items() (up to one item)

        _fileStatusList.SelectedGitItems = new List<GitItemStatus> { itemAt1 };

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt1);
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt1);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1 });

        _fileStatusList.SelectedItems = null!;

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1); // unchanged
        _fileStatusList.SelectedItem.Should().BeNull(); // empty
        _fileStatusList.SelectedGitItem.Should().BeNull(); // empty
        _fileStatusList.SelectedItems!.Items().Should().BeEmpty();

        _fileStatusList.SelectedGitItems = new List<GitItemStatus> { };

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1); // unchanged
        _fileStatusList.SelectedItem.Should().BeNull(); // empty
        _fileStatusList.SelectedGitItem.Should().BeNull(); // empty
        _fileStatusList.SelectedItems.Items().Should().BeEmpty();

        _fileStatusList.SelectedGitItems = new List<GitItemStatus> { itemAt2 };
        _fileStatusList.SelectedGitItems = new List<GitItemStatus> { itemNotInList }; // clears the selection

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(2); // unchanged
        _fileStatusList.SelectedItem.Should().BeNull(); // empty
        _fileStatusList.SelectedGitItem.Should().BeNull(); // empty
        _fileStatusList.SelectedItems.Items().Should().BeEmpty();

        _fileStatusList.SelectedGitItems = new List<GitItemStatus> { itemAt1 };

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt1);
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt1);
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1 });

        // SelectedItems.Items() (multiple items)

        _fileStatusList.SelectedGitItems = [itemAt2];
        _fileStatusList.SelectedGitItems = new List<GitItemStatus> { itemAt2, itemAt0, itemNotInList };

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(0);
        _fileStatusList.SelectedItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedGitItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt0, itemAt2 });

        accessor.FileStatusListView.FocusedNode = accessor.FileStatusListView.Nodes[1];

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedGitItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt0, itemAt2 });

        _fileStatusList.SelectedGitItems = [itemAt2];
        _fileStatusList.SelectedGitItems = new List<GitItemStatus> { itemAt2, itemAt1, itemNotInList };

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(1);
        _fileStatusList.SelectedItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedGitItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1, itemAt2 });

        accessor.FileStatusListView.FocusedNode = accessor.FileStatusListView.Nodes[0];

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(0);
        _fileStatusList.SelectedItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedGitItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt1, itemAt2 });

        // SelectAll

        _fileStatusList.SelectedGitItems = [itemAt2];
        _fileStatusList.SelectAll();

        foreach (TreeNode item in accessor.FileStatusListView.Items())
        {
            accessor.FileStatusListView.SelectedNodes.Contains(item).Should().BeTrue();
        }

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(0);
        _fileStatusList.SelectedItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedGitItem.Should().BeNull(); // due to multi-selection
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt0, itemAt1, itemAt2 });

        // SelectFirstVisibleItem

        _fileStatusList.SelectedGitItems = [itemAt2];
        _fileStatusList.SelectFirstVisibleItem();

        accessor.FileStatusListView.FocusedNode!.Index.Should().Be(0);
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt0); // first item
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt0); // first item
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt0 });

        // no focus
        accessor.FileStatusListView.FocusedNode = null;

        accessor.FileStatusListView.FocusedNode.Should().BeNull();
        _fileStatusList.SelectedItem!.Item.Should().Be(itemAt0); // unchanged
        _fileStatusList.SelectedGitItem.Should().BeSameAs(itemAt0); // unchanged
        _fileStatusList.SelectedItems.Items().Should().BeEquivalentTo(new List<GitItemStatus> { itemAt0 });
    }

    [Test]
    public void Test_FilterWatermarkLabelVisibility_on_FilterVisibleChange(
        [Values(null, "", "x")] string? filterText,
        [Values(true, false)] bool filterFocused)
    {
        FileStatusList.TestAccessor accessor = _fileStatusList.GetTestAccessor();

        accessor.FilterComboBox.Text = filterText; // must be set first because it does not need to update the visibility
        accessor.SetFileStatusListVisibility(showNoFiles: false);

        if (filterFocused)
        {
            accessor.FilterComboBox.Focus(); // must be done after FilterVisible = true
        }

        accessor.FilterWatermarkLabelVisible.Should().Be(!filterFocused && string.IsNullOrEmpty(filterText));
    }

    [Test]
    public void Test_FilterWatermarkLabelVisibility_on_Focus()
    {
        FileStatusList.TestAccessor accessor = _fileStatusList.GetTestAccessor();

        accessor.FilterComboBox.Text = "";
        accessor.SetFileStatusListVisibility(showNoFiles: false);
        accessor.FilterWatermarkLabelVisible.Should().BeTrue();

        accessor.FilterComboBox.Focus();
        accessor.SetFileStatusListVisibility(showNoFiles: false);
        accessor.FilterWatermarkLabelVisible.Should().BeFalse();

        accessor.FileStatusListView.Focus();
        accessor.SetFileStatusListVisibility(showNoFiles: false);
        accessor.FilterWatermarkLabelVisible.Should().BeTrue();
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("\\.cs", true)]
    public void Test_StoreFilter_valid(string? regex, bool active)
    {
        FileStatusList.TestAccessor accessor = _fileStatusList.GetTestAccessor();

        Color expectedColor = active ? accessor.ActiveInputColor : SystemColors.Window;
        string? expectedRegex = string.IsNullOrEmpty(regex) ? null : regex;

        _fileStatusList.SetFilter(regex!);
        accessor.SetFileStatusListVisibility(showNoFiles: false);

        CheckStoreFilter(expectedColor, expectedRegex!, accessor);

        accessor.DeleteFilterButton.Visible.Should().Be(active);
    }

    [Test]
    public void Test_StoreFilter_invalid()
    {
        const string validRegex = "\\.cs";
        const string invalidRegex = "(";

        FileStatusList.TestAccessor accessor = _fileStatusList.GetTestAccessor();

        // set a valid Filter that must not change
        accessor.StoreFilter(validRegex);

        ((Action)(() => accessor.StoreFilter(invalidRegex))).Should().Throw<ArgumentException>();

        CheckStoreFilter(accessor.InvalidInputColor, validRegex, accessor);
    }

    private static void CheckStoreFilter(Color expectedColor, string expectedRegex, FileStatusList.TestAccessor accessor)
    {
        accessor.FilterComboBox.BackColor.Should().Be(expectedColor);
        accessor.Filter?.ToString().Should().Be(expectedRegex);
    }

    private static string[] Names(MultiSelectTreeView tree)
        => [.. tree.Items().Select(node => node.Tag).OfType<FileStatusItem>().Select(item => item.Item.Name)];

    private static void SetDiffs(FileStatusList fileStatusList, params string[] names)
        => fileStatusList.SetDiffs(firstRev: null, new GitRevision(ObjectId.WorkTreeId), [.. names.Select(name => new GitItemStatus(name))]);

    private static List<FileStatusTreeMerger.SignatureEntry> Signature(FileStatusList fileStatusList)
        => FileStatusTreeMerger.Compute(fileStatusList.GetTestAccessor().FileStatusListView.Nodes.Cast<TreeNode>());

    private List<FileStatusTreeMerger.SignatureEntry> ReferenceSignatureFlat(params string[] names)
    {
        using FileStatusList reference = new()
        {
            UICommandsSource = _uiCommandsSource,
            Parent = _form
        };
        // The first load subscribes to the sort setting, which overwrites the display mode.
        SetDiffs(reference, names);
        reference.GetTestAccessor().SetFlatList(true);
        SetDiffs(reference, names);
        return Signature(reference);
    }

    private List<FileStatusTreeMerger.SignatureEntry> ReferenceSignature(params string[] names)
    {
        using FileStatusList reference = new()
        {
            // The control loads when it is added to the shown form, which needs the commands source.
            UICommandsSource = _uiCommandsSource,
            Parent = _form
        };
        SetDiffs(reference, names);
        return Signature(reference);
    }
}
