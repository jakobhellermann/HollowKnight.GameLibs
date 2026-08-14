# HollowKnight.GameLibs

Bundler Nuget package for Hollow Knight game libraries. Strips and publicizes game libraries for local and CI development.

## Versioning

This package uses semantic versioning and follows the naming scheme `(package version)-hollowknight.(game version).api(modding api version)`, for example
`1.0.0-hollowknight.1.5.78.11833.api77`. This allows the package to seamlessly support multiple game/api versions. When a new version of the game
is released, we can simply create a new version on nuget targeting that game version, without impacting existing consumers. When
the content of the package itself is changed (e.g. a new file being added to the publicizer) then the package version is bumped.

Game versions that use the speedrun community-maintained API builds/backports use the `74hksr` api version suffix.

### Available versions

| game version | modding api | target framework |
|---|---|---|
| 1.2.2.1 | 42hksr | net35 |
| 1.3.1.5 | 74hksr | net35 |
| 1.4.3.2 | 74hksr | net35 |
| 1.5.78.11833 | 77 | net472 |
| 1.5.12620 | 78 | netstandard2.1 |

Published versions:
- `1.0.0-hollowknight.1.2.2.1.api42hksr`
- `1.0.0-hollowknight.1.4.3.2.api74hksr`
- `1.0.0-hollowknight.1.3.1.5.api74hksr`
- `1.0.0-hollowknight.1.5.78.11833.api77`
- `1.0.0-hollowknight.1.5.12620.api78`
