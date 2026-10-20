using System.Diagnostics;
using System.Globalization;
using System.Net;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Reactive;
using Avalonia.Styling;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TextMateSharp.Grammars;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.Services.Update;

namespace VelaShell.Views;

/// <summary>按需读取并展示最近一次 GitHub 发布的更新说明。</summary>
public partial class ReleaseNotesDialog : Window
{
    private readonly IReleaseNotesSource? _source;
    private CancellationTokenSource? _request;
    private bool _closed;
    private Uri? _releaseUri;
    private readonly List<IDisposable> _skinSubscriptions = [];

    /// <summary>
    /// GitHub 风格的 Markdown:自动生成的发布说明里 PR 与 Full Changelog 都是裸 URL,
    /// 不开自动链接就是一串点不动的文字;表格、删除线、任务列表同样常见。
    /// 自动链接生成的也是 <see cref="LinkInline" />,照样经过 <see cref="ParseNotes" /> 的 https 过滤。
    /// </summary>
    private static readonly MarkdownPipeline NotesPipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .Build();

    /// <summary>仅初始化界面,供设计器及窗口外框测试使用,不联网。</summary>
    public ReleaseNotesDialog()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Styles.Add(new Style(x => x.OfType<Image>().Class("Link"))
        {
            Setters = { new Setter(AsyncImageLoader.HandlersProperty, Array.Empty<AsyncImageLoaderHandler>()) }
        });
        foreach ((string key, string token) in new[]
        {
            ("BorderColor", "VelaBorderPrimary"), ("BorderBrush", "VelaBorderPrimary"),
            ("SecondaryCardBackgroundColor", "VelaBgInput"), ("CardBackgroundColor", "VelaBgHover"),
            ("ForegroundColor", "VelaTextPrimary"), ("CodeInlineColor", "VelaAccent"),
            ("QuoteBorderColor", "VelaAccent")
        })
        {
            _skinSubscriptions.Add(this.GetResourceObservable(token).Subscribe(new AnonymousObserver<object?>(value =>
            {
                if (value != null && (!Resources.TryGetValue(key, out object? current) || !Equals(current, value)))
                {
                    Resources[key] = value;
                }
            })));
        }
        BodyMarkdown.LinkClick += OnMarkdownLinkClicked;
        ActualThemeVariantChanged += (_, _) => UpdateCodeTheme();
        UpdateCodeTheme();
    }

    /// <summary>窗口打开后通过指定发布源加载更新说明。</summary>
    public ReleaseNotesDialog(IReleaseNotesSource source) : this() => _source = source;

    /// <summary>「在 GitHub 上查看」要打开的发布页;没有可用地址时为 null。</summary>
    internal Uri? ReleaseUri => _releaseUri;

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Screens.ScreenFromWindow(this) is { } screen)
        {
            Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling);
        }
        FireAndForget.Run(LoadAsync);
    }

    private async Task LoadAsync()
    {
        if (_source == null || _request != null || _closed)
        {
            return;
        }
        using CancellationTokenSource request = new();
        _request = request;
        LoadingPanel.IsVisible = true;
        LoadingRing.IsIndeterminate = true;
        StatusText.IsVisible = false;
        ReleaseHeader.IsVisible = false;
        GitHubButton.IsEnabled = false;
        _releaseUri = null;
        NotesScroll.IsVisible = false;
        RetryButton.IsVisible = false;
        try
        {
            ReleaseNotes? notes = await _source.GetLatestReleaseNotesAsync(request.Token);
            if (_closed)
            {
                return;
            }
            VersionText.Text = notes?.Tag;
            ReleaseHeader.IsVisible = notes != null;
            PublishedText.IsVisible = notes?.PublishedAt != null;
            PublishedText.Text = notes?.PublishedAt is { } publishedAt
                ? Strings.Format("ReleaseNotes_PublishedAt", publishedAt.ToLocalTime().ToString("d", CultureInfo.CurrentCulture))
                : null;
            // 发布页地址取 API 给的 html_url,不在这里另拼一份仓库地址;照样只放行 https。
            _releaseUri = notes?.HtmlUrl is { } htmlUrl && IsAllowedLink(htmlUrl) ? new Uri(htmlUrl) : null;
            GitHubButton.IsEnabled = _releaseUri != null;
            if (notes == null || string.IsNullOrWhiteSpace(notes.Body))
            {
                StatusText.Text = Strings.Get(notes == null ? "ReleaseNotes_NoRelease" : "ReleaseNotes_EmptyBody");
                StatusText.IsVisible = true;
            }
            else
            {
                BodyMarkdown.DocumentUpdate = new MarkdownDocumentUpdate.Full(ParseNotes(notes.Body));
                NotesScroll.IsVisible = true;
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // 关闭窗口引起的取消不显示失败;发布源内部超时则进入错误状态,允许重试。
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[ReleaseNotes] {ex}");
            if (!_closed)
            {
                // 匿名 API 每小时只有 60 次,用完 GitHub 回 403/429;这时让用户去查网络是误导。
                bool rateLimited = ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests };
                StatusText.Text = Strings.Get(rateLimited ? "ReleaseNotes_RateLimited" : "ReleaseNotes_LoadFailed");
                StatusText.IsVisible = true;
                RetryButton.IsVisible = true;
            }
        }
        finally
        {
            _request = null;
            if (!_closed)
            {
                LoadingPanel.IsVisible = false;
                LoadingRing.IsIndeterminate = false;
            }
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _request?.Cancel();
        foreach (IDisposable subscription in _skinSubscriptions)
        {
            subscription.Dispose();
        }
        base.OnClosed(e);
    }

    internal static MarkdownDocument ParseNotes(string body)
    {
        MarkdownDocument document = Markdown.Parse(body, NotesPipeline);
        // 在控件生成前去掉图片节点,所有资源协议都不能触发读取。保留图片替代文字。
        foreach (LinkInline link in document.Descendants<LinkInline>().ToArray())
        {
            if (link.IsImage || !IsAllowedLink(link.Url))
            {
                while (link.FirstChild is { } child)
                {
                    child.Remove();
                    link.InsertBefore(child);
                }
                link.Remove();
            }
        }
        foreach (AutolinkInline link in document.Descendants<AutolinkInline>().ToArray())
        {
            if (link.IsEmail || !IsAllowedLink(link.Url))
            {
                link.ReplaceBy(new LiteralInline(link.Url));
            }
        }
        return document;
    }

    internal static bool IsAllowedLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    private void UpdateCodeTheme() => BodyMarkdown.CodeBlockColorTheme =
        ActualThemeVariant == ThemeVariant.Dark ? ThemeName.DarkPlus : ThemeName.LightPlus;

    private void OnMarkdownLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;
        if (e.HRef is { } uri && IsAllowedLink(uri.OriginalString))
        {
            FireAndForget.Run(async () => await Launcher.LaunchUriAsync(uri));
        }
    }

    private void GitHub_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(async () =>
    {
        if (_releaseUri is { } uri)
        {
            await Launcher.LaunchUriAsync(uri);
        }
    });

    private void Retry_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(LoadAsync);

    private void Close_Click(object? sender, RoutedEventArgs e) => this.PostClose();

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            this.PostClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }
}
