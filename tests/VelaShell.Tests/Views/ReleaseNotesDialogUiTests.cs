using System.Net;
using NSubstitute;
using VelaShell.Core.Services;
using VelaShell.Core.Data;
using VelaShell.Services;
using VelaShell.ViewModels;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using VelaShell.Core.Resources;
using VelaShell.Services.Update;
using VelaShell.Tests.TestSupport;
using VelaShell.Views;
using VelaShell.Views.Settings;

namespace VelaShell.Tests.Views;

[TestClass]
public sealed class ReleaseNotesDialogUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ReleaseNotesDialogUiTests).Assembly);

    [TestMethod]
    public Task LoadingThenSuccess_ShowsVersionAndCompleteScrollableSelectableBody() => _session.RunOnUiAsync(async () =>
    {
        TaskCompletionSource<ReleaseNotes?> completion = new();
        var source = new StubSource(_ => completion.Task);
        var dialog = new ReleaseNotesDialog(source);
        dialog.Show();
        try
        {
            Assert.IsTrue(dialog.IsVisible);
            Assert.IsTrue(dialog.FindControl<StackPanel>("LoadingPanel")!.IsVisible);
            Assert.AreEqual(1, source.Calls);
            string body = "## What's Changed\n" + string.Join('\n', Enumerable.Range(0, 200).Select(i => $"* 更新 {i} by @joesdu in #{i}"));
            completion.SetResult(new ReleaseNotes("1.7.1", body, DateTimeOffset.Parse("2026-10-16T03:11:41Z"),
                "https://github.com/owner/repo/releases/tag/1.7.1"));
            await WaitUntilAsync(() => dialog.FindControl<ScrollViewer>("NotesScroll")!.IsVisible);
            Assert.AreEqual("1.7.1", dialog.FindControl<SelectableTextBlock>("VersionText")!.Text);
            // 「在 GitHub 上查看」打开的是 API 给的发布页,而不是对话框里另拼的仓库地址。
            Assert.AreEqual(new Uri("https://github.com/owner/repo/releases/tag/1.7.1"), dialog.ReleaseUri);
            MarkdownRenderer renderer = dialog.FindControl<MarkdownRenderer>("BodyMarkdown")!;
            renderer.SelectAll();
            Assert.Contains("What's Changed", renderer.SelectedText!);
            Assert.Contains("更新 199", renderer.SelectedText!);
            Assert.DoesNotContain("##", renderer.SelectedText!);
            Assert.IsTrue(dialog.FindControl<TextBlock>("PublishedText")!.IsVisible);
            Assert.IsTrue(dialog.FindControl<Button>("GitHubButton")!.IsEnabled);
            dialog.UpdateLayout();
            ScrollViewer scroll = dialog.FindControl<ScrollViewer>("NotesScroll")!;
            Assert.IsGreaterThan(scroll.Viewport.Height, scroll.Extent.Height);
            Assert.IsTrue(dialog.FindControl<Button>("CloseButton")!.IsEffectivelyVisible);
            Assert.IsFalse(dialog.FindControl<StackPanel>("LoadingPanel")!.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EmptyStates_AreDistinct(bool hasRelease) => _session.RunOnUiAsync(() =>
    {
        var dialog = new ReleaseNotesDialog(new StubSource(_ =>
            Task.FromResult(hasRelease ? new ReleaseNotes("1.7.1", " \n") : null)));
        dialog.Show();
        try
        {
            Assert.AreEqual(Strings.Get(hasRelease ? "ReleaseNotes_EmptyBody" : "ReleaseNotes_NoRelease"),
                dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.AreEqual(hasRelease, dialog.FindControl<Border>("ReleaseHeader")!.IsVisible);
            Assert.IsFalse(dialog.FindControl<Button>("RetryButton")!.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    public Task FailureThenRetry_DoesNotStartConcurrentRequests() => _session.RunOnUiAsync(async () =>
    {
        TaskCompletionSource<ReleaseNotes?> completion = new();
        int attempts = 0;
        var source = new StubSource(_ => ++attempts == 1
            ? Task.FromException<ReleaseNotes?>(new HttpRequestException("offline")) : completion.Task);
        var dialog = new ReleaseNotesDialog(source);
        dialog.Show();
        try
        {
            Button retry = dialog.FindControl<Button>("RetryButton")!;
            Assert.IsTrue(retry.IsVisible);
            Assert.AreEqual(Strings.Get("ReleaseNotes_LoadFailed"), dialog.FindControl<TextBlock>("StatusText")!.Text);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(2, source.Calls);
            Assert.IsFalse(retry.IsVisible);
            completion.SetResult(new ReleaseNotes("latest", "notes"));
            await WaitUntilAsync(() => dialog.FindControl<ScrollViewer>("NotesScroll")!.IsVisible);
            MarkdownRenderer renderer = dialog.FindControl<MarkdownRenderer>("BodyMarkdown")!;
            renderer.SelectAll();
            Assert.AreEqual("notes", renderer.SelectedText?.Trim());
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    [DataRow("https://github.com/owner/repo/releases/tag/v1.7.1", true)]
    [DataRow("http://github.com/owner/repo/releases/tag/v1.7.1", false)]
    [DataRow("javascript:alert(1)", false)]
    [DataRow(null, false)]
    public Task GitHubButton_OnlyOffersHttpsReleasePage(string? htmlUrl, bool enabled) => _session.RunOnUiAsync(() =>
    {
        var dialog = new ReleaseNotesDialog(new StubSource(_ =>
            Task.FromResult<ReleaseNotes?>(new("v1.7.1", "notes", null, htmlUrl))));
        dialog.Show();
        try
        {
            Assert.AreEqual(enabled, dialog.FindControl<Button>("GitHubButton")!.IsEnabled);
            Assert.AreEqual(enabled ? new Uri(htmlUrl!) : null, dialog.ReleaseUri);
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, "ReleaseNotes_RateLimited")]
    [DataRow(HttpStatusCode.TooManyRequests, "ReleaseNotes_RateLimited")]
    [DataRow(HttpStatusCode.InternalServerError, "ReleaseNotes_LoadFailed")]
    public Task HttpFailure_RateLimitHasItsOwnMessage(HttpStatusCode status, string expectedKey) => _session.RunOnUiAsync(() =>
    {
        // 匿名 API 每小时 60 次,用完了 GitHub 回 403/429:这时让用户"检查网络"是误导。
        var dialog = new ReleaseNotesDialog(new StubSource(_ =>
            Task.FromException<ReleaseNotes?>(new HttpRequestException("failed", null, status))));
        dialog.Show();
        try
        {
            Assert.AreEqual(Strings.Get(expectedKey), dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.IsTrue(dialog.FindControl<Button>("RetryButton")!.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    public Task SourceInternalTimeout_ShowsRetryableError() => _session.RunOnUiAsync(async () =>
    {
        // 发布源自己的超时同样以取消异常的形式冒出来;只有关窗引起的取消才静默,超时必须进入可重试的错误态。
        var source = new GitHubReleaseSource("https://github.com/owner/repo", new HangingHandler())
        {
            RequestTimeout = TimeSpan.FromMilliseconds(100)
        };
        var dialog = new ReleaseNotesDialog(source);
        dialog.Show();
        try
        {
            Button retry = dialog.FindControl<Button>("RetryButton")!;
            await WaitUntilAsync(() => retry.IsVisible);
            Assert.AreEqual(Strings.Get("ReleaseNotes_LoadFailed"), dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.IsFalse(dialog.FindControl<StackPanel>("LoadingPanel")!.IsVisible);
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task CloseOrEscape_CancelsRequestAndIgnoresLateResult(bool escape) => _session.RunOnUiAsync(async () =>
    {
        TaskCompletionSource<ReleaseNotes?> completion = new();
        var source = new StubSource(_ => completion.Task);
        var dialog = new ReleaseNotesDialog(source);
        dialog.Show();
        if (escape)
        {
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await WaitUntilAsync(() => !dialog.IsVisible);
        }
        else
        {
            dialog.Close();
        }
        Assert.IsTrue(source.Token.IsCancellationRequested);
        completion.SetResult(new ReleaseNotes("late", "late body"));
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        Assert.IsNull(dialog.FindControl<MarkdownRenderer>("BodyMarkdown")!.DocumentUpdate);
    });

    [TestMethod]
    public Task AboutButton_Enabled_UsesSettingsOwnerAndPreventsDuplicateWindows() => _session.RunOnUiAsync(async () =>
    {
        var about = new AboutPage();
        var owner = new Window { Content = about };
        var source = new StubSource(_ => Task.FromResult<ReleaseNotes?>(new("1.7.1", "notes")));
        owner.Show();
        try
        {
            Assert.IsTrue(about.FindControl<Button>("ChangelogButton")!.IsEnabled);
            Task showing = about.ShowChangelogAsync(source);
            await about.ShowChangelogAsync(source);
            ReleaseNotesDialog dialog = owner.OwnedWindows.OfType<ReleaseNotesDialog>().Single();
            Assert.AreSame(owner, dialog.Owner);
            Assert.AreEqual(1, source.Calls);
            dialog.Close();
            await showing;
            Task second = about.ShowChangelogAsync(source);
            owner.OwnedWindows.OfType<ReleaseNotesDialog>().Single().Close();
            await second;
            Assert.AreEqual(2, source.Calls);
        }
        finally
        {
            foreach (Window child in owner.OwnedWindows.ToArray())
            {
                child.Close();
            }
            owner.Close();
        }
    });

    [TestMethod]
    public Task ParameterlessWindow_DoesNotLoadOrFail() => _session.RunOnUiAsync(() =>
    {
        var dialog = new ReleaseNotesDialog();
        dialog.Show();
        Assert.IsFalse(dialog.FindControl<Button>("RetryButton")!.IsVisible);
        dialog.Close();
    });

    [TestMethod]
    [DataRow("https://example.com/image.png")]
    [DataRow("http://example.com/image.png")]
    [DataRow("file:///C:/secret.png")]
    [DataRow("data:image/png;base64,aGVsbG8=")]
    [DataRow("avares://VelaShell/secret.png")]
    [DataRow("../secret.png")]
    [DataRow("//example.com/image.png")]
    public Task Markdown_DropsAllImageNodesBeforeRendering(string url) => _session.RunOnUiAsync(() =>
    {
        string body = $"## 标题\n\n![图片说明]({url})\n\n![引用][image]\n\n[image]: {url}\n\n[![嵌套]({url})](https://example.com)";
        MarkdownDocument document = ReleaseNotesDialog.ParseNotes(body);
        Assert.DoesNotContain(link => link.IsImage, document.Descendants<LinkInline>());
        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(new("1.7.1", body))));
        dialog.Show();
        try
        {
            MarkdownRenderer renderer = dialog.FindControl<MarkdownRenderer>("BodyMarkdown")!;
            Assert.IsEmpty(renderer.GetVisualDescendants().OfType<Image>());
            renderer.SelectAll();
            Assert.Contains("图片说明", renderer.SelectedText!);
            Assert.Contains("嵌套", renderer.SelectedText!);
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    [DataRow("https://github.com/joesdu/VelaShell/pull/626", true)]
    [DataRow("http://example.com", false)]
    [DataRow("file:///C:/secret", false)]
    [DataRow("javascript:alert(1)", false)]
    [DataRow("data:text/html,test", false)]
    [DataRow("avares://VelaShell/test", false)]
    [DataRow("//example.com/test", false)]
    [DataRow("../test", false)]
    [DataRow("mailto:user@example.com", false)]
    public void Markdown_OnlyAbsoluteHttpsLinksRemainInteractive(string url, bool allowed)
    {
        Assert.AreEqual(allowed, ReleaseNotesDialog.IsAllowedLink(url));
        MarkdownDocument document = ReleaseNotesDialog.ParseNotes($"[可读文字]({url})");
        Assert.AreEqual(allowed, document.Descendants<LinkInline>().Any());
    }

    [TestMethod]
    public Task Markdown_GitHubGeneratedNotes_BareUrlsBecomeLinksAndTablesRender() => _session.RunOnUiAsync(() =>
    {
        // GitHub 自动生成的发布说明就是这个样子:PR 与 Full Changelog 都是裸 URL。
        const string body = """
            ## What's Changed
            * 更新日志对话框 by @joesdu in https://github.com/joesdu/VelaShell/pull/635
            * 不安全的裸地址 http://example.com/x 与 www.example.com 只能是文字

            | 平台 | 状态 |
            |---|---|
            | Windows | 可用 |

            - [x] 已完成
            - [ ] ~~作废~~

            **Full Changelog**: https://github.com/joesdu/VelaShell/compare/v1.7.0...v1.7.1
            """;
        MarkdownDocument document = ReleaseNotesDialog.ParseNotes(body);
        Assert.AreSequenceEqual(
            ["https://github.com/joesdu/VelaShell/pull/635", "https://github.com/joesdu/VelaShell/compare/v1.7.0...v1.7.1"],
            document.Descendants<LinkInline>().Select(link => link.Url).ToArray());
        Assert.IsNotEmpty(document.Descendants<Markdig.Extensions.Tables.Table>());
        Assert.IsNotEmpty(document.Descendants<Markdig.Extensions.TaskLists.TaskList>());
        Assert.Contains(e => e.DelimiterChar == '~', document.Descendants<EmphasisInline>());

        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(new("v1.7.1", body))));
        dialog.Show();
        try
        {
            MarkdownRenderer renderer = dialog.FindControl<MarkdownRenderer>("BodyMarkdown")!;
            dialog.UpdateLayout();
            Assert.IsNotEmpty(renderer.GetVisualDescendants().OfType<MarkdownTableGrid>(), "管道表格应渲染成表格");
            renderer.SelectAll();
            string text = renderer.SelectedText!;
            Assert.Contains("https://github.com/joesdu/VelaShell/pull/635", text);
            Assert.Contains("http://example.com/x", text);
            Assert.Contains("Full Changelog", text);
            Assert.DoesNotContain("|", text, "管道表格应渲染成表格,而不是原样的竖线");
            Assert.DoesNotContain("~~", text);
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    public Task Markdown_RendersHeadingsEmphasisAndLinks_AndFollowsTheme() => _session.RunOnUiAsync(() =>
    {
        string body = "## 更新与下载\n\n- **下载速率**持续刷新，支持 `Tailscale SSH`。\n- [#626](https://github.com/joesdu/VelaShell/pull/626)\n\n<script>alert(1)</script><img src=\"file:///C:/secret.png\">";
        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(new("1.7.1", body))));
        dialog.Show();
        try
        {
            MarkdownRenderer renderer = dialog.FindControl<MarkdownRenderer>("BodyMarkdown")!;
            renderer.SelectAll();
            Assert.Contains("更新与下载", renderer.SelectedText!);
            Assert.DoesNotContain("**", renderer.SelectedText!);
            Assert.DoesNotContain("##", renderer.SelectedText!);
            Assert.Contains(b => b.Classes.Contains("Heading2Block"), renderer.GetVisualDescendants().OfType<Border>());
            Assert.IsNotEmpty(renderer.GetVisualDescendants().OfType<MarkdownTextBlock>());
            Assert.IsEmpty(renderer.GetVisualDescendants().OfType<Image>());
            Assert.IsNull(renderer.LinkContextMenu);
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                dialog.RequestedThemeVariant = variant;
                dialog.UpdateLayout();
                var expected = (IBrush)dialog.FindResource(variant, "VelaTextPrimary")!;
                Assert.IsTrue(renderer.GetVisualDescendants().OfType<MarkdownTextBlock>().All(block =>
                    Equals(expected, block.Foreground)));
            }
        }
        finally
        {
            dialog.Close();
        }
    });

    [TestMethod]
    [DataRow("1.2.0", "v1.4.0", "v1.4.0", true)]
    [DataRow("1.4.0", "v1.4.0", "v1.4.0", false)]
    [DataRow("1.5.0-beta.1", "v1.4.0", "v1.5.0-beta.1", false)]
    public Task VersionSelection_ShowsCurrentOrLatestStable(string current, string latest, string expected, bool newer) =>
        _session.RunOnUiAsync(() =>
        {
            var source = new StubSource(_ => Task.FromResult<ReleaseNotes?>(new(latest, "latest")))
            {
                CurrentNotes = version => Task.FromResult<ReleaseNotes?>(new("v" + version, "current"))
            };
            var dialog = new ReleaseNotesDialog(source, current);
            dialog.Show();
            try
            {
                Assert.AreEqual(expected, dialog.FindControl<SelectableTextBlock>("VersionText")!.Text);
                Assert.AreEqual(Strings.Get(newer ? "ReleaseNotes_Latest" : "ReleaseNotes_CurrentVersion"),
                    dialog.FindControl<TextBlock>("VersionBadge")!.Text);
                Assert.AreEqual(expected == latest ? 0 : 1, source.CurrentCalls);
                Assert.IsFalse(dialog.FindControl<Button>("UpdateButton")!.IsVisible);
            }
            finally { dialog.Close(); }
        });

    [TestMethod]
    public Task LatestFailure_KeepsCurrentNotesAndOffersRetry() => _session.RunOnUiAsync(() =>
    {
        var source = new StubSource(_ => Task.FromException<ReleaseNotes?>(new HttpRequestException("offline")))
        {
            CurrentNotes = _ => Task.FromResult<ReleaseNotes?>(new("v1.2.0", "current"))
        };
        var dialog = new ReleaseNotesDialog(source, "1.2.0");
        dialog.Show();
        try
        {
            Assert.AreEqual("v1.2.0", dialog.FindControl<SelectableTextBlock>("VersionText")!.Text);
            Assert.IsTrue(dialog.FindControl<ScrollViewer>("NotesScroll")!.IsVisible);
            Assert.AreEqual(Strings.Get("ReleaseNotes_CheckFailed"), dialog.FindControl<TextBlock>("CheckStatusText")!.Text);
            Assert.IsTrue(dialog.FindControl<Button>("RetryButton")!.IsVisible);
        }
        finally { dialog.Close(); }
    });

    [TestMethod]
    public Task MissingCurrentRelease_DoesNotShowOlderStableAsCurrent() => _session.RunOnUiAsync(() =>
    {
        var source = new StubSource(_ => Task.FromResult<ReleaseNotes?>(new("v1.2.0", "old")))
        {
            CurrentNotes = _ => Task.FromResult<ReleaseNotes?>(null)
        };
        var dialog = new ReleaseNotesDialog(source, "2.0.0-beta.1");
        dialog.Show();
        try
        {
            Assert.IsFalse(dialog.FindControl<Border>("ReleaseHeader")!.IsVisible);
            Assert.AreEqual(Strings.Get("ReleaseNotes_NoCurrentRelease"), dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.IsFalse(dialog.FindControl<Button>("UpdateButton")!.IsVisible);
        }
        finally { dialog.Close(); }
    });

    [TestMethod]
    public Task UnknownVersion_DoesNotRequestOrOfferInstallation() => _session.RunOnUiAsync(() =>
    {
        var source = new StubSource(_ => throw new AssertFailedException("不应请求未知版本"));
        var dialog = new ReleaseNotesDialog(source, (string?)null);
        dialog.Show();
        try
        {
            Assert.AreEqual(0, source.Calls);
            Assert.AreEqual(Strings.Get("ReleaseNotes_UnknownVersion"), dialog.FindControl<TextBlock>("StatusText")!.Text);
            Assert.IsFalse(dialog.FindControl<Button>("UpdateButton")!.IsVisible);
        }
        finally { dialog.Close(); }
    });

    [TestMethod]
    public Task StoreBuild_ShowsLogsButNoInstallAction() => _session.RunOnUiAsync(() =>
    {
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CurrentVersion.Returns("1.0.0");
        updates.IsStoreManaged.Returns(true);
        SettingsViewModel vm = new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>(), updateService: updates);
        var source = new StubSource(_ => Task.FromResult<ReleaseNotes?>(new("v2.0.0", "notes")));
        var dialog = new ReleaseNotesDialog(source, vm);
        dialog.Show();
        try
        {
            Assert.AreEqual("v2.0.0", dialog.FindControl<SelectableTextBlock>("VersionText")!.Text);
            Assert.IsFalse(dialog.FindControl<Button>("UpdateButton")!.IsVisible);
            Assert.AreEqual(Strings.Get("SetAbout_StoreManagedUpdates"), dialog.FindControl<TextBlock>("UpdateStatusText")!.Text);
        }
        finally { dialog.Close(); }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task NewRelease_ActionReflectsInstallationMode(bool selfUpdate) => _session.RunOnUiAsync(() =>
    {
        UpdateManifest manifest = UpdateManifest.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            version = "2.0.0",
            tag = "v2.0.0",
            assets = new Dictionary<string, object>
            {
                [UpdateManifest.CurrentRid()!] = new { name = "package.zip", sha256 = new string('a', 64), size = 12 }
            }
        }));
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CurrentVersion.Returns("1.0.0");
        updates.CanSelfUpdate.Returns(selfUpdate);
        SettingsViewModel vm = new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>(), updateService: updates);
        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(
            new("v2.0.0", "notes", HtmlUrl: "https://github.com/owner/repo/releases/tag/v2.0.0", Manifest: manifest))), vm);
        dialog.Show();
        try
        {
            Button button = dialog.FindControl<Button>("UpdateButton")!;
            Assert.IsTrue(button.IsVisible);
            Assert.IsTrue(button.IsEnabled);
            Assert.AreEqual(Strings.Get(selfUpdate && !OperatingSystem.IsLinux()
                ? "ReleaseNotes_UpdateNow" : "ReleaseNotes_OpenReleasePage"), button.Content);
            Assert.AreEqual(new Uri("https://github.com/owner/repo/releases/tag/v2.0.0"), dialog.ReleaseUri);
        }
        finally { dialog.Close(); }
    });

    [TestMethod]
    [OSCondition(OperatingSystems.Windows | OperatingSystems.OSX)]
    public Task UpdateButton_ReadyTargetRestarts_FailedDownloadCanRetry() => _session.RunOnUiAsync(async () =>
    {
        UpdateManifest manifest = UpdateManifest.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            version = "2.0.0",
            tag = "v2.0.0",
            assets = new Dictionary<string, object>
            {
                [UpdateManifest.CurrentRid()!] = new { name = "package.zip", sha256 = new string('a', 64), size = 12 }
            }
        }));
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CurrentVersion.Returns("1.0.0");
        updates.CanSelfUpdate.Returns(true);
        int attempts = 0;
        updates.DownloadUpdateAsync(manifest, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++attempts == 1)
                {
                    return Task.FromException(new IOException("offline"));
                }
                updates.ReadyUpdate.Returns(manifest);
                return Task.CompletedTask;
            });
        SettingsViewModel vm = new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>(), updateService: updates);
        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(
            new("v2.0.0", "notes", Manifest: manifest))), vm);
        dialog.Show();
        try
        {
            Button button = dialog.FindControl<Button>("UpdateButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !vm.IsUpdating);
            Assert.AreEqual(Strings.Get("SetAbout_DownloadFailed"), vm.UpdateStatus);
            Assert.IsTrue(button.IsEnabled, "失败后必须能够重试");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !vm.IsUpdating);
            Assert.AreEqual(Strings.Get("SetAbout_RestartToUpdate"), button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !vm.IsUpdating);
            updates.Received(1).ApplyUpdateAndRestart();
            await updates.DidNotReceive().CheckForUpdateAsync();
        }
        finally { dialog.Close(); }
    });

    [TestMethod]
    [OSCondition(OperatingSystems.Windows | OperatingSystems.OSX)]
    public Task UpdateClick_DownloadsDisplayedSnapshot_ContinuesAfterClose() => _session.RunOnUiAsync(async () =>
    {
        string rid = UpdateManifest.CurrentRid()!;
        UpdateManifest manifest = UpdateManifest.Parse($$$"""
            {"version":"2.0.0","tag":"v2.0.0","assets":{"{{{rid}}}":{"name":"package.zip","sha256":"{{{new string('a', 64)}}}","size":12} } }
            """);
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CurrentVersion.Returns("1.0.0");
        updates.CanSelfUpdate.Returns(true);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.DownloadUpdateAsync(manifest, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(_ => completion.Task);
        SettingsViewModel vm = new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>(), updateService: updates);
        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(
            new("v2.0.0", "notes", Manifest: manifest))), vm);
        dialog.Show();
        Button button = dialog.FindControl<Button>("UpdateButton")!;
        try
        {
            Assert.IsTrue(button.IsVisible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsTrue(vm.IsUpdating);
            Assert.IsFalse(button.IsEnabled);
            Assert.IsTrue(dialog.FindControl<TextBlock>("DownloadNotice")!.IsVisible);
            dialog.Close();
            Assert.IsTrue(vm.IsUpdating, "关闭日志不能取消已经启动的下载");
            updates.ReadyUpdate.Returns(manifest);
            completion.SetResult();
            await WaitUntilAsync(() => !vm.IsUpdating);
            Assert.IsTrue(vm.UpdateReady);
            await updates.DidNotReceive().CheckForUpdateAsync();
            updates.DidNotReceive().ApplyUpdateAndRestart();
            await updates.Received(1).DownloadUpdateAsync(manifest,
                Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            completion.TrySetResult();
            dialog.Close();
        }
    });

    [TestMethod]
    [OSCondition(OperatingSystems.Windows | OperatingSystems.OSX)]
    public Task DownloadProgress_RefreshesStatusTextWithoutProbingSelfUpdate() => _session.RunOnUiAsync(async () =>
    {
        UpdateManifest manifest = CreateManifest("2.0.0");
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CurrentVersion.Returns("1.0.0");
        updates.CanSelfUpdate.Returns(true);
        IProgress<UpdateDownloadProgress>? progress = null;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        updates.DownloadUpdateAsync(manifest, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                progress = call.Arg<IProgress<UpdateDownloadProgress>>();
                return completion.Task;
            });
        SettingsViewModel vm = new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>(), updateService: updates);
        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(
            new("v2.0.0", "notes", Manifest: manifest))), vm);
        dialog.Show();
        try
        {
            dialog.FindControl<Button>("UpdateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNotNull(progress);
            int statusChanges = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.UpdateStatus))
                {
                    statusChanges++;
                }
            };
            // 能否自更新要在应用目录里建删探测文件;下载开始后进度一秒刷新多次,每次刷新都不能再探测。
            updates.ClearReceivedCalls();
            for (int i = 1; i <= 20; i++)
            {
                progress.Report(new UpdateDownloadProgress(i * 1_000, 20_000, 4_096));
            }
            await WaitUntilAsync(() => statusChanges >= 20);
            TextBlock status = dialog.FindControl<TextBlock>("UpdateStatusText")!;
            Assert.IsTrue(status.IsVisible);
            Assert.AreEqual(vm.UpdateStatus, status.Text, "状态文字仍要跟着下载进度刷新");
            _ = updates.DidNotReceive().CanSelfUpdate;
        }
        finally
        {
            completion.TrySetResult();
            dialog.Close();
        }
    });

    [TestMethod]
    [OSCondition(OperatingSystems.Windows | OperatingSystems.OSX)]
    [DataRow(false)]
    [DataRow(true)]
    public Task DownloadNotice_HiddenOnceDownloadFailsOrIsRejected(bool otherUpdateReady) => _session.RunOnUiAsync(async () =>
    {
        UpdateManifest manifest = CreateManifest("2.0.0");
        IUpdateService updates = Substitute.For<IUpdateService>();
        updates.CurrentVersion.Returns("1.0.0");
        updates.CanSelfUpdate.Returns(true);
        if (otherUpdateReady)
        {
            // 另一个版本已经下载就绪:立即更新被当场拒绝,根本不会开始下载。
            updates.ReadyUpdate.Returns(CreateManifest("1.5.0"));
        }
        updates.DownloadUpdateAsync(manifest, Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("offline")));
        SettingsViewModel vm = new(Substitute.For<ISettingsService>(), Substitute.For<IThemeService>(), updateService: updates);
        var dialog = new ReleaseNotesDialog(new StubSource(_ => Task.FromResult<ReleaseNotes?>(
            new("v2.0.0", "notes", Manifest: manifest))), vm);
        dialog.Show();
        try
        {
            Button button = dialog.FindControl<Button>("UpdateButton")!;
            Assert.AreEqual(Strings.Get("ReleaseNotes_UpdateNow"), button.Content);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => !vm.IsUpdating);
            // 点击处理在下载结束之后还有收尾,先让派发队列里排着的工作跑完。
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            Assert.IsFalse(dialog.FindControl<TextBlock>("DownloadNotice")!.IsVisible, "没有进行中的下载,不能再提示关闭窗口后下载仍会继续");
            TextBlock status = dialog.FindControl<TextBlock>("UpdateStatusText")!;
            Assert.IsTrue(status.IsVisible, "失败或被拒绝的原因要留在窗口里");
            Assert.AreEqual(Strings.Get(otherUpdateReady ? "ReleaseNotes_UpdateConflict" : "SetAbout_DownloadFailed"), status.Text);
            Assert.IsTrue(button.IsEnabled);
            await updates.Received(otherUpdateReady ? 0 : 1).DownloadUpdateAsync(manifest,
                Arg.Any<IProgress<UpdateDownloadProgress>>(), Arg.Any<CancellationToken>());
        }
        finally { dialog.Close(); }
    });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static UpdateManifest CreateManifest(string version) => UpdateManifest.Parse(System.Text.Json.JsonSerializer.Serialize(new
    {
        version,
        tag = "v" + version,
        assets = new Dictionary<string, object>
        {
            [UpdateManifest.CurrentRid()!] = new { name = "package.zip", sha256 = new string('a', 64), size = 12 }
        }
    }));

    /// <summary>一直不回应、直到请求被取消的处理器(模拟连上了却迟迟不给响应的 API)。</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class StubSource(Func<CancellationToken, Task<ReleaseNotes?>> getNotes) : IReleaseNotesSource
    {
        public int Calls { get; private set; }
        public int CurrentCalls { get; private set; }
        public Func<string, Task<ReleaseNotes?>>? CurrentNotes { get; init; }
        public CancellationToken Token { get; private set; }

        public Task<ReleaseNotes?> GetLatestReleaseNotesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = cancellationToken;
            return getNotes(cancellationToken);
        }

        public Task<ReleaseNotes?> GetReleaseNotesForVersionAsync(string version, CancellationToken cancellationToken = default)
        {
            Calls++;
            CurrentCalls++;
            Token = cancellationToken;
            return CurrentNotes?.Invoke(version) ?? getNotes(cancellationToken);
        }
    }
}
