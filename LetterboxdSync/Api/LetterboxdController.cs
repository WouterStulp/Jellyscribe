using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync.Api;

[ApiController]
[Authorize]
[Route("Jellyfin.Plugin.LetterboxdSync")]
[Produces(MediaTypeNames.Application.Json)]
public class LetterboxdController : JellyfinUserApiController
{
    private readonly ILogger<LetterboxdController> _logger;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IApplicationPaths _appPaths;
    private readonly LetterboxdSyncRunner _syncRunner;
    private readonly WatchlistSyncRunner _watchlistRunner;

    // Holds the most recent fire-and-forget sync task from StartSync/StartWatchlistSync.
    // Production code never reads it; tests await it so a background sync can't outlive
    // its test and hold SyncGate while the next test runs.
    internal Task? LastBackgroundSync { get; private set; }

    public LetterboxdController(
        ILogger<LetterboxdController> logger,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IApplicationPaths appPaths,
        LetterboxdSyncRunner syncRunner,
        WatchlistSyncRunner watchlistRunner)
        : base(userManager)
    {
        _logger = logger;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _appPaths = appPaths;
        _syncRunner = syncRunner;
        _watchlistRunner = watchlistRunner;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    [HttpGet("Progress")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetProgress()
    {
        return Ok(SyncProgress.GetSnapshot());
    }

    /// <summary>
    /// Trigger a sync for the calling user. Any logged-in user can call this; it only ever
    /// touches their own Letterboxd account. Returns 202 immediately and runs in the background;
    /// the UI polls /Progress for completion. Returns 409 if a sync is already running.
    /// </summary>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartSync([FromQuery] string? letterboxdUsername = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (LetterboxdSyncRunner.IsRunning)
            return Conflict(new { error = "Sync already running" });

        // When letterboxdUsername is supplied, target only that account. Otherwise the
        // runner fans out across all enabled accounts for this Jellyfin user.
        if (!string.IsNullOrEmpty(letterboxdUsername))
        {
            if (Config.FindAccount(userId, letterboxdUsername) == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{letterboxdUsername}' for your user" });
        }
        else if (!Config.GetEnabledAccountsForUser(userId).Any())
        {
            return BadRequest(new { error = "No enabled Letterboxd accounts are configured for your user" });
        }

        // Fire and forget. Errors during the run are logged by the runner; the UI polls /Progress.
        LastBackgroundSync = Task.Run(async () =>
        {
            try
            {
                await _syncRunner.TryRunForUserAsync(
                    userId, "manual", new Progress<double>(), System.Threading.CancellationToken.None,
                    letterboxdUsername).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User-triggered sync for {UserId} crashed", userId);
            }
        });

        return Accepted(new { started = true });
    }

    /// <summary>
    /// Trigger a Letterboxd → Jellyfin playlist (and optional Seerr) watchlist sync
    /// for the calling user. Same shape as <see cref="StartSync"/>: 202 immediately,
    /// 409 if any sync is in flight, 400 if the user has no watchlist-enabled account.
    /// </summary>
    [HttpPost("SyncWatchlist")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult StartWatchlistSync([FromQuery] string? letterboxdUsername = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (LetterboxdSyncRunner.IsRunning)
            return Conflict(new { error = "Sync already running" });

        // Validate up front so we can return a 400 instead of starting an empty run.
        if (!string.IsNullOrEmpty(letterboxdUsername))
        {
            var account = Config.FindAccount(userId, letterboxdUsername);
            if (account == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{letterboxdUsername}' for your user" });
            if (!account.EnableWatchlistSync)
                return BadRequest(new { error = $"Watchlist sync is disabled for '{letterboxdUsername}'; enable it in Settings first" });
        }
        else
        {
            var enabled = Config.GetEnabledAccountsForUser(userId).Where(a => a.EnableWatchlistSync).ToList();
            if (enabled.Count == 0)
                return BadRequest(new { error = "No enabled accounts with watchlist sync turned on for your user" });
        }

        LastBackgroundSync = Task.Run(async () =>
        {
            try
            {
                await _watchlistRunner.TryRunForUserAsync(
                    userId, "manual", new Progress<double>(), System.Threading.CancellationToken.None,
                    letterboxdUsername).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "User-triggered watchlist sync for {UserId} crashed", userId);
            }
        });

        return Accepted(new { started = true });
    }

    [HttpGet("Stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetStats()
    {
        // SyncHistory treats a null username as "everyone", so an unresolved caller must stop here.
        var jellyfinUsername = GetJellyfinUsername();
        if (string.IsNullOrEmpty(jellyfinUsername))
            return BadRequest(new { error = "Could not determine user" });

        var (total, success, failed, skipped, rewatches, requested) = SyncHistory.GetStats(jellyfinUsername);
        return Ok(new
        {
            total,
            success,
            failed,
            skipped,
            rewatches,
            requested,
            watchlist = WatchlistStats.GetFilm(GetCurrentUserId() ?? string.Empty)
        });
    }

    /// <summary>
    /// Paginated sync history. Returns the slice plus the total so the dashboard can
    /// render a paginator. Without an offset the response is backwards-compatible with
    /// pre-pagination clients that just consumed the array, since callers that ignore
    /// the wrapper still get the most recent items via /History?count=N.
    /// </summary>
    [HttpGet("History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetHistory([FromQuery] int count = 50, [FromQuery] int offset = 0)
    {
        var jellyfinUsername = GetJellyfinUsername();
        if (string.IsNullOrEmpty(jellyfinUsername))
            return BadRequest(new { error = "Could not determine user" });

        var capped = Math.Min(Math.Max(count, 1), 200);
        var (events, total) = SyncHistory.GetPage(Math.Max(offset, 0), capped, jellyfinUsername);
        return Ok(new { events, total, offset = Math.Max(offset, 0), count = capped });
    }

    [HttpGet("Account")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetAccount()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var account = Config.Accounts.FirstOrDefault(a => a.UserJellyfinId == userId);
        if (account == null)
        {
            return Ok(new
            {
                letterboxdUsername = string.Empty,
                hasPassword = false,
                hasCookies = false,
                userAgent = (string?)null,
                enabled = false,
                syncFavorites = false,
                syncRatings = true,
                enableDateFilter = false,
                dateFilterDays = 7,
                enableWatchlistSync = false,
                enableDiaryImport = false,
                autoRequestWatchlist = false,
                mirrorJellyseerrWatchlist = false,
                skipPreviouslySynced = true,
                stopOnFailure = false,
                excludedLibraryIds = new List<string>(),
                isConfigured = false
            });
        }

        return Ok(new
        {
            letterboxdUsername = account.LetterboxdUsername,
            hasPassword = account.HasPassword,
            hasCookies = account.HasRawCookies,
            userAgent = account.UserAgent,
            enabled = account.Enabled,
            syncFavorites = account.SyncFavorites,
            syncRatings = account.SyncRatings,
            enableDateFilter = account.EnableDateFilter,
            dateFilterDays = account.DateFilterDays,
            enableWatchlistSync = account.EnableWatchlistSync,
            enableDiaryImport = account.EnableDiaryImport,
            autoRequestWatchlist = account.AutoRequestWatchlist,
            backfillAvailableRequests = account.BackfillAvailableRequests,
            mirrorJellyseerrWatchlist = account.MirrorJellyseerrWatchlist,
            skipPreviouslySynced = account.SkipPreviouslySynced,
            stopOnFailure = account.StopOnFailure,
            excludedLibraryIds = account.ExcludedLibraryIds,
            isConfigured = true
        });
    }

    /// <summary>
    /// Open auth breakers across all users, for the admin dashboard's paused badges.
    /// Admin-only: the payload names other users' Letterboxd accounts.
    /// </summary>
    [HttpGet("AuthBreakers")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetAuthBreakers()
    {
        var open = AuthBreaker.GetOpenEntries().Select(e => new
        {
            userJellyfinId = e.UserJellyfinId,
            letterboxdUsername = e.LetterboxdUsername,
            failingSinceUtc = e.FirstFailureUtc
        });
        return Ok(new { breakers = open });
    }

    /// <summary>Test-only replacements for the two login attempts in <see cref="VerifyLogin"/>.</summary>
    internal static Func<string, string, Task>? VerifyApiLoginForTesting;

    internal static Func<string, string, string?, string?, Task>? VerifyWebsiteLoginForTesting;

    /// <summary>
    /// Message when a Letterboxd account name is an email address, else null. Letterboxd's API
    /// rejects email sign-in ("Sign-in via email address has been disabled"), and the website
    /// path builds diary URLs from the username, so an email never works.
    /// </summary>
    internal static string? EmailAsUsernameError(string? username) =>
        username != null && username.Contains('@')
            ? "Letterboxd no longer accepts an email address to sign in. Use your Letterboxd username, the name in letterboxd.com/<username>/."
            : null;

    /// <summary>
    /// Message when a Letterboxd username is already linked to another Jellyfin user, else null.
    /// Letting a second user save it would let them sync, review and rate as that account.
    /// </summary>
    private static string? LinkedToOtherUserError(string userId, string? username) =>
        !string.IsNullOrWhiteSpace(username) && Config.Accounts.Any(a => a.UserJellyfinId != userId
            && string.Equals(a.LetterboxdUsername?.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
            ? $"The Letterboxd account '{username.Trim()}' is already linked to another Jellyfin user."
            : null;

    /// <summary>Letterboxd's own reason (an OAuth error_description) when present, else the sanitised message.</summary>
    internal static string DescribeLoginError(Exception ex)
    {
        var match = System.Text.RegularExpressions.Regex.Match(ex.Message, "\"error_description\"\\s*:\\s*\"([^\"]+)\"");
        return match.Success ? match.Groups[1].Value : AuthBreaker.Sanitize(ex.Message) ?? "Unknown error";
    }

    /// <summary>
    /// Checks Letterboxd credentials the way sync will use them: the official API first, then the
    /// website login (with the optional raw cookies and user agent). Reports which one worked, or
    /// both reasons. Saves nothing and does not touch the auth breaker. An empty password or cookie
    /// field uses the stored account with this username, so a saved login can be re-checked
    /// without the secret ever going back to the browser.
    /// </summary>
    [HttpPost("Verify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> VerifyLogin([FromBody] LetterboxdVerifyRequest request)
    {
        var username = request?.LetterboxdUsername?.Trim();
        if (string.IsNullOrEmpty(username))
            return BadRequest(new { error = "Username and password are required." });

        var stored = Config.FindStored(GetCredentialOwnerId(request!.UserJellyfinId) ?? string.Empty, username);
        var password = SecretMerge.KeepIfEmpty(request.LetterboxdPassword, stored?.LetterboxdPassword);
        var rawCookies = request.ClearRawCookies ? null : SecretMerge.KeepIfEmpty(request.RawCookies, stored?.RawCookies);
        if (string.IsNullOrEmpty(password))
            return BadRequest(new { error = "Username and password are required." });

        var emailError = EmailAsUsernameError(username);
        if (emailError != null)
            return BadRequest(new { error = emailError });

        string apiError;
        try
        {
            if (VerifyApiLoginForTesting != null)
            {
                await VerifyApiLoginForTesting(username, password).ConfigureAwait(false);
            }
            else
            {
                using var api = new LetterboxdApiClient(_logger);
                await api.AuthenticateAsync(username, password).ConfigureAwait(false);
            }

            return Ok(new { ok = true, via = "api" });
        }
        catch (Exception ex)
        {
            apiError = DescribeLoginError(ex);
        }

        try
        {
            if (VerifyWebsiteLoginForTesting != null)
            {
                await VerifyWebsiteLoginForTesting(username, password, rawCookies, request.UserAgent).ConfigureAwait(false);
            }
            else
            {
                using var website = new ScrapingLetterboxdService(_logger, request.UserAgent);
                await website.AuthenticateAsync(username, password, rawCookies).ConfigureAwait(false);
            }

            return Ok(new { ok = true, via = "website", apiError });
        }
        catch (Exception ex)
        {
            var websiteError = DescribeLoginError(ex);
            _logger.LogWarning("Letterboxd credential verification failed for {LbUser}: API: {ApiError}; website: {WebsiteError}",
                username, apiError, websiteError);
            return BadRequest(new { error = "Login failed.", apiError, websiteError });
        }
    }

    [HttpPut("Account")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PutAccount([FromBody] AccountUpdateRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (EmailAsUsernameError(request.LetterboxdUsername) is { } emailError)
            return BadRequest(new { error = emailError });

        if (LinkedToOtherUserError(userId, request.LetterboxdUsername) is { } linkedError)
            return BadRequest(new { error = linkedError });

        var account = Config.Accounts.FirstOrDefault(a => a.UserJellyfinId == userId);
        if (account == null)
        {
            account = new Account { UserJellyfinId = userId };
            Config.Accounts.Add(account);
        }

        // Empty secrets keep the stored ones, but only for the same login: a new username starts empty.
        var sameLogin = string.Equals(account.LetterboxdUsername, request.LetterboxdUsername, StringComparison.OrdinalIgnoreCase);
        var secrets = new Account
        {
            LetterboxdPassword = request.LetterboxdPassword ?? string.Empty,
            RawCookies = request.RawCookies,
            ClearRawCookies = request.ClearRawCookies
        };
        secrets.KeepSecretsFrom(sameLogin ? account : null);

        account.LetterboxdUsername = request.LetterboxdUsername;
        account.LetterboxdPassword = secrets.LetterboxdPassword;
        account.RawCookies = secrets.RawCookies;
        account.UserAgent = request.UserAgent;
        account.Enabled = request.Enabled;
        account.SyncFavorites = request.SyncFavorites;
        account.SyncRatings = request.SyncRatings ?? account.SyncRatings;
        account.EnableDateFilter = request.EnableDateFilter;
        account.DateFilterDays = request.DateFilterDays;
        account.EnableWatchlistSync = request.EnableWatchlistSync;
        account.EnableDiaryImport = request.EnableDiaryImport;
        account.AutoRequestWatchlist = request.AutoRequestWatchlist;
        account.BackfillAvailableRequests = request.BackfillAvailableRequests;
        account.MirrorJellyseerrWatchlist = request.MirrorJellyseerrWatchlist;
        account.SkipPreviouslySynced = request.SkipPreviouslySynced;
        account.StopOnFailure = request.StopOnFailure;
        account.ExcludedLibraryIds = LibraryExclusion.ResolveForSave(request.ExcludedLibraryIds, account.ExcludedLibraryIds);

        // IsPrimary and PlaylistName are deliberately NOT copied from the request.
        // The userPage form does not expose them; deserialisation would set them to
        // their type defaults (false / null) and clobber values that only the admin
        // config page sets. Treat them as admin-managed and let NormalisePrimaryFlags
        // promote a new account to primary when it's the user's only enabled one.
        Config.NormalisePrimaryFlags();

        Plugin.Instance!.SaveConfiguration();

        // Credentials were just (re-)persisted: close any open auth breaker so the
        // next run attempts login with the new values (issue #103's reset path).
        AuthBreaker.Reset(userId, account.LetterboxdUsername);

        _logger.LogInformation("User {UserId} saved their Letterboxd account settings", userId);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Returns every Letterboxd account belonging to the calling Jellyfin user, in
    /// config order with primary first. Multi-account companion to the single-account
    /// /Account endpoint: the userPage uses this to render the full list of accounts
    /// the user can edit on their own sidebar page.
    /// </summary>
    [HttpGet("Accounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetAccounts()
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var accounts = Config.Accounts
            .Where(a => a.UserJellyfinId == userId)
            .OrderByDescending(a => a.IsPrimary)
            .Select(a => new
            {
                letterboxdUsername = a.LetterboxdUsername,
                hasPassword = a.HasPassword,
                hasCookies = a.HasRawCookies,
                userAgent = a.UserAgent,
                authPaused = AuthBreaker.IsOpen(userId, a.LetterboxdUsername),
                authPausedSince = AuthBreaker.GetState(userId, a.LetterboxdUsername)?.FirstFailureUtc,
                enabled = a.Enabled,
                syncFavorites = a.SyncFavorites,
                syncRatings = a.SyncRatings,
                enableDateFilter = a.EnableDateFilter,
                dateFilterDays = a.DateFilterDays,
                enableWatchlistSync = a.EnableWatchlistSync,
                enableDiaryImport = a.EnableDiaryImport,
                autoRequestWatchlist = a.AutoRequestWatchlist,
                backfillAvailableRequests = a.BackfillAvailableRequests,
                mirrorJellyseerrWatchlist = a.MirrorJellyseerrWatchlist,
                skipPreviouslySynced = a.SkipPreviouslySynced,
                stopOnFailure = a.StopOnFailure,
                isPrimary = a.IsPrimary,
                playlistName = a.PlaylistName,
                excludedLibraryIds = a.ExcludedLibraryIds
            })
            .ToList();

        return Ok(new { accounts });
    }

    /// <summary>
    /// Bulk-replace the calling user's set of Letterboxd accounts. Accounts owned by
    /// other Jellyfin users are preserved (security: a non-admin user cannot touch
    /// another user's row). Each submitted account is stamped with the calling user's
    /// id regardless of what the request body claimed. NormalisePrimaryFlags runs after
    /// so the single-primary-per-user invariant holds across the save.
    /// </summary>
    [HttpPut("Accounts")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult PutAccounts([FromBody] AccountsUpdateRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        if (request?.Accounts == null)
            return BadRequest(new { error = "accounts is required" });

        // Reject empty username up front so a malformed row doesn't silently produce
        // a half-broken account that fails authentication later.
        for (var i = 0; i < request.Accounts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(request.Accounts[i].LetterboxdUsername))
                return BadRequest(new { error = $"Account #{i + 1} is missing a Letterboxd username" });
            if (EmailAsUsernameError(request.Accounts[i].LetterboxdUsername) is { } emailError)
                return BadRequest(new { error = emailError });
            if (LinkedToOtherUserError(userId, request.Accounts[i].LetterboxdUsername) is { } linkedError)
                return BadRequest(new { error = linkedError });
        }

        // Preserve every account that doesn't belong to the calling user. The admin
        // page is the only path that should touch other users' rows; this endpoint
        // is per-user scope.
        var preserved = Config.Accounts.Where(a => a.UserJellyfinId != userId).ToList();
        var previous = Config.Accounts.Where(a => a.UserJellyfinId == userId).ToList();

        var mine = new List<Account>();
        foreach (var req in request.Accounts)
        {
            var account = new Account
            {
                UserJellyfinId = userId,
                LetterboxdUsername = req.LetterboxdUsername,
                LetterboxdPassword = req.LetterboxdPassword ?? string.Empty,
                RawCookies = req.RawCookies,
                ClearRawCookies = req.ClearRawCookies,
                UserAgent = req.UserAgent,
                Enabled = req.Enabled,
                SyncFavorites = req.SyncFavorites,
                SyncRatings = req.SyncRatings
                    ?? previous.FirstOrDefault(p => string.Equals(p.LetterboxdUsername, req.LetterboxdUsername, StringComparison.OrdinalIgnoreCase))?.SyncRatings
                    ?? true,
                EnableDateFilter = req.EnableDateFilter,
                DateFilterDays = req.DateFilterDays,
                EnableWatchlistSync = req.EnableWatchlistSync,
                EnableDiaryImport = req.EnableDiaryImport,
                AutoRequestWatchlist = req.AutoRequestWatchlist,
                BackfillAvailableRequests = req.BackfillAvailableRequests,
                MirrorJellyseerrWatchlist = req.MirrorJellyseerrWatchlist,
                SkipPreviouslySynced = req.SkipPreviouslySynced,
                StopOnFailure = req.StopOnFailure,
                IsPrimary = req.IsPrimary,
                PlaylistName = string.IsNullOrWhiteSpace(req.PlaylistName) ? null : req.PlaylistName.Trim(),
                // A client that omits the field keeps the account's stored exclusions.
                ExcludedLibraryIds = LibraryExclusion.ResolveForSave(req.ExcludedLibraryIds,
                    previous.FirstOrDefault(p => string.Equals(p.LetterboxdUsername, req.LetterboxdUsername, StringComparison.OrdinalIgnoreCase))?.ExcludedLibraryIds)
            };
            account.KeepSecretsFrom(previous.FirstOrDefault(p => string.Equals(p.LetterboxdUsername, req.LetterboxdUsername, StringComparison.OrdinalIgnoreCase)));
            mine.Add(account);
        }

        Config.Accounts.Clear();
        Config.Accounts.AddRange(preserved);
        Config.Accounts.AddRange(mine);

        Config.NormalisePrimaryFlags();
        Plugin.Instance!.SaveConfiguration();

        // Credentials were just (re-)persisted for every submitted account: close any
        // open auth breakers so the next run retries login (issue #103's reset path).
        foreach (var saved in mine)
            AuthBreaker.Reset(userId, saved.LetterboxdUsername);

        _logger.LogInformation("User {UserId} saved {Count} Letterboxd account(s) via /Accounts", userId, mine.Count);
        return Ok(new { success = true, count = mine.Count });
    }

    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestConnection([FromBody] TestConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LetterboxdUsername))
            return BadRequest(new { success = false, error = "Username and password are required" });

        // Empty secrets fall back to the caller's own stored account with this username.
        var stored = Config.FindStored(GetCurrentUserId() ?? string.Empty, request.LetterboxdUsername);
        var password = SecretMerge.KeepIfEmpty(request.LetterboxdPassword, stored?.LetterboxdPassword);
        if (string.IsNullOrWhiteSpace(password))
            return BadRequest(new { success = false, error = "Username and password are required" });

        try
        {
            using var service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                request.LetterboxdUsername, password, SecretMerge.KeepIfEmpty(request.RawCookies, stored?.RawCookies), _logger, request.UserAgent)
                .ConfigureAwait(false);

            return Ok(new { success = true, letterboxdUsername = request.LetterboxdUsername });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Test connection failed for {Username}: {Message}", request.LetterboxdUsername, ex.Message);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Checks the Seerr URL and API key from the admin settings form. Admin-only: it makes the
    /// server GET any URL and echoes the error back.
    /// </summary>
    [HttpPost("TestJellyseerr")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestJellyseerr([FromBody] JellyseerrTestRequest request)
    {
        // An empty key uses the stored one, but only against the stored URL: any signed-in user can
        // call this, and the key must never be sent to an address the admin didn't save it for.
        var apiKey = string.IsNullOrEmpty(request.ApiKey) && SecretMerge.IsStoredUrl(request.Url, Config.JellyseerrUrl)
            ? Config.JellyseerrApiKey
            : request.ApiKey;
        if (!SeerrClient.IsConfigured(request.Url, apiKey))
            return BadRequest(new { success = false, error = "URL and API key are required" });

        try
        {
            using var client = new SeerrClient(request.Url!, apiKey!, _logger);
            var userId = await client.GetJellyseerrUserIdAsync(GetCurrentUserId() ?? string.Empty)
                .ConfigureAwait(false);
            return Ok(new
            {
                success = true,
                linkedToCurrentUser = userId.HasValue,
                jellyseerrUserId = userId
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Seerr test failed: {Message}", ex.Message);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Sends a test ntfy message using the URL and token from the settings form, so the admin
    /// can check them before saving. Admin-only: it makes the server POST to any URL. An empty
    /// token uses the stored one when the URL is the stored URL.
    /// </summary>
    [HttpPost("Notifications/Test")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestNotification([FromBody] NtfyTestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
            return BadRequest(new { success = false, error = "ntfy URL is required" });

        try
        {
            var token = string.IsNullOrEmpty(request.Token) && SecretMerge.IsStoredUrl(request.Url, Config.NtfyUrl)
                ? Config.NtfyToken
                : request.Token;
            await Notifier.SendAsync(request.Url.Trim(), token, "Jellyscribe: test notification",
                "Notifications from Jellyscribe work. You'll get a message here when syncing needs attention.",
                "default").ConfigureAwait(false);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("ntfy test failed: {Message}", ex.Message);
            return BadRequest(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("Review")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> PostReview([FromBody] ReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilmSlug))
            return BadRequest(new { error = "filmSlug is required" });

        if (string.IsNullOrWhiteSpace(request.ReviewText) && !request.IsRewatch)
            return BadRequest(new { error = "reviewText is required unless logging a rewatch" });

        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        // When LetterboxdUsername is set, post under that single account. When it's
        // null/empty, fan out to every enabled account for this Jellyfin user, so
        // shared TV-user setups (e.g. Lachlan + Deb) get the review on both diaries.
        List<Account> accounts;
        if (!string.IsNullOrWhiteSpace(request.LetterboxdUsername))
        {
            var single = Config.FindAccount(userId, request.LetterboxdUsername!);
            if (single == null)
                return BadRequest(new { error = $"No enabled Letterboxd account '{request.LetterboxdUsername}' for this user" });
            accounts = new List<Account> { single };
        }
        else
        {
            accounts = Config.GetEnabledAccountsForUser(userId).ToList();
            if (accounts.Count == 0)
                return BadRequest(new { error = "No Letterboxd accounts configured for this user" });
        }

        var jellyfinUsername = GetJellyfinUsername() ?? userId;
        var perAccount = new List<object>();
        var anySuccess = false;
        Exception? lastError = null;

        foreach (var account in accounts)
        {
            try
            {
                using var service = await LetterboxdServiceFactory.CreateAuthenticatedAsync(
                    account.LetterboxdUsername, account.LetterboxdPassword, account.RawCookies, _logger, account.UserAgent)
                    .ConfigureAwait(false);

                await service.PostReviewAsync(request.FilmSlug, request.ReviewText, request.ContainsSpoilers, request.IsRewatch, request.Date, request.Rating, request.TmdbId)
                    .ConfigureAwait(false);

                _logger.LogInformation("Posted review for {FilmSlug} by {Username}",
                    request.FilmSlug, account.LetterboxdUsername);

                var status = request.IsRewatch ? SyncStatus.Rewatch : SyncStatus.Success;
                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = request.FilmSlug.Replace("-", " "),
                    FilmSlug = request.FilmSlug,
                    Username = jellyfinUsername,
                    Timestamp = DateTime.UtcNow,
                    Status = status,
                    Source = "review"
                });

                perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = true });
                anySuccess = true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogError("Failed to post review for {FilmSlug} as {LbUser}: {Message}",
                    request.FilmSlug, account.LetterboxdUsername, ex.Message);

                SyncHistory.Record(new SyncEvent
                {
                    FilmTitle = request.FilmSlug.Replace("-", " "),
                    FilmSlug = request.FilmSlug,
                    Username = jellyfinUsername,
                    Timestamp = DateTime.UtcNow,
                    Status = SyncStatus.Failed,
                    Error = ex.Message,
                    Source = "review"
                });

                perAccount.Add(new { letterboxdUsername = account.LetterboxdUsername, success = false, error = ex.Message });
            }
        }

        // Mirror the rating to Jellyfin once if any account accepted it; the rating
        // belongs to the Jellyfin user, not to a specific Letterboxd account, so a
        // single writeback is correct regardless of how many accounts we posted to.
        if (anySuccess)
            WriteJellyfinRating(userId, request.TmdbId, request.Rating);

        if (!anySuccess && lastError != null)
            return BadRequest(new { error = lastError.Message, accounts = perAccount });

        return Ok(new { success = true, accounts = perAccount });
    }

    /// <summary>
    /// Returns the most recent LetterboxdSync log lines from Jellyfin's log files,
    /// for in-dashboard debugging and "send me your logs" support flows.
    /// Reads only LetterboxdSync-tagged lines so users can share without leaking
    /// unrelated server activity. The plugin already redacts review text and never
    /// logs auth tokens, passwords, or cookies, so this output is safe to share.
    /// </summary>
    [HttpGet("Logs")]
    [Authorize(Policy = "RequiresElevation")] // raw server logs name every user's Letterboxd account + watched films; admin-only
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLogs([FromQuery] int maxLines = 500)
    {
        var cap = Math.Min(Math.Max(maxLines, 1), 5000);
        var (all, source, error) = LogReader.ReadRecentLogLines(_appPaths, _logger);
        if (error != null)
            return Ok(new { lines = Array.Empty<string>(), source = (string?)null, error });

        var trimmed = all.Count > cap ? all.GetRange(all.Count - cap, cap) : all;
        return Ok(new { lines = trimmed, totalMatches = all.Count, returned = trimmed.Count, source });
    }

    /// <summary>
    /// Returns the calling user's stored Jellyfin rating for an item, both raw
    /// (1-10) and mapped to Letterboxd half-stars, so the review modal can
    /// pre-fill its star widget. Movies resolve by TMDb id; TV resolves the
    /// series by TMDb id, or a single episode when season and episode numbers
    /// are supplied (matching how Serializd sync sources episode vs show
    /// ratings). Always 200 with null fields when the item or rating is absent:
    /// the pre-fill fetch must never surface an error in the modal.
    /// </summary>
    [HttpGet("ItemRating")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult GetItemRating(
        [FromQuery] int? tmdbId = null,
        [FromQuery] bool isShow = false,
        [FromQuery] int? seasonNumber = null,
        [FromQuery] int? episodeNumber = null)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(userId))
            return BadRequest(new { error = "Could not determine user" });

        var none = Ok(new { rating = (double?)null, stars = (double?)null });
        if (!tmdbId.HasValue || tmdbId.Value <= 0)
            return none;

        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return none;

        BaseItem? item;
        if (!isShow)
            item = FindMovieByTmdbId(user, tmdbId.Value);
        else if (seasonNumber.HasValue && episodeNumber.HasValue)
            item = FindEpisodeByTmdbId(user, tmdbId.Value, seasonNumber.Value, episodeNumber.Value);
        else
            item = FindSeriesByTmdbId(user, tmdbId.Value);

        if (item == null)
            return none;

        var stored = _userDataManager.GetUserData(user, item)?.Rating;
        if (!stored.HasValue || stored.Value <= 0)
            return none;

        return Ok(new { rating = (double?)stored.Value, stars = Helpers.MapRating(stored.Value) });
    }

    /// <summary>
    /// Resolve a library movie by TMDb id for the given user. Shared by the
    /// rating writeback and the ItemRating read path so the two cannot drift.
    /// </summary>
    private BaseItem? FindMovieByTmdbId(User user, int tmdbId)
        => FindByTmdbId(user, BaseItemKind.Movie, tmdbId).FirstOrDefault();

    private BaseItem? FindSeriesByTmdbId(User user, int tmdbId)
        => FindByTmdbId(user, BaseItemKind.Series, tmdbId).FirstOrDefault();

    /// <summary>
    /// Resolve an episode by series TMDb id + season/episode number. The series
    /// TMDb id lives on the parent Series entity, so the series is resolved first
    /// and its episodes are then queried by number.
    /// </summary>
    private BaseItem? FindEpisodeByTmdbId(User user, int seriesTmdbId, int seasonNumber, int episodeNumber)
    {
        var seriesIds = FindByTmdbId(user, BaseItemKind.Series, seriesTmdbId).Select(s => s.Id).ToArray();
        if (seriesIds.Length == 0)
            return null;

        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
            AncestorIds = seriesIds,
            ParentIndexNumber = seasonNumber,
            IndexNumber = episodeNumber
        }).OfType<MediaBrowser.Controller.Entities.TV.Episode>()
          .FirstOrDefault(ep => ep.ParentIndexNumber == seasonNumber && ep.IndexNumber == episodeNumber);
    }

    /// <summary>
    /// Library items of one kind carrying the given TMDb id, filtered in the database
    /// instead of loading the whole library. The id is re-checked in memory, so a query
    /// that ever came back looser could not resolve (and write a rating to) the wrong item.
    /// </summary>
    private IEnumerable<BaseItem> FindByTmdbId(User user, BaseItemKind kind, int tmdbId)
    {
        var id = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { kind },
            IsVirtualItem = false,
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { [MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString()] = id }
        }).Where(item => item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb) == id);
    }

    /// <summary>
    /// Mirror the dashboard review's star rating into Jellyfin's UserItemData.Rating
    /// so it survives plugin uninstall and is visible to other clients/plugins.
    /// Always overwrites: posting a review is the user's latest input, so it wins.
    /// No-op if the review had no rating, no TmdbId, or the film isn't in the library.
    /// </summary>
    private void WriteJellyfinRating(string userId, int? tmdbId, double? letterboxdRating)
    {
        if (!tmdbId.HasValue || !letterboxdRating.HasValue)
            return;

        var jellyfinRating = Helpers.LetterboxdToJellyfinRating(letterboxdRating);
        if (!jellyfinRating.HasValue)
            return;

        var user = _userManager.GetUsers().FirstOrDefault(u => u.Id.ToString("N") == userId);
        if (user == null)
            return;

        var movie = FindMovieByTmdbId(user, tmdbId.Value);

        if (movie == null)
        {
            _logger.LogDebug("Skipping Jellyfin rating writeback: TMDb {TmdbId} not in library", tmdbId.Value);
            return;
        }

        try
        {
            var userData = _userDataManager.GetUserData(user, movie);
            if (userData == null) return;

            userData.Rating = jellyfinRating;
            // Import, not UpdateUserRating: the value originates on the Letterboxd side (the review
            // post already carried it there), and RatingSyncHandler ignores Import saves, so this
            // mirror can never echo back out as a second push.
            _userDataManager.SaveUserData(user, movie, userData, UserDataSaveReason.Import, CancellationToken.None);

            _logger.LogInformation("Mirrored Letterboxd rating {LbRating} -> Jellyfin {JfRating} for {Title} ({UserId})",
                letterboxdRating.Value, jellyfinRating.Value, movie.Name, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to write Jellyfin rating for TMDb {TmdbId}: {Message}", tmdbId.Value, ex.Message);
        }
    }
}

public class ReviewRequest
{
    public string FilmSlug { get; set; } = string.Empty;
    public string? ReviewText { get; set; }
    public bool ContainsSpoilers { get; set; }
    public bool IsRewatch { get; set; }
    public string? Date { get; set; }
    public double? Rating { get; set; }
    public int? TmdbId { get; set; }

    /// <summary>
    /// Optional. When set, the review is posted only to that Letterboxd account.
    /// When null/empty, the review is fanned out to every enabled Letterboxd account
    /// for the calling Jellyfin user.
    /// </summary>
    public string? LetterboxdUsername { get; set; }
}
