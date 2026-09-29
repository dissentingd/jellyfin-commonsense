# Changelog

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
