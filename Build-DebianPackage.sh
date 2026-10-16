#!/usr/bin/env bash
set -euo pipefail

version=${1:-1.0.0}
maintainer=${2:-}
sdk_version=10.0.402
runtime_version=10.0.13

if [[ $# -gt 2 || ! $version =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ||
      ! $maintainer =~ ^[^\<\>]+\ \<[^\<\>\ ]+@[^\<\>\ ]+\>$ ]]; then
    echo 'Usage: bash Build-DebianPackage.sh [version] "Maintainer Name <email@example.org>"' >&2
    exit 1
fi
if [[ $maintainer == *$'\n'* || $maintainer == *$'\r'* ]]; then
    echo 'The maintainer must be a single line.' >&2
    exit 1
fi
if [[ $(uname -s) != Linux || $(uname -m) != x86_64 || $EUID == 0 ]]; then
    echo 'Generate the package as a normal user on x64 Ubuntu 24.04 or Debian 13.' >&2
    exit 1
fi
# These are the intended build hosts, not an installed compatibility claim.
source /etc/os-release
if [[ ${ID:-}:${VERSION_ID:-} != ubuntu:24.04 && ${ID:-}:${VERSION_ID:-} != debian:13 ]]; then
    echo 'This procedure targets Ubuntu 24.04 and Debian 13.' >&2
    exit 1
fi
for tool in dotnet dpkg dpkg-deb dpkg-query python3 desktop-file-validate install find touch sha256sum mktemp; do
    command -v "$tool" >/dev/null
done
python3 - "$version" <<'PY'
import sys

if any(int(component) > 65535 for component in sys.argv[1].split(".")):
    raise SystemExit("Version components must be between 0 and 65535.")
PY
if ! dpkg --compare-versions "$(dpkg-query -W -f='${Version}' dpkg)" ge 1.22; then
    echo 'This procedure requires dpkg-deb 1.22 or later.' >&2
    exit 1
fi

repository=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
cd -- "$repository"
export LC_ALL=C TZ=UTC AVALONIA_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
umask 022
export SOURCE_DATE_EPOCH=${SOURCE_DATE_EPOCH:-0}
if [[ ! $SOURCE_DATE_EPOCH =~ ^[0-9]+$ ]]; then
    echo 'SOURCE_DATE_EPOCH must be a nonnegative integer.' >&2
    exit 1
fi
mkdir -p -- "$repository/obj/debian-package/$version"
output=$(mktemp -d "$repository/obj/debian-package/$version/run-XXXXXXXX")
export DOTNET_CLI_HOME="$output/dotnet-home"
if [[ $(dotnet --version) != "$sdk_version" ]]; then
    echo "This procedure requires .NET SDK $sdk_version." >&2
    exit 1
fi
publish="$output/publish"
package_root="$output/package"

# Keep each attempt separate; never delete previous output or personal data.
dotnet publish src/LeRobocopier.Desktop/LeRobocopier.Desktop.csproj \
    --configuration Release --framework net10.0 --runtime linux-x64 \
    --self-contained true --output "$publish" \
    "-p:Version=$version" "-p:AssemblyVersion=$version.0" "-p:FileVersion=$version.0" \
    "-p:RuntimeFrameworkVersion=$runtime_version" -p:TargetLatestRuntimePatch=false \
    -p:UseAppHost=true -p:PublishSingleFile=false -p:PublishTrimmed=false \
    -p:PublishReadyToRun=false

for file in LeRobocopier.Desktop LeRobocopier.Desktop.dll LeRobocopier.Core.dll \
    LeRobocopier.Desktop.deps.json LeRobocopier.Desktop.runtimeconfig.json \
    libhostfxr.so libhostpolicy.so libcoreclr.so System.Private.CoreLib.dll \
    libSkiaSharp.so libHarfBuzzSharp.so; do
    [[ -f "$publish/$file" ]] || { echo "Missing publication file: $file" >&2; exit 1; }
done
python3 - "$publish/LeRobocopier.Desktop.runtimeconfig.json" "$runtime_version" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    options = json.load(stream)["runtimeOptions"]
frameworks = options.get("includedFrameworks", [])
if [entry["version"] for entry in frameworks if entry["name"] == "Microsoft.NETCore.App"] != [sys.argv[2]]:
    raise SystemExit("The publication must include the pinned Microsoft.NETCore.App runtime.")
PY

install -d -m 0755 "$package_root/DEBIAN" "$package_root/usr/lib/le-robocopier"
find "$publish" -type d -exec chmod 0755 {} +
find "$publish" -type f -exec chmod 0644 {} +
chmod 0755 "$publish/LeRobocopier.Desktop"
cp -R -- "$publish/." "$package_root/usr/lib/le-robocopier/"
install -D -m 0755 LeRobocopier.Linux.sh "$package_root/usr/bin/le-robocopier"
install -D -m 0644 LeRobocopier.Linux.desktop "$package_root/usr/share/applications/le-robocopier.desktop"
install -D -m 0644 LeRobocopier.Linux.svg "$package_root/usr/share/icons/hicolor/scalable/apps/le-robocopier.svg"
desktop-file-validate "$package_root/usr/share/applications/le-robocopier.desktop"

installed_size=$(python3 - "$package_root/usr" <<'PY'
from pathlib import Path
import sys

root = Path(sys.argv[1])
# Round each file to KiB; count directories independently of filesystem block allocation.
print(1 + sum((entry.stat().st_size + 1023) // 1024 if entry.is_file() else 1
              for entry in root.rglob("*")))
PY
)
cat > "$package_root/DEBIAN/control" <<EOF
Package: le-robocopier
Version: $version
Section: utils
Priority: optional
Architecture: amd64
Maintainer: $maintainer
Installed-Size: $installed_size
Depends: ca-certificates, libc6 (>= 2.39), libgcc-s1, libgssapi-krb5-2, libicu74 | libicu76, libssl3t64, libstdc++6, tzdata, zlib1g, libx11-6, libice6, libsm6, libfontconfig1, libxcursor1, libxext6, libxi6, libxrandr2
Recommends: libgl1, libgtk-3-0t64, xdg-desktop-portal, xdg-desktop-portal-gtk | xdg-desktop-portal-kde | xdg-desktop-portal-gnome
Description: Organize verified file backups by project
 Copy, move and restore registered files and folders with SHA-256 verification.
 Includes the .NET runtime. Personal settings and backups remain in user directories.
EOF
chmod 0644 "$package_root/DEBIAN/control"
find "$package_root" -type d -exec chmod 0755 {} +
find "$package_root" -exec touch -h -d "@$SOURCE_DATE_EPOCH" {} +
package="$output/le-robocopier_${version}_amd64.deb"
dpkg-deb --root-owner-group --uniform-compression --threads-max=1 -Zxz -z9 --build "$package_root" "$package"
dpkg-deb --info "$package"
dpkg-deb --contents "$package"
(cd -- "$output" && sha256sum "$(basename -- "$package")" > "$(basename -- "$package").sha256")
printf 'Package: %s\nSHA-256: %s.sha256\n' "$package" "$package"
