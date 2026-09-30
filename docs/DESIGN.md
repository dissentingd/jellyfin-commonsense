# Design notes — Ratings Fixer

Working design for v0.1. Findings marked **verified** were checked against a real Jellyfin 12.1 server;
everything else is from the 12.1 API surface and still needs a live check (see *To verify* below).

## Why a plugin, not a CLI

The first scaffold was a Python CLI talking to the REST API. It became a server plugin because:

- it can rate **new items as their metadata arrives** (`ILibraryManager.ItemUpdated`), instead of
  waiting for someone to run a script;
- settings, runs, reports and the ledger live in the **dashboard**, not in a shell and a cron job;
- it scores ratings with **Jellyfin's own rating tables** (`ILocalizationManager.GetRatingScore`), so the
  plugin and the server's enforcement can never disagree about what a rating means;
- users install it from a plugin repository, with no Python environment to maintain.

## How Jellyfin enforces parental ratings

- Each rating string maps to a **score** (and optional sub-score) from Jellyfin's per-country rating
  tables. A user's `MaxParentalRating` (plus optional sub-score) is compared against the item's
  effective score; with **Block unrated items** enabled, items with no score are hidden too.
- Notable US scores (12.1): `Approved`/`G`/`TV-G` = 0, `PG` = 10, `PG-13` = 13, `TV-14` = 14,
  `R` = 17, `NC-17` = 17 (sub-score 1), `TV-MA` = 17 (sub-score 1), `XXX` = 1000, `Banned` = 1001.
  `Passed`, `NR`, `Not Rated`, `Unrated` and `GP` have **no score** (treated as unrated).
- **Verified:** legacy strings missing from the US table can still score through another country's
  table: `X` enforces as 1000, `M` and `AO` as 18. The plugin scores with the same call the server
  uses, so it agrees with enforcement; the legacy map only applies to strings that truly have no score
  (in practice `GP`, `18+`).
- **Verified** foreign scores (`/RatingsFixer/Score`): GB `12A` 12, `15` 15, `18` 18, `R18` 1000;
  DE `12` 12, `16` 16; IE `15A` 15; AU `M` 15, `MA15+` 15; NZ `M` 16, `R16` 16. Note AU/NZ `M` is
  where many US `PG-13` films land, so including AU/NZ pushes those to `R` — see *Open*.
- **Custom Rating overrides Official Rating for enforcement on 12.1 (verified):** a `G` movie given
  `CustomRating = XXX` disappeared for a user capped at 17 and reappeared when cleared. Earlier
  releases had regressions here (jellyfin/jellyfin#12007, #13338), which is why the plugin targets 12.1+.

## Decision rules

Implemented in `Rating/RatingEngine.cs` (pure, unit-tested):

1. **Skip** items whose rating field is locked, or that have a Custom Rating someone set by hand
   (one the ledger doesn't record as ours). Both are configurable.
2. **User rules first.** If any rule matches (tag, genre or collection → rating), the strictest matching
   rule decides and no source is consulted. Rules are how adult content gets `XXX`: a board rating is
   never rounded up to `XXX`.
3. Otherwise gather candidates:
   - a **legacy mapping** if the official rating is one Jellyfin can't score (`X=NC-17`, `AO=XXX`,
     `GP=PG`, `M=PG`, `18+=NC-17` by default);
   - **TMDb certifications** for the configured countries — per country, the certification of the
     most recent release that has one;
   - the **Common Sense Media age** from MDBList, if enabled. (MDBList's `certification` field is
     *not* used: live data showed it carries other countries' values — `U`, `15`, `12` — unlabelled.)
   Each is scored with Jellyfin's table for its country; an age N scores N. Unscorable values are ignored.
   Only the selected libraries are considered (all when none are selected); restore and export ignore
   the selection.
4. **Combine** the candidates: by default the **consensus** (upper median — one unusually strict country
   can't move a title alone); optionally the strictest. Then map it onto the rating ladder for the item's
   type (movies: `G, PG, PG-13, R, NC-17`; series: `TV-Y … TV-MA`): by default to the **nearest** step,
   ties going looser (14–15 → `PG-13`, 16 → `R`); optionally always up. Board ratings are compared on the
   whole score and never land on a sub-scored step that shares a score with a plain one; among steps
   sharing the chosen score, the closest sub-score wins (a BBFC 15 = 15.3 → Australia's MA15+ = 15.3,
   not PG = 15.1). So a foreign
   `18` becomes `R`, not `NC-17` (a series still reaches `TV-MA`). A certification that *is* a step on the
   item's own ladder (e.g. TMDb US `NC-17`) is used as-is.
5. **Raise-only** (default): write only if that's stricter than the current effective rating
   (custom, else official). With raise-only off, a looser rating may be written.
6. No candidates → **Review** when the item is unrated/unscorable or carries an *unverified* rating
   (`Approved`, `Passed`); otherwise **Keep**.

### Enforcement value (verified 2026-09-29)

Jellyfin doesn't filter on the rating strings at query time: it filters on each item's stored
`InheritedParentalRatingValue` / `InheritedParentalRatingSubValue`. The metadata editor recomputes those on
save; a plain `ILibraryManager.UpdateItemAsync` does **not** — the first live Apply wrote thousands of
Custom Ratings that had no effect at all (e.g. `XXX` still enforcing 17). Every write now sets both values
from `BaseItem.GetParentalRatingScore()` before saving, and each run **repairs** any of our ratings whose
stored value is out of step (reported as *repaired*).

### Series and episodes (verified 2026-09-29)

Enforcement is **per item**: an episode is filtered on its own rating, not its series'. A test series
given `CustomRating = XXX` through the dashboard's metadata editor disappeared (with all its episodes)
from a 17-capped user's listings, search, Next Up and Latest — but only because the editor's save
**copies the rating down** to every season and episode (it also overwrites their *official* ratings
with the series' one). Writes through `ILibraryManager.UpdateItemAsync` don't cascade.

So the plugin cascades itself: after rating a series it gives each season and episode the same
Custom Rating, **only where that's stricter** than the child's own rating, never touching official
ratings, and leaving a looser hand-set episode rating alone unless it's the series rating previously
put there. Every Apply also tops up series whose rating is ours (new episodes since the last run), and
auto-rate queues a new episode's series. Children aren't ledgered individually; Restore re-cascades
from each series entry.

### Settled (2026-09-29)

- Adult-tagged content gets `XXX` (score 1000), not `NC-17`, via a user rule — so it's hidden from every
  account that has a maximum rating, not just those capped below 17.
- v0.1 sources: TMDb certifications + Common Sense via MDBList.
- Raise-only by default.
- Consensus (median) over strictest, round to nearest, foreign 18 → `R` never `NC-17`. Chosen after a
  full-library preview on a real library of tens of thousands of titles: strictest-of-6 with round-up
  would have raised about half of all titles, most of them already modern-rated (e.g. every AU `M` /
  DE `16` PG-13 film → `R`). With these defaults about a fifth are raised — mostly `PG → PG-13`,
  unrated → `PG-13`/`R`, and rule-driven `XXX` — and no board `18` becomes `NC-17`. Unrated titles no
  source knows about go to review; "Block unrated items" on kids' accounts covers those.

### Open

- Common Sense counts as one voice in the consensus; a per-source weight may still be wanted.
- Target countries other than the US need their own ladders (the setting exists; defaults are US).

## Data sources

| Source | Provides | Notes |
|---|---|---|
| User rules | tag / genre / collection → rating | Exact; uses curation the user already did |
| TMDb `release_dates`, `content_ratings` | Every country's certification | v3 key or v4 token; 4 requests in flight |
| TVDb v4 `/series/{id}/extended` | Series content ratings, every country (three-letter codes, mapped to two-letter) | Optional; needs a project key (+ PIN for user-supported keys); token cached per key |
| TMDb `/find/{id}` | TMDb id from an IMDb id (or TVDb id for series) | Only for titles with no TMDb id; answers cached in `tmdb-ids.json`, misses retried after the cache period |
| MDBList `POST /tmdb/{movie\|show}/` | `commonsense` flag + `age_rating` (Common Sense age) | Batches of 100; free tier has a daily limit |

Lookups are cached per source in the plugin's data folder (default 30 days, empty answers included), and
sources cache everything they get — country and Common Sense filtering happen at decision time, so
changing those settings never needs a cache clear. If MDBList's limit is hit mid-run, what was fetched is
kept and the rest is picked up on a later run.

## Persistence

- **Ledger** (`ledger.json` in the plugin data folder): item id, name, year, provider ids, previous
  official and custom rating, new custom rating, reason, timestamp. One entry per item, latest wins.
- **Restore** re-applies a ledger — the stored one or an uploaded file — matching by item id first, then
  TMDb/IMDb/TVDb id, so it survives re-scans and database restores. It always shows a dry-run count first.
- **Export** snapshots every Custom Rating in the library (including hand-set ones) in ledger format.
- Jellyfin's NFO saver (if enabled) also writes `<customrating>`, giving a second copy beside the media (verified).

## Surfaces

- **Settings page** (`Configuration/configPage.html`): keys, countries, rules, behaviour, run buttons,
  ledger download/export/restore, and the latest report with filters.
- **Scheduled tasks** (category *Ratings Fixer*): Preview, Apply, Restore. No default triggers.
- **API** (admin only, `/RatingsFixer/...`): `Report`, `Ledger`, `Export`, `Restore?apply=`, `ClearCache`,
  `Score?rating=&country=` (scores a rating exactly as the server does — for checking mappings).
- **Auto-rate** (opt-in): movies/series whose metadata was downloaded, and the series of newly added
  episodes, are batched for a minute, then run through Apply. The plugin's own writes use `MetadataEdit`, so they don't re-trigger it.

## Safety

- Nothing is written until Apply runs; the settings page asks for confirmation.
- Writes go through `ILibraryManager.UpdateItemAsync` / `UpdateItemsAsync` (episodes in chunks of 100),
  never the DB, and always recompute the stored enforcement value (see above).
- The ledger is checkpointed every 200 items, so an interrupted Apply still knows which ratings are ours.
- Only one run at a time.

## Verified on a live server (Jellyfin 12.1, 2026-09-29)

- Foreign certifications score as expected through `/RatingsFixer/Score`.
- A full Apply plus the enforcement repair: every item carrying one of our Custom Ratings has the
  matching stored score (e.g. `XXX` → 1000, `TV-MA` → 17.1), movies through episodes.
- A capped user loses an `XXX` movie from search and listings, and an `XXX` series from listings along
  with all of its episodes (browse and search); an uncapped user with the same libraries still sees both.
- Collection rules resolve real collection membership: a temporary collection with one PG movie and a
  rule "collection → R" produced exactly one proposed raise, PG → R, via that rule (preview only).

## Still to verify / improve

- Auto-rate on episodes: new episodes of an already-rated series (movies verified live: a new movie was
  picked up ~13 minutes after it was added, once its metadata arrived).

## Write cost (measured 2026-09-29)

On a large library (hundreds of thousands of items in Jellyfin's database), re-saving one episode costs
~220 ms however it's done — `UpdateItemsAsync` batches of 20 (~260 ms each), with or without the NFO saver
(~5% more with it), or one `UpdateItemAsync` at a time (~380 ms). The cost is Jellyfin's own item save, so
the plugin can't remove it; it can only write less. Hence: only the first Apply is slow (every rated title
plus its episodes — a couple of hours at that scale); later runs write only changes, new episodes and
repairs. With the NFO saver enabled, each written item's `.nfo` also gets `<customrating>`.
