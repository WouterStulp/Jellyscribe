using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Serializd;

internal static class SerializdShowStatus
{
    internal static Func<Series?, Func<Episode, bool>, bool> FinishedReader { get; set; } = IsFinished;

    // Pending means a Serializd status call is still due, so the runner logs in for it. Once
    // the show is marked watched nothing is left to do, which keeps a finished show from
    // forcing a login (and an episode walk) every run.
    public static bool IsPending(string userId, string email, int showTmdbId, Func<bool> seriesFinished)
    {
        if (Has(userId, email, showTmdbId, SerializdSyncHistory.KindShowWatched))
            return false;

        return IsFinishedRecorded(userId, email, showTmdbId, seriesFinished)
            || !Has(userId, email, showTmdbId, SerializdSyncHistory.KindCurrentlyWatching);
    }

    public static async Task<bool> MarkCurrentlyWatchingAsync(
        ISerializdService service, string userId, string email, int showTmdbId, Func<bool> seriesFinished)
    {
        if (Has(userId, email, showTmdbId, SerializdSyncHistory.KindCurrentlyWatching)
            || Has(userId, email, showTmdbId, SerializdSyncHistory.KindShowWatched)
            || IsFinishedRecorded(userId, email, showTmdbId, seriesFinished))
            return false;

        await service.SetCurrentlyWatchingAsync(showTmdbId).ConfigureAwait(false);
        Record(userId, email, showTmdbId, SerializdSyncHistory.KindCurrentlyWatching);
        return true;
    }

    // Nothing is recorded when the watched call fails, so the next run retries it.
    public static async Task<bool> MarkWatchedAsync(
        ISerializdService service, string userId, string email, int showTmdbId, Func<bool> seriesFinished, ILogger logger)
    {
        if (Has(userId, email, showTmdbId, SerializdSyncHistory.KindShowWatched)
            || !IsFinishedRecorded(userId, email, showTmdbId, seriesFinished))
            return false;

        await service.SetWatchedAsync(showTmdbId).ConfigureAwait(false);
        Record(userId, email, showTmdbId, SerializdSyncHistory.KindShowWatched);

        // The show may never have been on currently watching, so a failure here is no reason to retry.
        try
        {
            await service.RemoveCurrentlyWatchingAsync(showTmdbId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not take TMDb {Show} off currently watching on Serializd: {Message}",
                showTmdbId, ex.Message);
        }

        return true;
    }

    // The finished marker is permanent, so later checks skip walking every episode again.
    private static bool IsFinishedRecorded(string userId, string email, int showTmdbId, Func<bool> seriesFinished)
    {
        if (Has(userId, email, showTmdbId, SerializdSyncHistory.KindFinished))
            return true;
        if (!seriesFinished())
            return false;

        Record(userId, email, showTmdbId, SerializdSyncHistory.KindFinished);
        return true;
    }

    private static bool Has(string userId, string email, int showTmdbId, string kind)
        => SerializdSyncHistory.Has(userId, email, showTmdbId, 0, 0, kind);

    private static void Record(string userId, string email, int showTmdbId, string kind)
        => SerializdSyncHistory.Record(userId, email, showTmdbId, 0, 0, kind);

    // Only an ended show can be finished: being caught up on one that is still airing means
    // still watching it, and the finished marker is permanent.
    internal static bool IsFinished(Series? series, Func<Episode, bool> isPlayed)
    {
        if (series?.Status != SeriesStatus.Ended)
            return false;

        var episodes = series.GetRecursiveChildren(i => i is Episode)
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber > 0 && !e.IsVirtualItem)
            .ToList();
        return episodes.Count > 0 && episodes.All(isPlayed);
    }
}
