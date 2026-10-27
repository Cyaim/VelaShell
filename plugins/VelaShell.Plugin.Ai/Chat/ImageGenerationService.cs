using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI.Images;

namespace VelaShell.Plugin.Ai.Chat;

/// <summary>One image request, with no URL fetching or automatic retries.</summary>
internal sealed class ImageGenerationService(ImageClient client, string model, bool requestBytes)
{
    private const int MaxInputBytes = 20 * 1024 * 1024;

    public async Task<DataContent> GenerateAsync(string prompt, IReadOnlyList<DataContent>? inputs, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        token.ThrowIfCancellationRequested();
        if (inputs is null || inputs.Count == 0)
        {
            OpenAI.Images.ImageGenerationOptions? options = requestBytes ? new() { ResponseFormat = GeneratedImageFormat.Bytes } : null;
            GeneratedImage image = (await client.GenerateImageAsync(prompt, options, token).ConfigureAwait(false)).Value;
            if (image.ImageBytes is null || image.ImageBytes.ToMemory().IsEmpty)
                throw new InvalidOperationException("The image endpoint did not return image bytes.");
            return new DataContent(image.ImageBytes.ToMemory(), "image/png");
        }
        if (inputs.Count > 3) throw new ArgumentException("At most three input images are supported.", nameof(inputs));
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(model), "model");
        multipart.Add(new StringContent(prompt), "prompt");
        if (requestBytes) multipart.Add(new StringContent("b64_json"), "response_format");
        foreach (DataContent input in inputs)
        {
            if (input is null || input.Data.IsEmpty || input.Data.Length > MaxInputBytes)
                throw new ArgumentException("Each input image must be nonempty and at most 20 MiB.", nameof(inputs));
            string extension = input.MediaType?.ToLowerInvariant() switch
            {
                "image/png" => "png",
                "image/jpeg" => "jpg",
                "image/webp" => "webp",
                _ => throw new ArgumentException("Input images must be PNG, JPEG, or WebP.", nameof(inputs))
            };
            var part = new ByteArrayContent(input.Data.ToArray());
            part.Headers.ContentType = new MediaTypeHeaderValue(input.MediaType);
            multipart.Add(part, "image[]", $"image-{multipart.Count()}.{extension}");
        }
        using var stream = new MemoryStream();
        await multipart.CopyToAsync(stream, token).ConfigureAwait(false);
        stream.Position = 0;
        using BinaryContent body = BinaryContent.Create(stream);
        ClientResult result = await client.GenerateImageEditsAsync(body, multipart.Headers.ContentType!.ToString(),
            new RequestOptions { CancellationToken = token }).ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(result.GetRawResponse().Content.ToMemory());
        if (!json.RootElement.TryGetProperty("data", out JsonElement data) || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("b64_json", out JsonElement encoded) || encoded.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(encoded.GetString()))
            throw new InvalidOperationException("The image endpoint did not return image bytes.");
        return new DataContent(Convert.FromBase64String(encoded.GetString()!), "image/png");
    }
}
