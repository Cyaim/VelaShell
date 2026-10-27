using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Images;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>Adapts the Images endpoint to a single non-streaming chat response.</summary>
internal sealed class OpenAiImageChatClient(ImageClient client, bool requestBytes = false) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ChatMessage user = messages.LastOrDefault(message => message.Role == ChatRole.User)
            ?? throw new ArgumentException("Image generation requires a user prompt.", nameof(messages));
        if (user.Contents.Any(content => content is DataContent or UriContent))
            throw new NotSupportedException("Image attachments are not supported for image generation.");
        string prompt = string.Join("\n", user.Contents.OfType<TextContent>().Select(content => content.Text)).Trim();
        if (prompt.Length == 0) throw new ArgumentException("Image generation requires a user prompt.", nameof(messages));

        OpenAI.Images.ImageGenerationOptions? imageOptions = requestBytes
            ? new OpenAI.Images.ImageGenerationOptions { ResponseFormat = GeneratedImageFormat.Bytes }
            : null;
        GeneratedImage image = (await client.GenerateImageAsync(prompt, imageOptions, cancellationToken).ConfigureAwait(false)).Value;
        if (image.ImageBytes is null || image.ImageBytes.ToMemory().IsEmpty)
            throw new InvalidOperationException("The image endpoint did not return image bytes.");
        return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new DataContent(image.ImageBytes.ToMemory(), "image/png")]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
