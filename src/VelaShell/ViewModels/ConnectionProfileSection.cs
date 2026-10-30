namespace VelaShell.ViewModels;

/// <summary>
/// 新建 / 编辑连接对话框右侧的分页。哪几页出现由协议决定(见
/// <see cref="ConnectionProfileViewModel.ShowTerminalSection" /> 等),「常规」恒在。
/// </summary>
public enum ConnectionProfileSection
{
    /// <summary>目标、认证、整理(名称 / 分组 / 标签),以及插件协议的常用字段。</summary>
    General = 0,

    /// <summary>认证后执行命令与会话级终端覆盖。只有 SSH 有终端。</summary>
    Terminal = 1,

    /// <summary>压缩与算法协商。SSH 与 SFTP 共用一条 SSH 连接,两者都有。</summary>
    SshOptions = 2,

    /// <summary>ssh-agent 转发与 X11 转发。只挂在交互式 shell 上,只有 SSH 有。</summary>
    Forwarding = 3,

    /// <summary>FTP 的默认打开路径,或插件协议落在「高级」页的调优字段(没有页键、标了 <c>IsAdvanced</c>)。</summary>
    Advanced = 4,

    /// <summary>
    /// 插件声明的自定义分页(「数据库」「SSL」……),介于「常规」与「高级」之间;
    /// 是哪一页看 <see cref="ConnectionProfileViewModel.SelectedPluginPageKey" />。
    /// </summary>
    PluginPage = 5
}
