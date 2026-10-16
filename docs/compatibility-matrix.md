# Compatibility matrix

Every derived number in this repository (difficulty labels, slider durations, tick counts) belongs
to one row of this table. Changing a row is a deliberate migration, not a dependency bump.

## Current pin

| Component | Pinned | Notes |
|---|---|---|
| Target client | osu!lazer `2026.921.0-lazer` | Historical integration target. Previously checked (`osu!.exe` product version `2026.921.0-lazer+aaa75b7`). |
| Official packages | `ppy.osu.Game` / `ppy.osu.Game.Rulesets.Osu` `2026.916.0` | Repository pin. See the historical gap analysis below. |
| .NET SDK | `10.0.100`, `rollForward: latestFeature` | Matches ppy/osu's `global.json`; built here with 10.0.303. |
| Difficulty profile | `osu-2026.916.0-nomod` | Name stored with every difficulty value (`DifficultyReport.Profile`). |
| Interchange profile | `osu-interchange-v1` | `.osu` v128 from the official encoder, `.osz` with the audio at the root. Not a stable (v14) profile. |

## Gap between the package and the target client

The original comparison used the client tag `2026.921.0-lazer` and NuGet packages `2026.916.0`.
This is the recorded integration baseline, not a claim about today's newest release.
`git compare 2026.916.0...2026.921.0-lazer` on ppy/osu shows 11 commits touching:

- chat, ranked-play UI, online API and settings (`osu.Game/Online/**`, `osu.Game/Overlays/**`, `osu.Game/Screens/OnlinePlay/**`, `OsuConfigManager`)
- `osu.Game/IO/Archives/ZipArchiveReader.cs` (client-side archive reading during import)
- skinning (`LegacyKeyCounterDisplay`) and mobile props

None touch `osu.Game/Beatmaps/Formats/**`, `osu.Game/Rulesets/Objects/**` or
`osu.Game.Rulesets.Osu/**`, so decoding, encoding, slider geometry and difficulty are the same code
in both. The `ZipArchiveReader` change is why the import of a produced `.osz` must still be checked
in the real `2026.921.0-lazer` client: headless tests cannot cover it.

## Known transitive advisory

`ppy.osu.Game` depends on AutoMapper 13.0.1 (GHSA-rvv3-g6hj-g44x, high). Nothing here calls
AutoMapper or detaches Realm models, which is where osu! uses it. The advisory is suppressed by ID
in `Directory.Build.props` instead of disabling NuGet audit, and the suppression must go when the pin
moves past it.

## Upgrading the pin

1. Read the ppy/osu diff between the old and new tags for `Beatmaps/Formats`, `Rulesets/Objects`,
   `Rulesets.Osu/Objects` and `Rulesets.Osu/Difficulty`.
2. Bump `Directory.Packages.props`, restore, run `dotnet test`.
3. A failing `DifficultyTests.MatchesGoldenSnapshot` is expected when the calculator changed.
   Regenerate with `osu-ai-harness snapshot tests/fixtures/osu tests/fixtures/golden/difficulty-osu-<version>.json`,
   keep the old file, and treat old labels as a different version. Never overwrite a golden file to
   make a test pass without that review.
4. Repeat the manual import check (`docs/foundation-status.md`) in the matching client.
