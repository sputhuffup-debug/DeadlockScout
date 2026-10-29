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

All kept in `deadlockscout.patch` (applies to upstream `fd59701`). Each was found by replaying a recorded live
broadcast from Deadlock build 6821 (Oct 27, 2026) through this code and, side by side, through the haste parser that
deadlock-api uses, then comparing bits consumed field by field until the first disagreement.

1. **Integer encoders** (`src/DemoFile/FieldDecode.cs`, `FallbackDecoder.cs`): every integer decoder honours the field's
   var encoder, as haste does: `fixed8` is 8 raw bits (sign-extended for signed fields), `fixed64` is 64 raw bits, anything
   else a varint. Upstream ignored the encoder, so `uint8` fields now sent as `fixed8` (e.g. `STeamFOWEntity.m_nPositionX`)
   swallowed extra bytes for values >= 128 and misaligned every following field.
2. **Unsigned id types** (`src/DemoFile.Game.Deadlock/Sdk/Schema.cs`): `HeroID`, `HeroBadgeXP`, `HeroBuildID`, `ItemID`,
   `ItemDefinitionIndex`, `MatchID`, `AttachmentHandle`, `AttribDefinitionIndex`, `CitadelLobbyPlayerSlot` and `StyleIndex`
   decode as unsigned varints. Upstream read them zigzag-signed, which turned hero 18 into 9 and odd ids negative. Checked
   against the API's hero list for the recorded match: all 12 heroes and teams match.
3. **Types newer than the schema** (`src/DemoFile.Game.Deadlock/DeadlockDecoderSet.cs`, plus a protected
   `TryGetSerializer` and public `TryGetSerializerKey` in `src/DemoFile/Sdk/DecoderSet.cs`):
   - an unknown scalar (alone, in a fixed array or in a vector) decodes as an unsigned integer per its encoder, haste's
     default (`CitadelAbilityUpgradeInfoPacked_t`);
   - an unknown struct with its own serializer (embedded, pointer, fixed array or vector) decodes field by field from
     the serializer the broadcast sends (`AnimGraph2SerializedPoseRecipeSlot_t`, `CCitadelPlayOfTheGame*`);
   - `CUtlBinaryBlock` is in the fallback list (varint length + bytes; `AnimGraph2SerializedPoseRecipeSlot_t.m_topology`).
   Values decoded this way are discarded; the point is to keep the bit stream aligned.
4. **Entity classes newer than the schema** (`src/DemoFile/DemoParser.Entities.cs`,
   `src/DemoFile.Game.Deadlock/DeadlockDemoParser.cs`): a class with no generated type (`CEnvironmentLight`) becomes a plain
   `CBaseEntity` whose decoder is built from the class's own serializer, instead of throwing "Attempted to create unknown
   entity".

`Directory.Build.targets` (above) is the fifth change: upstream's `Debug.Assert`s are compiled out, because "Deletion on
full update" fires on current replays and kills a Debug process.

6. **.NET 10** (`src/DemoFile/DemoFile.csproj`, `src/DemoFile.Game.Deadlock/DemoFile.Game.Deadlock.csproj`): both target
   `net10.0` instead of upstream's `net9.0`, the same framework as the rest of the solution, so the .NET 10 SDK builds
   everything without the .NET 9 reference pack. No source changes were needed (the projects build with
   `TreatWarningsAsErrors`); a live broadcast decoded through `tools/DeadlockScout.ReplayCheck` afterwards (all players,
   net worth by minute, clock difference +0.0 s).

7. **Grpc.Tools 2.64.0 and Google.Protobuf 3.26.1** (both `.csproj` files, instead of upstream's 2.40.0 and 3.22.1).
   Grpc.Tools 2.40's `ProtoCompile` reads protoc's output with a regex on a 100 ms timeout, and on a slow Windows CI
   runner it threw `RegexMatchTimeoutException` (MSB4018) on protoc's "Import ... is unused" warning, failing the build
   twice in two days. gRPC fixed it in 1.64 ("Grpc.Tools: Handle regex timeout when parsing protoc output", #36185);
   Google.Protobuf moves to the runtime matching that release's protoc. The wire format is unchanged; the solution
   builds with `TreatWarningsAsErrors` and the tests pass.

## Updating

When upstream releases a version that reads the current patch, either switch `DeadlockScout.Api` back to the
`DemoFile.Game.Deadlock` NuGet package and delete this folder, or replace `src/` with the new release, drop `Schema/`,
re-apply the patch if still needed, and update the commit and version above. Check with
`dotnet run --project tools/DeadlockScout.ReplayCheck -- <recent replay>`.
