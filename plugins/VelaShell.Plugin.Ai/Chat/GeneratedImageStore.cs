using System.Buffers;
using Microsoft.Extensions.AI;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>将生成图片的原始字节保存在插件数据目录中。</summary>
public sealed class GeneratedImageStore(string dataDirectory)
{
    private static readonly SearchValues<char> MyChars = SearchValues.Create("0123456789abcdef");

    /// <summary>每张图片允许保存的最大字节数。</summary>
    public const int MaxImageBytes = 20 * 1024 * 1024;
    /// <summary>每轮最多保存及展示的图片数。</summary>
    public const int MaxImagesPerTurn = 4;
    private readonly string _directory = Path.Combine(dataDirectory, "generated-images");

    /// <summary>校验并保存图片原始字节，返回可用于历史元数据的安全引用。</summary>
    public ChatGeneratedImage Save(DataContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string extension = Extension(content.MediaType) ?? throw new ArgumentException("Unsupported image media type.", nameof(content));
        ReadOnlyMemory<byte> data = content.Data;
        if (data.Length is 0 or > MaxImageBytes || !MatchesType(data.Span, content.MediaType))
        {
            throw new ArgumentException("Invalid image data or image exceeds the size limit.", nameof(content));
        }
        Directory.CreateDirectory(_directory);
        RejectLink(_directory);
        string name = Guid.NewGuid().ToString("N") + extension;
        using var stream = new FileStream(Path.Combine(_directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(data.Span);
        return new ChatGeneratedImage(name, content.MediaType);
    }

    /// <summary>从插件私有目录读取图片，拒绝非法引用、链接与不匹配的格式。</summary>
    public DataContent Load(ChatGeneratedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsValidReference(image))
        {
            throw new ArgumentException("Invalid image reference.", nameof(image));
        }
        RejectLink(_directory);
        string path = Path.Combine(_directory, image.FileName);
        RejectLink(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > MaxImageBytes)
        {
            throw new InvalidDataException("Invalid image size.");
        }
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (!MatchesType(bytes, image.MediaType))
        {
            throw new InvalidDataException("Image data does not match its media type.");
        }
        return new DataContent(bytes, image.MediaType);
    }

    internal static bool IsValidReference(ChatGeneratedImage? image)
    {
        if (image is null || Extension(image.MediaType) is not { } extension || image.FileName is not { } name ||
            name.Length != 32 + extension.Length || !name.EndsWith(extension, StringComparison.Ordinal))
        {
            return false;
        }
        return name.AsSpan(0, 32).IndexOfAnyExcept(MyChars) < 0;
    }

    private static string? Extension(string? mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => null
    };

    private static bool MatchesType(ReadOnlySpan<byte> data, string mediaType) => mediaType switch
    {
        "image/png" => data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => data.StartsWith(new byte[] { 255, 216, 255 }),
        "image/gif" => data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8),
        "image/webp" => data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Image store cannot follow a symbolic link.");
        }
    }
}
