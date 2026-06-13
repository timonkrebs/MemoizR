# Running the Coyote concurrency tests

MemoizR uses [Microsoft Coyote](https://microsoft.github.io/coyote/) to systematically
explore thread interleavings of the locking/scheduling code. The systematic test lives in
`MemoizR.Tests/CoyoteTests.cs` (`TestThreadSafetyWithCoyote`).

Coyote can only control scheduling once the assemblies under test have been **rewritten**
(binary-instrumented) with `coyote rewrite`. Without rewriting, the engine runs only
*partially controlled* and reports false deadlocks. To avoid spurious failures, the test
**detects whether the assemblies were rewritten and skips itself when they were not** — so a
plain `dotnet test` is always green, and the systematic check only runs when meaningful.

## Regular test run (no Coyote)

```bash
dotnet test
```

The whole suite runs against normal assemblies; `TestThreadSafetyWithCoyote` skips itself.

## Running the systematic Coyote test

### 1. Install the Coyote CLI (once)

```bash
dotnet tool install --global Microsoft.Coyote.CLI
```

The CLI targets .NET 8. If only a newer runtime is installed, allow roll-forward:

```bash
export DOTNET_ROLL_FORWARD=Major   # needed only if you don't have the .NET 8 runtime
```

### 2. Build

```bash
dotnet build
```

### 3. Rewrite the assemblies (in dependency order)

```bash
cd MemoizR.Tests/bin/Debug/net10.0/
coyote rewrite MemoizR.StructuredAsyncLock.dll
coyote rewrite MemoizR.dll
coyote rewrite MemoizR.Reactive.dll
coyote rewrite MemoizR.StructuredConcurrency.dll
coyote rewrite MemoizR.Tests.dll
cd -
```

### 4. Run only the systematic test against the rewritten assemblies

```bash
dotnet test --no-build --filter "FullyQualifiedName~CoyoteTests"
```

Now `TestThreadSafetyWithCoyote` detects the rewrite and runs the full exploration
(`WithTestingIterations(100)`); if Coyote finds a bug it throws `Coyote found a bug: ...`.

> **Important:** only run the *systematic* test against rewritten assemblies. The rest of the
> suite is timing-sensitive and the rewrite's per-operation instrumentation changes timing and
> exception semantics, which makes those tests fail. Run the regular suite on clean assemblies
> (`--filter "FullyQualifiedName!~CoyoteTests"`) and rewrite only for the Coyote step. This is
> exactly what CI does (see `.github/workflows/dotnet.yml`).

### Restoring clean assemblies

Rewriting modifies the DLLs in place. Rebuild to get clean (non-instrumented) binaries again:

```bash
dotnet build
```
