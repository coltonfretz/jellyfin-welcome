using System.Collections.Concurrent;
using System.Text.Json;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Welcome.Services;

/// <summary>
/// Posts "New on (server name)" to a Discord channel everyone can see when movies and
/// episodes land in the library.
///
/// WHY here and not Seerr's Discord bot: Seerr always prints "Requested By &lt;username&gt;"
/// on its posts (no setting to hide it), and people don't always want everyone seeing what
/// they asked for. Jellyfin only knows that something arrived, not who asked, so a name
/// can't leak. It also covers things nobody requested.
///
/// Shape: additions are queued and posted as one message once the library has been quiet
/// for a while, so a season pack becomes "Show: 10 new episodes", not ten posts, and
/// metadata/posters have time to arrive first. Every announced title is remembered, so a
/// quality upgrade (new file, same episode) is not announced twice. Titles are only
/// remembered once Discord has taken the post; a failed post is tried again after the
/// next quiet spell (up to <see cref="MaxTries"/> times), so a Discord outage doesn't
/// silently swallow a batch.
/// </summary>
public sealed class NewArrivals : IHostedService, IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);     // post after this long with no new arrivals
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(45);   // ...but never hold a batch longer than this
    private const int MaxRemembered = 5000;
    private const int MaxEmbeds = 10;                                       // Discord's limit per message
    private const int MaxTries = 3;                                         // posts of one batch before giving up

    private readonly ILibraryManager _library;
    private readonly IServerApplicationHost _host;
    private readonly DiscordNotifier _discord;
    private readonly ILogger<NewArrivals> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _queue = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private Timer? _timer;
    private DateTime _lastAdd = DateTime.MinValue;
    private DateTime _firstAdd = DateTime.MinValue;
    private int _failures;                                                  // failed posts of the current batch

    public NewArrivals(ILibraryManager library, IServerApplicationHost host, DiscordNotifier discord, ILogger<NewArrivals> logger)
    {
        _library = library;
        _host = host;
        _discord = discord;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _flushLock.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // Virtual items are episodes Jellyfin knows exist but has no file for ("missing"),
        // not arrivals.
        if (e.Item is not (Movie or Episode) || e.Item.IsVirtualItem || !WebhookSet())
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_queue.IsEmpty)
        {
            _firstAdd = now;
        }

        _queue[e.Item.Id] = now;
        _lastAdd = now;
    }

    private async Task TickAsync()
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastAdd < Quiet && now - _firstAdd < MaxWait)
        {
            return;
        }

        if (!await _flushLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var ids = _queue.Keys.ToList();
            foreach (var id in ids)
            {
                _queue.TryRemove(id, out _);
            }

            if (await AnnounceAsync(ids, CancellationToken.None).ConfigureAwait(false))
            {
                _failures = 0;
            }
            else if (++_failures < MaxTries)
            {
                // Back in the queue, to go out after another quiet spell.
                var again = DateTime.UtcNow;
                foreach (var id in ids)
                {
                    _queue.TryAdd(id, again);
                }

                _firstAdd = _lastAdd = again;
            }
            else
            {
                _failures = 0;
                _logger.LogWarning("Welcome: gave up on announcing {Count} new items after {Tries} failed posts", ids.Count, MaxTries);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Welcome: new-arrivals post failed");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private static bool WebhookSet() => DiscordNotifier.IsWebhook(Plugin.Instance?.Configuration.NewArrivalsWebhookUrl);

    private bool InAnnouncedLibrary(BaseItem item, HashSet<Guid> allowed)
    {
        // Empty selection = every movie and TV library.
        return allowed.Count == 0 || _library.GetCollectionFolders(item).Any(f => allowed.Contains(f.Id));
    }

    // False only when there was something to post and the post failed.
    private async Task<bool> AnnounceAsync(List<Guid> ids, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var allowed = cfg.NewArrivalsLibraries
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
        var remembered = LoadRemembered();
        var fresh = new HashSet<string>();      // what this post announces

        var movies = new List<Movie>();
        var episodes = new List<Episode>();
        foreach (var id in ids)
        {
            var item = _library.GetItemById(id);
            if (item is null || item.IsVirtualItem || !InAnnouncedLibrary(item, allowed))
            {
                continue;   // gone again, or not a library we announce
            }

            var key = KeyFor(item);
            if (remembered.Contains(key) || !fresh.Add(key))
            {
                continue;   // announced before, or already in this batch (a better copy)
            }

            if (item is Movie m)
            {
                movies.Add(m);
            }
            else if (item is Episode ep && ep.Series is not null)
            {
                episodes.Add(ep);
            }
        }

        if (movies.Count == 0 && episodes.Count == 0)
        {
            return true;
        }

        var embeds = new List<object>();
        foreach (var m in movies.OrderBy(m => m.SortName))
        {
            embeds.Add(Embed(m, m.ProductionYear is int y ? $"{m.Name} ({y})" : m.Name, Genres(m)));
        }

        foreach (var show in episodes.GroupBy(e => e.Series!.Id).OrderBy(g => g.First().Series!.SortName))
        {
            var series = show.First().Series!;
            var eps = show.OrderBy(e => e.ParentIndexNumber).ThenBy(e => e.IndexNumber).ToList();
            embeds.Add(Embed(series, series.Name, EpisodeLine(eps)));
        }

        var extra = embeds.Count - MaxEmbeds;
        var content = $"🍿 **New on {ServerName}**" + (extra > 0 ? $"\n…plus {extra} more. Check Recently Added." : string.Empty);
        if (!await _discord.PostAsync(cfg.NewArrivalsWebhookUrl, new { content, embeds = embeds.Take(MaxEmbeds).ToArray() }, ct).ConfigureAwait(false))
        {
            return false;
        }

        remembered.UnionWith(fresh);
        SaveRemembered(remembered);
        _logger.LogInformation("Welcome: announced {Movies} movies and {Shows} shows", movies.Count, embeds.Count - movies.Count);
        return true;
    }

    private object Embed(BaseItem item, string title, string description)
    {
        var baseUrl = Plugin.Instance!.Configuration.PublicUrl.Trim().TrimEnd('/');
        // Jellyfin serves item images without a login, so Discord can fetch the poster
        // through the public address. The link opens the item for signed-in people.
        // Without a public address there's nothing Discord could reach: no link, no poster.
        var linked = baseUrl.Length > 0;
        return new
        {
            title = Trim(title, 240),
            description = Trim(description, 300),
            url = linked ? $"{baseUrl}/web/#/details?id={item.Id:N}&serverId={_host.SystemId}" : null,
            color = 0xE8743B,
            thumbnail = linked && item.HasImage(ImageType.Primary)
                ? new { url = $"{baseUrl}/Items/{item.Id:N}/Images/Primary?fillHeight=450&quality=90" }
                : null,
        };
    }

    private static string EpisodeLine(List<Episode> eps)
    {
        static string Code(Episode e) => $"S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00}";
        if (eps.Count == 1)
        {
            return $"New episode: {Code(eps[0])} {eps[0].Name}";
        }

        var seasons = eps.Select(e => e.ParentIndexNumber).Distinct().Count();
        return seasons == 1
            ? $"{eps.Count} new episodes: {Code(eps[0])} to {Code(eps[^1])}"
            : $"{eps.Count} new episodes across {seasons} seasons";
    }

    private static string Genres(BaseItem item) =>
        item.Genres is { Length: > 0 } g ? string.Join(" · ", g.Take(3)) : "New movie";

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "…" : s;

    // Identity that survives a file being replaced by a better copy (new item id).
    private static string KeyFor(BaseItem item)
    {
        string Ids(BaseItem b) =>
            b.GetProviderId(MetadataProvider.Tmdb) ?? b.GetProviderId(MetadataProvider.Tvdb)
            ?? b.GetProviderId(MetadataProvider.Imdb) ?? $"{b.Name}|{b.ProductionYear}";
        return item switch
        {
            Episode e when e.Series is not null => $"ep:{Ids(e.Series)}:{e.ParentIndexNumber}:{e.IndexNumber}",
            Movie m => $"movie:{Ids(m)}",
            _ => $"item:{item.Id:N}",
        };
    }

    private static string StatePath() => Path.Combine(Plugin.Instance!.DataFolderPath, "announced.json");

    private HashSet<string> LoadRemembered()
    {
        try
        {
            return File.Exists(StatePath())
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StatePath())) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning("Welcome: could not read announced.json, starting fresh: {Message}", ex.Message);
            return new();
        }
    }

    private void SaveRemembered(HashSet<string> keys)
    {
        // Oldest entries aren't tracked by age, so just cap the size; 5000 titles is years.
        // (A HashSet that's never had anything removed enumerates in insertion order, and
        // this one is rebuilt from the file each time, so Skip drops the oldest.)
        var list = keys.Count > MaxRemembered ? keys.Skip(keys.Count - MaxRemembered) : keys;
        Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
        var tmp = StatePath() + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list));
        File.Move(tmp, StatePath(), true);
    }

    private static string ServerName => Plugin.Instance!.Configuration.ServerName.Trim() is { Length: > 0 } n ? n : "Jellyfin";

    internal Task<bool> PostTestAsync(CancellationToken ct) =>
        _discord.PostAsync(
            Plugin.Instance!.Configuration.NewArrivalsWebhookUrl,
            new { content = $"🍿 New movies and shows on {ServerName} will show up here." },
            ct);
}
