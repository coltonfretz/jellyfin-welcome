using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Welcome.Services;

public enum InviteState
{
    /// <summary>Link sent, nobody has opened it and asked to join yet.</summary>
    Open,

    /// <summary>Someone asked to join; waiting on an admin.</summary>
    Pending,

    /// <summary>Approved; waiting for them to pick a username and password.</summary>
    Approved,

    /// <summary>Account created. The link is spent.</summary>
    Done,

    /// <summary>Declined or withdrawn by an admin. The link is spent.</summary>
    Closed,
}

/// <summary>One invite link and the request made through it (one person per link).</summary>
public sealed class Invite
{
    public string Code { get; set; } = string.Empty;

    /// <summary>Who the admin made it for ("Sam"), so the ping says who it is.</summary>
    public string Label { get; set; } = string.Empty;

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset Expires { get; set; }

    public InviteState State { get; set; }

    /// <summary>Name they typed when asking to join.</summary>
    public string? Name { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset? Requested { get; set; }

    /// <summary>
    /// SHA-256 of the secret the asking browser holds. Only that browser can finish
    /// the sign-up, so a forwarded or overheard invite link can't be used to claim the
    /// account an admin approved for someone else.
    /// </summary>
    public string? TokenHash { get; set; }

    public string? Username { get; set; }

    /// <summary>When the link was spent: account made (Done) or declined/withdrawn (Closed).</summary>
    public DateTimeOffset? Finished { get; set; }
}

/// <summary>
/// Invites, kept as one JSON file in the plugin's data folder. A handful of new people
/// a year doesn't warrant a database; every change rewrites the file atomically.
///
/// An invite that's over (account made, declined, or expired unused) is deleted
/// <see cref="KeepEnded"/> after it ended: the name, note and browser-token hash someone
/// left aren't needed once the account exists or the link is dead, and the Jellyfin
/// account is the record of who joined. The grace period is so a friend who reopens the
/// link that week still gets the "All set" screen with their username and app steps.
/// Pending and approved invites are kept, since they're still waiting on someone.
/// </summary>
public sealed class InviteStore
{
    private static readonly TimeSpan KeepEnded = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly ILogger<InviteStore> _logger;
    private List<Invite>? _invites;

    public InviteStore(ILogger<InviteStore> logger)
    {
        _logger = logger;
    }

    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "invites.json");

    public static string NewSecret(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public IReadOnlyList<Invite> All()
    {
        lock (_lock)
        {
            return Live().Select(Clone).ToList();
        }
    }

    public Invite? Find(string code)
    {
        lock (_lock)
        {
            return Live().FirstOrDefault(i => FixedEquals(i.Code, code)) is { } i ? Clone(i) : null;
        }
    }

    /// <summary>Applies a change to one invite under the lock and saves. Returns the updated copy.</summary>
    public Invite? Update(string code, Func<Invite, bool> change)
    {
        lock (_lock)
        {
            var inv = Live().FirstOrDefault(i => FixedEquals(i.Code, code));
            if (inv is null || !change(inv))
            {
                return null;
            }

            Save();
            return Clone(inv);
        }
    }

    public Invite Create(string label, int days)
    {
        lock (_lock)
        {
            var inv = new Invite
            {
                // 128 bits: not guessable, short enough to text.
                Code = NewSecret(16),
                Label = label,
                Created = DateTimeOffset.UtcNow,
                Expires = DateTimeOffset.UtcNow.AddDays(Math.Clamp(days, 1, 60)),
                State = InviteState.Open,
            };
            Live().Add(inv);
            Save();
            return Clone(inv);
        }
    }

    public bool Delete(string code)
    {
        lock (_lock)
        {
            var n = Live().RemoveAll(i => FixedEquals(i.Code, code));
            if (n > 0)
            {
                Save();
            }

            return n > 0;
        }
    }

    public int CountPending()
    {
        lock (_lock)
        {
            return Live().Count(i => i.State == InviteState.Pending);
        }
    }

    // The invites, minus any that ended more than KeepEnded ago (saved if any went).
    private List<Invite> Live()
    {
        var cutoff = DateTimeOffset.UtcNow - KeepEnded;
        if (Load().RemoveAll(i => Ended(i) < cutoff) > 0)
        {
            Save();
        }

        return _invites!;
    }

    private static DateTimeOffset? Ended(Invite i) => i.State switch
    {
        // Older files have no Finished on declined invites; when they were asked for is close enough.
        InviteState.Done or InviteState.Closed => i.Finished ?? i.Requested ?? i.Created,
        InviteState.Open => i.Expires,      // in the future (not ended) until it expires
        _ => null,                          // Pending/Approved: still waiting on someone
    };

    private List<Invite> Load()
    {
        if (_invites is not null)
        {
            return _invites;
        }

        try
        {
            _invites = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<Invite>>(File.ReadAllText(FilePath), Json) ?? new()
                : new();
        }
        catch (JsonException ex)
        {
            // Keep the bad file for a look later rather than silently overwriting it.
            _logger.LogError(ex, "Welcome: invites.json unreadable; starting empty, old file kept as .bad");
            File.Copy(FilePath, FilePath + ".bad", true);
            _invites = new();
        }

        return _invites;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_invites, Json));
        File.Move(tmp, FilePath, true);
    }

    private static Invite Clone(Invite i) => JsonSerializer.Deserialize<Invite>(JsonSerializer.Serialize(i, Json), Json)!;

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
