# jellyfin-commonsense

> **Status: pre-alpha (v0.1.0).** Builds and passes its unit tests; not yet verified end-to-end on a live
> server. See [docs/DESIGN.md](docs/DESIGN.md).

A Jellyfin plugin that re-rates your library to **modern parental-rating standards**, so Jellyfin's
built-in parental controls actually work on older and unrated films — and keeps those corrections
safe from metadata refreshes.

## The problem

Jellyfin's parental controls are only as good as each item's rating, and film ratings have a
history problem:

- **Films older than the rating system were never re-rated.** A pre-1968 US film typically carries
  `Approved` or `Passed` (a Production Code seal, not an age rating) — and Jellyfin scores `Approved`
  the same as `G`.
- **Old ratings were often under-classified.** Other countries' boards (BBFC, FSK, …) routinely
  re-rate old films on re-release, but the US rating in your metadata never changes.
- **Huge swathes of a typical library are simply unrated** (`NR`, `Not Rated`, blank), and some legacy
  ratings (`X`, `AO`, `GP`, `M`) aren't in Jellyfin's rating table at all.
- **Manual fixes don't stick.** Editing the official rating by hand is undone by the next metadata refresh.

## What it does

- Writes corrected ratings to Jellyfin's **Custom Rating** field. Parental controls enforce it
  (verified on Jellyfin 12.1), metadata providers never overwrite it, and the official rating stays
  visible for reference.
- Derives the correction from:
  - **your rules** — e.g. everything in the collection “Adult” → `XXX`. Rules win over every source.
  - **TMDb certifications** from the countries you choose, using each country's most recent
    certification (re-releases are where old films get re-rated).
  - **Common Sense Media ages** via MDBList.
  - **legacy mappings** for ratings Jellyfin can't score (`X` → `NC-17`, …).
- Takes the **strictest** answer and rounds it up to the next step on your rating ladder
  (e.g. German FSK 16 → `R`; a Common Sense age of 14 → `R` for a movie, `TV-14` for a series).
- **Raise-only by default** — never loosens a rating.
- Items it can't decide confidently (unrated, or `Approved` with nothing to go on) go on a
  **review list** instead of being guessed.
- **Preview first:** a dry run produces a report of every proposed change and its reason.
- Every write is recorded in a **ledger** you can download and re-apply (matched by TMDb/IMDb/TVDb id)
  — e.g. after restoring Jellyfin's database from an older backup.
- Optionally rates **new items automatically** as their metadata arrives.

## Requirements

- Jellyfin **12.1** or later.
- A free [TMDb API key](https://www.themoviedb.org/settings/api) and/or a free
  [MDBList API key](https://mdblist.com/preferences/). With neither, only rules and legacy mappings apply.

## Install

Until there's a release in a plugin repository, build and copy it in by hand:

```bash
dotnet publish Jellyfin.Plugin.CommonSense -c Release -o out
```

Copy `out/Jellyfin.Plugin.CommonSense.dll` into a new folder in Jellyfin's plugin directory
(e.g. `<config>/data/plugins/Common Sense Ratings_0.1.0.0/`) and restart Jellyfin.

## Use

1. **Dashboard → Plugins → Common Sense Ratings**: add your API keys, pick the countries whose ratings
   count, and add any rules. Save.
2. Click **Preview (dry run)**, wait for the task to finish (Dashboard → Scheduled Tasks), then
   **Refresh report** and read it: *stricter* items, *needs review*, *skipped*.
3. Happy? Click **Apply**. Download the ledger and keep a copy.
4. Optionally turn on **Rate new items automatically**, or schedule the Apply task.

The three tasks — *Preview re-rating*, *Apply re-rating*, *Restore ratings from ledger* — also appear
under Dashboard → Scheduled Tasks → Common Sense.

## Develop

```bash
dotnet build jellyfin-commonsense.sln
dotnet test jellyfin-commonsense.sln
```

The rating logic (`Rating/`, `Sources/SourceParsers.cs`) has no Jellyfin dependencies and is
unit-tested directly.

## License

[MIT](LICENSE)
