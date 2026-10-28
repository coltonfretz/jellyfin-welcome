using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Welcome.Services;

/// <summary>
/// Gives a newly created account the same library order as the template user's (if set)
/// or the server admin's, so the top bar and "My Media" row follow the order the admin
/// picked instead of Jellyfin's alphabetical default.
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

            var config = _users.GetUserDto(user, null).Configuration;
            config.OrderedViews = ordered;
            await _users.UpdateConfigurationAsync(user.Id, config).ConfigureAwait(false);
            _logger.LogInformation("Welcome: gave {Username} the library order {Order}", user.Username, string.Join(" / ", wanted));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Welcome: couldn't set the library order for {Username}; it keeps Jellyfin's default", user.Username);
        }
    }

    // A user's views in the order they see them (their OrderedViews, if any, applied).
    private MediaBrowser.Controller.Entities.Folder[] ViewsFor(User u) =>
        _views.GetUserViews(new UserViewQuery { User = u, IncludeHidden = false });
}
