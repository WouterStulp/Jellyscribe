using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace LetterboxdSync.Serializd;

internal static class SerializdShowStatus
{
    internal static Func<Series?, Func<Episode, bool>, bool> FinishedReader { get; set; } = IsFinished;

    // A finished show is remembered as such, so later runs neither walk every one of its
    // episodes again nor count it as pending work (which forced a Serializd login every run).
    public static bool IsPending(string userId, string email, int showTmdbId, Func<bool> seriesFinished)
    {
        if (SerializdSyncHistory.Has(userId, email, showTmdbId, 0, 0, SerializdSyncHistory.KindCurrentlyWatching)
            || SerializdSyncHistory.Has(userId, email, showTmdbId, 0, 0, SerializdSyncHistory.KindFinished))
            return false;

        if (!seriesFinished())
            return true;

        SerializdSyncHistory.Record(userId, email, showTmdbId, 0, 0, SerializdSyncHistory.KindFinished);
        return false;
    }

    public static async Task<bool> MarkCurrentlyWatchingAsync(
        ISerializdService service, string userId, string email, int showTmdbId, Func<bool> seriesFinished)
    {
        if (!IsPending(userId, email, showTmdbId, seriesFinished))
            return false;

        await service.SetCurrentlyWatchingAsync(showTmdbId).ConfigureAwait(false);
        SerializdSyncHistory.Record(userId, email, showTmdbId, 0, 0, SerializdSyncHistory.KindCurrentlyWatching);
        return true;
    }

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
