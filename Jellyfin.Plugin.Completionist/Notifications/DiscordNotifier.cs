using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.Completionist.Notifications;

/// <summary>
/// Posts messages to a Discord webhook.
/// </summary>
public sealed class DiscordNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscordNotifier"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    public DiscordNotifier(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Checks that a URL looks like a Discord webhook, so the server never posts somewhere unexpected.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>Whether it's a Discord webhook URL.</returns>
    public static bool IsWebhookUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host is "discord.com" or "discordapp.com" or "ptb.discord.com" or "canary.discord.com")
            && uri.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.Ordinal);

    /// <summary>Posts messages in order, waiting out Discord's rate limit when asked to.</summary>
    /// <param name="webhookUrl">The webhook URL.</param>
    /// <param name="messages">The payloads.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SendAsync(string webhookUrl, IEnumerable<JsonObject> messages, CancellationToken cancellationToken)
    {
        if (!IsWebhookUrl(webhookUrl))
        {
            throw new ArgumentException("That isn't a Discord webhook URL. It should start with https://discord.com/api/webhooks/");
        }

        foreach (var message in messages)
        {
            var body = message.ToJsonString();
            await PostAsync(webhookUrl, () => new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Posts one message with an image file attached, which the message can show as <c>attachment://name</c>.</summary>
    /// <param name="webhookUrl">The webhook URL.</param>
    /// <param name="message">The payload.</param>
    /// <param name="image">The image bytes.</param>
    /// <param name="fileName">The name to upload it under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SendWithImageAsync(string webhookUrl, JsonObject message, byte[] image, string fileName, CancellationToken cancellationToken)
    {
        if (!IsWebhookUrl(webhookUrl))
        {
            throw new ArgumentException("That isn't a Discord webhook URL. It should start with https://discord.com/api/webhooks/");
        }

        var body = message.ToJsonString();
        await PostAsync(
            webhookUrl,
            () =>
            {
                var form = new MultipartFormDataContent
                {
                    { new StringContent(body, Encoding.UTF8, "application/json"), "payload_json" },
                };
                var file = new ByteArrayContent(image);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ImageType(fileName));
                form.Add(file, "files[0]", fileName);
                return form;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PostAsync(string webhookUrl, Func<HttpContent> makeContent, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        for (var attempt = 0; ; attempt++)
        {
            using var content = makeContent();
            using var response = await client.PostAsync(webhookUrl.Trim(), content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                await Task.Delay(await RetryAfterAsync(response, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"Discord answered {(int)response.StatusCode}: {Truncate(error, 300)}", null, response.StatusCode);
            }

            break;
        }
    }

    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan header)
        {
            return header;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("retry_after", out var seconds) && seconds.TryGetDouble(out var s))
            {
                return TimeSpan.FromSeconds(Math.Clamp(s, 0.5, 60));
            }
        }
        catch (JsonException)
        {
        }

        return TimeSpan.FromSeconds(5);
    }

    private static string ImageType(string fileName) => System.IO.Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
