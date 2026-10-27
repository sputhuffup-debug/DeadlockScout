# DemoFile (vendored)

[DemoFile](https://github.com/saul/demofile-net) by Saul Rennison, MIT licence (see `LICENSE` and `ACKNOWLEDGEMENTS`).
DeadlockScout's Direct live feed uses it to read Valve's broadcast.

## What is here

- Upstream commit `fd59701a998cf30a46adc4942e063d90de73c07a` (May 9, 2026), the 0.44.1 release.
- Only `src/DemoFile` and `src/DemoFile.Game.Deadlock`, plus upstream's `LICENSE`, `ACKNOWLEDGEMENTS` and `README.md`.
- Left out: `src/DemoFile.Game.Deadlock/Schema/` (JSON input for upstream's code generator, not used to build), tests,
  examples, benchmarks and the CS2 package.
- `Directory.Build.props` replaces upstream's root props: it pins the version to 0.44.1 instead of computing it from git
  history, and keeps DeadlockScout's own build settings away from this code.
- `Directory.Build.targets` compiles upstream's `Debug.Assert` checks out in every configuration. They encode
  expectations about older builds: a current-patch replay failed "Deletion on full update" in `DemoParser.Entities.cs`,
  a case haste handles without complaint and the code after the assert handles too. In a Debug build a failed assert
  ends the process, which would kill a debug run of DeadlockScout mid-match.

## Local changes

Kept together as `deadlockscout-unknown-types.patch` (applies to upstream `fd59701`):

- `src/DemoFile/Sdk/DecoderSet.cs`: a protected `TryGetSerializer`, so a decoder set can read the field layouts the
  demo or broadcast sends.
- `src/DemoFile.Game.Deadlock/DeadlockDecoderSet.cs`: fallbacks for field types newer than this schema. DemoFile only
  falls back for types on its fixed list, so current-patch replays failed with "Unrecognised serializer field", first on
  `m_nUpgradeInfo (CitadelAbilityUpgradeInfoPacked_t)`, then on
  `m_SerializePoseRecipeAG2Slots (CUtlVectorEmbeddedNetworkVar< AnimGraph2SerializedPoseRecipeSlot_t >)`.
  - An unknown scalar type, alone, in a fixed array or in a vector, decodes as an unsigned integer (varint, or
    fixed64/fixed8 per its encoder). This is the default of the haste parser deadlock-api uses on the current patch
    (`haste_core/src/fieldmetadata.rs`, `visit_ident`); its schema exposes both fields above as lists of raw 64-bit values.
  - An unknown struct with its own serializer (embedded, behind a pointer, in a fixed array or in a vector) decodes field
    by field from the serializer the broadcast sends, using the same fallbacks recursively.
  - All such values are discarded; the point is to keep the bit stream aligned so the fields DeadlockScout reads stay right.

## Updating

When upstream releases a version that reads the current patch, either switch `DeadlockScout.Api` back to the
`DemoFile.Game.Deadlock` NuGet package and delete this folder, or replace `src/` with the new release, drop `Schema/`,
re-apply the patch if still needed, and update the commit and version above. Check with
`dotnet run --project tools/DeadlockScout.ReplayCheck -- <recent replay>`.
