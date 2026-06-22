# Changelog

## 0.2.0

- Add Eggman/RomVault collection-dat disambiguation to profile registration: folders whose executable is shared by many unrelated titles, and that don't fuzzy-match any candidate profile code, can now be resolved via the dat's authoritative game-name -> ProfileCode mapping. Configured via the new optional `eggmanDatPath` setting (a local .dat or .zip the user already has -- this plugin still does not download third-party files itself).
- Add a live read-only fetch of the full teknogods/TeknoParrotUI profile-code list (falls back to the local GameProfiles listing on any failure) so a dat-resolved ProfileCode that doesn't exactly match a local template filename can still be fuzzy-resolved to the right one.
- Fix: an ambiguous shared-executable report for a folder that a later registration pass went on to resolve cleanly (via a different exe in the same folder, or the new dat/profile-set passes) is no longer surfaced as still needing attention.

## 0.1.0

- Add optional TeknoParrot Tools plugin with profile scanning, dry-run import preview, HyperHQ system/emulator/game sync, and profile backup/restore actions.
- Align the HyperHQ first-run wizard with the established plugin-page form/action flow and expose a health-check button.
- Add source-aligned profile registration, unique GamePath repair, Description title parsing, profile health counts, and TeknoParrot profile-name launch arguments.
