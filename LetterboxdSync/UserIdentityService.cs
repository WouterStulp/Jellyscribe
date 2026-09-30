using System;
using System.Threading;
using System.Threading.Tasks;
using LetterboxdSync.Serializd;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class UserIdentityService : IHostedService
{
    private readonly IUserManager _userManager;
    private readonly ILogger<UserIdentityService> _logger;

    public UserIdentityService(IUserManager userManager, ILogger<UserIdentityService> logger)
    {
        _userManager = userManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SyncHistory.UserIdResolver = name => _userManager.GetUserByName(name)?.Id.ToString("N");

        try
        {
            var films = SyncHistory.StampMissingUserIds();
            var episodes = SerializdActivity.StampMissingUserIds();
            if (films + episodes > 0)
                _logger.LogInformation(
                    "Linked {Films} Letterboxd and {Episodes} Serializd history entries to their Jellyfin user ids",
                    films, episodes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not link existing history entries to Jellyfin user ids");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
