#!/bin/sh
# Universal launcher: runs the Apple Silicon or Intel build, whichever matches this Mac.
DIR="$(cd "$(dirname "$0")" && pwd)"
if [ "$(uname -m)" = "arm64" ]; then exec "$DIR/../Resources/arm64/FanKick" "$@"; fi
exec "$DIR/../Resources/x64/FanKick" "$@"
