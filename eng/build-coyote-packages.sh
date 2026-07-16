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
SRC_DIR="${COYOTE_SRC_DIR:-$(mktemp -d)/coyote-src}"

if ls "$FEED_DIR"/Microsoft.Coyote."$COYOTE_VERSION_PREFIX-$COYOTE_VERSION_SUFFIX".nupkg >/dev/null 2>&1; then
    echo "Coyote packages already present in $FEED_DIR; skipping."
    exit 0
fi

echo "Cloning $COYOTE_REPO at $COYOTE_COMMIT"
git clone --no-checkout --filter=blob:none "$COYOTE_REPO" "$SRC_DIR"
git -C "$SRC_DIR" checkout --quiet "$COYOTE_COMMIT"

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
