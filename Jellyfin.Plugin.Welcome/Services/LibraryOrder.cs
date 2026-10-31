using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Welcome.Services;

/// <summary>
/// Gives a newly created account the same library order as the template user's (if set)
/// or the server admin's, so the top bar and "My Media" row follow the order the admin
/// picked instead of Jellyfin's alphabetical default. Libraries that account hides from
/// My Media are hidden on the new account too.
///
/// WHY copy an existing account's order instead of a list in the plugin: the admin changes
/// the order in their own Settings > Home screen, and new people simply follow, with nothing to keep in
/// sync here. Views are matched by NAME, not id, because some views are per user: every
/// account has its own Playlists folder with its own id, so copying ids would drop
/// Playlists to the end.
///
/// Cosmetic only: any failure is logged and the account keeps Jellyfin's default order;
/// it must never get in the way of someone finishing sign-up.
/// </summary>
public sealed class LibraryOrder
{
    private readonly IUserManager _users;
    private readonly IUserViewManager _views;
    private readonly ILogger<LibraryOrder> _logger;

    public LibraryOrder(IUserManager users, IUserViewManager views, ILogger<LibraryOrder> logger)
    {
        _users = users;
        _views = views;
        _logger = logger;
    }

    public async Task CopyOrderToAsync(User user, User? template)
    {
        try
        {
            // Whose order to copy: the template user, else an administrator who has
            // actually set one.
            var admin = template ?? _users.GetUsers().FirstOrDefault(u => u.Id != user.Id
                && _users.GetUserDto(u, null) is { Policy.IsAdministrator: true, Configuration.OrderedViews.Length: > 0 });
            if (admin is null)
            {
                return;
            }

            var wanted = ViewsFor(admin).Select(v => v.Name).ToList();
            var theirs = ViewsFor(user);
            // Known names in the admin's order, then anything the admin doesn't have.
            var ordered = theirs
                .OrderBy(v => wanted.IndexOf(v.Name) is var i && i >= 0 ? i : int.MaxValue)
                .Select(v => v.Id)
                .ToArray();

            // The libraries the source account hides from My Media come along too. Without
            // this a new account showed every library, Collections included, until something
            // else hid it (on Fretzflix the Tuner plugin's nightly run). Matched by name for
            // the same reason as the order. Anything the new account already hides stays.
            var source = _users.GetUserDto(admin, null).Configuration;
            var hiddenNames = ViewsFor(admin, includeHidden: true)
                .Where(v => source.MyMediaExcludes.Contains(v.Id))
                .Select(v => v.Name)
                .ToHashSet();

            var config = _users.GetUserDto(user, null).Configuration;
            config.OrderedViews = ordered;
            config.MyMediaExcludes = ViewsFor(user, includeHidden: true)
                .Where(v => hiddenNames.Contains(v.Name))
                .Select(v => v.Id)
                .Union(config.MyMediaExcludes)
                .ToArray();
            await _users.UpdateConfigurationAsync(user.Id, config).ConfigureAwait(false);
            _logger.LogInformation(
                "Welcome: gave {Username} the library order {Order} (hidden from My Media: {Hidden})",
                user.Username,
                string.Join(" / ", wanted),
                hiddenNames.Count > 0 ? string.Join(" / ", hiddenNames) : "none");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Welcome: couldn't set the library order for {Username}; it keeps Jellyfin's default", user.Username);
        }
    }

    // A user's views in the order they see them (their OrderedViews, if any, applied).
    // includeHidden: false leaves out what they hide from My Media (that's what Jellyfin's
    // IncludeHidden means), true lists everything they can see.
    private MediaBrowser.Controller.Entities.Folder[] ViewsFor(User u, bool includeHidden = false) =>
        _views.GetUserViews(new UserViewQuery { User = u, IncludeHidden = includeHidden });
}
