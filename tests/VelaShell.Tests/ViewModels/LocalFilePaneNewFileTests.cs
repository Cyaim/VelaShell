using ReactiveUI.Primitives;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.ViewModels;

namespace VelaShell.Tests.ViewModels;

/// <summary>
/// 本地栏右键「新建文件」(维护者 2026-10-25 定):与远端栏同一套名字校验;同名的已经在了就报错,
/// 绝不把已有的文件清空。
/// </summary>
[TestClass]
[TestCategory("LocalFilePane")]
public sealed class LocalFilePaneNewFileTests
{
    [TestMethod]
    public async Task NewFile_CreatesAnEmptyFileInTheCurrentFolder_AndListsIt()
    {
        string root = NewRoot();
        try
        {
            using var vm = new LocalFilePaneViewModel(new TransferOptions()) { PromptForText = (_, _) => Task.FromResult<string?>(" notes.txt ") };
            await vm.NavigateToAsync(root);

            await vm.NewFileCommand.Execute().FirstAsync();

            string created = Path.Combine(root, "notes.txt");
            Assert.IsTrue(File.Exists(created), "名字两头的空白去掉。");
            Assert.AreEqual(0, new FileInfo(created).Length);
            Assert.Contains(entry => entry.Name == "notes.txt", vm.Entries, "建完刷新,新文件就在列表里。");
            Assert.IsNull(vm.ErrorMessage);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task NewFile_WithTheNameOfAnExistingFile_ReportsIt_AndLeavesTheFileAlone()
    {
        string root = NewRoot();
        try
        {
            string existing = Path.Combine(root, "keep.txt");
            File.WriteAllText(existing, "important");
            using var vm = new LocalFilePaneViewModel(new TransferOptions()) { PromptForText = (_, _) => Task.FromResult<string?>("keep.txt") };
            await vm.NavigateToAsync(root);

            await vm.NewFileCommand.Execute().FirstAsync();

            Assert.IsNotNull(vm.ErrorMessage);
            Assert.AreEqual("important", File.ReadAllText(existing));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task NewFile_RejectsANameThatIsAPath()
    {
        string root = NewRoot();
        try
        {
            using var vm = new LocalFilePaneViewModel(new TransferOptions()) { PromptForText = (_, _) => Task.FromResult<string?>("../escape.txt") };
            await vm.NavigateToAsync(root);

            await vm.NewFileCommand.Execute().FirstAsync();

            Assert.AreEqual(Strings.Get("KeySvc_InvalidName"), vm.ErrorMessage);
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string NewRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"vela-newfile-{Guid.NewGuid():N}")).FullName;
}
