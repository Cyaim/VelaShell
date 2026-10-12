using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// <see cref="LocalFileEntry.ExtensionLabel" />:上传选择框图标视图里印在文件图标上的扩展名。
/// 图标上只放得下四个字符,截断的扩展名比不写更误导,所以超长的干脆不印。
/// </summary>
[TestClass]
public sealed class LocalFileEntryTests
{
    [TestMethod]
    [DataRow("back.pfx", "PFX")]
    [DataRow("photo.JPEG", "JPEG")]
    [DataRow("archive.tar.gz", "GZ")]
    [DataRow("readme", "")]
    [DataRow("trailing.", "")]
    [DataRow(".bashrc", "")]
    [DataRow("notes.markdown", "")]
    public void ExtensionLabel_IsTheUpperCasedShortExtension(string name, string expected) =>
        Assert.AreEqual(expected, new LocalFileEntry(name, "/x/" + name, false, 1, DateTime.Now).ExtensionLabel);

    [TestMethod]
    public void ExtensionLabel_IsEmptyForFoldersAndTheParentRow()
    {
        Assert.AreEqual("", new LocalFileEntry("photos.d", "/x/photos.d", true, 0, DateTime.Now).ExtensionLabel);
        Assert.AreEqual("", LocalFileEntry.CreateParent("/").ExtensionLabel);
    }
}
