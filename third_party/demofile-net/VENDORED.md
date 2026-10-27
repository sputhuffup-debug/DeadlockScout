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

## Local change

One change, in `src/DemoFile.Game.Deadlock/DeadlockDecoderSet.cs`, kept as `deadlockscout-unknown-scalar.patch`:

The Deadlock patch current in October 2026 added the field type `CitadelAbilityUpgradeInfoPacked_t`
(`m_nUpgradeInfo`). DemoFile 0.44.1 only falls back for types it lists, so every replay and broadcast failed with
"Unrecognised serializer field". The patch decodes any unrecognised plain scalar field without its own serializer as an
unsigned integer (varint, or fixed64/fixed8 when the encoder says so) and discards the value. This mirrors the default of
the haste parser (`haste_core/src/fieldmetadata.rs`, `visit_ident`), which deadlock-api uses on the current patch.

## Updating

When upstream releases a version that reads the current patch, either switch `DeadlockScout.Api` back to the
`DemoFile.Game.Deadlock` NuGet package and delete this folder, or replace `src/` with the new release, drop `Schema/`,
re-apply the patch if still needed, and update the commit and version above. Check with
`dotnet run --project tools/DeadlockScout.ReplayCheck -- <recent replay>`.
