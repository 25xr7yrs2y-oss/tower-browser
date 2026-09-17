# Third-Party Components

The repository source does not vendor third-party binaries. The version 1.0.10
portable release redistributes pinned, unmodified runtime files as described
below and in `docs/DEPENDENCIES.md` inside the package.

## Mullvad Browser

- Project: <https://github.com/mullvad/mullvad-browser>
- Tested package target: Mullvad Browser 15.0.14 for Windows x86-64
- License: Mozilla Public License 2.0 and applicable bundled-component licenses
- Distribution: the official Windows application tree is extracted without
  running its installer. Official source and release materials remain available
  from the project link above.

## myst-lmprove

- Project: <https://github.com/25xr7yrs2y-oss/myst-lmprove>
- Audited branch: `custom-proxy-build`
- Audited commit: `7944a4c634834aac10a4e8e49934e326ac3f0e7a`
- Desktop license: MIT; bundled custom node: GNU General Public License v3
- Distribution: only the required `myst.exe` node is included; the Electron
  shell, supervisor, and installer are excluded.
- Corresponding source: attached to the version 1.0.10 release as
  `TowerBrowser-1.0.10-myst-lmprove-source-7944a4c.tar.gz`.

Users must review and comply with the licenses and terms shipped by each
third-party component.
