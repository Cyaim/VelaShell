using VelaShell.Core.Models;

namespace VelaShell.Core.Tests.Models;

/// <summary>目录收藏的路径规整与旧配置兼容行为。</summary>
[TestClass]
public sealed class RemoteDirectoryBookmarkTests
{
    [TestMethod]
    public void RemotePathIsCollapsedToAnAbsolutePosixPath()
    {
        Assert.AreEqual("/var/www/site", RemoteDirectoryBookmark.NormalizeRemotePath("/var//www/./logs/../site/"));
        Assert.AreEqual("/", RemoteDirectoryBookmark.NormalizeRemotePath("../../"));
    }

    /// <summary>POSIX 目录名可以以空格开头或结尾:修剪掉就指向了另一个目录。</summary>
    [TestMethod]
    public void RemotePathKeepsLeadingAndTrailingSpacesOfSegments() =>
        Assert.AreEqual("/srv/ data /logs ", RemoteDirectoryBookmark.NormalizeRemotePath("/srv/ data /logs "));

    [TestMethod]
    public void LocalPathPreservesTheFileSystemRoot()
    {
        string root = Path.GetPathRoot(Path.GetFullPath(Environment.CurrentDirectory))!;
        Assert.AreEqual(root, RemoteDirectoryBookmark.NormalizeLocalPath(root));
    }

    [TestMethod]
    public void LegacyProfileWithoutBookmarksGetsAnEmptyCollection()
    {
        SessionProfile? profile = System.Text.Json.JsonSerializer.Deserialize<SessionProfile>("{\"name\":\"legacy\"}");

        Assert.IsNotNull(profile);
        Assert.IsNotNull(profile.DirectoryBookmarks);
        Assert.IsEmpty(profile.DirectoryBookmarks);
    }

    [TestMethod]
    public void BookmarkCloneDoesNotShareTheMutableModel()
    {
        var source = new RemoteDirectoryBookmark
        {
            Name = "site",
            LocalPath = "C:\\site",
            RemotePath = "/srv/site",
        };

        RemoteDirectoryBookmark copy = source.Clone();
        copy.Name = "changed";

        Assert.AreEqual("site", source.Name);
        Assert.AreNotSame(source, copy);
        Assert.AreEqual(source.Id, copy.Id);
    }
}
