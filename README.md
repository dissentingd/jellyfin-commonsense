# jellyfin-commonsense

> **Status: pre-alpha — design stage, nothing to run yet.** See [docs/DESIGN.md](docs/DESIGN.md).

Re-rate a Jellyfin library to **modern parental-rating standards**, so Jellyfin's
built-in parental controls actually work on older and unrated films — and keep
those corrections safe from metadata refreshes.

## The problem

Jellyfin's parental controls are only as good as each item's rating, and film
ratings have a history problem:

- **Films older than the rating system were never re-rated.** A pre-1968 US film
  typically carries `Approved` or `Passed` (a Production Code seal, not an age
  rating) — and Jellyfin scores `Approved` the same as `G`.
- **Old ratings were often under-classified.** Content that earned a `PG` or `R`
  decades ago would frequently rate higher today; other countries' boards
  (e.g. the BBFC, FSK) routinely re-rate old films on re-release, but the US
  rating in your metadata never changes.
- **Huge swathes of a typical library are simply unrated** (`NR`, `Not Rated`,
  `Unrated`, blank), and some legacy ratings (`X`, `AO`, `GP`, `M`, `18+`) aren't
  in Jellyfin's rating table at all, so they're treated as unrated.
- **Manual fixes don't stick.** Editing the official rating by hand is undone by
  the next metadata refresh.

## The approach

- Write corrected ratings to Jellyfin's **Custom Rating** field. Parental
  controls enforce it (verified on Jellyfin 12.1), metadata providers never
  overwrite it, and the original official rating stays visible for reference.
- Derive the correction from **modern sources**: other countries' current
  certifications (via TMDb), Common Sense Media advisory ages (via MDBList),
  and your own rules (e.g. "everything tagged X is adult").
- **Raise-only by default** — never loosen a rating.
- Everything is **dry-run first**, and every write is recorded in a **ledger**
  file you can back up, diff, and re-apply in bulk.

## Planned usage

```bash
jellyfin-commonsense plan    # dry run: what would change, and why
jellyfin-commonsense apply   # write Custom Ratings + ledger
jellyfin-commonsense export  # snapshot current Custom Ratings to a ledger
jellyfin-commonsense restore # re-apply a ledger (e.g. after a DB restore)
```

## License

[MIT](LICENSE)
