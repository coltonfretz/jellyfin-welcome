using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Welcome.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The address people use from outside. Invite links, the Discord "approve" link and
    /// the "server address" shown to new users are built from it. Empty: links use the
    /// address the page was opened at, and Discord posts go out without links.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Discord webhook for "someone wants in" pings. Server-side only: the admin page
    /// shows whether it is set, never the value.
    /// </summary>
    public string DiscordWebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Discord webhook for the "New on (server name)" channel everyone can see. Separate
    /// from <see cref="DiscordWebhookUrl"/>, which should go to the admin's private
    /// channel. Posts never say who requested anything. Server-side only.
    /// </summary>
    public string NewArrivalsWebhookUrl { get; set; } = string.Empty;

    /// <summary>Library ids (comma separated) to announce from. Empty = every movie and TV library.</summary>
    public string NewArrivalsLibraries { get; set; } = string.Empty;

    /// <summary>How long an unused invite link stays valid.</summary>
    public int InviteDays { get; set; } = 7;

    /// <summary>Shortest password a new user may pick.</summary>
    public int MinPasswordLength { get; set; } = 8;

    /// <summary>
    /// A user whose permissions and library order new accounts copy. Empty: the built-in
    /// permissions (<see cref="Services.FriendPolicy"/>) and the first administrator's
    /// library order. An administrator is never used as a template.
    /// </summary>
    public string TemplateUserId { get; set; } = string.Empty;

    // ---- what the join page says (everything a new person reads) ----

    /// <summary>Who runs the server, as the join page says it: "Ask Sam", "Waiting for Sam".</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>The server's name: the join page footer and "New on (name)" posts.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>The first paragraph of the join page. Empty: a plain default sentence.</summary>
    public string IntroText { get; set; } = string.Empty;

    /// <summary>
    /// "What's inside" tiles on the finished page: "Title=description" lines. Empty: one
    /// "Movies &amp; shows" tile.
    /// </summary>
    public string InsideTiles { get; set; } = string.Empty;

    /// <summary>A short note under the tiles (for example where extra pages are). Optional.</summary>
    public string InsideNote { get; set; } = string.Empty;

    /// <summary>
    /// An optional web music app on the same accounts (for example Fretzpod): its name,
    /// address and a few words about it. Empty address: no music app step.
    /// </summary>
    public string MusicAppName { get; set; } = string.Empty;

    public string MusicAppUrl { get; set; } = string.Empty;

    public string MusicAppAbout { get; set; } = string.Empty;
}
