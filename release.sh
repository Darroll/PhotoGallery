#!/bin/bash
# Build PhotoGallery and push it to the local registry so Unraid's normal update flow works.
# Normally started by deploy.sh on the server that builds the image.
set -euo pipefail
cd "$(dirname "$0")"
IMG=${REGISTRY:?Set REGISTRY to the registry to push to, for example 192.168.1.10:5000}/photogallery
SHA=$(cat .gitsha 2>/dev/null || echo dev)
docker build --build-arg GIT_SHA="$SHA" --build-arg BUILD_DATE="$(date -u '+%Y-%m-%d %H:%M UTC')" -t "$IMG:latest" .
docker push "$IMG:latest"
echo "Pushed. In Unraid's Docker tab, click 'check for updates', then 'apply update' on photogallery."
