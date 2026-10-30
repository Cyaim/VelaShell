namespace VelaShell.ViewModels;

/// <summary>
/// 新建 / 编辑连接对话框右栏当前所处的步骤。
/// <para>
/// 一个插件页签承载几种相近的连接类型(数据库页签里的 MySQL / PostgreSQL / SQLite……)时,
/// 新建连接先在一屏卡片里选类型,再进表单;别的协议(SSH、Redis、S3……)全程只有表单。
/// </para>
/// </summary>
public enum ConnectionProfileStep
{
    /// <summary>表单(分页页签 + 字段)。没有类型可选的协议恒在这一步。</summary>
    Form = 0,

    /// <summary>选类型的卡片。只在页签带类型选择、且还没确认过类型时出现。</summary>
    PickType = 1
}
