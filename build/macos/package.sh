#!/usr/bin/env bash
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "${script_directory}/../.." && pwd)"
runtime_id="${1:-osx-arm64}"
publish_directory="${repository_root}/artifacts/publish/${runtime_id}"
bundle_directory="${repository_root}/artifacts/NEOCR.app"

case "${runtime_id}" in
  osx-arm64|osx-x64) ;;
  *)
    echo "Unsupported runtime identifier: ${runtime_id}" >&2
    exit 2
    ;;
esac

dotnet publish "${repository_root}/src/OcrWorkbench.Gui/OcrWorkbench.Gui.csproj" \
  --configuration Release \
  --runtime "${runtime_id}" \
  --self-contained false \
  --output "${publish_directory}"

mkdir -p "${bundle_directory}/Contents/MacOS" "${bundle_directory}/Contents/Resources"
ditto "${publish_directory}" "${bundle_directory}/Contents/MacOS"
cp "${script_directory}/Info.plist" "${bundle_directory}/Contents/Info.plist"
codesign --force --deep --sign - "${bundle_directory}"

echo "Created ${bundle_directory}"
