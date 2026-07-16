#!/usr/bin/env bash
# Builds the Microsoft.Coyote packages from the timonkrebs/coyote fork into the
# local feed that nuget.config maps 'Microsoft.Coyote*' to (packages/coyote).
#
# The fork carries the .NET 10 upgrade and the System.Threading.Lock model that
# MemoizR's systematic concurrency tests depend on; until those changes are on a
# published feed, the packages are packed from source at a pinned commit so the
# build stays reproducible and no binary artifacts live in this repository.
set -euo pipefail

COYOTE_REPO="https://github.com/timonkrebs/coyote"
COYOTE_COMMIT="7143589c54b1310d0fde0cb7dca208875a79df69"
COYOTE_VERSION_PREFIX="1.8.0"
COYOTE_VERSION_SUFFIX="net10.1"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FEED_DIR="$REPO_ROOT/packages/coyote"
# Where the clone lives matters twice over:
#  - NOT inside this repository: MSBuild's upward directory walk would impose
#    MemoizR's Directory.Build.props/Directory.Packages.props (central package
#    management) onto Coyote's projects, which fails their restore with NU1008.
#  - NOT under macOS's $TMPDIR (what plain mktemp returns there): that path is
#    under /var, a symlink to /private/var, and MSBuild's publish/pack pipeline
#    mixes the raw and canonicalized spellings -- the packed Coyote CLI tool then
#    ships without its Mono.Cecil dependencies and `coyote rewrite` dies at startup.
# On CI, RUNNER_TEMP is a real (non-symlinked) per-job directory outside the
# workspace on every OS. Local fallbacks: /tmp is a real path on Linux, and on
# macOS the user cache directory is used instead of the known-bad mktemp location.
if [ -n "${COYOTE_SRC_DIR:-}" ]; then
    SRC_DIR="$COYOTE_SRC_DIR"
elif [ -n "${RUNNER_TEMP:-}" ]; then
    SRC_DIR="$RUNNER_TEMP/coyote-src"
elif [ "$(uname -s)" = "Darwin" ]; then
    SRC_DIR="$HOME/Library/Caches/memoizr/coyote-src"
else
    SRC_DIR="$(mktemp -d)/coyote-src"
fi

if ls "$FEED_DIR"/Microsoft.Coyote."$COYOTE_VERSION_PREFIX-$COYOTE_VERSION_SUFFIX".nupkg >/dev/null 2>&1; then
    echo "Coyote packages already present in $FEED_DIR; skipping."
    exit 0
fi

echo "Cloning $COYOTE_REPO at $COYOTE_COMMIT"
rm -rf "$SRC_DIR"
git clone --no-checkout --filter=blob:none "$COYOTE_REPO" "$SRC_DIR"
git -C "$SRC_DIR" checkout --quiet "$COYOTE_COMMIT"

# Fail fast on the wrong SDK band instead of building a feed that only breaks
# later: the 10.0.3xx SDK's NuGet drops local-feed transitive dependencies at
# restore and packs the Coyote CLI tool without its Mono.Cecil dependencies,
# which is why CI pins 10.0.1xx (see .github/workflows/dotnet.yml). Resolved
# from inside the clone so Coyote's own global.json roll-forward is honored.
SDK_VERSION="$(cd "$SRC_DIR" && dotnet --version)"
case "$SDK_VERSION" in
    10.0.1*) ;;
    *)
        echo "error: packing the Coyote feed needs a .NET SDK from the 10.0.1xx band;" >&2
        echo "       the selected SDK is $SDK_VERSION. Install 10.0.1xx (it can sit" >&2
        echo "       side by side with newer bands) and re-run." >&2
        exit 1
        ;;
esac

for project in \
    Source/Core/Core.csproj \
    Source/Actors/Actors.csproj \
    Source/Test/Test.csproj \
    Tools/CLI/Coyote.CLI.csproj \
    Scripts/NuGet/Coyote.Meta.csproj; do
    # -p: (not /p:) -- git-bash on Windows applies MSYS path conversion to
    # slash-prefixed arguments, mangling /p:Foo=bar into a stray 'p:Foo=bar'
    # that MSBuild rejects with MSB1008. The dash form is never converted.
    dotnet pack -c Release \
        "-p:VersionPrefix=$COYOTE_VERSION_PREFIX" \
        "-p:VersionSuffix=$COYOTE_VERSION_SUFFIX" \
        "$SRC_DIR/$project"
done

mkdir -p "$FEED_DIR"
cp "$SRC_DIR"/bin/nuget/*.nupkg "$FEED_DIR/"
echo "Coyote packages available in $FEED_DIR:"
ls "$FEED_DIR"
