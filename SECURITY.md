# Security Policy

## Supported versions

Only the **latest release** of this fork receives security fixes. If you are on an older version, update through the Jellyfin plugin catalog before reporting.

| Version | Supported |
| ------- | --------- |
| Latest release | ✅ |
| Older releases | ❌ |

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report privately via [GitHub's private vulnerability reporting](https://github.com/WouterStulp/Jellyscribe/security/advisories/new) (Security tab → "Report a vulnerability").

You can expect an acknowledgement within a few days.

## Scope

In scope:

- The plugin itself (`Jellyscribe.dll`), including anything that could expose Letterboxd credentials, raw cookies, or other users' data on a shared Jellyfin server
- The plugin's REST endpoints (e.g. privilege escalation between Jellyfin users, non-admins reaching admin-only data)

Out of scope:

- Vulnerabilities in Jellyfin itself (report to the [Jellyfin project](https://github.com/jellyfin/jellyfin/security))
- Vulnerabilities in Letterboxd's website or API (report to Letterboxd)
- The Letterboxd API key and secret in `LetterboxdApiConstants.cs`. They are public by nature (see below), so finding them is not a vulnerability.
- The File Transformation plugin (report to [its repository](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation))

## The bundled Letterboxd API key

### What it is

`LetterboxdSync/LetterboxdApiConstants.cs` holds an API key and an HMAC signing secret for Letterboxd's JSON API (`api.letterboxd.com`). They were added in v1.6.0, together with the website-login fallback that takes over if they stop working. Where the plugin and its docs say "official API", they mean Letterboxd's own JSON API as opposed to its website, not an integration Letterboxd has approved. The key is out of scope for security reports: it is public by nature (see below).

### Why it is public

Every request to the API carries the key and a signature made with the secret, so both have to ship inside `Jellyscribe.dll`, where any decompiler can read them. Moving them out of the source (for example injecting them from a CI secret at build time) would not change that. Treat them as public: they identify the client app, while each user's own Letterboxd password and session tokens are what protect their account.

### What happens if Letterboxd rotates or revokes it

The plugin signs every API request with the key, including sign-ins, token refreshes and requests made with a user's access token. A revoked key will most likely make all of them fail. The plugin then behaves like this:

- **New sign-ins fall back to the website login.** `LetterboxdServiceFactory.CreateAuthenticatedAsync` tries the API first. If that sign-in throws, it logs `Official API auth failed for <user>, falling back to scraping` and signs in through letterboxd.com instead.
- **Accounts that are already signed in keep failing for up to about an hour.** `LetterboxdApiClient` keeps access tokens in memory and reuses one without contacting Letterboxd while it has more than 5 minutes left. Until that token expires (Letterboxd issues them for about an hour) or Jellyfin restarts, the API client is still chosen and its calls fail. A failed call is never retried on the website path. After that, the refresh and the password sign-in fail and the fallback takes over.
- **The website login has its own limits.** It goes through Cloudflare, which often blocks sign-ins from servers unless the user has pasted their browser's raw cookies and user agent (README, "Cloudflare issues"). If both sign-in paths fail 3 times in a row, the plugin pauses that account and writes a Jellyfin activity-log entry.

### Symptoms

- The Jellyfin log shows `Official API auth failed ... falling back to scraping` for every account at about the same time, usually followed by `Using web scraping fallback`.
- **Verify login** in the account settings reports that the website login was used, along with the API error.
- Users whose website login is blocked by Cloudflare see 403 errors, then accounts paused after 3 failures. Several users may report this at once.

### Runbook

1. **Confirm it is the key.** Run the live tests locally against a test account (`dotnet test -c Release --filter Category=Integration`, setup in `LetterboxdSync.Tests/Integration/README.md`). The key is the likely cause when `ApiClient_ValidCredentials_AuthenticatesWithTheBundledKey` fails on `/auth/token` while the test account still signs in on letterboxd.com.
2. **Tell users how to keep syncing.** Pin an issue explaining that the API path is down and that pasting Raw Cookies and a matching User-Agent (README, "Cloudflare issues") keeps the website fallback working.
3. **Get a replacement key and secret,** for example by applying to Letterboxd for API access.
4. **Ship it in a patch release.** Replace both constants in `LetterboxdApiConstants.cs`, run the live tests again, and publish a release. Users only need to update the plugin and restart Jellyfin; no settings change.
