# Roadmap to full TeknoParrot Manager parity

This plugin currently covers profile scan/register/repair/backup/restore
and HyperHQ import (see README's "What It Does"). The original PowerShell
TeknoParrot Manager has eight more features this plugin doesn't have yet.
This file tracks them as phases; check them off and update CHANGELOG.md as
each one lands. CLAUDE.md's "see the approved plan at commit time" line
refers to this file.

## Important boundary to resolve before Phase B starts

The remaining features split into two groups with very different risk
profiles:

- **Group A -- no new permission needed.** Pure XML field manipulation or
  deploying assets this plugin would bundle itself. Fits cleanly inside
  the existing permission set.
- **Group B -- downloads and runs third-party binaries.** ReShade,
  dgVoodoo2, the GPU compatibility fix, the FFB plugin/Blaster, BepInEx,
  and PostgreSQL setup all fetch a DLL/installer from GitHub or
  reshade.me and place it on disk (Postgres setup also installs a Windows
  service and creates a Windows user account). This directly contradicts
  this plugin's own README Safety Notes line: *"The plugin does not
  download, install, or modify third-party runtime binaries."* That line
  was a deliberate design boundary in the adopted base, not an oversight.

Before any Group B phase is implemented, this needs an explicit decision:
update the Safety Notes boundary and add the corresponding `permissions`
entries to `plugin.json` (network + file-write scopes per feature, mirroring
how the dat-index work added an explicit network permission entry), gated
by per-feature settings so a user who never touches FFB/Postgres/etc. never
has those permissions exercised. Group A has no such conflict and can
proceed without that conversation.

## Phase order

### Phase 1 -- Control propagation (Group A, highest complexity in this group) -- DONE (v0.3.0)
Port `Invoke-ControlPropagation` + `Invoke-DeviceSurvey`
(tpm.ps1:6025, 6194). Binds one reference game per control type in
TeknoParrotUI; copies those bindings to every other profile of the same
type, matched by button function so a wheel axis never lands on a gun.
Has real accumulated regression history worth preserving exactly (see the
archetype-Input-API comment at tpm.ps1:6034-6052 -- a v0.99.12 regression
where "correcting" an archetype's own Input API against the best overlap
match silently flipped a deliberately-configured profile). Needs:
button-node comparison (`Get-ButtonNodes`/`Get-ButtonKey`/
`Test-ButtonIsBound`), archetype pooling (`Build-ArchetypePool`), profile
family/Input-API helpers (`Get-ProfileFamily`, `Get-ProfileInputApi`,
`Set-ProfileInputApi`), and a device-survey wizard step (new `form`/
`async-action` onboarding steps) feeding `noPropagate`/`forceArchetype`/
`familyOverride` overrides.

### Phase 2 -- Crosshair deployment (Group A) -- DONE (v0.4.0)
Port `Invoke-CrosshairSetup` + `Export-CrosshairPreview` (tpm.ps1:2293,
1063). The original ships 321 curated crosshair PNGs in a `Crosshairs/`
folder plus an HTML preview; per the earlier base-adoption decision these
ship as packaged plugin assets under `assets/` (resolved, not revisited
here -- see project memory). Maps to a `selection-list` wizard/action step
since HyperHQ has no native image-preview step type.

### Phase 3 -- Cursor-hide setup (Group A, smallest) -- DONE (v0.4.0)
Port `Invoke-CursorHideSetup` (tpm.ps1:2507). Pure profile XML field
writes (PCSX2 cursor path fields per `Set-Pcsx2CursorPaths`, tpm.ps1:2241)
-- no third-party downloads, no new permissions.

### Phase 4 -- ReShade setup (Group B)
Port `Invoke-ReShadeSetup` + `Test-ReShadeDllSignature` +
`Get-ReShadeLatestVersion`/`Get-ReShadeTargetInfo` (tpm.ps1:1300, 1229,
1262, 1275). Auto-detects 32/64-bit per game, verifies Authenticode
signature before deploying, checks reshade.me for version drift.

### Phase 5 -- dgVoodoo2 setup (Group B)
Port `Invoke-DgVoodoo2Setup` + `Test-DgVoodoo2UpToDate` (tpm.ps1:1578,
1563). DirectX 8/DirectDraw/Glide-to-DX11/12 interception layer.

### Phase 6 -- GPU compatibility fix (Group B)
Port `Invoke-GpuFixSetup` + `Get-DetectedGpuVendor` +
`Get-GpuFixFieldNames`/`Test-GpuFixUpToDate` (tpm.ps1:2111, 1798, 1827,
1932). Auto-detects AMD/NVIDIA/Intel and applies the matching profile
field fix.

### Phase 7 -- Force feedback (Group B)
Port `Invoke-FFBBlasterSetup` + `Invoke-FFBPluginSetup` +
`Get-FFBPluginGameMap`/`Invoke-FFBPluginDownload` (tpm.ps1:4158, 3913,
3843, 3879). Two independent paths -- TeknoParrot's own FFB Blaster
(requires a paid TeknoParrot membership to function, the plugin can still
deploy the field config) and a free third-party plugin covering a
different game set -- with a conflict-resolution prompt for games covered
by both.

### Phase 8 -- BepInEx update check (Group B, update-only by design)
Port `Invoke-BepInExUpdateCheck` + `Get-BepInExInstalledVersion`/
`Get-BepInExInstalledArch`/`Get-BepInExLatestRelease` (tpm.ps1:4375, 4349,
4364, 4310). Deliberately never fresh-installs BepInEx, only updates an
already-present 64-bit install -- preserve that constraint exactly, it's a
safety choice in the original, not a gap.

### Phase 9 -- PostgreSQL setup (Group B, highest risk in this group)
Port `Install-Postgres83` + `Invoke-PostgresGameSetup` +
`Test-GameNeedsPostgres`/`Test-PostgresInstalled`/
`Test-PostgresPassword` + `Backup-PostgresDatabases`/
`Invoke-RestorePostgresBackup` (tpm.ps1:3534, 3739, 1997, 2052, 2092,
6653, 6712). For Incredible Technologies titles (Golden Tee Live, Power
Putt Live, etc). Installing PostgreSQL itself creates a Windows service
and a Windows user account, and requires Administrator privileges --
needs the self-elevation helper noted in the original architecture plan
(`ProcessStartInfo { Verb = "runas" }` for just this step, not the whole
plugin process) rather than the original script's "close and re-run as
Administrator" instruction.

## Out of scope (confirmed, not revisited)
LaunchBox direct integration, LaunchBox XML export, RetroBat/Batocera
naming mode -- HyperSpin 2 only per the original project decision.
