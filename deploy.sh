#!/bin/bash
# One-command release from the dev machine: test, make sure GitHub has the commit, sync the source to the
# Unraid server, build the image there and push it to the local registry.
#
#   ./deploy.sh              run tests, check git, sync, build, push
#   SKIP_TESTS=1 ./deploy.sh skip the test run
#   DEPLOY_HOST=other ./deploy.sh
#
# Needs DEPLOY_HOST and REGISTRY, from the environment or from a .deploy.env file next to this script.
#
# Afterwards, in Unraid's Docker tab: "check for updates", then "apply update" on photogallery.
set -euo pipefail
cd "$(dirname "$0")"

# Personal settings (DEPLOY_HOST, REGISTRY) live in .deploy.env, which is not in git.
[ -f .deploy.env ] && . ./.deploy.env
HOST=${DEPLOY_HOST:?Set DEPLOY_HOST (an ssh host name) in .deploy.env}
REGISTRY=${REGISTRY:?Set REGISTRY (for example 192.168.1.10:5000) in .deploy.env}
DIR=${DEPLOY_DIR:-/mnt/user/appdata/photogallery-src}

branch=$(git rev-parse --abbrev-ref HEAD)
[ "$branch" = "main" ] || { echo "Deploy from main (you are on $branch)." >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "Uncommitted changes: commit them first." >&2; git status --short >&2; exit 1; }
git fetch -q origin
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "HEAD is not the same as origin/main: push (or pull) first." >&2; exit 1; }
SHA=$(git rev-parse --short HEAD)

if [ -z "${SKIP_TESTS:-}" ]; then
  echo "== Running tests"
  dotnet test tests/PhotoGallery.Tests -c Release --nologo -v q
fi

echo "== Syncing $SHA to $HOST:$DIR"
git archive HEAD | ssh -o BatchMode=yes "$HOST" "mkdir -p '$DIR' && cd '$DIR' && tar -xf - && echo $SHA > .gitsha && chmod +x release.sh"

echo "== Building and pushing on $HOST"
ssh -o BatchMode=yes "$HOST" "REGISTRY='$REGISTRY' '$DIR/release.sh'" | tail -4

echo "== Done: $SHA is in the registry. Update the container from Unraid's Docker tab."
