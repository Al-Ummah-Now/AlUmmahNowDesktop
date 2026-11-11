# Al Ummah Now desktop and Tizen apps: plan

Spec: `docs/native-apps/EPICS.md` in the AlUmmahNow monorepo (§1–4, Epics 15, 16, 19, 30).

## Solution layout

| Project | Target | Purpose |
|---|---|---|
| `src/AlUmmahNow.CoreBridge` | netstandard2.0, net9.0 | C# interfaces in front of every `aun-core` call, plus clearly marked temporary implementations under `Temporary/` |
| `src/AlUmmahNow.App` | netstandard2.0, net9.0 | Shared application layer: view-models, services, navigation, i18n, HLS and VTT parsing (CommunityToolkit.Mvvm) |
| `src/AlUmmahNow.Windows` | net9.0-windows | Viewer: WinUI 3, Windows.Media.Playback, SMTC |
| `src/AlUmmahNow.Linux` | net9.0 | Viewer: GTK 4 and libadwaita through Gir.Core, GStreamer, MPRIS |
| `src/AlUmmahNow.Tizen` | tizen | Samsung TV viewer: NUI, 10-foot UI |
| `src/AlUmmahNow.Studio` | net9.0 | Shared Studio logic: upload queue, folder watch, encode queue, timeline model |
| `src/AlUmmahNow.Studio.Windows` | net9.0-windows | Studio surface: WinUI 3, Media Foundation |
| `src/AlUmmahNow.Studio.Linux` | net9.0 | Studio surface: GTK 4, GStreamer |
| `spikes/` | | Throwaway validation (D9) |

The shared layer targets netstandard2.0 because Samsung TVs run an older .NET runtime than the
desktop apps. Do not use net9-only APIs in `App` or `CoreBridge`.

## Decisions made here

- **Quality selection** is shared: the app parses the master playlist and opens a variant URL, or the
  master for Auto. Platform players need no bitrate APIs. Variant URLs inherit Bunny's path-form token.
- **Subtitles** are shared: the app fetches and parses WebVTT and each UI draws one caption label. This
  gives identical Arabic rendering on every platform and avoids sideloading differences.
- **Dependencies not yet available** sit behind interfaces: the core (`ICatalogClient`, upload engine),
  sign-in (needs a registered native client from Epic 9), and Fanous tokens (placeholders from spec §3).

- **Tizen playback** uses `Tizen.Multimedia.Player`. "AVPlay" in the spec is the Web API name; the .NET
  equivalent is this player. `tizen50` keeps Samsung's 2019+ TVs in range (Epic 15.9). The viewer compiles
  and packages in CI but has not run on a TV or emulator.

## Environments

`AUN_ENV=qa|prod` overrides the build default. Debug and CI builds default to QA
(`videos.api.qa.alummahnow.net`); builds with `-p:AunProd=true`, which only the release workflow sets, default to prod (`videos.api.alummahnow.net`).

## Testing

| Layer | Tool | Where |
|---|---|---|
| Shared layer | xUnit, with fake catalog, player and session | every push, `ubuntu-latest` |
| GTK UI | xvfb launch, screenshots en + ar, dogtail/AT-SPI smoke | `ubuntu-latest` |
| WinUI UI | launch and screenshots en + ar; WinAppDriver/Appium smoke | `windows-latest` |
| Tizen | compile and package only (no emulator or TV in CI) | `ubuntu-latest` |
| Encoders | real software encode of a generated source; hardware paths need self-hosted GPUs | Linux and Windows runners |

Every UI story ships in Arabic and English with RTL verified by a screenshot.

## Release

`.github/workflows/release.yml` is the only release path:

- **Pre-release:** push `v0.x.y-preview.N` (any `-suffix`) on a commit already on `main`. The packages
  default to **QA** and the workflow publishes a GitHub **pre-release** at once.
- **Release:** push `v1.2.3` on `main`. The packages default to production and the workflow creates a
  **draft** release that a person publishes.
- Changing packaging in a pull request, or running the workflow by hand, is a dry run: same builds and
  install checks, no release.

| Platform | Packages |
|---|---|
| Windows x64 | per-machine MSI (WiX 5, `packaging/windows/app.wxs`), portable zip |
| Linux x86-64 | `.deb` (`scripts/build-deb.sh`, Ubuntu 24.04+ for libadwaita 1.5), Flatpak bundle |
| Tizen | unsigned TPK |

CI installs the Studio MSI and `.deb`, launches them in demo mode, checks they start, and removes them.
Every package is **unsigned** and the release notes say so (`scripts/release-notes.py`). Code signing
waits for the owner inputs in `docs/OWNER_INPUTS.md` (#15).

## Studio

- Sign-in: aun-core `AuthManager`, system browser, PKCE, loopback redirect on an OS-assigned port;
  several accounts; sign-out revokes the refresh token.
- Uploads: aun-core `UploadEngine` per account (resumable across restarts); finish steps (language,
  playlists, schedule) are saved and retried.
- Videos: Recently deleted is local to the device until the API has creator soft delete
  (AlUmmahNow#778).
- `AUN_DEMO=1` runs on canned accounts and data (CI screenshots); `AUN_SCENE` opens a dialog or menu.
- Logs: `%LOCALAPPDATA%\AlUmmahNowStudio\logs` (Windows), `$XDG_STATE_HOME/alummahnow-studio/logs`
  (Linux); tokens, codes, emails and search text are scrubbed.
