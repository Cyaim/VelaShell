using System.Collections.ObjectModel;
using ReactiveUI;
using VelaShell.Core.Credentials;
using VelaShell.Core.Data;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;

namespace VelaShell.ViewModels;

/// <summary>只持有可显示的元数据;秘密由填入请求在确认后读取。</summary>
/// <remarks>创建不持有密码的选择器。</remarks>
public sealed class SharedPasswordPickerViewModel(string target) : ReactiveObject
{
    private List<SharedCredentialMetadata> _items = [];
    /// <summary>筛选后的密码凭据摘要。</summary>
    public ObservableCollection<SharedCredentialMetadata> Items { get; } = [];
    /// <summary>用户将要填入的目标会话名称。</summary>
    public string Target { get; } = target;

    /// <summary>凭据名称或用户名的筛选词。</summary>
    public string SearchQuery
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ApplyFilter();
        }
    } = string.Empty;

    /// <summary>当前候选;选中本身不发送。</summary>
    public SharedCredentialMetadata? SelectedItem
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>不含秘密的空态或错误说明。</summary>
    public string? Status
    {
        get;
        internal set => this.RaiseAndSetIfChanged(ref field, value);
    }

    internal async Task LoadAsync(ISharedCredentialRepository repository)
    {
        try
        {
            _items = (await repository.ListMetadataAsync())
                .Where(c => c.AuthMethod == AuthMethod.Password && c.HasPassword).ToList();
            ApplyFilter();
        }
        catch
        {
            Status = Strings.Get("PasswordFill_ReadFailed");
        }
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (SharedCredentialMetadata item in _items)
        {
            if (item.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
                || item.Username.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
            {
                Items.Add(item);
            }
        }
        SelectedItem = null;
        Status = Items.Count == 0 ? Strings.Get("PasswordFill_Empty") : null;
    }
}
