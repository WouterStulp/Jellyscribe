using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace LetterboxdSync;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<LetterboxdSyncRunner>();
        serviceCollection.AddSingleton<WatchlistSyncRunner>();
        serviceCollection.AddSingleton<Serializd.SerializdSyncRunner>();
        serviceCollection.AddSingleton<Serializd.SerializdWatchlistSyncRunner>();
        serviceCollection.AddSingleton<Serializd.SerializdDiaryImportRunner>();
        serviceCollection.AddHostedService<PlaybackHandler>();
        serviceCollection.AddHostedService<RatingSyncHandler>();
        serviceCollection.AddHostedService<RepositoryMigrationService>();
        serviceCollection.AddHostedService<UserIdentityService>();

        // Adds the sidebar link to the web client without the File Transformation plugin.
        serviceCollection.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, SidebarScriptStartupFilter>();
    }
}
