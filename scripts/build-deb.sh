#!/usr/bin/env bash
# Builds a .deb from a self-contained linux-x64 publish.
# Usage: build-deb.sh <publish-dir> <package> <version> <app-id> <exe> <desktop-file> <icon-png> <description> <out-dir>
# Installs to /opt/<package>, links /usr/bin/<exe>, and adds the desktop entry and icon.
set -euo pipefail
publish=$1 package=$2 version=$3 appid=$4 exe=$5 desktop=$6 icon=$7 description=$8 out=$9
root=$(mktemp -d)
mkdir -p "$root/DEBIAN" "$root/opt/$package" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/pixmaps"
cp -r "$publish"/. "$root/opt/$package/"
chmod 755 "$root/opt/$package/$exe"
ln -s "/opt/$package/$exe" "$root/usr/bin/$exe"
install -m644 "$desktop" "$root/usr/share/applications/$appid.desktop"
install -m644 "$icon" "$root/usr/share/pixmaps/$appid.png"
size=$(du -sk "$root" | cut -f1)
# Debian versions sort "~" before release, so 0.1.0-preview.1 becomes 0.1.0~preview.1.
debversion=${version/-/\~}
cat > "$root/DEBIAN/control" <<CONTROL
Package: $package
Version: $debversion
Architecture: amd64
Maintainer: Al Ummah Now (https://github.com/Al-Ummah-Now/AlUmmahNowDesktop)
Installed-Size: $size
Depends: libgtk-4-1, libadwaita-1-0 (>= 1.5), libsecret-1-0, gstreamer1.0-plugins-base, gstreamer1.0-plugins-good, libicu74 | libicu72 | libicu-dev, ca-certificates
Recommends: gstreamer1.0-plugins-bad, gstreamer1.0-libav, gnome-keyring
Section: video
Priority: optional
Homepage: https://alummahnow.net
Description: $description
CONTROL
mkdir -p "$out"
dpkg-deb --root-owner-group --build "$root" "$out/${package}_${debversion}_amd64.deb"
rm -rf "$root"
