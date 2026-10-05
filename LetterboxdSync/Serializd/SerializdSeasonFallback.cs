using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;

namespace LetterboxdSync.Serializd;

internal sealed record SerializdSeasonTarget(int SeasonId, int EpisodeOffset);

internal static class SerializdSeasonFallback
{
    internal static Func<Series?, IReadOnlyDictionary<int, int>> SeasonLengthsReader { get; set; } = ReadSeasonLengths;

    public static async Task<SerializdSeasonTarget?> ResolveAsync(
        ISerializdService service, int showTmdbId, int seasonNumber, Func<IReadOnlyDictionary<int, int>> seasonLengths)
    {
        var seasonId = await service.ResolveSeasonIdAsync(showTmdbId, seasonNumber).ConfigureAwait(false);
        if (seasonId != null)
            return new SerializdSeasonTarget(seasonId.Value, 0);

        if (seasonNumber < 2)
            return null;
        if (seasonNumber != 2 && await service.ResolveSeasonIdAsync(showTmdbId, 2).ConfigureAwait(false) != null)
            return null;

        var firstSeasonId = await service.ResolveSeasonIdAsync(showTmdbId, 1).ConfigureAwait(false);
        if (firstSeasonId == null)
            return null;

        var offset = EpisodeOffset(seasonNumber, seasonLengths());
        return offset == null ? null : new SerializdSeasonTarget(firstSeasonId.Value, offset.Value);
    }

    internal static int? EpisodeOffset(int seasonNumber, IReadOnlyDictionary<int, int> seasonLengths)
    {
        var offset = 0;
        for (var season = 1; season < seasonNumber; season++)
        {
            if (!seasonLengths.TryGetValue(season, out var length) || length <= 0)
                return null;
            offset += length;
        }

        return offset;
    }

    internal static IReadOnlyDictionary<int, int> ReadSeasonLengths(Series? series)
    {
        if (series == null)
            return new Dictionary<int, int>();

        return series.GetRecursiveChildren(i => i is Episode)
            .OfType<Episode>()
            .Where(e => e.ParentIndexNumber > 0 && e.IndexNumber > 0)
            .GroupBy(e => e.ParentIndexNumber!.Value)
            .ToDictionary(g => g.Key, g => g.Max(e => Math.Max(e.IndexNumber!.Value, e.IndexNumberEnd ?? 0)));
    }
}
