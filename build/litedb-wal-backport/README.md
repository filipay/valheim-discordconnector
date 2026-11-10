# LiteDB 5.0.21 WAL flush backport — opt-in testing only

This downstream source build applies only `stream.Flush()` after the page loop,
**inside** `DiskService.WriteLogDisk`'s stream lock, from upstream
[PR2717 / merged fix 7ae5d1f](https://github.com/litedb-org/LiteDB/commit/7ae5d1ffb825c6656c07870ce0dcbe719b01b61b.patch).
It is not an official LiteDB release, a v6 upgrade, a database-format change, or
proof that the production Mono failure is fixed. No binary patching is used.

## Pinned inputs

- Official `v5.0.21` tag commit: `84065086a8e8716063b255d0abb332708d0b2ad3`.
- Source: `https://codeload.github.com/litedb-org/LiteDB/tar.gz/84065086a8e8716063b255d0abb332708d0b2ad3`.
- Archive SHA-256: `6479655da7efe6beaaa59713127f5df7f8220019bc321ac2797f6bbd6e8f1f14`.
- `wal-flush.patch` SHA-256: `05c31472e8c122e5193c02807c536cdbabb06854a9ce9ac264df04e95cc9df62`.
- Exact package versions/hashes and official NuGet URLs are pinned in `build.py`.
  It copies cached `.nupkg` files when available; otherwise downloads only those
  exact packages. All extraction/restore/cache writes remain in the workspace.

## Source build

Requires Python 3.12+, `patch`, and an existing .NET SDK. Validated with SDK
10.0.402/runtime 10.0.13 on macOS Arm64. Supply your existing SDK executable and
a fresh disposable workspace explicitly; neither is installed by these instructions.
From the repository root:

```sh
ROOT="$PWD"
: "${SDK:?Set SDK to the absolute path of your existing dotnet executable}"
: "${D:?Set D to an absolute, unused disposable build workspace}"
test -x "$SDK" || exit 1
export DOTNET_ROOT="$(dirname "$SDK")"
export DOTNET_GENERATE_ASPNET_CERTIFICATE=false DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_ROLL_FORWARD=Major
python3 build/litedb-wal-backport/build.py --help # Read-only argument check.
# The next command downloads pinned inputs if not cached and builds; opt-in only.
python3 build/litedb-wal-backport/build.py --dotnet "$SDK" --workspace "$D"
```

The recipe refuses to reuse an existing `source/` directory. It verifies pinned
hashes, safely extracts source, applies the patch with zero fuzz, checks that no
other original source file changed, and builds the upstream project in Release.
It disables enclosing repository Directory.Build imports; it never builds the
plugin, runs tests, opens a database, publishes, or modifies the global NuGet cache.

**Target/constant compatibility is deliberate:** upstream lists `net4.5` but
conditions HAVE flags on `net45`. The shipped NuGet net45 DLL actually uses the
fallback ObjectId branches. We retain canonical **net45**, but explicitly set
`DefineConstants=RELEASE` (no HAVE flags) to preserve that shipped behavior. Do not
enable HAVE_APP_DOMAIN/HAVE_PROCESS/HAVE_ENVIRONMENT or fix ObjectId here.

The tag's source version fields are 5.0.20. Explicit build properties instead emit:

- AssemblyVersion **5.0.21.0** (binding identity unchanged).
- FileVersion **5.0.21.1**.
- InformationalVersion **`5.0.21-dc-walflush+84065086a8e8716063b255d0abb332708d0b2ad3`**.
- TargetFramework **`.NETFramework,Version=v4.5`**.
- `SignAssembly=true`, `PublicSign=false`, `DelaySign=false`, original upstream
  `LiteDB.snk`; expected public-key token **`4ee40123013c9f27`**.

Outputs: `$D/dist/LiteDB.dll`, `LICENSE.LiteDB.txt`, `provenance.json`.
Exact commands, evaluated properties, source/package/key/output hashes, compiler
log and binlog are retained under `$D`. Inputs and commands are reproducible;
byte identity across different SDKs/absolute workspace paths is not promised.
The current compiler also emits extra cleanup in generated iterator Dispose
methods; this is not a byte-identical rebuild of the published NuGet binary.

## Explicit testing reference override

**Normal builds remain unpatched NuGet 5.0.21.** Selecting the backport requires
both an explicit targets import and an absolute DLL path:

```sh
"$SDK" build path/to/project.csproj --no-restore \
  -p:CustomAfterMicrosoftCommonTargets="$ROOT/build/litedb-wal-backport/LiteDbBackport.targets" \
  -p:LiteDbBackportPath="$D/dist/LiteDB.dll"
```

Use the caller's existing isolated output/intermediate and safe-build properties;
the snippet only specifies dependency selection, **not permission to run the
plugin's publish/copy targets**. It applies to the server and the consuming
harness projects without editing their PackageReferences. Importing these targets
requires a nonempty existing `LiteDbBackportPath`; omission/missing file fails.
The targets remove original NuGet compile/runtime assets and verify that exactly
the selected DLL is the compile and CopyLocal reference. Existing server ILRepack
then consumes that copied `$(OutputPath)/LiteDB.dll`, not a second package DLL.

If `CustomAfterMicrosoftCommonTargets` already names safe packaging overrides,
create a **scratch wrapper targets file** importing that file **and** this one,
then pass the wrapper as `CustomAfterMicrosoftCommonTargets`. Do not discard
the existing safety overrides. Leave this opt-in out of ordinary builds.

## Verification and distribution

The retained `litedb-wal-backport/implementation-report.md` in the ignored
workspace records baseline error999, final patched immediate-read success with
the same upstream delayed-publication stream and no external Flush, and a new
ordinary FileStream default-threshold checkpoint/reopen fixture. Metadata checks
cover identity/version/public key/framework, flush ordering under the stream
lock, assembly-reference equality and **all 29 ObjectId method bodies** against
NuGet. Minimal net45/.NET probes verify override compile/CopyLocal hashes,
missing-path rejection, and original NuGet selection without the import.

Parent/build owner must still run the four application harnesses, isolated
native net481/ILRepack and final merged-library inspection before replacing any
public testing artifact. No production database, game, server or Discord action
is part of this recipe. Full cryptographic strong-name signature verification
is not claimed by the metadata checks.

The November 2 observation redesign has no newly built plugin DLL. Its focused
Reporting/Statistics checks and disposable `--growth-check` do not replace the
parent's final five-harness review or a separately authorized native build.
Only `python3 build/litedb-wal-backport/build.py --help` was executed for this
README update; the argument names above match that actual parser output.

**License:** upstream LiteDB is MIT, copyright 2014–2022 Mauricio David. The recipe
copies its exact pinned LICENSE to `dist/LICENSE.LiteDB.txt`. Include that notice
with any standalone or merged distribution, together with a clear downstream
backport identification; do not label this as unmodified official 5.0.21.
