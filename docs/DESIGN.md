# Design notes

Working design for v0.1. Findings below were verified against a real Jellyfin
12.1 server unless marked otherwise.

## How Jellyfin enforces parental ratings

- Each rating string maps to a **score** (and optional sub-score) from
  Jellyfin's per-country rating tables — see `GET /Localization/ParentalRatings`.
  A user's `MaxParentalRating` (plus optional sub-score) is compared against the
  item's effective score; with **Block unrated items** enabled, items whose
  rating has no score are hidden too.
- Notable US scores (12.1): `Approved`/`G`/`TV-G` = 0, `PG` = 10, `PG-13` = 13,
  `TV-14` = 14, `R` = 17, `NC-17` = 17 (sub-score 1), `TV-MA` = 17 (sub-score 1),
  `XXX` = 1000, `Banned` = 1001. `X`, `AO`, `GP`, `M`, `Passed`, `NR`,
  `Not Rated`, `18+` have **no score** (treated as unrated).
- **Custom Rating overrides Official Rating for enforcement on 12.1.** Test:
  a `G` movie given `CustomRating = XXX` disappeared for a user capped at 17
  (effective score 0 → 1000), and reappeared when cleared. Earlier releases
  had regressions here (jellyfin/jellyfin#12007, #13338 — the latter marked
  fixed); the tool should check the server version and self-test once before
  writing anything.

## Data sources

| Source | Provides | Strength | Weakness |
|---|---|---|---|
| User rules | e.g. tag/collection → rating | Exact, uses curation the user already did | Only what the user defines |
| TMDb `release_dates` | Every country's certification, incl. modern re-releases | Old films re-rated abroad (BBFC, FSK, …) | Not every title has a modern re-rating |
| MDBList | Release certification + Common Sense Media advisory age | Content-based age for mainstream titles | Sparse for old/obscure/adult titles; needs a (free) API key |
| IMDb Parents Guide | Per-category severity | Most granular | No API — out of scope for v0.1 |

Prior art: Kometa's `mass_content_rating_update: mdb_commonsense` does the
Common Sense part for **Plex only**; a Jellyfin Common Sense plugin was requested
but never built.

## Rules (v0.1 draft)

1. User rules first (e.g. items in a given collection or with a given tag → `XXX`).
2. Otherwise collect candidate ratings from each source, **map each to the
   target country's scale** (e.g. BBFC `18` → US `NC-17`/`R`, BBFC `15` → `R`,
   FSK `16` → `R`, Common Sense age 17+ → `R`, …), and take the **strictest**.
3. **Raise-only:** write a Custom Rating only if it scores higher than the
   item's current effective rating. Never loosen.
4. No confident answer → the item goes on a **review list**, not a guess.
5. Legacy strings with no score (`X`, `AO`, `GP`, `M`, …) get a documented
   default mapping, overridable in config.

Open questions: exact cross-country mapping table; how to weigh Common Sense
against official boards; TV series vs episodes (rate at series level and let
episodes inherit?); target country other than US.

## Persistence

- **Ledger** (TSV/JSON): item id, name, year, provider ids (TMDb/IMDb/TVDb),
  previous official + custom rating, new custom rating, source, rule, timestamp.
  Keyed by provider ids so it survives item re-ids / re-scans / DB restores.
- `restore` re-applies a ledger by provider-id match.
- Jellyfin's NFO saver (if enabled) also writes `<customrating>`, giving a second
  copy beside the media.
- Optionally set the item's locked fields so manual edits in the UI are
  intentional.

## Safety

- Dry run by default; `apply` requires an explicit flag.
- Writes go through Jellyfin's REST API only (`POST /Items/{id}`), never the DB.
- Use `Authorization: MediaBrowser Token="<key>"` — the legacy `X-Emby-Token`
  header is rejected on 12.x.
