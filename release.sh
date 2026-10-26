#!/bin/bash
# Build PhotoGallery and push it to the local registry so Unraid's normal update flow works.
# Run on voltron:  /mnt/user/appdata/photogallery-src/release.sh
set -euo pipefail
cd "$(dirname "$0")"
IMG=192.168.2.3:5000/photogallery
docker build -t "$IMG:latest" .
docker push "$IMG:latest"
echo "Pushed. In Unraid's Docker tab, click 'check for updates', then 'apply update' on photogallery."
