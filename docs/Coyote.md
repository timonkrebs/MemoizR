# Running the Coyote concurrency tests

MemoizR uses [Microsoft Coyote](https://microsoft.github.io/coyote/) to systematically
explore thread interleavings of the locking/scheduling code. The systematic tests live in
`MemoizR.Tests/CoyoteTests.cs`.

Coyote can only control scheduling once the assemblies under test have been **rewritten**
(binary-instrumented) with `coyote rewrite`. Without rewriting, the engine runs only
*partially controlled* and reports false deadlocks. To avoid spurious failures, the tests
**detect whether the assemblies were rewritten and skip themselves when they were not** — so a
plain `dotnet test` is always green, and the systematic check only runs when meaningful.

## 0. Bootstrap the Coyote feed (once per clone)

The `Microsoft.Coyote*` packages are not consumed from nuget.org: they come from the
[timonkrebs/coyote](https://github.com/timonkrebs/coyote) fork, which is upgraded to
.NET 10 and models `System.Threading.Lock` (the synchronization primitive MemoizR's core
uses — the nuget.org release can neither rewrite net10.0 assemblies nor control `Lock`).
`nuget.config` maps `Microsoft.Coyote*` to a gitignored local feed (`packages/coyote`)
that is packed from source at a pinned commit:

```bash
bash eng/build-coyote-packages.sh
```

Until this has run once, any `dotnet restore`/`build`/`test` fails to resolve the Coyote
packages. CI runs the same script as its first step (see `.github/workflows/dotnet.yml`).

## Regular test run (no Coyote)

```bash
dotnet test
```

The whole suite runs against normal assemblies; the systematic tests skip themselves.

## Running the systematic Coyote tests

### 1. Install the Coyote CLI (once)

```bash
dotnet tool install --global Microsoft.Coyote.CLI --version 1.8.0-net10.2
```

Run this from the repository root: the version comes from the local feed bootstrapped in
step 0 (the package source mapping routes it there), and the CLI targets .NET 10.

### 2. Build

```bash
dotnet build
```

### 3. Rewrite the assemblies (in dependency order)

Build output follows the centralized artifacts layout (`UseArtifactsOutput`):

```bash
cd artifacts/bin/MemoizR.Tests/debug/
coyote rewrite MemoizR.StructuredAsyncLock.dll
coyote rewrite MemoizR.dll
coyote rewrite MemoizR.Reactive.dll
coyote rewrite MemoizR.StructuredConcurrency.dll
coyote rewrite MemoizR.Tests.dll
cd -
```

### 4. Run only the systematic tests against the rewritten assemblies

```bash
dotnet test --no-build --filter "FullyQualifiedName~CoyoteTests"
```

The tests detect the rewrite and run the full exploration
(`WithTestingIterations(100)`); if Coyote finds a bug they throw `Coyote found a bug: ...`.

> **Important:** only run the *systematic* tests against rewritten assemblies. The rest of the
> suite is timing-sensitive and the rewrite's per-operation instrumentation changes timing and
> exception semantics, which makes those tests fail. Run the regular suite on clean assemblies
> (`--filter "FullyQualifiedName!~CoyoteTests"`) and rewrite only for the Coyote step. This is
> exactly what CI does (see `.github/workflows/dotnet.yml`).

### Restoring clean assemblies

Rewriting modifies the DLLs in place. Rebuild to get clean (non-instrumented) binaries again:

```bash
dotnet build
```
