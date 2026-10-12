using System.Collections.ObjectModel;
using System.Security;
using ReactiveUI;
using ReactiveUI.Primitives;
using VelaShell.Core.Credentials;
using VelaShell.Core.Models;
using VelaShell.Core.Net;
using VelaShell.Core.Resources;
using VelaShell.Presentation.Services;
using VelaShell.Security;

namespace VelaShell.ViewModels;

/// <summary>编辑框的结果:要保存的凭据,以及保存后引用它的全部连接。</summary>
/// <param name="Credential">要保存的凭据。</param>
/// <param name="Users">保存后引用它的连接 Id。</param>
public sealed record SharedCredentialEditResult(SharedCredential Credential, IReadOnlyCollection<Guid> Users);

/// <summary>「使用这条凭据的连接」清单里的一行。</summary>
public sealed class SharedCredentialUserItem : ReactiveObject
{
    /// <summary>以一条连接配置建一行。</summary>
    /// <param name="profile">连接配置。</param>
    /// <param name="isChecked">初始是否勾选(已经引用这条凭据)。</param>
    public SharedCredentialUserItem(SessionProfile profile, bool isChecked)
    {
        Profile = profile;
        IsChecked = isChecked;
        Detail = string.IsNullOrWhiteSpace(profile.Username)
            ? $"{HostAddress.FormatEndPoint(profile.Host, profile.Port)} · {SharedCredentialEditorViewModel.TypeLabel(profile)}"
            : $"{HostAddress.FormatTarget(profile.Username, profile.Host, profile.Port)} · {SharedCredentialEditorViewModel.TypeLabel(profile)}";
    }

    /// <summary>这一行对应的连接配置。</summary>
    public SessionProfile Profile { get; }

    /// <summary>连接名称。</summary>
    public string Name => Profile.Name;

    /// <summary>目标与协议(<c>user@host:port · SSH</c>)。</summary>
    public string Detail { get; }

    /// <summary>是否让这条连接使用凭据。</summary>
    public bool IsChecked
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>凭据当前的认证方式能不能给它用(FTP 与插件协议只认密码)。不能用的行置灰。</summary>
    public bool IsCompatible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;

    /// <summary>是否被筛选框筛出(行本身一直在,只是藏起来 —— 勾选状态不因筛选丢失)。</summary>
    public bool IsVisible
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = true;
}

/// <summary>「从连接复制」下拉的一项。</summary>
/// <param name="Profile">连接配置。</param>
public sealed record SharedCredentialCopySource(SessionProfile Profile)
{
    /// <summary>下拉里显示连接名与登录目标。</summary>
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Profile.Username)
            ? $"{Profile.Name} ({Profile.Host})"
            : $"{Profile.Name} ({Profile.Username}@{Profile.Host})";
}

/// <summary>
/// 新建 / 编辑一条共享凭据(#550)的对话框视图模型:凭据本身,加上「哪些连接用它」。
/// </summary>
/// <remarks>
/// <para>
/// 两块放在同一个框里,是因为这个功能的用法就是两步连着做:建一条凭据,再把一批连接挂上去。
/// 「勾选凭据相同的连接」负责把已有连接迁过来 —— 一百台设备原本各存一份同样的密码,
/// 一键勾出来,比一条条打开连接配置去改省事得多。
/// </para>
/// <para>
/// 从连接配置页的「新建…」进来时不显示连接清单(<see cref="ShowUsers" /> 为 false):
/// 那时要挂的就是正在编辑的那一条,由连接配置页自己保存。
/// </para>
/// </remarks>
public sealed class SharedCredentialEditorViewModel : ReactiveObject
{
    private readonly Guid _id;
    private readonly HashSet<Guid> _originalUsers;

    /// <summary>创建编辑框视图模型。</summary>
    /// <param name="existing">要编辑的凭据;新建时为 null。</param>
    /// <param name="profiles">全部连接配置(仓储读出的,密码已解密)。</param>
    /// <param name="showUsers">是否显示「使用这条凭据的连接」清单。</param>
    /// <param name="draft">新建时的预填值(连接配置页把当前表单里的凭据带过来);编辑时忽略。</param>
    public SharedCredentialEditorViewModel(
        SharedCredential? existing,
        IReadOnlyList<SessionProfile> profiles,
        bool showUsers = true,
        SharedCredential? draft = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        IsNew = existing is null;
        ShowUsers = showUsers;
        _id = existing?.Id ?? Guid.NewGuid();
        SharedCredential source = existing ?? draft ?? new SharedCredential();
        Name = source.Name;
        Username = source.Username;
        AuthMethod = Enum.IsDefined(source.AuthMethod) ? source.AuthMethod : AuthMethod.Password;
        Password = SecureStringConvert.FromPlaintext(source.Password);
        PrivateKeyPath = source.PrivateKeyPath;
        PrivateKeyPassphrase = source.PrivateKeyPassphrase;
        CertificatePath = source.CertificatePath;
        Notes = source.Notes;

        List<SessionProfile> ordered = [.. profiles.OrderBy(static p => p.Name, StringComparer.OrdinalIgnoreCase)];
        _originalUsers = [.. ordered.Where(p => SharedCredentialService.SharedCredentialIdOf(p) == _id).Select(static p => p.Id)];
        foreach (SessionProfile profile in ordered)
        {
            Users.Add(new(profile, _originalUsers.Contains(profile.Id)));
        }
        // 能拿来复制的:凭据就存在连接自己身上的那些(引用了别的凭据的,复制过来也只是空的)。
        foreach (SessionProfile profile in ordered.Where(static p => p.CredentialSource is null
                                                                     && (CredentialMaterial.HasInline(p) || p.AuthMethod == AuthMethod.Agent)))
        {
            CopySources.Add(new(profile));
        }
        foreach (SharedCredentialUserItem item in Users)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(SharedCredentialUserItem.IsChecked) or nameof(SharedCredentialUserItem.IsCompatible))
                {
                    this.RaisePropertyChanged(nameof(UsersSummary));
                }
            };
        }
        RefreshCompatibility();

        IObservable<bool> canSave = this.WhenAnyValue(
            x => x.NameError,
            x => x.SecretError,
            (nameError, secretError) => nameError is null && secretError is null);
        SaveCommand = ReactiveCommand.Create(BuildResult, canSave);
        CancelCommand = ReactiveCommand.Create<SharedCredentialEditResult?>(() => null);
        SelectAuthMethodCommand = ReactiveCommand.Create<AuthMethod>(method => AuthMethod = method);
        TogglePasswordVisibilityCommand = ReactiveCommand.Create(() => { ShowPassword = !ShowPassword; });
        CheckMatchingCommand = ReactiveCommand.Create(CheckMatching);
    }

    /// <summary>是否在新建(决定标题)。</summary>
    public bool IsNew { get; }

    /// <summary>对话框标题。</summary>
    public string Title => Strings.Get(IsNew ? "SharedCred_NewTitle" : "SharedCred_EditTitle");

    /// <summary>是否显示「使用这条凭据的连接」清单。</summary>
    public bool ShowUsers { get; }

    /// <summary>名称。</summary>
    public string Name
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(NameError));
        }
    } = string.Empty;

    /// <summary>名称为空时的提示;没问题为 null。</summary>
    public string? NameError => string.IsNullOrWhiteSpace(Name) ? Strings.Get("SharedCred_ErrNameRequired") : null;

    /// <summary>用户名;留空 = 由使用它的连接各自提供。</summary>
    public string Username
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>认证方式。</summary>
    public AuthMethod AuthMethod
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsPasswordAuth));
            this.RaisePropertyChanged(nameof(IsKeyAuth));
            this.RaisePropertyChanged(nameof(IsCertAuth));
            this.RaisePropertyChanged(nameof(IsAgentAuth));
            this.RaisePropertyChanged(nameof(ShowsPrivateKeyFields));
            this.RaisePropertyChanged(nameof(SecretError));
            RefreshCompatibility();
        }
    }

    /// <summary>是否为密码认证。</summary>
    public bool IsPasswordAuth => AuthMethod == AuthMethod.Password;

    /// <summary>是否为私钥认证。</summary>
    public bool IsKeyAuth => AuthMethod == AuthMethod.PrivateKey;

    /// <summary>是否为证书认证。</summary>
    public bool IsCertAuth => AuthMethod == AuthMethod.Certificate;

    /// <summary>是否为 SSH Agent 认证。</summary>
    public bool IsAgentAuth => AuthMethod == AuthMethod.Agent;

    /// <summary>私钥与口令两个字段是否可见(私钥认证与证书认证都要)。</summary>
    public bool ShowsPrivateKeyFields => IsKeyAuth || IsCertAuth;

    /// <summary>密码,以 SecureString 承载。</summary>
    public SecureString? Password
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SecretError));
        }
    }

    /// <summary>是否明文显示密码。</summary>
    public bool ShowPassword
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>私钥文件路径。</summary>
    public string? PrivateKeyPath
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SecretError));
        }
    }

    /// <summary>私钥口令。</summary>
    public string? PrivateKeyPassphrase
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>OpenSSH 用户证书文件路径。</summary>
    public string? CertificatePath
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SecretError));
        }
    }

    /// <summary>备注。</summary>
    public string? Notes
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>认证材料缺了什么;齐全(或 Agent 认证)时为 null。</summary>
    /// <remarks>
    /// 不齐全就不让存:一条没有密码的共享凭据挂上一百条连接,结果是一百条连接一起弹登录框。
    /// </remarks>
    public string? SecretError => AuthMethod switch
    {
        AuthMethod.Password when Password is not { Length: > 0 } => Strings.Get("SharedCred_ErrPasswordRequired"),
        AuthMethod.PrivateKey when string.IsNullOrWhiteSpace(PrivateKeyPath) => Strings.Get("SharedCred_ErrKeyRequired"),
        AuthMethod.Certificate when string.IsNullOrWhiteSpace(CertificatePath) => Strings.Get("Profile_ErrCertRequired"),
        AuthMethod.Certificate when string.IsNullOrWhiteSpace(PrivateKeyPath) => Strings.Get("Profile_ErrCertKeyRequired"),
        _ => null
    };

    /// <summary>「使用这条凭据的连接」清单(全部连接,勾选的即为使用者)。</summary>
    public ObservableCollection<SharedCredentialUserItem> Users { get; } = [];

    /// <summary>清单的筛选文本(按名称、主机、用户名)。</summary>
    public string FilterText
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            ApplyFilter();
        }
    } = string.Empty;

    /// <summary>清单下方的一句统计:勾了几条。</summary>
    public string UsersSummary => Strings.Format("SharedCred_UsersSummary", Users.Count(static u => u.IsChecked && u.IsCompatible));

    /// <summary>上一次「勾选凭据相同的连接」的结果说明;还没点过为 null。</summary>
    public string? MatchStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>「从连接复制」的候选:凭据就存在自己身上的连接。</summary>
    public ObservableCollection<SharedCredentialCopySource> CopySources { get; } = [];

    /// <summary>是否有可复制的连接(没有就不显示那个下拉)。</summary>
    public bool HasCopySources => CopySources.Count > 0;

    /// <summary>选中一条连接 → 把它的用户名与认证材料填进表单(名称空着时顺带用它的用户名)。</summary>
    public SharedCredentialCopySource? SelectedCopySource
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (value?.Profile is { } profile)
            {
                CopyFrom(profile);
            }
        }
    }

    /// <summary>保存:交出凭据与使用者。</summary>
    public ReactiveCommand<RxVoid, SharedCredentialEditResult> SaveCommand { get; }

    /// <summary>取消:交出 null。</summary>
    public ReactiveCommand<RxVoid, SharedCredentialEditResult?> CancelCommand { get; }

    /// <summary>认证方式分段按钮。</summary>
    public ReactiveCommand<AuthMethod, RxVoid> SelectAuthMethodCommand { get; }

    /// <summary>切换密码明文显示。</summary>
    public ReactiveCommand<RxVoid, RxVoid> TogglePasswordVisibilityCommand { get; }

    /// <summary>勾选认证材料与表单完全相同的连接(把已有连接迁过来)。</summary>
    public ReactiveCommand<RxVoid, RxVoid> CheckMatchingCommand { get; }

    /// <summary>按表单当前内容组出一条凭据(只带当前认证方式用得上的字段)。</summary>
    /// <returns>凭据。</returns>
    public SharedCredential BuildCredential()
    {
        bool keyBased = AuthMethod is AuthMethod.PrivateKey or AuthMethod.Certificate;
        return new()
        {
            Id = _id,
            Name = Name.Trim(),
            Username = Username.Trim(),
            AuthMethod = AuthMethod,
            Password = IsPasswordAuth ? SecureStringConvert.ToPlaintext(Password) : null,
            PrivateKeyPath = keyBased ? TrimToNull(PrivateKeyPath) : null,
            PrivateKeyPassphrase = keyBased && !string.IsNullOrEmpty(PrivateKeyPassphrase) ? PrivateKeyPassphrase : null,
            CertificatePath = IsCertAuth ? TrimToNull(CertificatePath) : null,
            Notes = TrimToNull(Notes)
        };
    }

    /// <summary>连接类型的简短标签(清单里显示)。</summary>
    internal static string TypeLabel(SessionProfile profile) => profile.ConnectionType switch
    {
        ConnectionType.Plugin => profile.PluginProtocolId ?? "Plugin",
        _ => profile.ConnectionType.ToString()
    };

    private SharedCredentialEditResult BuildResult()
    {
        SharedCredential credential = BuildCredential();
        IReadOnlyCollection<Guid> users = ShowUsers
            ? [.. Users.Where(static u => u.IsChecked && u.IsCompatible).Select(static u => u.Profile.Id)]
            // 清单没显示时谁在用它就不归这个框管:原样交回,保存时不增不减。
            : [.. _originalUsers];
        return new(credential, users);
    }

    private void CopyFrom(SessionProfile profile)
    {
        Username = profile.Username;
        AuthMethod = Enum.IsDefined(profile.AuthMethod) ? profile.AuthMethod : AuthMethod.Password;
        Password = SecureStringConvert.FromPlaintext(profile.Password);
        PrivateKeyPath = profile.PrivateKeyPath;
        PrivateKeyPassphrase = profile.PrivateKeyPassphrase;
        CertificatePath = profile.CertificatePath;
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = string.IsNullOrWhiteSpace(profile.Username) ? profile.Name : profile.Username;
        }
    }

    private void CheckMatching()
    {
        SharedCredential credential = BuildCredential();
        int added = 0;
        foreach (SharedCredentialUserItem item in Users)
        {
            if (!item.IsChecked && SharedCredentialService.HasSameCredential(credential, item.Profile))
            {
                item.IsChecked = true;
                added++;
            }
        }
        MatchStatus = Strings.Format("SharedCred_MatchResult", added);
    }

    private void RefreshCompatibility()
    {
        // 构造早期 Users 还是空的;认证方式变了才需要重算。
        SharedCredential probe = new() { AuthMethod = AuthMethod };
        foreach (SharedCredentialUserItem item in Users)
        {
            item.IsCompatible = SharedCredentialService.IsCompatible(probe, item.Profile);
        }
    }

    private void ApplyFilter()
    {
        string filter = FilterText.Trim();
        foreach (SharedCredentialUserItem item in Users)
        {
            item.IsVisible = filter.Length == 0
                             || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                             || item.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
