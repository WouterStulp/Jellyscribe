using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public enum SyncStatus
{
    Success,
    Skipped,
    Failed,
    Rewatch,

    /// <summary>A watchlist sync successfully auto-requested a title via Seerr. Not a
    /// watch/diary outcome; see <see cref="SyncEventSources.SeerrAutoRequestFilm"/>/
    /// <see cref="SyncEventSources.SeerrAutoRequestTv"/> for which watchlist triggered it.</summary>
    Requested,

    /// <summary>A Jellyfin rating change was pushed to the member's Letterboxd film rating
    /// (<see cref="SyncEventSources.Rating"/>). Deliberately not Success: it is not a diary
    /// entry, so it must not count as a sync or satisfy the duplicate-entry backstop.</summary>
    Rated
}

/// <summary>
/// Well-known values for SyncEvent.Source. Free-form strings are still allowed,
/// but these are the ones the runner switches on.
/// </summary>
public static class SyncEventSources
{
    /// <summary>DiaryImportTask marked a Jellyfin item played because it appeared on the user's Letterboxd diary.</summary>
    public const string DiaryImport = "diary-import";

    /// <summary>WatchlistSyncRunner's Seerr auto-request step, for a Letterboxd (film) watchlist.</summary>
    public const string SeerrAutoRequestFilm = "seerr-auto-request-film";

    /// <summary>SerializdWatchlistSyncRunner's Seerr auto-request step, for a Serializd (TV) watchlist.</summary>
    public const string SeerrAutoRequestTv = "seerr-auto-request-tv";

    /// <summary>RatingSyncHandler pushed a Jellyfin rating change to the Letterboxd film rating.</summary>
    public const string Rating = "rating";
}

public class SyncEvent
{
    public string FilmTitle { get; set; } = string.Empty;
    public string FilmSlug { get; set; } = string.Empty;
    public int TmdbId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public DateTime Timestamp { get; set; }
    public DateTime? ViewingDate { get; set; }
    public SyncStatus Status { get; set; }
    public string? Error { get; set; }
    public string? Source { get; set; }
}

public static class SyncHistory
{
    private static readonly object _lock = new();
    private static List<SyncEvent>? _events;
    private static Dictionary<int, List<SyncEvent>>? _byTmdbId;
    private static ILogger? _logger;

    /// <summary>
    /// Skipped/Failed events kept per (user, film) when the file is compacted on load. Every run
    /// appends another one, so without a cap the history grows forever. Must stay at or above
    /// <see cref="LetterboxdSyncRunner.MaxConsecutiveSyncFailures"/>, or a capped streak would
    /// read as too short to abandon the film.
    /// </summary>
    internal const int MaxPrunableEventsPerFilm = 5;

    /// <summary>
    /// Test-only hook for the JSONL file location, plus a way to clear the in-memory
    /// list between tests. Production never assigns these; the default DataPath logic
    /// uses the plugin's configurations directory.
    /// </summary>
    internal static string? DataPathOverride { get; set; }

    /// <summary>Test hook: drop the in-memory cache so the next access re-reads from disk.</summary>
    internal static void ResetForTesting()
    {
        lock (_lock) { _events = null; _byTmdbId = null; }
    }

    public static void SetLogger(ILogger logger) => _logger = logger;

    internal static Func<string, string?>? UserIdResolver { get; set; }

    internal static string? ResolveUserId(string? username)
        => string.IsNullOrEmpty(username) ? null : UserIdResolver?.Invoke(username);

    internal static bool BelongsTo(SyncEvent e, string username, string? userId)
        => !string.IsNullOrEmpty(e.UserId) && !string.IsNullOrEmpty(userId)
            ? string.Equals(e.UserId, userId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(e.Username, username, StringComparison.Ordinal);

    public static int StampMissingUserIds()
    {
        lock (_lock)
        {
            var stamped = StampMissingUserIds(LoadEvents());
            if (stamped > 0) SaveAllEvents();
            return stamped;
        }
    }

    internal static int StampMissingUserIds(IEnumerable<SyncEvent> events)
    {
        var stamped = 0;
        foreach (var e in events)
        {
            if (!string.IsNullOrEmpty(e.UserId)) continue;
            var id = ResolveUserId(e.Username);
            if (string.IsNullOrEmpty(id)) continue;
            e.UserId = id;
            stamped++;
        }
        return stamped;
    }

    private static string DataPath
    {
        get
        {
            if (!string.IsNullOrEmpty(DataPathOverride))
                return DataPathOverride!;

            var assembly = typeof(SyncHistory).Assembly.Location;
            var pluginDir = Path.GetDirectoryName(assembly);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                var configDir = Path.Combine(pluginDir, "..", "configurations");
                if (Directory.Exists(configDir))
                    return Path.Combine(configDir, "letterboxd-sync-history.jsonl");
            }

            if (!string.IsNullOrEmpty(pluginDir))
                return Path.Combine(pluginDir, "sync-history.jsonl");

            return "sync-history.jsonl";
        }
    }

    private static List<SyncEvent> LoadEvents()
    {
        if (_events != null) return _events;

        _events = new List<SyncEvent>();
        if (ReadEventsFromDisk())
        {
            var dropped = Compact(_events);
            if (dropped > 0)
            {
                SaveAllEvents();
                _logger?.LogInformation("Compacted sync history: dropped {Count} old skipped/failed events", dropped);
            }
        }

        _byTmdbId = new Dictionary<int, List<SyncEvent>>();
        foreach (var evt in _events)
            IndexEvent(evt);

        return _events;
    }

    private static void IndexEvent(SyncEvent evt)
    {
        if (!_byTmdbId!.TryGetValue(evt.TmdbId, out var list))
        {
            list = new List<SyncEvent>();
            _byTmdbId[evt.TmdbId] = list;
        }

        list.Add(evt);
    }

    // Keyed by film only: BelongsTo matches on UserId or Username depending on what each event
    // carries, so the per-user filter stays in the query over this (small) per-film list.
    private static List<SyncEvent> EventsForFilm(int tmdbId)
    {
        LoadEvents();
        return _byTmdbId!.TryGetValue(tmdbId, out var list) ? list : new List<SyncEvent>();
    }

    /// <summary>
    /// Drops all but the newest <see cref="MaxPrunableEventsPerFilm"/> Skipped/Failed events per
    /// (user, film), in place, returning how many were dropped. Every other status is a real
    /// outcome and kept forever, as are diary-import markers (Skipped, but the import-then-export
    /// loop guard reads them).
    /// </summary>
    internal static int Compact(List<SyncEvent> events)
    {
        var drop = events
            .Where(e => (e.Status == SyncStatus.Skipped || e.Status == SyncStatus.Failed)
                && !string.Equals(e.Source, SyncEventSources.DiaryImport, StringComparison.Ordinal))
            .GroupBy(e => (
                User: string.IsNullOrEmpty(e.UserId) ? "name:" + e.Username : "id:" + e.UserId.ToLowerInvariant(),
                e.TmdbId,
                e.FilmTitle))
            .SelectMany(g => g.OrderByDescending(e => e.Timestamp).Skip(MaxPrunableEventsPerFilm))
            .ToHashSet();
        return drop.Count == 0 ? 0 : events.RemoveAll(drop.Contains);
    }

    /// <summary>Reads the JSONL (or migrates the legacy JSON) into <see cref="_events"/>; false if the read failed partway.</summary>
    private static bool ReadEventsFromDisk()
    {
        try
        {
            var jsonlPath = DataPath;
            if (File.Exists(jsonlPath))
            {
                var unreadable = 0;
                foreach (var line in File.ReadLines(jsonlPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var evt = JsonSerializer.Deserialize<SyncEvent>(line);
                        if (evt != null) _events!.Add(evt);
                    }
                    catch
                    {
                        unreadable++;
                    }
                }

                if (unreadable > 0)
                    _logger?.LogWarning("Skipped {Count} unreadable lines in sync history {Path}", unreadable, jsonlPath);
                return true;
            }

            // Migrate from old JSON format if it exists
            var legacyPath = jsonlPath.Replace(".jsonl", ".json");
            if (File.Exists(legacyPath))
            {
                var json = File.ReadAllText(legacyPath);
                _events = JsonSerializer.Deserialize<List<SyncEvent>>(json) ?? new List<SyncEvent>();
                // Write in new JSONL format
                SaveAllEvents();
                _logger?.LogInformation("Migrated {Count} sync history events from JSON to JSONL", _events.Count);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load sync history from {Path}", DataPath);
            return false;
        }
    }

    private static void SaveAllEvents()
    {
        try
        {
            var path = DataPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Write beside the file and swap it in, so a crash mid-write can't truncate the history.
            var tmp = path + ".tmp";
            using (var writer = new StreamWriter(tmp, append: false))
            {
                foreach (var evt in _events!)
                {
                    writer.WriteLine(JsonSerializer.Serialize(evt));
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save sync history to {Path}", DataPath);
        }
    }

    public static void Record(SyncEvent evt)
    {
        if (string.IsNullOrEmpty(evt.UserId))
            evt.UserId = ResolveUserId(evt.Username);

        lock (_lock)
        {
            var events = LoadEvents();
            events.Add(evt);
            IndexEvent(evt);

            try
            {
                var path = DataPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(path, JsonSerializer.Serialize(evt) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to append sync event to {Path}", DataPath);
            }
        }
    }

    public static List<SyncEvent> GetRecent(int count = 100, string? username = null)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            IEnumerable<SyncEvent> filtered = events;

            if (!string.IsNullOrEmpty(username))
            {
                var userId = ResolveUserId(username);
                filtered = filtered.Where(e => BelongsTo(e, username, userId));
            }

            return filtered.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }
    }

    /// <summary>
    /// Page through history newest-first. Returns the slice plus the total count for the
    /// caller's paginator. Username, when supplied, restricts both the slice and the total
    /// to that user's events.
    /// </summary>
    public static (List<SyncEvent> Events, int Total) GetPage(int offset, int count, string? username = null)
    {
        lock (_lock)
        {
            return GetPage(LoadEvents(), offset, count, username);
        }
    }

    internal static (List<SyncEvent> Events, int Total) GetPage(IEnumerable<SyncEvent> events, int offset, int count, string? username = null)
    {
        IEnumerable<SyncEvent> filtered = events;
        if (!string.IsNullOrEmpty(username))
        {
            var userId = ResolveUserId(username);
            filtered = filtered.Where(e => BelongsTo(e, username, userId));
        }

        var ordered = filtered.OrderByDescending(e => e.Timestamp).ToList();
        var safeOffset = Math.Max(0, offset);
        var safeCount = Math.Max(0, count);
        var slice = ordered.Skip(safeOffset).Take(safeCount).ToList();
        return (slice, ordered.Count);
    }

    /// <summary>
    /// Most recent status recorded for this user/film, or null if there's no history.
    /// Used to prioritise previously-failed films at the head of the sync queue.
    /// </summary>
    public static SyncStatus? GetLastStatusForFilm(string username, int tmdbId)
    {
        lock (_lock)
        {
            return GetLastStatusForFilm(EventsForFilm(tmdbId), username, tmdbId);
        }
    }

    /// <summary>
    /// True if we have a Success or Rewatch entry for this user/film combo whose ViewingDate
    /// matches the current viewing date. Used to short-circuit the duplicate check without
    /// making an HTTP call to Letterboxd.
    /// </summary>
    public static bool WasSuccessfullySynced(string username, int tmdbId, DateTime viewingDate)
    {
        lock (_lock)
        {
            return WasSuccessfullySynced(EventsForFilm(tmdbId), username, tmdbId, viewingDate);
        }
    }

    // Pure overloads, exposed for unit testing without touching the on-disk store.

    internal static SyncStatus? GetLastStatusForFilm(IEnumerable<SyncEvent> events, string username, int tmdbId)
    {
        var userId = ResolveUserId(username);
        SyncEvent? latest = null;
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            // A rating push says nothing about the film's diary sync, which is what callers rank by.
            if (e.Status == SyncStatus.Rated) continue;
            if (latest == null || e.Timestamp > latest.Timestamp) latest = e;
        }
        return latest?.Status;
    }

    /// <summary>
    /// Number of consecutive Failed events at the tail of this user/film's history
    /// (most recent first), stopping at the first non-Failed event. The runner uses this
    /// to abandon a film that fails on every run instead of retrying it indefinitely,
    /// BuildSyncQueue otherwise pushes previously-failed films to the head of the queue.
    /// </summary>
    public static int GetConsecutiveFailureCount(string username, int tmdbId)
    {
        lock (_lock)
        {
            return GetConsecutiveFailureCount(EventsForFilm(tmdbId), username, tmdbId);
        }
    }

    internal static int GetConsecutiveFailureCount(IEnumerable<SyncEvent> events, string username, int tmdbId)
    {
        // Rated events are skipped: a rating push neither continues nor breaks the film's diary
        // failure streak.
        var userId = ResolveUserId(username);
        var ordered = events
            .Where(e => e.TmdbId == tmdbId && BelongsTo(e, username, userId)
                && e.Status != SyncStatus.Rated)
            .OrderByDescending(e => e.Timestamp);

        var count = 0;
        foreach (var e in ordered)
        {
            if (e.Status != SyncStatus.Failed) break;
            count++;
        }
        return count;
    }

    internal static bool WasSuccessfullySynced(IEnumerable<SyncEvent> events, string username, int tmdbId, DateTime viewingDate)
    {
        var userId = ResolveUserId(username);
        var target = viewingDate.Date;
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            if (e.Status != SyncStatus.Success && e.Status != SyncStatus.Rewatch) continue;
            if (e.ViewingDate?.Date == target) return true;
        }
        return false;
    }

    /// <summary>
    /// ViewingDate of the most recent Success or Rewatch entry for this user/film, or null
    /// if there isn't one. Used as a local-history backstop against duplicates that can
    /// otherwise be created when Letterboxd's own duplicate-check call fails (Cloudflare
    /// 403 returns null lastDate, which the original IsDuplicate check then treats as
    /// "not a duplicate").
    /// </summary>
    public static DateTime? GetLastSuccessfulSyncDate(string username, int tmdbId)
    {
        lock (_lock)
        {
            return GetLastSuccessfulSyncDate(EventsForFilm(tmdbId), username, tmdbId);
        }
    }

    internal static DateTime? GetLastSuccessfulSyncDate(IEnumerable<SyncEvent> events, string username, int tmdbId)
    {
        var userId = ResolveUserId(username);
        SyncEvent? latest = null;
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            if (e.Status != SyncStatus.Success && e.Status != SyncStatus.Rewatch) continue;
            if (latest == null || e.Timestamp > latest.Timestamp) latest = e;
        }
        return latest?.ViewingDate;
    }

    /// <summary>
    /// True if DiaryImportTask has previously marked this user's Jellyfin copy of this
    /// film as played because the film was found on their Letterboxd diary. The runner
    /// uses this as a guard against the import-then-export loop described in
    /// https://github.com/builtbyproxy/jellyfin-plugin-letterboxd/issues/32.
    /// </summary>
    public static bool WasImportedFromDiary(string username, int tmdbId)
    {
        lock (_lock)
        {
            return WasImportedFromDiary(EventsForFilm(tmdbId), username, tmdbId);
        }
    }

    internal static bool WasImportedFromDiary(IEnumerable<SyncEvent> events, string username, int tmdbId)
    {
        var userId = ResolveUserId(username);
        foreach (var e in events)
        {
            if (e.TmdbId != tmdbId) continue;
            if (!BelongsTo(e, username, userId)) continue;
            if (string.Equals(e.Source, SyncEventSources.DiaryImport, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static (int Total, int Success, int Failed, int Skipped, int Rewatches, int Requested) GetStats(string? username = null)
    {
        lock (_lock)
        {
            var events = LoadEvents();
            IEnumerable<SyncEvent> filtered = events;

            if (!string.IsNullOrEmpty(username))
            {
                var userId = ResolveUserId(username);
                filtered = filtered.Where(e => BelongsTo(e, username, userId));
            }

            var list = filtered.ToList();
            return (
                // A rating push is not a logged film (the dashboards show Total as "Films logged"),
                // the same way Serializd's stats leave reviews out of theirs.
                list.Count(e => e.Status != SyncStatus.Rated),
                list.Count(e => e.Status == SyncStatus.Success),
                list.Count(e => e.Status == SyncStatus.Failed),
                list.Count(e => e.Status == SyncStatus.Skipped),
                list.Count(e => e.Status == SyncStatus.Rewatch),
                list.Count(e => e.Status == SyncStatus.Requested)
            );
        }
    }
}
