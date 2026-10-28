using Jellyfin.Plugin.Welcome.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Welcome;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<InviteStore>();
        serviceCollection.AddSingleton<DiscordNotifier>();
        serviceCollection.AddSingleton<LibraryOrder>();
        // One instance: the hosted service listens for new items, the controller uses the
        // same object for the "send a test post" button.
        serviceCollection.AddSingleton<NewArrivals>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<NewArrivals>());
    }
}
