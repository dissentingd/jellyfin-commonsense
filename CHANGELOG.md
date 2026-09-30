# Changelog

## 1.2.0

- **OMDb as a gap-filler** (optional, free key from omdbapi.com): IMDb's US rating, asked only about
  titles no other source could rate. Because a free key allows 1,000 lookups a day, it runs last, every
  answer is cached (including "no rating"), a review re-check keeps its cache, and it stops cleanly at
  the daily limit — a large review list fills in over a few days of runs. Expect modest gains: on a real
  library's review list, about 3% of titles had a usable IMDb rating (most show N/A or Not Rated).
- **Fixed:** a source answering `Approved` or `Passed` (the pre-1968 US seals, which Jellyfin scores
  like G) was taken as a real rating, which could turn an unrated old film into G. It's now ignored like
  any unusable rating, so such titles stay on the review list.

## 1.1.0

- **Fallback countries.** Certifications from a second list of countries (default: Canada, most of
  Europe, Brazil, Mexico, Japan, Korea, Singapore) count only for titles that none of your main
  countries — nor Common Sense — rate. They fill gaps on the review list without changing any other
  decision. On a large real library this resolved about a fifth of the titles that needed review.
- **Revert.** A new *Written ratings* section lists every rating the plugin wrote; search it (by
  title, rating or reason — e.g. everything a collection rule wrote), tick titles and revert them to
  what they had before the plugin first touched them (usually no custom rating). A series' seasons and
  episodes go back too. It previews the counts first, and leaves alone any rating someone changed since.
  By default reverted titles are **excluded** from future runs (listed under *Excluded titles*, with
  *Include again*).
- **Re-check review list:** asks the sources afresh (skipping the cache) about just the titles that
  need review, and updates the report — optionally writing what it finds.
- The ledger now keeps each title's rating from *before the plugin first wrote it*, even across
  repeated re-ratings, so a revert never lands on one of the plugin's own earlier ratings.
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
