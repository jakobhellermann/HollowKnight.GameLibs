# HollowKnight.GameLibs

Bundler Nuget package for Hollow Knight game libraries. Strips and publicizes game libraries for local and CI development.

## Versioning

This package uses semantic versioning and follows the naming scheme `(package version)-hollowknight.(game version).api(modding api version)`, for example
`1.0.0-hollowknight.1.5.78.11833.api77`. This allows the package to seamlessly support multiple game/api versions. When a new version of the game
is released, we can simply create a new version on nuget targeting that game version, without impacting existing consumers. When
the content of the package itself is changed (e.g. a new file being added to the publicizer) then the package version is bumped.

Game versions that use the speedrun community-maintained API builds/backports use the `74hksr` api version suffix.
