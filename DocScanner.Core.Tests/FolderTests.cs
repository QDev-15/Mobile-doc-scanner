namespace DocScanner.Core.Tests;

public class FolderTests
{
    [Fact]
    public void Documents_are_filed_into_folders_and_keep_their_order()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        DocumentRecord a = store.Create("A"), b = store.Create("B"), c = store.Create("C");
        FolderRecord f = store.CreateFolder("Hợp đồng");

        Assert.Equal(2, store.MoveToFolder([c.Id, a.Id], f.Id));
        // Same order as before (newest first), whatever order they were moved in.
        Assert.Equal(["C", "A"], store.List().Where(d => d.FolderId == f.Id).Select(d => d.Name));
        Assert.Equal(["B"], store.List().Where(d => d.FolderId == null).Select(d => d.Name));

        // Survives a restart.
        var again = new DocumentStore(root.Path);
        Assert.Equal("Hợp đồng", again.Folders().Single().Name);
        Assert.Equal(f.Id, again.Get(c.Id)!.FolderId);

        Assert.Equal(1, again.MoveToFolder([c.Id], null));
        Assert.Null(again.Get(c.Id)!.FolderId);
        Assert.Equal(0, again.MoveToFolder([a.Id], "no-such-folder"));
    }

    [Fact]
    public void Deleting_a_folder_keeps_its_documents()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        DocumentRecord a = store.Create("A");
        FolderRecord f = store.CreateFolder("X");
        store.MoveToFolder([a.Id], f.Id);
        Assert.True(store.RenameFolder(f.Id, "Y"));
        Assert.Equal("Y", store.Folder(f.Id)!.Name);

        Assert.True(store.DeleteFolder(f.Id));
        Assert.Empty(store.Folders());
        Assert.Null(store.Get(a.Id)!.FolderId);
        Assert.Single(store.List());
    }

    [Fact]
    public void Folders_are_listed_by_name()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        store.CreateFolder("b");
        store.CreateFolder("A");
        store.CreateFolder("c");
        Assert.Equal(["A", "b", "c"], store.Folders().Select(f => f.Name));
    }

    [Fact]
    public void Folders_can_nest_and_be_listed_by_level()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        FolderRecord top = store.CreateFolder("2026");
        FolderRecord mid = store.CreateFolder("Quý 1", top.Id);
        FolderRecord leaf = store.CreateFolder("Tháng 1", mid.Id);

        Assert.Equal([top.Id], store.ChildFolders(null).Select(f => f.Id));
        Assert.Equal([mid.Id], store.ChildFolders(top.Id).Select(f => f.Id));
        Assert.Equal([leaf.Id], store.ChildFolders(mid.Id).Select(f => f.Id));
        Assert.Empty(store.ChildFolders(leaf.Id));

        // Nearest first.
        Assert.Equal([mid.Id, top.Id], store.Ancestors(leaf.Id).Select(f => f.Id));
        Assert.Equal([top.Id], store.Ancestors(mid.Id).Select(f => f.Id));
        Assert.Empty(store.Ancestors(top.Id));

        // Survives a restart.
        var again = new DocumentStore(root.Path);
        Assert.Equal(mid.Id, again.Folder(leaf.Id)!.ParentFolderId);
    }

    [Fact]
    public void Creating_a_folder_under_an_unknown_parent_falls_back_to_the_top_level()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        FolderRecord f = store.CreateFolder("Orphan", "no-such-folder");
        Assert.Null(f.ParentFolderId);
    }

    [Fact]
    public void Deleting_a_nested_folder_promotes_its_children_to_its_own_parent()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        FolderRecord top = store.CreateFolder("2026");
        FolderRecord mid = store.CreateFolder("Quý 1", top.Id);
        FolderRecord leaf = store.CreateFolder("Tháng 1", mid.Id);
        DocumentRecord doc = store.Create("A");
        store.MoveToFolder([doc.Id], mid.Id);

        Assert.True(store.DeleteFolder(mid.Id));

        // "Tháng 1" and the document move up to "2026" -- NOT all the way to the top level.
        Assert.Equal(top.Id, store.Folder(leaf.Id)!.ParentFolderId);
        Assert.Equal(top.Id, store.Get(doc.Id)!.FolderId);
        Assert.Equal(new[] { top.Id, leaf.Id }.OrderBy(x => x), store.Folders().Select(f => f.Id).OrderBy(x => x));
    }

    [Fact]
    public void Deleting_a_top_level_folder_promotes_its_children_to_the_top_level()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        FolderRecord top = store.CreateFolder("2026");
        FolderRecord mid = store.CreateFolder("Quý 1", top.Id);

        Assert.True(store.DeleteFolder(top.Id));
        Assert.Null(store.Folder(mid.Id)!.ParentFolderId);
    }

    [Fact]
    public void A_folder_can_be_moved_under_a_different_parent()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        FolderRecord a = store.CreateFolder("A");
        FolderRecord b = store.CreateFolder("B");
        FolderRecord child = store.CreateFolder("Child", a.Id);

        Assert.True(store.MoveFolder(child.Id, b.Id));
        Assert.Equal(b.Id, store.Folder(child.Id)!.ParentFolderId);
        Assert.Equal([b.Id], store.Ancestors(child.Id).Select(f => f.Id));
    }

    [Fact]
    public void Moving_a_folder_into_itself_or_its_own_descendant_is_refused()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        FolderRecord a = store.CreateFolder("A");
        FolderRecord b = store.CreateFolder("B", a.Id);
        FolderRecord c = store.CreateFolder("C", b.Id);

        Assert.False(store.MoveFolder(a.Id, a.Id)); // into itself
        Assert.False(store.MoveFolder(a.Id, b.Id)); // into its own child
        Assert.False(store.MoveFolder(a.Id, c.Id)); // into its own grandchild
        // Nothing changed.
        Assert.Null(store.Folder(a.Id)!.ParentFolderId);
        Assert.Equal(a.Id, store.Folder(b.Id)!.ParentFolderId);
    }

    [Fact]
    public void Moving_a_folder_under_an_unknown_parent_is_refused()
    {
        using var root = new TempRoot();
        var store = new DocumentStore(root.Path);
        FolderRecord a = store.CreateFolder("A");
        Assert.False(store.MoveFolder(a.Id, "no-such-folder"));
        Assert.Null(store.Folder(a.Id)!.ParentFolderId);
    }

    [Theory]
    [InlineData("Hợp đồng thuê nhà", "hop dong", true)]
    [InlineData("Hợp đồng thuê nhà", "NHÀ thuê", true)]
    [InlineData("Đơn xin việc", "don", true)]
    [InlineData("Tài liệu 27-09-2026", "27-09", true)]
    [InlineData("Tài liệu 27-09-2026", "hop", false)]
    [InlineData("anything", "", true)]
    public void Search_ignores_case_and_diacritics(string name, string query, bool expected) =>
        Assert.Equal(expected, TextSearch.Matches(name, query));
}
