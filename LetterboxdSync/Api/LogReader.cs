using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Api;

/// <summary>Reads the plugin's own lines out of Jellyfin's log files for the dashboard Logs tab.</summary>
internal static class LogReader
{
    // Matches a Jellyfin log-entry header start: "[2026-06-14 ...". Continuation lines
    // (stack frames, exception messages) do not begin this way.
    private static readonly System.Text.RegularExpressions.Regex _logHeader =
        new(@"^\[\d{4}-\d{2}-\d{2}", System.Text.RegularExpressions.RegexOptions.Compiled);

    // ANSI CSI escape sequences (colour codes) that some console sinks emit.
    private static readonly System.Text.RegularExpressions.Regex _ansi =
        new(@"\x1B\[[0-9;]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsLogHeader(string line) => _logHeader.IsMatch(line);

    private static string StripAnsi(string line) => _ansi.Replace(line, string.Empty);

    /// <summary>
    /// Reads ALL recent LetterboxdSync-tagged lines from Jellyfin's two newest main
    /// log files (covering a just-rolled-over file), untrimmed, for the Logs tab. The
    /// plugin never logs auth tokens, passwords, cookies, or review text, so these
    /// lines are safe to share.
    /// </summary>
    internal static (List<string> Lines, string? Source, string? Error) ReadRecentLogLines(
        IApplicationPaths appPaths, ILogger logger)
    {
        try
        {
            var logDir = appPaths.LogDirectoryPath;
            if (!Directory.Exists(logDir))
                return (new List<string>(), null, "log directory not found");

            var mainLogs = Directory.GetFiles(logDir, "log_*.log")
                .OrderByDescending(f => f)
                .Take(2)
                .ToList();
            if (mainLogs.Count == 0)
                return (new List<string>(), null, "no log files");

            var lines = new List<string>();
            foreach (var path in mainLogs.AsEnumerable().Reverse())
            {
                // Force UTF-8 so non-ASCII (accented/CJK film titles) round-trips cleanly.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
                string? line;
                // A matched LetterboxdSync entry is often multi-line: the header line carries
                // the tag, but the exception message and "   at ..." stack frames continue on
                // following lines that DON'T carry the tag. Capture those continuation lines
                // too (until the next timestamped header) or the most useful diagnostic, the
                // stack trace, gets shredded by a per-line filter.
                var inMatch = false;
                while ((line = sr.ReadLine()) != null)
                {
                    line = StripAnsi(line);
                    var isHeader = IsLogHeader(line);
                    if (isHeader)
                    {
                        inMatch = line.Contains("LetterboxdSync", StringComparison.Ordinal) ||
                                  line.Contains("Letterboxd ", StringComparison.Ordinal);
                        if (inMatch) lines.Add(line);
                    }
                    else if (inMatch)
                    {
                        lines.Add(line); // continuation of a matched entry (stack frame / message)
                    }
                }
            }

            return (lines, string.Join(", ", mainLogs.Select(Path.GetFileName)), null);
        }
        catch (Exception ex)
        {
            logger.LogWarning("ReadRecentLogLines failed: {Message}", ex.Message);
            return (new List<string>(), null, ex.Message);
        }
    }
}
