# Duration Filter — Jellyfin plugin

Duration Filter adds a **Min duration** and **Max duration** control (in minutes) to
the jellyfin-web library filter panel. It composes with every other active filter
(genre, year, watched state, …) and respects the current sort order, so you can do
things like *"newest movies under 60 minutes"* without leaving the normal library
view. The filter works in any video library and supports both movies and episodes.

> **Status:** `0.1.2` — removes the active-filter chip. Targets the Jellyfin
> **10.10.x** plugin ABI (`net8.0`, `Jellyfin.Controller` 10.10.6).

---

## How it works (and an important design note)

The original idea was "the Jellyfin `/Items` API already supports `MinRunTimeTicks` /
`MaxRunTimeTicks`, so the plugin only needs to add UI." **That turned out to be
false.** The 10.10 server (`ItemsController.GetItems` and `InternalItemsQuery`) has
`minWidth` / `minHeight` / `minPremiereDate` / `minCommunityRating` and friends, but
**no runtime/duration query parameter at all**. Modifying the server is out of scope.

So the filter is applied **in the browser**:

1. A small client script is injected into jellyfin-web.
2. When a duration filter is active, the script intercepts the library's `/Items`
   request, removes only `StartIndex` / `Limit`, and fetches the **full** result set
   — every other filter and the sort order are still applied **by the server**.
3. It drops items whose `RunTimeTicks` falls outside the chosen range.
4. It re-paginates the remaining items locally and returns a corrected response, so
   the item count and the pager stay accurate.

The full result set is cached briefly so paging through results does not re-fetch.

`SortBy` / `SortOrder` are never touched — sort order is always preserved.

### Getting the script into jellyfin-web

Jellyfin 10.10 has **no native mechanism** for a plugin to add JavaScript to
jellyfin-web. The plugin therefore uses, in order of preference:

1. **[File Transformation plugin][filetransform]** *(recommended)* — if installed,
   Duration Filter registers an in-memory transformation of `index.html`. Nothing on
   disk is modified and it is undone automatically when the plugin is removed.
2. **Direct `index.html` patch** *(fallback)* — if the File Transformation plugin is
   not installed, Duration Filter edits `jellyfin-web/index.html` on disk (wrapped in
   a marked, idempotent block, removed again on shutdown). This can be disabled in
   the plugin settings if your web root is read-only.

[filetransform]: https://github.com/IAmParadox27/jellyfin-plugin-file-transformation

---

## Requirements

- Jellyfin Server **10.10.x**.
- *(Optional but recommended)* the **File Transformation** plugin.
- A jellyfin-web client (the desktop/browser web UI). See **Known limitations**.

---

## Installation

### Install via the plugin catalog (recommended)

This repository **is** a Jellyfin plugin repository, so the plugin can be
installed and updated from the dashboard:

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → +** (Add).
2. Set a **Repository Name** (e.g. `Duration Filter`) and this **Repository URL**:
   ```
   https://raw.githubusercontent.com/Pipazoul/jellyfin-filters/main/manifest.json
   ```
3. Save, then open **Dashboard → Plugins → Catalog**.
4. Find **Duration Filter** under the *General* category and click **Install**.
5. *(Recommended)* Also install the **File Transformation** plugin — see step 3
   of the manual install below for why.
6. **Restart Jellyfin**, then reload your browser tab.

Future versions appear in the catalog automatically and can be updated from the
same screen.

### Manual install (from a build)

1. Build the plugin (see **Building from source**) or download
   `JellyfinPluginDurationFilter.dll`.
2. Copy the DLL into a `Duration Filter` folder inside your Jellyfin **plugins**
   directory:
   - Linux: `/var/lib/jellyfin/plugins/Duration Filter/`
   - Docker: `/config/plugins/Duration Filter/`
   - Windows: `%ProgramData%\Jellyfin\Server\plugins\Duration Filter\`
3. *(Recommended)* Install the **File Transformation** plugin from its repository.
4. **Restart Jellyfin.**
5. Open **Dashboard → Plugins** and confirm **Duration Filter** is listed and
   *Active*.
6. Reload your browser tab so jellyfin-web picks up the injected script.

### Verifying it loaded

- Server log should contain either
  `Duration Filter: registered index.html transformation with the File Transformation plugin.`
  or `Duration Filter: patched index.html directly at …`.
- The browser console (with verbose/debug level enabled) shows
  `[DurationFilter] initialised …`.

---

## Configuration

**Dashboard → Plugins → Duration Filter** exposes:

| Setting | Description |
| --- | --- |
| **Default minimum (minutes)** | Pre-fills the "Min" input the first time a library is opened. `0` = no minimum. Not applied automatically. |
| **Default maximum (minutes)** | Pre-fills the "Max" input. `0` = no maximum. |
| **Enabled library IDs** | Comma-separated library IDs the filter appears in. Empty = every video library. |
| **Patch index.html directly** | Allows the on-disk fallback when the File Transformation plugin is absent. Uncheck for read-only web roots. |

Configuration changes take effect after users **reload their browser tab**.

---

## Usage

1. Open a library and click the **Filter** button.
2. The panel now starts with a **Duration (minutes)** section. Enter a **Min**
   and/or **Max** value (`0` or blank = no limit).
3. Click **Apply**. The view reloads with the duration filter applied on top of all
   other filters and the current sort.
4. To remove the filter, click **Clear** in the filter panel.

The chosen range is stored in `localStorage` **per library**, so it survives
navigation until you clear it.

---

## Building from source

Requires the **.NET 8 SDK**.

```bash
dotnet build JellyfinPluginDurationFilter.sln -c Release
```

The plugin assembly is produced at:

```
JellyfinPluginDurationFilter/bin/Release/net8.0/JellyfinPluginDurationFilter.dll
```

Copy that DLL into your Jellyfin plugins directory as described above.

### Packaging & releasing

To produce an installable zip locally:

```bash
build/package.sh 0.1.0.0        # -> dist/duration-filter-0.1.0.0.zip (+ MD5)
```

To publish a new catalog version, bump the number and **push a tag from
`main`'s HEAD**:

```bash
git tag v0.2.0 && git push origin v0.2.0
```

The [`Release plugin`](.github/workflows/release.yml) GitHub Action then builds
the zip, attaches it to a GitHub Release, and updates `manifest.json` — so the
new version shows up in everyone's plugin catalog. `build.yaml` is also included
for the [Jellyfin Plugin Repository Manager](https://github.com/jellyfin/jellyfin-plugin-repository-manager)
if you prefer that toolchain.

---

## Manual test checklist

The C# project builds cleanly, but the client behaviour must be verified against a
running Jellyfin instance. With the plugin installed and the browser tab reloaded:

- [ ] Plugin loads without errors in the Jellyfin server log.
- [ ] **Dashboard → Plugins → Duration Filter** renders and saves settings.
- [ ] Opening a library's **Filter** panel shows the **Duration (minutes)** section.
- [ ] Applying a filter: in the browser **Network** tab, the library's `/Items`
      request has **no** `Limit` (the full set is fetched) and the rendered grid
      only shows items inside the range — confirm a known short/long title appears
      or disappears as expected.
- [ ] Combining with a genre or year filter narrows results correctly.
- [ ] Sort order (e.g. *Date Added, Descending*) is preserved while filtering.
- [ ] The item count / pager reflects the filtered total, and paging works.
- [ ] Clearing the filter with **Clear** restores normal behaviour.
- [ ] No console errors on pages without a filter panel (home, item details).

---

## Tick math

Jellyfin measures runtime in **ticks**, where **1 tick = 100 nanoseconds**:

```
1 second = 10,000,000 ticks
1 minute = 600,000,000 ticks
```

All conversion goes through a single helper in `durationFilter.js`:

```js
function minutesToTicks(m) { return m * 600000000; }
```

Items are kept when `minTicks ≤ RunTimeTicks ≤ maxTicks` (an unset bound is ignored).
Items with an unknown runtime are excluded while a filter is active.

---

## Screenshots

<!-- Add screenshots here once captured against a running instance. -->

| Filter panel section | Admin settings |
| --- | --- |
| _screenshot placeholder_ | _screenshot placeholder_ |

---

## Known limitations

- **jellyfin-web only.** The filter is implemented as injected client JavaScript, so
  it works in the browser/desktop web UI. **Native apps** (Android, iOS, Android TV,
  Swiftfin, …) will not show it.
- **Server has no runtime parameter.** Because filtering is client-side, the browser
  fetches the full (otherwise-filtered, sorted) result set for a library when a
  duration filter is active. For very large libraries this first request is heavier
  than a normal page; results are cached briefly so paging stays fast. A native
  client could not reuse this approach — adding server-side runtime filtering would
  require a change to Jellyfin itself, which is intentionally out of scope here.
- **Web internals are not a stable API.** The filter-panel injection depends on
  jellyfin-web's DOM (`.filterDialog` / `.filterDialogContent`) and request shape.
  Every hook is wrapped in `try/catch` and fails quietly — a future jellyfin-web
  change could disable the UI, but it will not break the page.
- **Uninstalling:** if the on-disk fallback was used, restart the server after
  removing the plugin so the `index.html` patch is cleaned up.

### Out of scope / possible follow-ups

- Native client support (would need server-side runtime filtering).
- Any non-runtime filtering (bitrate, resolution, codec, …).

---

## License

This plugin is provided as-is. See repository for license details.
