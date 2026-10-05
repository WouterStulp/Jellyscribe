using System;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace LetterboxdSync.Serializd;

internal static class SerializdShowStatus
{
    internal static Func<Series?, Func<Episode, bool>, bool> FinishedReader { get; set; } = IsFinished;

    public static bool IsPending(string userId, string email, int showTmdbId, Func<bool> seriesFinished)
        => !SerializdSyncHistory.Has(userId, email, showTmdbId, 0, 0, SerializdSyncHistory.KindCurrentlyWatching)
            && !seriesFinished();

    public static async Task<bool> MarkCurrentlyWatchingAsync(
        ISerializdService service, string userId, string email, int showTmdbId, Func<bool> seriesFinished)
    {
        if (!IsPending(userId, email, showTmdbId, seriesFinished))
            return false;

        await service.SetCurrentlyWatchingAsync(showTmdbId).ConfigureAwait(false);
        SerializdSyncHistory.Record(userId, email, showTmdbId, 0, 0, SerializdSyncHistory.KindCurrentlyWatching);
        return true;
    }

    internal static bool IsFinished(Series? series, Func<Episode, bool> isPlayed)
    {
        if (series == null)
            return false;

        var episodes = series.GetRecursiveChildren(i => i is Episode)
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber > 0 && !e.IsVirtualItem)
            .ToList();
        return episodes.Count > 0 && episodes.All(isPlayed);
    }
}
