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
(`videos.api.qa.alummahnow.net`); tagged release builds default to prod (`videos.api.alummahnow.net`).

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

Tag-triggered from `main` only, never by hand:

- `v*` tag: MSIX for Microsoft Store and winget, Flatpak for Flathub, TPK for Samsung Seller Office.
- CI currently produces **unsigned** MSIX and Flatpak artifacts. Store submission stays disabled until
  the owner provides the inputs in `docs/OWNER_INPUTS.md`.
