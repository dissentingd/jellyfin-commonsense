# Changelog

## 1.1.0

- **Choose which libraries to re-rate** (settings → Libraries). Restore and export still cover every
  library, so a ledger can bring back ratings in a library that's no longer selected.
- **Titles without a TMDb id are looked up by their IMDb id** (and series by their TVDb id) through
  TMDb, so they're no longer skipped. Jellyfin's own ids aren't changed; lookups are cached.
- **TVDb as an optional source for series** — often has ratings TMDb lacks. Needs a TVDb v4 key (and
  the subscriber PIN for user-supported keys).
- **Rating-system presets** for the US, UK, Canada, Ireland, Germany, Australia and New Zealand fill in
  the rating ladders; the page shows your server's metadata country and warns on a mismatch.
- When several ratings share a score (Australia's PG, M and MA15+ are all 15), a source's rating now
  maps to the one with the closest sub-score — a BBFC 15 becomes MA15+, not PG.
- **Fixed: the settings page didn't work.** Two JavaScript strings were broken in earlier versions, so
  the page's buttons and saving failed. A test now parses the page's script on every build.

## 1.0.1

No change in behaviour.

- Service-level tests against an in-memory fake of Jellyfin's library, covering what gets written,
  the enforced parental-rating score, series → episode cascades, collection/tag rules, the ledger,
  restore and auto-rate triggers.
- The service now receives its settings through an injected context (internal refactor for testing).

## 1.0.0

First release, verified on Jellyfin 12.1.

- Re-rates movies and series by writing Custom Ratings: your rules first (tag, genre or collection →
  rating), then the consensus of TMDb certifications from chosen countries and Common Sense Media ages
  (via MDBList), mapped to the nearest rating on a per-type ladder.
- Raise-only by default; a foreign 18 becomes R, never NC-17; titles with nothing to go on go to a
  review list instead of being guessed.
- Series ratings are copied to their seasons and episodes, which Jellyfin enforces separately.
- Every write updates the score Jellyfin's parental controls actually filter on.
- Preview (dry run), Apply and Restore as scheduled tasks; a report on the settings page; a ledger you
  can download, export and restore by TMDb/IMDb/TVDb id.
- Optional auto-rating of new titles and episodes.
