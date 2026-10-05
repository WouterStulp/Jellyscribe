# Jellyscribe (WouterStulp fork)

[![GitHub release](https://img.shields.io/github/v/release/WouterStulp/Jellyscribe)](https://github.com/WouterStulp/Jellyscribe/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

> **This is a standalone fork** of [builtbyproxy/Jellyscribe](https://github.com/builtbyproxy/Jellyscribe), built and released from this repository. It tracks upstream and adds changes of its own (see [What this fork adds](#what-this-fork-adds)). Upstream's [website](https://jellyscribe.dev/) and [release notes](https://jellyscribe.dev/releases/) describe the upstream plugin, not this fork.

Automatically sync your Jellyfin watch history to your Letterboxd diary (films) and Serializd diary (TV). Titles are logged in real-time when you finish watching, with a daily scheduled sync as a safety net.

Uses Letterboxd's current JSON API (`/api/v0/production-log-entries`) and Serializd's API.

<img alt="Jellyscribe dashboard inside the Jellyfin admin UI, showing sync stats and recent activity" src="docs/images/dashboard.png" />

## What this fork adds

- **Serializd "Currently watching"**, a show is marked as currently watching on your Serializd profile when you start it in Jellyfin, and left alone once you've finished it
- **Shows Serializd keeps as one season**, later seasons are logged as continued episodes of that single season instead of failing
- **History survives a user rename**, sync history and duplicate checks are tied to the Jellyfin user id, so renaming a user no longer empties the dashboard

## Features

### Syncing

- **Real-time sync**, titles logged to your diary the moment you finish watching
- **Daily catch-up**, scheduled task picks up anything missed
- **Multi-user, multi-account**, each Jellyfin user can link their own Letterboxd account, Serializd account, or both
- **TMDb matching**, films and episodes matched by TMDb ID, so foreign titles and special characters work
- **Duplicate detection**, won't log the same title twice on the same day
- **Rewatch detection**, real-time playback automatically marks rewatches
- **Date filtering**, limit catch-up syncs to recently watched titles
- **Library exclusion**, keep whole libraries (an Anime library you track elsewhere, say) off a Letterboxd or Serializd account

### Ratings, reviews & diary

- **Rating sync, both ways**, Jellyfin ratings (0-10) map to Letterboxd stars (0.5-5.0) or Serializd's 1-10 scale, and ratings you set on either service seed your Jellyfin user rating back
- **Ratings after the watch**, rate a film any time after you've watched it (or without watching it) and, about ten seconds after you settle on a score, it becomes your Letterboxd rating for that film. Jellyfin's own web app has no star rating (only the favourite heart), so the easy way in is [Jellyfin Enhanced](https://github.com/n00bcodr/Jellyfin-Enhanced) 12.10+: in its settings turn on "Enable User Written Reviews" and "Also save review star ratings as the user's Jellyfin rating", and the stars on your reviews sync. Any other app that saves ratings to Jellyfin works too. Only changes are sent (ratings already in Jellyfin when you update are left alone until you change them), and a failed send is retried a few times. As on Letterboxd, rating a film marks it watched and removes it from your watchlist; clearing a rating in Jellyfin does not unrate it on Letterboxd. Some apps (Infuse, for one) never save ratings to Jellyfin, so their ratings can't be synced. Letterboxd only for now, not Serializd
- **Favorites**, sync Jellyfin favorites as Letterboxd likes or Serializd likes
- **Reviews**, write and post reviews to Letterboxd (films) or Serializd (shows or individual episodes) from the plugin dashboard
- **Diary import**, mark Jellyfin movies or episodes as played if they're already in your Letterboxd or Serializd diary

### TV shows → Serializd

Full feature parity with the Letterboxd side: real-time sync, ratings, reviews, favorites, diary import, rewatch detection, watchlist sync, and Seerr auto-request/backfill/mirror all work the same way for [Serializd](https://www.serializd.com) as they do for Letterboxd, just scoped to TV episodes instead of films.

- **TV sync**, finished TV episodes are logged to your Serializd watched list in real time, the TV counterpart to the Letterboxd film sync
- **Per-user accounts**, each Jellyfin user links their own Serializd account (by email or username), with a Verify login button and passwords encrypted at rest
- **Daily catch-up**, a "Sync watched TV to Serializd" scheduled task picks up anything real-time missed, plus a Sync TV Now button
- **TMDb matching**, episodes matched by their series' TMDb id + season/episode number
- **Isolated from Letterboxd**, films still sync to Letterboxd; a Serializd failure never blocks the Letterboxd path, or vice versa
- **No cookie fallback needed**, Serializd's API never needs the Cloudflare cookie workaround Letterboxd sometimes does (see [Cloudflare issues](#cloudflare-issues) below), so TV sync has nothing to babysit

### Watchlist & Seerr

- **Watchlist sync**, import your Letterboxd or Serializd watchlist as a Jellyfin playlist (Serializd also gets a Jellyfin collection for the shows themselves)
- **Seerr integration**, auto-request watchlisted films or shows missing from your library, attributed to the right user; optionally backfill requests for titles that arrived outside Seerr, and mirror your Letterboxd or Serializd watchlist into Seerr

### Dashboard & diagnostics

- **Dashboard**, sync stats, activity history, and one-click sync from the plugin page
- **Cloudflare resilient**, automatic retry with backoff on rate limits and transient Letterboxd errors, raw cookie fallback

## Install

### Plugin repository (recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add the Jellyscribe repository:
   - **Name:** `Jellyscribe (WouterStulp fork)`
   - **URL:** `https://raw.githubusercontent.com/WouterStulp/Jellyscribe/main/fork/manifest.json`
3. Remove the upstream Jellyscribe repository if you have it (any `lachlanbyoung.workers.dev` or `builtbyproxy` entry). The fork uses the same plugin id, so with both listed Jellyfin picks whichever has the higher version
4. Go to **Catalog** and install **Jellyscribe**
5. Restart Jellyfin
6. Hard-refresh the Jellyfin web UI (Ctrl/Cmd + Shift + R) so the new sidebar link loads

### Manual install

1. Download the latest Jellyscribe ZIP from [Releases](https://github.com/WouterStulp/Jellyscribe/releases)
2. Extract `Jellyscribe.dll` and `HtmlAgilityPack.dll` to your Jellyfin plugins directory
3. Restart Jellyfin

## Setup

1. Go to **Dashboard > Plugins > Jellyscribe**
2. Switch to the **Settings** tab
3. Click **+ Add Account**
4. Select your Jellyfin user, enter your Letterboxd **username** (the name in `letterboxd.com/<username>/`, not your email: Letterboxd no longer accepts email sign-in) and password
5. Click **Verify login** to check it works; it says whether the official API or the website login was used, or why both failed
6. Check **Enabled**
7. Click **Save**

That's it. Watch a movie and check your Letterboxd diary.

Adding a Serializd account works the same way, just enter your Serializd email/username and password instead; a Jellyfin user can link a Letterboxd account, a Serializd account, or both.

### Settings per account

These apply the same way whether the account is a Letterboxd (film) or Serializd (TV) account, just scoped to the matching media type.

| Setting | Description |
|---|---|
| **Enabled** | Master switch for this account; nothing syncs while unchecked, saved settings are kept |
| **Favorites as liked** | Marks the title as "liked" on Letterboxd or Serializd if favorited in Jellyfin |
| **Sync ratings to Letterboxd** | Letterboxd accounts only, on by default. Sends a film's rating to Letterboxd whenever you change it in Jellyfin, not just when the watch is logged |
| **Recently played only** | Limits daily catch-up to titles played in the last N days |
| **Primary account** | When one Jellyfin user links multiple accounts on the same service, the primary wins on rating-import conflicts and is preselected in the review modal |
| **Watchlist to playlist** | Mirrors your Letterboxd or Serializd watchlist into a Jellyfin playlist daily; each account gets its own playlist (name configurable) |
| **Auto-request via Seerr** | Watchlisted films or shows missing from your library are requested in Seerr, attributed to this user's Seerr account (set the Seerr URL and API key above the account list) |
| **Backfill available requests** | Extends auto-request to titles already in the library that have no request record, so titles that arrived outside Seerr still show a requester; never triggers re-downloads |
| **Mirror into Seerr watchlist** | Two-way mirror of your watchlist into your Seerr user's own watchlist (movies for Letterboxd accounts, TV for Serializd accounts) |
| **Import diary as played** | Marks Jellyfin movies or episodes as played if they appear in your Letterboxd or Serializd diary |
| **Skip previously synced** | Uses the plugin's local sync history to skip titles already logged without hitting Letterboxd/Serializd; recommended, especially on large libraries |
| **Excluded libraries** | Jellyfin libraries whose films or episodes are never logged to this account's diary (by the scheduled sync or the real-time one) and whose ratings are never sent. Applies to future syncs only; anything already logged stays on Letterboxd or Serializd. It governs exports only: diary import, watchlist sync, and Seerr requests still look at every library |
| **Stop on failure** | Halts the run at the first failure to avoid inflaming rate limits; the rest are picked up next run |
| **Raw Cookies** | For Cloudflare bypass, Letterboxd accounts only, see below |

### Dashboard

The **Dashboard** tab shows the same for both Letterboxd and Serializd accounts:
- Sync statistics (total, synced, rewatches, skipped, failed, requested)
- Recent activity with links to each title on Letterboxd or Serializd
- **Run Sync Now** button to trigger a sync on demand
- **Review** buttons to write and post reviews directly to Letterboxd or Serializd

### Cloudflare issues

If login fails with a 403 error:

1. Log into Letterboxd in your browser
2. Open DevTools (F12) > Network tab
3. Reload and click any request to `letterboxd.com`
4. Copy the **Cookie** header value (everything after `Cookie: `, not the label itself)
5. Paste it into the **Raw Cookies** field
6. Copy the **User-Agent** request header value from the same request and paste it into the **User-Agent** field

**Important:** Cloudflare ties `cf_clearance` to the exact User-Agent that solved the challenge. If you copied cookies from Chrome but leave the User-Agent field blank, the plugin sends the default Firefox UA and Cloudflare will reject the cookie. Always paste the User-Agent from the same browser you copied the cookies from. Leave it blank only if you copied cookies from Firefox 134 on Windows.

#### Still 403ing after pasting Raw Cookies and a matching User-Agent

When a correctly-copied cookie still gets blocked, it's usually one of these:

1. **Different IP.** Cloudflare pins `cf_clearance` to the IP address that solved the challenge, not just the User-Agent. If the box running Jellyfin reaches the internet via a different public IP than the browser did (different machine, VPN, mobile tether, a server in a datacenter), Cloudflare sees the token arrive from a new IP and rejects it. Fix: paste fresh cookies from a browser running on the **same network as the Jellyfin server**, and watch out for VPNs or split tunnels.

2. **It expired.** `cf_clearance` from a managed challenge is short-lived, often around 30 minutes. If there's a gap between copying the cookies and the sync actually running, the token can already be dead. Fix: paste fresh cookies and immediately trigger a sync from the plugin dashboard rather than waiting for the scheduled run.

3. **Connection fingerprint.** Cloudflare doesn't only check the cookie and UA, it also fingerprints the TLS handshake and HTTP/2 behaviour of the connection. A plugin's HTTP client doesn't look like a real browser at that layer, so on a site running bot-fight mode the right cookie isn't always enough on its own. There isn't much the plugin can do about this one.

If you've ruled all three out and a single film keeps getting stuck on the TMDb lookup, open an issue. A workaround that skips the Cloudflare-protected lookup for that one film (pointing a TMDb ID directly at a Letterboxd slug) is being considered.

## Privacy

This fork sends nothing anywhere except to the services you link: Letterboxd, Serializd and your own Seerr. The upstream plugin's usage telemetry, "Send logs to developer" upload, and install-counting repository mirror are removed.

## Requirements

- **Jellyfin 10.11.9 or newer, including Jellyfin 12.x.** One release serves both, with nothing to change in your config and no separate Jellyfin 12 download. Verified by loading the shipped build on a clean Jellyfin 12.0.0 server, and every change is built and tested against the Jellyfin 12 SDK in CI.
  - **Migrating your server to Jellyfin 12?** Jellyfin advises removing (or disabling) external plugins before the upgrade, and that is safe to follow here: your accounts, settings, and sync history live outside the plugin folder and all survive a reinstall from the catalog.
  - Note that Jellyfin 12 moved where plugins live, from `config/data/plugins/` to `config/plugins/`. Jellyfin handles that move for you on upgrade. It only matters if you install the plugin by hand rather than from the catalog, in which case use the new path on 12.x.
- A Letterboxd and/or Serializd account
- Jellyscribe opens as its own page inside Jellyfin (no reload, like Jellyfin Enhanced's Bookmarks), from the sidebar on Jellyfin 10.11 or the profile (avatar) menu on Jellyfin 12, and you can bookmark it at `#/jellyscribe`. This needs no other plugin; if you already run the [File Transformation plugin](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation), Jellyscribe uses it too, and you still get one link
- Optional: a [Seerr](https://github.com/seerr-team/seerr) instance for the auto-request and watchlist-mirror integrations

## Building from source

```bash
git clone https://github.com/WouterStulp/Jellyscribe.git
cd Jellyscribe
dotnet build -c Release
```

Output `Jellyscribe.dll` is in `LetterboxdSync/bin/Release/net9.0/`. Without a local .NET 9 SDK, run the same in a container:

```bash
docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 dotnet build -c Release
```

## Releasing

Versions are the upstream version plus a fourth number (`2.10.0` → `2.10.0.1`). A PR that ships to users bumps `AssemblyVersion`/`FileVersion` in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj` and fills in its `## Release notes` section. When it's merged, `release.yml` runs `fork/release.sh`, which tests, publishes the GitHub release and adds the version to `fork/manifest.json`. `fork/release.sh "<changelog>"` also works by hand.

## Contributing

Issues and PRs go to [this repository](https://github.com/WouterStulp/Jellyscribe), not upstream. Changes land on `main` through a PR and ship with the next fork release.

## License

[MIT](LICENSE)
