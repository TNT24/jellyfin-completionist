using Jellyfin.Plugin.Completionist.Notifications;
using Jellyfin.Plugin.Completionist.Scanning;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Completionist;

/// <summary>
/// Registers the plugin's shared services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ActivityLog>();
        serviceCollection.AddSingleton<GuideClient>();
        serviceCollection.AddSingleton<ReportStore>();
        serviceCollection.AddSingleton<DiscordNotifier>();
        serviceCollection.AddSingleton<CompletionistScanner>();
    }
}
