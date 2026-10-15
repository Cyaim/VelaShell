using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VelaShell.Core.Data;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.ViewModels;

namespace VelaShell.Views;

/// <summary>只展示凭据摘要,通过明确确认发起一次密码填入。</summary>
public partial class SharedPasswordPickerDialog : Window
{
    private readonly ISharedCredentialRepository? _repository;
    private readonly SharedPasswordFillRequest? _request;
    private bool _closed;
    private bool _busy;

    /// <summary>初始化主题化选择窗口。</summary>
    public SharedPasswordPickerDialog()
    {
        InitializeComponent();
        WindowChrome.Apply(this, WindowChromeKind.Dialog);
        Closing += (_, _) => _request?.Invalidate();
        Closed += (_, _) =>
        {
            _closed = true;
            _request?.Invalidate();
        };
        Deactivated += (_, _) => _request?.Invalidate();
    }

    internal SharedPasswordPickerDialog(ISharedCredentialRepository repository, SharedPasswordFillRequest request, string target)
        : this()
    {
        _repository = repository;
        _request = request;
        DataContext = new SharedPasswordPickerViewModel(target);
        Opened += (_, _) =>
        {
            SearchInput.Focus();
            FireAndForget.Run(() => ((SharedPasswordPickerViewModel)DataContext).LoadAsync(repository));
        };
    }

    private void Fill_Click(object? sender, RoutedEventArgs e) => FireAndForget.Run(FillAsync);

    private async Task FillAsync()
    {
        if (_busy || _request is null || _repository is null
            || DataContext is not SharedPasswordPickerViewModel { SelectedItem: { } selected } vm)
        {
            return;
        }
        _busy = true;
        FillButton.IsEnabled = false;
        SearchInput.IsEnabled = false;
        CredentialList.IsEnabled = false;
        PasswordFillResult result = await _request.FillAsync(_repository, selected.Id);
        if (_closed)
        {
            return;
        }
        if (result == PasswordFillResult.Queued)
        {
            this.PostClose(true);
            return;
        }
        vm.Status = Strings.Get(result switch
        {
            PasswordFillResult.Expired => "PasswordFill_Expired",
            PasswordFillResult.InvalidPassword => "PasswordFill_Invalid",
            PasswordFillResult.Unavailable => "PasswordFill_Unavailable",
            _ => "PasswordFill_ReadFailed",
        });
        _busy = false;
        FillButton.IsEnabled = result != PasswordFillResult.Expired;
        SearchInput.IsEnabled = result != PasswordFillResult.Expired;
        CredentialList.IsEnabled = result != PasswordFillResult.Expired;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _request?.Invalidate();
        this.PostClose(false);
    }
    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            this.BeginWindowMoveDrag(e);
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _request?.Invalidate();
            this.PostClose(false);
            e.Handled = true;
            return;
        }
        // Enter 不等同于批准:列表导航或搜索输入不得隐式发送秘密。
        base.OnKeyDown(e);
    }
}
