# Ratings Fixer for Jellyfin

A Jellyfin plugin that re-rates your library to **modern parental-rating standards**, so Jellyfin's
built-in parental controls actually work on older and unrated titles — and keeps those corrections safe
from metadata refreshes.

**Version 1.0** · Jellyfin **12.1+** · [Changelog](CHANGELOG.md) · [Design notes](docs/DESIGN.md)

> [!WARNING]
> **Back up Jellyfin's database before your first Apply** (Dashboard → Backups, or copy `jellyfin.db`
> with the server stopped). Apply edits thousands of items at once. Every change is recorded in the
> plugin's ledger so it can be re-applied, but only a database backup can undo it.

## The problem

Jellyfin's parental controls are only as good as each item's rating, and film ratings have a
history problem:

- **Films older than the rating system were never re-rated.** A pre-1968 US film typically carries
  `Approved` or `Passed` (a Production Code seal, not an age rating) — and Jellyfin scores `Approved`
  the same as `G`.
- **Old ratings were often under-classified.** Other countries' boards (BBFC, FSK, …) routinely
  re-rate old films on re-release, but the US rating in your metadata never changes.
- **Large parts of a typical library are simply unrated** (`NR`, `Not Rated`, blank).
- **Manual fixes don't stick.** Editing the official rating by hand is undone by the next metadata refresh.

## What it does

- Writes corrected ratings to each item's **Custom Rating**. Parental controls enforce it, metadata
  providers never overwrite it, and the official rating stays untouched for reference.
- Decides each title's rating from, in order:
  1. **Your rules** — e.g. everything in the collection “Adult” → `XXX`. Rules win outright.
  2. The **consensus** of current **TMDb certifications** from the countries you choose (each country's
     most recent certification, so re-releases count) and the **Common Sense Media** age (via MDBList).
     One unusually strict country can't re-rate a title on its own.
  3. **Legacy mappings** for strings Jellyfin can't score (e.g. `GP` → `PG`).
- Maps the result to the **nearest** rating on a ladder per type — movies `G, PG, PG-13, R, NC-17`,
  series `TV-Y … TV-MA` — so a BBFC 12A becomes `PG-13`, an FSK 16 becomes `R`. A foreign 18 becomes
  `R`, never `NC-17`; `NC-17` and `XXX` only come from your rules or an actual `NC-17`.
- **Raise-only by default** — never loosens a rating, and leaves hand-set Custom Ratings and locked
  rating fields alone.
- Titles it can't decide (unrated with no source, or `Approved` with nothing modern) go on a
  **review list** instead of being guessed. Turn on **Block unrated items** for children's accounts to
  hide those too.
- Copies a series' rating to its **seasons and episodes** (Jellyfin enforces each episode separately),
  including episodes added later.
- Keeps a **ledger** of every rating written, which you can download and re-apply by TMDb/IMDb/TVDb id —
  e.g. after restoring an older database backup. With Jellyfin's NFO saver enabled, each `.nfo` also
  gets a `<customrating>` copy.
- Optionally rates **new titles and episodes automatically** as their metadata arrives.

## Install

**From the plugin catalog (recommended):** Dashboard → Plugins → Repositories → add

```
https://raw.githubusercontent.com/dissentingd/jellyfin-ratings-fixer/main/manifest.json
```

then install **Ratings Fixer** from the catalog and restart Jellyfin.

**By hand:** download the zip from [Releases](https://github.com/dissentingd/jellyfin-ratings-fixer/releases),
unzip `Jellyfin.Plugin.RatingsFixer.dll` into a new folder in Jellyfin's plugin directory
(e.g. `<config>/data/plugins/Ratings Fixer_1.0.0.0/`), and restart Jellyfin.

## Use

1. **Back up Jellyfin's database** (see above).
2. **Dashboard → Plugins → Ratings Fixer**: add a free [TMDb API key](https://www.themoviedb.org/settings/api)
   and/or a free [MDBList API key](https://mdblist.com/preferences/), choose the countries whose ratings
   count, and add any rules. Save.
3. Click **Preview (dry run)**. When the task finishes (Dashboard → Scheduled Tasks → Ratings Fixer),
   click **Refresh report** and read it: which titles would get stricter, which need review, and why.
4. Happy with it? Click **Apply**, then **Download ledger** and keep the file.
5. Optionally turn on **Rate new items automatically**, or schedule the Apply task.

Settings worth knowing:

| Setting | Default | Notes |
|---|---|---|
| When sources disagree | Consensus | *Strictest* lets any one country raise a title |
| Round to the nearest rating | On | Off rounds everything above 13 up to `R` |
| Only ever make ratings stricter | On | Off lets sources lower ratings too |
| Countries | US, GB, IE, AU, NZ, DE | ISO codes |

## Good to know

- **The first Apply on a big library is slow.** Jellyfin's own save costs a fraction of a second per item,
  and the first run writes every re-rated title plus all of its episodes — on a library with tens of
  thousands of titles, that's a couple of hours. Later runs only write what changed.
- **Raise-only can't take anything back.** Removing a rule doesn't lower the ratings it wrote; restore
  from a backup, or clear those Custom Ratings in the metadata editor.
- **MDBList's free tier has a daily request limit.** Lookups are cached for 30 days, so a large library
  may take a few runs to fill in.
- Defaults are US-centric. For another target country, set the rating ladders to your country's ratings.

## Develop

```bash
dotnet build jellyfin-ratings-fixer.sln
dotnet test jellyfin-ratings-fixer.sln
```

Tests cover the rating engine and source parsers directly, and the service end to end against an
in-memory fake of Jellyfin's library (`ServiceTestKit.cs`): what gets written, the enforced score,
series → episode cascades, the ledger, restore, and auto-rate triggers.

To release: bump `<AssemblyVersion>`, add a `CHANGELOG.md` section, and push a tag like `v1.0.0`. The
release workflow builds, tests, publishes the zip, and adds the version to `manifest.json`.

## Attribution

This product uses the TMDB API but is not endorsed or certified by TMDB. Common Sense Media ages are
retrieved through [MDBList](https://mdblist.com/). This project is not affiliated with or endorsed by
TMDB, MDBList or Common Sense Media.

## License

[MIT](LICENSE)
