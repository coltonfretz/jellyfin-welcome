using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Welcome.Services;

/// <summary>
/// Everything that goes to Discord: "someone wants in" pings to the admin's private
/// channel, and the "New on (server)" posts (<see cref="NewArrivals"/>) to a channel
/// everyone can see. One place checks webhook addresses and sends, so both behave the same.
/// </summary>
public sealed class DiscordNotifier
{
    // A Discord that doesn't answer shouldn't hold anything up for long (.NET's default
    // is 100 s).
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // Leave out null fields (a movie without a poster sends no "thumbnail" at all).
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly IHttpClientFactory _http;
    private readonly ILogger<DiscordNotifier> _logger;

    public DiscordNotifier(IHttpClientFactory http, ILogger<DiscordNotifier> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Only real Discord webhooks are ever posted to; anything else counts as "not set".</summary>
    public static bool IsWebhook(string? url) =>
        url is not null
        && (url.StartsWith("https://discord.com/api/webhooks/", StringComparison.Ordinal)
            || url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.Ordinal));

    /// <summary>Pings the admin that someone asked to join. False if it didn't go out.</summary>
    public Task<bool> NotifyRequestAsync(Invite inv, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        // No public address set: no link (a bare path is useless in Discord).
        var baseUrl = cfg.PublicUrl.Trim().TrimEnd('/');
        var who = Clean(inv.Name ?? "Someone");
        var label = Clean(inv.Label);
        var lines = new List<string> { $"🎟️ **{who}** wants in on Jellyfin" + (label.Length > 0 && !label.Equals(who, StringComparison.OrdinalIgnoreCase) ? $" (invite for {label})" : string.Empty) };
        if (!string.IsNullOrWhiteSpace(inv.Note))
        {
            lines.Add($"> {Clean(inv.Note)}");
        }

        lines.Add(baseUrl.Length > 0
            ? $"[Approve or decline]({baseUrl}/web/#/configurationpage?name=Welcome)"
            : "Approve or decline in Dashboard > Plugins > Welcome.");
        return PostAsync(cfg.DiscordWebhookUrl, new { content = string.Join('\n', lines) }, ct);
    }

    /// <summary>
    /// Posts a message (content and/or embeds) to a webhook. Mentions are always off:
    /// names and notes are typed by a stranger with an invite link, and titles come from
    /// file metadata, so nothing may @everyone, ping a role or mention anyone.
    /// False if the webhook isn't set or the post failed (logged).
    /// </summary>
    public async Task<bool> PostAsync(string url, object message, CancellationToken ct)
    {
        if (!IsWebhook(url))
        {
            return false;
        }

        var body = JsonSerializer.SerializeToNode(message, Json)!.AsObject();
        body["allowed_mentions"] = new System.Text.Json.Nodes.JsonObject { ["parse"] = new System.Text.Json.Nodes.JsonArray() };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var resp = await _http.CreateClient().PostAsJsonAsync(url, body, timeout.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Welcome: Discord webhook returned {Status}", (int)resp.StatusCode);
            }

            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogWarning("Welcome: Discord webhook failed: {Message}", ex.Message);
            return false;
        }
    }

    // Single line, no markdown/link tricks, bounded length.
    private static string Clean(string s)
    {
        var flat = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        foreach (var ch in new[] { "\\", "*", "_", "~", "`", "|", ">", "[", "]", "@", "#" })
        {
            flat = flat.Replace(ch, "\\" + ch, StringComparison.Ordinal);
        }

        // Break auto-linking so a typed URL shows as text, not a clickable link.
        flat = flat.Replace("://", ":​//", StringComparison.Ordinal);
        return flat.Length > 120 ? flat[..120] + "…" : flat;
    }
}
