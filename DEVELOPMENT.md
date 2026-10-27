# Developer notes

Notes for working on the Completionist plugin. For what it does and how to install it, see [README.md](README.md).

## Layout

| File | Role |
|---|---|
| `Plugin.cs` | Entry point: name, GUID, dashboard page (in the main menu). |
| `PluginServiceRegistrator.cs` | Registers the singletons below. |
| `Scanning/CompletionistScanner.cs` | One check: loads shows and films, gets guides and collections, compares, diffs against the last report, notifies. A `SemaphoreSlim` stops two checks overlapping. |
| `Scanning/MissingFinder.cs` | Pure comparison logic: aired filter, whole seasons, multi-episode files, numbering mismatch, gaps, missing films. Unit tested. |
| `Scanning/GuideClient.cs` | TMDb (with key) then TVmaze for episodes; TMDb for collections. Throttled to one request per 550 ms, retries 429s, in-memory cache (episodes 12 h, ended shows 7 days, collections 3 days). |
| `Scanning/ReportStore.cs` | Last report as JSON in `plugins/configurations/Completionist/report.json`. |
| `Scanning/ActivityLog.cs` | The page's log, the same as in Forced Subtitles. In memory only. |
| `Notifications/DiscordMessageBuilder.cs` | Builds webhook payloads within Discord's limits. Unit tested. |
| `Notifications/DiscordNotifier.cs` | Posts payloads; only accepts `https://discord.com/api/webhooks/…` URLs; honours `retry_after`. |
| `ScheduledTasks/CompletionistTask.cs` | Daily at 5 AM, under Scheduled Tasks → Library. |
| `ScheduledTasks/LibraryScanHook.cs` | `ILibraryPostScanTask`; starts a background check after each library scan. |
| `Api/CompletionistController.cs` | `/Completionist/*` endpoints for the page (admin only). |
| `Configuration/configPage.html` | Report and settings page, embedded in the DLL. Uses the old `Dashboard`/`ApiClient` globals like the other plugins. |

## How "new" works

Every missing season, episode and film in the report carries `FirstSeen`. On each check, `StampFirstSeen` copies it from the previous report:
- episodes and seasons by number; an episode also inherits from its season if the whole season was missing before (so getting half a missing season doesn't make the other half "new");
- films by TMDb movie id within the same collection.

Anything without a previous entry gets the current check's time, and `FindNew` / `FindNewFilms` pick exactly those. That's what Discord gets.

- **First check** (no previous report): Discord gets a summary only. `ScanReport.FirstScannedAt` is kept from then on, and the page only tags items first seen *after* it as NEW (for 7 days).
- **Guide or TMDb unreachable:** that show's or collection's previous entry is copied over unchanged, so its items don't flip to resolved and back to new. A rejected TMDb key stops the film part and carries over every remaining collection.
- **Resolved:** only for shows and collections checked this time (`checkedFresh`), so ignoring, turning a part off, or failing to check never reads as "caught up". Ignoring also removes the item from the stored report right away.
- **Discord send failed:** the change is logged but not retried; the next check won't see it as new again.

## Matching rules

### TV

- Local episodes need `ParentIndexNumber` and `IndexNumber`; `IndexNumberEnd` covers multi-episode files. Virtual (missing-placeholder) items are excluded.
- An episode counts once it aired before `now - GraceDays`. An undated guide episode counts as aired if a later one has aired. Specials need their own date.
- **Numbering mismatch:** if none of the regular local episodes exist in the guide, the guide is ignored for that show (listed under Problems) and gap detection is used instead, if enabled.
- TVmaze specials have `number: null` and sit inside regular seasons, so they're dropped; only TMDb provides season 0.
- TVmaze lookup order: TheTVDB id, then IMDb id. The lookup endpoint redirects (301) to the show, which `HttpClient` follows.

### Films

- **Which collections:** the `TmdbCollection` provider id of every movie in the selected libraries (Jellyfin's TMDb movie provider sets it from `belongs_to_collection`), plus every `BoxSet` with a `TmdbCollection` id (set by Franchise Collections) or a `Tmdb` id (set by Jellyfin's box set provider). Box sets also give the report a link to the Jellyfin collection.
- **Owned:** TMDb movie ids of all movies in the selected libraries. Films without a TMDb id can't be matched.
- **Missing:** parts with a release date before `now - FilmGraceDays` that aren't owned or ignored. Parts with no date are unreleased.
- A collection is only reported if you own at least one of its released films, so a box set whose films are all in unselected libraries stays quiet.

## Jellyfin server behavior this relies on

Checked against `v12.4` and built against `10.11.0` / `12.0.0`:

- In 12.x, `DtoOptions` only controls *related tables* (provider IDs, locked fields, user data, images). Scalar columns like `SeriesId`, `ParentIndexNumber` and `IndexNumberEnd` always load. So the episode query uses `new DtoOptions(false)` and the movie query asks only for `ItemFields.ProviderIds`. Both queries are read-only. The series and box set queries keep the default options.
- `ILibraryPostScanTask` and `IScheduledTask` implementations are discovered automatically; services are not (see `PluginServiceRegistrator`).
- `meta.json` must be UTF-8 without a BOM; `package.ps1` writes it with `UTF8Encoding($false)`.
- Zips are made with `tar.exe` so paths use `/`, with files at the zip root.

## Releasing

Version lives in `$baseVersion` in `package.ps1` (the `.12`/`.11` suffix is added per target). Update `$changelog` too.

1. `.\package.ps1`: tests both lines, builds, zips to `dist\`, updates `manifest.json` with MD5 checksums.
2. `gh release create v<baseVersion> dist\*.zip --title "<baseVersion>" --notes "..."`
3. Commit and push `manifest.json`.

Never rebuild a published version (checksums change). The plugin GUID `aceeaf3d-1c56-410f-af3b-0f4f8fa40746` is in `Plugin.cs`, `configPage.html`, `package.ps1` and `build.yaml`. Never change it.

## Testing

`dotnet test tests/Jellyfin.Plugin.Completionist.Tests` (add `-p:JellyfinTarget=10.11` for the other line). The TVmaze client was smoke-tested live; the TMDb paths are only covered by parsing tests. **The plugin hasn't been run on a real server yet.** Smoke test:

1. Install, restart, check it's Active and appears in the dashboard menu.
2. Add a Discord webhook, **Send test message**. Add a TMDb key.
3. **Check now**. The first run posts a summary to Discord. Both tabs fill in.
4. Move one episode file of a show out of the library, scan. That episode appears as NEW and is posted to Discord.
5. Move one film of a franchise out of the library, scan. Same for the Films tab.
6. Put both back, scan again. With *Also post when missing items turn up* on, ✅ messages follow.
7. Ignore a show, a collection and a film; each disappears and shows under *Ignored*.

## Ideas

- Retry failed Discord sends on the next check.
- Other notification targets (ntfy, Gotify, generic webhook).
- Per-show "only track from season N" instead of all-or-nothing ignore.
