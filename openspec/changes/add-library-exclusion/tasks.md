## 1. Config model

- [x] 1.1 Add `ExcludedLibraryIds` (`List<string>`, default empty) to `Configuration/Account.cs` and `Configuration/SerializdAccount.cs`
- [x] 1.2 Test: an XML config without the element deserializes to an empty list, and a populated list round-trips (`AccountTests`, Serializd account tests)

## 2. Shared membership rule

- [x] 2.1 Add `LibraryExclusion.IsExcluded(ILibraryManager, BaseItem, IReadOnlyCollection<string>)`: false on empty list, true when any `GetCollectionFolders(item)` id (N format, case-insensitive) is listed
- [x] 2.2 Test: empty list never calls the library manager; single library excluded; two libraries with one excluded; unknown id; item with no collection folders

## 3. Scheduled sync paths

- [ ] 3.1 `LetterboxdSyncRunner.SyncOneUserAsync`: drop excluded films per account next to the date filter, count and log skips at Information
- [ ] 3.2 Test (`LetterboxdSyncRunnerTests`): excluded film is not posted and records no failure; second account without the exclusion still posts it
- [ ] 3.3 `Serializd/SerializdSyncRunner.SyncOneAsync`: drop excluded episodes per account, caching the result per series within a run, count and log skips
- [ ] 3.4 Test (Serializd runner tests): excluded episode is not logged; per-series cache means one library lookup per series
- [ ] 3.5 Bench a large synthetic episode set with and without an exclusion; record the numbers in the PR

## 4. Real-time path

- [ ] 4.1 `PlaybackHandler`: skip an account whose exclusions match the finished film or episode, before auth or any network call, with one Information log line
- [ ] 4.2 Test (`PlaybackHandlerTests`): excluded film makes no Letterboxd call for that account; excluded episode makes no Serializd call; non-excluding account on the same user still syncs (films and episodes)

## 5. API

- [ ] 5.1 Add `Api/LibrariesController.cs` serving `GET Jellyfin.Plugin.LetterboxdSync/Libraries`: film/TV/mixed/unset virtual folders with id (N format), name, collection type; non-admins see only libraries they can access
- [ ] 5.2 Carry `excludedLibraryIds` through the Letterboxd `Account`/`Accounts` GET and PUT payloads (`AccountUpdateRequest`) and the Serializd `Accounts` GET and PUT
- [ ] 5.3 Test (new `LibrariesControllerTests`, `LetterboxdControllerTests`, Serializd controller tests): listing filters collection types and user access; PUT persists the list; GET echoes it

## 6. UI

- [ ] 6.1 `Web/configPage.html`: "Excluded libraries" checklist on each Letterboxd and Serializd account card, saved via the full-config round trip, stale ids dropped on save, hidden when no libraries are listed
- [ ] 6.2 `Web/userPage.html`: same checklist, saved via the account PUT endpoints
- [ ] 6.3 Update visual baselines and check both pages in light and dark themes
- [ ] 6.4 Manual check on the dev server: exclude a library, play an item from it, confirm no post and the skip log line

## 7. Docs and release

- [ ] 7.1 README: document excluded libraries and that exclusion applies to future syncs only
- [ ] 7.2 CLAUDE.md: add the Serializd scheduled sync to "Sync entry points", the `Libraries` endpoint to "Plugin surface", and one line that library exclusion is a pre-filter above `ILetterboxdService`
- [ ] 7.3 Implementation review gates: security, ai-smells, performance (sync-loop change)
- [ ] 7.4 Bump `AssemblyVersion` / `FileVersion` minor in `Directory.Build.props` and `LetterboxdSync/LetterboxdSync.csproj`
- [ ] 7.5 Add the `site/src/data/release-notes.ts` entry and write the PR `## Release notes` paragraph; reference #124
