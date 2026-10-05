#!/bin/sh
# Builds this fork, publishes it as a release on WouterStulp/Jellyscribe and lists it in
# fork/manifest.json, the plugin repository Jellyfin installs Jellyscribe from.
# Version scheme: the upstream version it is based on plus a fourth number (2.10.0 -> 2.10.0.1).
set -eu
cd "$(dirname "$0")/.."

version=$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' Directory.Build.props)
tag="v$version"
zip="jellyscribe-$tag.zip"
changelog=${1:?usage: fork/release.sh "<one-paragraph changelog>"}

rm -rf out "$zip"
build='dotnet test -c Release LetterboxdSync.Tests/LetterboxdSync.Tests.csproj --filter "FullyQualifiedName!~Integration" && dotnet publish -c Release LetterboxdSync/LetterboxdSync.csproj -o out'
if command -v dotnet >/dev/null; then
    sh -c "$build"
else
    docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:9.0 sh -c "$build"
fi
zip -j -q "$zip" out/Jellyscribe.dll out/HtmlAgilityPack.dll

gh release create "$tag" "$zip" -R WouterStulp/Jellyscribe --target "$(git rev-parse HEAD)" \
    --title "Jellyscribe $version (fork)" --notes "$changelog"

python3 - "$version" "$tag" "$zip" "$changelog" <<'PY'
import datetime, hashlib, json, sys
version, tag, zip_name, changelog = sys.argv[1:]
path = 'fork/manifest.json'
manifest = json.load(open(path))
manifest[0]['versions'].insert(0, {
    'version': version,
    'changelog': changelog,
    'targetAbi': open('targetAbi.txt').read().strip(),
    'sourceUrl': f'https://github.com/WouterStulp/Jellyscribe/releases/download/{tag}/{zip_name}',
    'checksum': hashlib.md5(open(zip_name, 'rb').read()).hexdigest(),
    'timestamp': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
})
json.dump(manifest, open(path, 'w'), indent=2)
open(path, 'a').write('\n')
PY

git add fork/manifest.json
git commit -q -m "fork: release $version"
git push -q origin HEAD:main
echo "Released $version"
