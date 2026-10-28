using Jellyfin.Plugin.Welcome.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Welcome;

/// <summary>
/// Welcome: invite links for new users, with an admin approving each one.
///
/// WHY: handing out a password in a chat message leaves it there forever, and most
/// people never change it. Here the new person opens a link, says who they are, and
/// waits; the admin gets a Discord ping and approves in the dashboard; only then does
/// the new person pick their own password, which never passes through anyone else.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Welcome";

    public override Guid Id => Guid.Parse("99a863ba-0370-40a0-b6a4-23995415384b");

    public override string Description => "Invite friends with a link; you approve, they pick their own password.";

    public static string PluginVersion => typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = "Welcome.Configuration.configPage.html",
            },
        };
    }
}
