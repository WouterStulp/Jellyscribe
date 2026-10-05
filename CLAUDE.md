# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Jellyfin plugin ("Jellyscribe") that syncs watch history to Letterboxd (film) and Serializd (TV). C#/.NET 9, targets Jellyfin 10.11 (`Jellyfin.Controller`/`Jellyfin.Model` 10.11.0). Letterboxd's official endpoint (`/api/v0/production-log-entries`) is preferred; the plugin falls back to web scraping (cookie login, CSRF tokens, HtmlAgilityPack) when the API path fails. Serializd's API needs no such fallback. The C# namespace, project folder, and solution file all still say `LetterboxdSync` (pre-rebrand name); only the compiled `AssemblyName` and every user-visible surface say Jellyscribe.

The sidebar link in the Jellyfin web UI is injected by `SidebarScriptStartupFilter`, an `IStartupFilter` middleware that adds the `sidebar.js` tag to the web client's index page at request time (the same approach as Jellyfin Enhanced), so no other plugin is needed. The third-party **File Transformation** plugin, when installed, injects the same tag; both paths share `SidebarScript.Inject`, which never adds a second copy. `sidebar.js` is the whole client: it adds the entry points (the sidebar before `.btnSettings` on 10.11, a clone of the avatar menu's Settings item in `#app-user-menu` on 12) and the in-app page at `#/jellyscribe`, which mounts `userPage.html` (fetched from Jellyfin's own `web/ConfigurationPage?name=letterboxduser`) into a `.mainAnimatedPage` of its own, the pattern Jellyfin Enhanced's Bookmarks uses. Capture-phase `hashchange`/`popstate` listeners keep Jellyfin's router from rendering "Page not found" for the route; the dashboard is unmounted when the page is left; anything that stops it mounting falls back to the configuration page. No JS test harness exists: verify client changes with a Playwright run against throwaway Jellyfin 10.11 (with a base URL) and 12 containers.

## Build & Test

```bash
dotnet build -c Release
dotnet test  -c Release --verbosity normal
```

Run a single test class or method (xUnit, `dotnet test` filter syntax):

```bash
dotnet test --filter "FullyQualifiedName~ScraperTests"
dotnet test --filter "FullyQualifiedName~ScraperTests.LookupBySlug_Returns_Result"
```

CI also collects coverage via `--collect:"XPlat Code Coverage"` into `TestResults/`; Codecov consumes the Cobertura XML.

Deploy a debug build to the local Jellyfin server: `./deploy.sh` (scp's `Jellyscribe.dll` + `HtmlAgilityPack.dll` and restarts the container).

## Architecture

### Service abstraction with fallback

`ILetterboxdService` (`ILetterboxdService.cs`) is the seam every caller uses. Two implementations:

- `LetterboxdApiClient`, preferred, talks to Letterboxd's JSON endpoints.
- `ScrapingLetterboxdService`, fallback, composes `LetterboxdHttpClient` (cookies/CSRF/Cloudflare retry), `LetterboxdAuth` (login + re-auth on 401), `LetterboxdScraper` (HTML parsing, film lookup, diary/watchlist scraping), and `LetterboxdDiary` (diary writes, review posting).

`LetterboxdServiceFactory.CreateAuthenticatedAsync` tries the API first and silently falls back to scraping if auth fails. That is the only fallback: a method that throws later is never retried on the other implementation, so a capability gap must be handled when the service is selected, not at call time. `FilmResult.FilmId` is the LID on the API path and the numeric id on the scraping path; pass it only back to the instance that returned it. The factory also exposes an `internal static OverrideForTesting` hook used via `InternalsVisibleTo` from `LetterboxdSync.Tests` to inject mock services, production code never touches it.

### Sync entry points

- `SyncTask`, scheduled, exports recent watches to the Letterboxd diary.
- `Serializd/SerializdSyncTask` / `SerializdSyncRunner`, scheduled, exports played episodes to Serializd.
- `WatchlistSyncTask` / `WatchlistSyncRunner`, imports the user's Letterboxd watchlist as a Jellyfin playlist.
- `DiaryImportTask`, marks Jellyfin items as played if present in the Letterboxd diary.
- `PlaybackHandler`, `IHostedService` registered in `ServiceRegistrator`, fires the real-time sync on playback completion.
- `RatingSyncHandler`, `IHostedService` registered beside it, subscribes to `IUserDataManager.UserDataSaved` and pushes movie rating changes to the member's Letterboxd film rating (`ILetterboxdService.SetFilmRatingAsync`, not a diary entry). Numeric ratings save with `UpdateUserData` and favorite/like toggles with `UpdateUserRating`, and the event carries no previous value, so it seeds every user's current movie ratings in memory at startup and queues only saves whose rating differs from that baseline; `RatingPushStore` (last value pushed per account, persisted) then gates each push; plugin-originated rating writes (diary import, the review-modal writeback) save with `Import`, which it ignores. Debounced 10s per (user, film) by one sweep loop that reuses one login per account per pass, paces pushes, and gives a failed push up to 3 attempts in total, with backoff (auth failures are not retried; they feed the breaker). Successes record `SyncStatus.Rated` (never `Success`, which the diary duplicate backstop and stats read); failures are logged only, because `Failed` events feed the diary runner's per-film abandon counter.
- `LetterboxdSyncRunner`, shared engine used by `SyncTask` and `PlaybackHandler`; `SyncGate`, `SyncHistory`, `SyncProgress`, and `TmdbCache` coordinate dedupe, progress UI, and TMDb lookups.
- `LibraryExclusion.IsExcluded` is the one per-account "excluded library" rule (issue #124). Every export path (both scheduled runners, `PlaybackHandler`, and `RatingSyncHandler`) calls it before handing an item to a service client, so it is a pre-filter above `ILetterboxdService`, never a check inside either implementation. Import paths deliberately ignore it.

### Plugin surface

- `Plugin.cs` + `ServiceRegistrator.cs` register services and config.
- `Api/LetterboxdController.cs` and `Api/SidebarController.cs` expose REST endpoints consumed by the config dashboard. `LetterboxdController` also serves the read-only `ItemRating` endpoint the review modal uses to pre-fill its stars from the caller's stored Jellyfin rating.
- `Api/LibrariesController.cs` lists the film, TV, and mixed libraries the caller can access, for the per-account "Excluded libraries" checklist on both settings pages (Jellyfin's own `/Library/VirtualFolders` is admin-only).
- `Web/*.html` and `Web/*.js` are embedded resources (see `LetterboxdSync.csproj`) served as the plugin's config pages.
- `SidebarScriptStartupFilter.cs` injects the sidebar link without any other plugin (kill switch: `PluginConfiguration.DisableSidebarScriptMiddleware`, XML only). `SidebarInjection.cs` also registers the same injection with the File Transformation plugin when it is installed.

## Releasing

This is a standalone fork of builtbyproxy/Jellyscribe; never open PRs against upstream. Versions are the upstream version the fork is based on plus a fourth number (`2.10.0` → `2.10.0.1`).

Every change reaches `main` through a PR (branch protection enforces it); releases commit nothing.

1. Merge the PRs that should ship.
2. Publish a GitHub release whose tag is the version (`v2.10.0.4`) and whose text is the changelog: one paragraph of user-facing prose, no symbol names.
3. `.github/workflows/release.yml` runs on the published release. Its build job (read-only token) tests the tag and publishes it with `-p:AssemblyVersion`/`FileVersion` from the tag. Its publish job takes the previous release's `manifest.json`, adds the new version, and uploads the zip and manifest to the release. Jellyfin's plugin repository URL is `https://github.com/WouterStulp/Jellyscribe/releases/latest/download/manifest.json`.

The version in `Directory.Build.props` / the csproj only matters for local builds; releases take it from the tag.

- **SDK floor policy (issue #63)**: the `Jellyfin.Controller`/`Jellyfin.Model` PackageReference version MUST equal `targetAbi.txt`. Jellyfin assemblies have per-patch AssemblyVersions, so the SDK we compile against is the real minimum Jellyfin a release can load on. Bump it only when a newer API is needed, raising `targetAbi.txt` in the same PR.
- **Jellyfin 12 cliff (verified 2026-07-06)**: the 12.x SDK packages are net10.0-only and don't restore against this net9.0 project (NU1202). net9.0 builds run fine on Jellyfin 12 servers, but compiling against the 12 SDK forces net10.0, which can't load on 10.11's .NET 9 host, so adopting it is a one-way split of the release stream. `ci.yml`'s non-blocking probe job reports whether the code still builds against 12.
