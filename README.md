# jellyplug-cache-headers-dist
JellyPlug Cache Headers Jellyfin plugin distribution (JEL-659, JELA-76).

`src/` holds the plugin source (1.0.0.0 shipped DLL-only; source was reconstructed
by decompiling it and evolved for 1.1.0.0 — see JELA-76).

Build: `dotnet build -c Release src/Jellyfin.Plugin.JellyPlugCacheHeaders`
Package: zip the built DLL + `meta.json` (version/changelog bumped), MD5 of the zip
goes in `manifest.json` `checksum`, then add a `versions[0]` entry pointing at the
new zip. The live server has this repo's `manifest.json` registered as plugin
repository "JellyPlug Cache Headers".
