using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Pushes "sync needs attention" alerts to an ntfy topic (<see cref="Configuration.PluginConfiguration.NtfyUrl"/>).
/// Disabled while the URL is empty. The event methods never throw: a failed send is logged and
/// the sync carries on. Messages carry account names and sanitised errors, never secrets.
/// </summary>
public static class Notifier
{
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);

    // ponytail: in-memory dedupe, resets on restart (one repeat alert per restart at worst).
    private static readonly Dictionary<string, DateTime> _lastSent = new();
    private static readonly object _lock = new();

    /// <summary>Shared client. Tests swap in one bound to a mock handler.</summary>
    internal static HttpClient Http { get; set; } = new() { Timeout = TimeSpan.FromSeconds(10) };

    internal static void ResetForTesting()
    {
        lock (_lock) { _lastSent.Clear(); }
    }

    public static Task LetterboxdLoginFailedAsync(string? jellyfinUsername, string account, string? reason, ILogger logger)
        => TrySendAsync(
            $"auth|{jellyfinUsername}|{account.ToLowerInvariant()}",
            "Jellyscribe: Letterboxd login failing",
            $"Letterboxd login failed for {jellyfinUsername ?? "unknown user"} ({account}): {reason ?? "unknown error"}. " +
            "Syncing for this account is paused until you fix the login in Jellyscribe settings.",
            "high",
            logger);

    public static Task GaveUpAsync(string title, string? jellyfinUsername, int tmdbId, int attempts, string? lastError, ILogger logger)
        => TrySendAsync(
            $"gaveup|{jellyfinUsername}|{tmdbId}",
            "Jellyscribe: gave up syncing a film",
            $"Gave up on {title} for {jellyfinUsername} after {attempts} failed attempts: {AuthBreaker.Sanitize(lastError) ?? "unknown error"}",
            "default",
            logger);

    /// <summary>Sends one message, throwing on any failure. Used directly by the admin test endpoint.</summary>
    public static async Task SendAsync(string url, string? token, string title, string message, string priority)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(message, Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation("Title", title);
        request.Headers.TryAddWithoutValidation("Tags", "warning");
        request.Headers.TryAddWithoutValidation("Priority", priority);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ntfy returned {(int)response.StatusCode} {response.ReasonPhrase}");
    }

    private static async Task TrySendAsync(string key, string title, string message, string priority, ILogger logger)
    {
        var config = Plugin.Instance?.Configuration;
        var url = config?.NtfyUrl?.Trim();
        if (string.IsNullOrEmpty(url))
            return;

        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_lastSent.TryGetValue(key, out var last) && now - last < DedupeWindow)
                return;
            _lastSent[key] = now;
        }

        try
        {
            await SendAsync(url, config!.NtfyToken, title, message, priority).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Let the next occurrence retry instead of staying silent for 24h.
            lock (_lock) { _lastSent.Remove(key); }
            logger.LogWarning("Failed to send ntfy notification: {Message}", ex.Message);
        }
    }
}
