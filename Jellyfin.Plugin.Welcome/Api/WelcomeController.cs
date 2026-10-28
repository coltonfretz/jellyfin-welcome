using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Welcome.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Welcome.Api;

/// <summary>
/// Two audiences:
///  - People with an invite link (anonymous). Every call needs the invite code, a
///    128-bit secret in the link; finishing sign-up also needs the per-browser token
///    handed out when they asked to join. Nothing creates an account until an admin has
///    approved that specific request.
///  - Admins (Jellyfin administrators only, "RequiresElevation") from the dashboard page.
/// </summary>
[ApiController]
[Route("Welcome")]
public partial class WelcomeController : ControllerBase
{
    // Wrong codes are the only thing worth throttling: real ones are unguessable, so a
    // burst of misses is someone poking. Global, in memory; resets on restart.
    private static readonly object MissLock = new();
    private static readonly Queue<DateTime> Misses = new();
    private static readonly SemaphoreSlim FinishLock = new(1, 1);

    private readonly InviteStore _store;
    private readonly DiscordNotifier _discord;
    private readonly NewArrivals _arrivals;
    private readonly LibraryOrder _libraryOrder;
    private readonly IUserManager _users;
    private readonly ILogger<WelcomeController> _logger;

    public WelcomeController(InviteStore store, DiscordNotifier discord, NewArrivals arrivals, LibraryOrder libraryOrder, IUserManager users, ILogger<WelcomeController> logger)
    {
        _store = store;
        _discord = discord;
        _arrivals = arrivals;
        _libraryOrder = libraryOrder;
        _users = users;
        _logger = logger;
    }

    private static Configuration.PluginConfiguration Cfg => Plugin.Instance!.Configuration;

    // The configured public address, else the address this request came in on (right for
    // the person on the other end, since that's how they reached the server).
    private string PublicUrl => Cfg.PublicUrl.Trim().TrimEnd('/') is { Length: > 0 } url
        ? url
        : $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    // ---------------- the friend's page ----------------

    [HttpGet("join")]
    [HttpGet("join/{code}")]
    [AllowAnonymous]
    public IActionResult JoinPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Welcome.Web.join.html")!;
        using var r = new StreamReader(s);
        // The invite code is in the URL path: keep it out of Referer headers (app store
        // links) and out of caches, and don't let other sites frame the page.
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        return Content(r.ReadToEnd(), "text/html; charset=utf-8");
    }

    /// <summary>
    /// What the join page says: who runs the server, its name, the intro and the "what's
    /// inside" tiles. Public on purpose (the page needs it before any code is checked);
    /// it holds only what the admin chose to show new people.
    /// </summary>
    [HttpGet("api/site")]
    [AllowAnonymous]
    public IActionResult Site()
    {
        Response.Headers.CacheControl = "no-store";
        var tiles = Cfg.InsideTiles
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2))
            .Select(p => new { title = p[0].Trim(), text = p.Length > 1 ? p[1].Trim() : string.Empty })
            .Where(t => t.title.Length > 0)
            .Take(12);
        var music = Cfg.MusicAppUrl.Trim();
        return Ok(new
        {
            owner = Cfg.OwnerName.Trim(),
            server = Cfg.ServerName.Trim(),
            intro = Cfg.IntroText.Trim(),
            tiles,
            note = Cfg.InsideNote.Trim(),
            // Only plain web addresses: this goes into a link on the page.
            musicApp = music.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || music.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? new { name = Cfg.MusicAppName.Trim(), url = music, about = Cfg.MusicAppAbout.Trim() }
                : null,
        });
    }

    [HttpGet("api/invite/{code}")]
    [AllowAnonymous]
    public IActionResult Status(string code, [FromQuery] string? token)
    {
        var inv = Lookup(code);
        if (inv is null)
        {
            return Throttled() ? StatusCode(StatusCodes.Status429TooManyRequests) : Ok(new { state = "invalid" });
        }

        var mine = token is { Length: > 0 } && inv.TokenHash == InviteStore.Hash(token);
        var state = inv.State == InviteState.Open && inv.Expires < DateTimeOffset.UtcNow ? "expired" : inv.State.ToString().ToLowerInvariant();
        return Ok(new
        {
            state,
            mine,
            label = inv.Label,
            name = mine ? inv.Name : null,
            suggestedUsername = mine && inv.State == InviteState.Approved ? SuggestUsername(inv.Name ?? inv.Label) : null,
            username = mine && inv.State == InviteState.Done ? inv.Username : null,
            serverUrl = mine && inv.State == InviteState.Done ? PublicUrl : null,
            minPassword = Cfg.MinPasswordLength,
        });
    }

    public sealed record JoinRequest(string? Name, string? Note);

    [HttpPost("api/invite/{code}/request")]
    [AllowAnonymous]
    public IActionResult AskToJoin(string code, [FromBody] JoinRequest body)
    {
        if (Lookup(code) is null)
        {
            return Throttled() ? StatusCode(StatusCodes.Status429TooManyRequests) : NotFound();
        }

        var name = (body.Name ?? string.Empty).Trim();
        var note = (body.Note ?? string.Empty).Trim();
        if (name.Length is < 1 or > 60 || note.Length > 300)
        {
            return BadRequest(new { error = "Please enter your name (60 characters at most)." });
        }

        // Cap on requests waiting at once, in case a link leaks and gets passed around.
        if (_store.CountPending() >= 10)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Lots of people are waiting right now. Try again later." });
        }

        var token = InviteStore.NewSecret(32);
        var inv = _store.Update(code, i =>
        {
            if (i.State != InviteState.Open || i.Expires < DateTimeOffset.UtcNow)
            {
                return false;
            }

            i.State = InviteState.Pending;
            i.Name = name;
            i.Note = note.Length > 0 ? note : null;
            i.Requested = DateTimeOffset.UtcNow;
            i.TokenHash = InviteStore.Hash(token);
            return true;
        });
        if (inv is null)
        {
            return Conflict(new { error = "This invite was already used to ask. If that was you on another device, finish there." });
        }

        _logger.LogInformation("Welcome: {Name} asked to join (invite for {Label})", inv.Name, inv.Label);
        // The friend's page doesn't wait on Discord: the ping goes out on its own (and
        // logs if it fails; the request is waiting on the dashboard either way).
        _ = _discord.NotifyRequestAsync(inv, CancellationToken.None);
        return Ok(new { token });
    }

    public sealed record FinishRequest(string? Token, string? Username, string? Password);

    [HttpPost("api/invite/{code}/finish")]
    [AllowAnonymous]
    public async Task<IActionResult> Finish(string code, [FromBody] FinishRequest body)
    {
        var inv = Lookup(code);
        if (inv is null)
        {
            return Throttled() ? StatusCode(StatusCodes.Status429TooManyRequests) : NotFound();
        }

        if (body.Token is not { Length: > 0 } || inv.TokenHash != InviteStore.Hash(body.Token) || inv.State != InviteState.Approved)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "This link isn't ready to finish yet." });
        }

        var username = (body.Username ?? string.Empty).Trim();
        var password = body.Password ?? string.Empty;
        if (!UsernameRegex().IsMatch(username) || username.Length > 40)
        {
            return BadRequest(new { error = "Usernames can use letters, numbers, spaces and . _ - ' @ + (40 characters at most)." });
        }

        if (password.Length < Cfg.MinPasswordLength || password.Length > 256)
        {
            return BadRequest(new { error = $"Your password needs at least {Cfg.MinPasswordLength} characters." });
        }

        // One sign-up at a time: two taps on "Finish" must not make two accounts.
        await FinishLock.WaitAsync().ConfigureAwait(false);
        try
        {
            inv = _store.Find(code);
            if (inv is null || inv.State != InviteState.Approved)
            {
                return Conflict(new { error = "This invite was already used." });
            }

            if (_users.GetUserByName(username) is not null)
            {
                return Conflict(new { error = "That username is taken. Try another one." });
            }

            Jellyfin.Database.Implementations.Entities.User user;
            try
            {
                user = await _users.CreateUserAsync(username).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return BadRequest(new { error = "Jellyfin didn't accept that username. Try a simpler one." });
            }

            var template = TemplateUser();
            try
            {
                await _users.ChangePassword(user.Id, password).ConfigureAwait(false);
                // A template user's permissions if one is set (not an admin, see
                // TemplateUser), else the built-in ones.
                var policy = template is null ? _users.GetUserDto(user, null).Policy : _users.GetUserDto(template, null).Policy;
                if (template is null)
                {
                    FriendPolicy.Apply(policy);
                }

                policy.IsAdministrator = false;
                policy.IsDisabled = false;
                await _users.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Never leave a half-made account behind (it would have no password).
                _logger.LogError(ex, "Welcome: setting up {Username} failed; removing the account", username);
                await _users.DeleteUserAsync(user.Id).ConfigureAwait(false);
                return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Something went wrong on the server. Try again in a minute." });
            }

            // Same library order as the template user, else the admin (cosmetic; never
            // fails the sign-up).
            await _libraryOrder.CopyOrderToAsync(user, template).ConfigureAwait(false);

            _store.Update(code, i =>
            {
                i.State = InviteState.Done;
                i.Username = username;
                i.Finished = DateTimeOffset.UtcNow;
                return true;
            });
            _logger.LogInformation("Welcome: created Jellyfin user {Username} for {Name}", username, inv.Name);
            return Ok(new { username, serverUrl = PublicUrl });
        }
        finally
        {
            FinishLock.Release();
        }
    }

    // ---------------- the admin's dashboard page ----------------

    [HttpGet("admin/invites")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult List() => Ok(new
    {
        webhookSet = DiscordNotifier.IsWebhook(Cfg.DiscordWebhookUrl),
        arrivalsSet = DiscordNotifier.IsWebhook(Cfg.NewArrivalsWebhookUrl),
        invites = _store.All().OrderByDescending(i => i.Requested ?? i.Created).Select(i => new
        {
            code = i.Code,
            link = $"{PublicUrl}/Welcome/join/{i.Code}",
            label = i.Label,
            state = i.State == InviteState.Open && i.Expires < DateTimeOffset.UtcNow ? "expired" : i.State.ToString().ToLowerInvariant(),
            created = i.Created,
            expires = i.Expires,
            name = i.Name,
            note = i.Note,
            requested = i.Requested,
            username = i.Username,
            finished = i.Finished,
        }),
    });

    public sealed record NewInvite(string? Label);

    [HttpPost("admin/invites")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult CreateInvite([FromBody] NewInvite body)
    {
        var label = (body.Label ?? string.Empty).Trim();
        if (label.Length is < 1 or > 60)
        {
            return BadRequest(new { error = "Say who the invite is for." });
        }

        var inv = _store.Create(label, Cfg.InviteDays);
        return Ok(new { code = inv.Code, link = $"{PublicUrl}/Welcome/join/{inv.Code}", expires = inv.Expires });
    }

    [HttpPost("admin/invites/{code}/approve")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Approve(string code)
        => _store.Update(code, i => { if (i.State != InviteState.Pending) { return false; } i.State = InviteState.Approved; return true; }) is null
            ? Conflict()
            : NoContent();

    [HttpPost("admin/invites/{code}/decline")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Decline(string code)
        => _store.Update(code, i => { if (i.State is InviteState.Done or InviteState.Closed) { return false; } i.State = InviteState.Closed; i.Finished = DateTimeOffset.UtcNow; return true; }) is null
            ? Conflict()
            : NoContent();

    [HttpDelete("admin/invites/{code}")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Delete(string code) => _store.Delete(code) ? NoContent() : NotFound();

    [HttpPost("admin/test-discord")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> TestDiscord(CancellationToken ct) =>
        await _discord.NotifyRequestAsync(new Invite { Name = "Test ping", Label = "Test ping", Note = "Discord notifications are working." }, ct).ConfigureAwait(false)
            ? NoContent()
            : StatusCode(502);

    [HttpPost("admin/test-new-arrivals")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<IActionResult> TestNewArrivals(CancellationToken ct) =>
        await _arrivals.PostTestAsync(ct).ConfigureAwait(false) ? NoContent() : StatusCode(502);

    [HttpGet("version")]
    [AllowAnonymous]
    public IActionResult Version() => Ok(new { version = Plugin.PluginVersion });

    // ---------------- helpers ----------------

    private Invite? Lookup(string code) => code.Length is > 10 and < 64 ? _store.Find(code) : null;

    // The template user, if one is set and still exists. Never an administrator: a
    // template is copied into accounts strangers end up holding, so copying an admin's
    // permissions by mistake would be the worst possible slip.
    private Jellyfin.Database.Implementations.Entities.User? TemplateUser()
    {
        if (!Guid.TryParse(Cfg.TemplateUserId, out var id))
        {
            return null;
        }

        var u = _users.GetUserById(id);
        if (u is null || _users.GetUserDto(u, null).Policy.IsAdministrator)
        {
            _logger.LogWarning("Welcome: the template user is missing or an administrator; using the built-in permissions");
            return null;
        }

        return u;
    }

    private static bool Throttled()
    {
        lock (MissLock)
        {
            var now = DateTime.UtcNow;
            while (Misses.Count > 0 && now - Misses.Peek() > TimeSpan.FromMinutes(10))
            {
                Misses.Dequeue();
            }

            Misses.Enqueue(now);
            return Misses.Count > 30;
        }
    }

    private static string SuggestUsername(string name)
    {
        var s = UsernameStripRegex().Replace(name.Trim(), string.Empty);
        return s.Length > 40 ? s[..40].Trim() : s;
    }

    // Jellyfin's own rule (UserManager): word chars, space, - ' . _ @ +; no leading/trailing space.
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex(@"[^\w\ \-'._@+]")]
    private static partial Regex UsernameStripRegex();
}
