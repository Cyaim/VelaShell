namespace VelaShell.Core.Models;

/// <summary>
/// 一个连接配置下的本地目录与远程目录配对收藏。
/// </summary>
/// <remarks>
/// 收藏不是访问历史:只有用户明确保存的稳定路径才进入这里。路径不要求在保存时存在,
/// 因为服务器可能暂时离线,或者收藏是从另一台机器导入的。
/// </remarks>
public sealed class RemoteDirectoryBookmark
{
    /// <summary>收藏名称的最大长度。</summary>
    public const int MaxNameLength = 200;

    /// <summary>本地和远程路径的最大长度。</summary>
    public const int MaxPathLength = 4096;

    /// <summary>收藏的稳定标识。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>用户可读的收藏名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>本地目录的规范化绝对路径。</summary>
    public string LocalPath { get; set; } = string.Empty;

    /// <summary>远程目录的规范化 POSIX 绝对路径。</summary>
    public string RemotePath { get; set; } = "/";

    /// <summary>返回不共享可变字符串容器的副本。</summary>
    public RemoteDirectoryBookmark Clone() => new()
    {
        Id = Id,
        Name = Name,
        LocalPath = LocalPath,
        RemotePath = RemotePath,
    };

    /// <summary>
    /// 规范化本地路径。目录是否存在由应用收藏时检查,这里不访问文件系统。
    /// </summary>
    public static string NormalizeLocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path.Trim());
        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        return string.Equals(fullPath, root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal)
            ? root
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 规范化远程 POSIX 路径:统一根目录、重复分隔符、<c>.</c> 与 <c>..</c>。
    /// </summary>
    public static string NormalizeRemotePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string value = path.Trim();
        var segments = new List<string>();
        foreach (string segment in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }
                    continue;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }
}
