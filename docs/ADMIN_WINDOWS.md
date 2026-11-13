# Al Ummah Now Admin for Windows

The internal staff app (wiki `epics/AUN-ADMIN-NATIVE-native-admin-app.md`): the video review queue
in a native WinUI 3 window. Projects: `src/AlUmmahNow.Admin` (shared, no UI types, tested in
`tests/AlUmmahNow.Admin.Tests`) and `src/AlUmmahNow.Admin.Windows`. macOS is the SwiftUI app in
AlUmmahNowApple; there is no Avalonia/WinUI macOS admin.

## What it does

- **Sign-in**: system browser + PKCE on the Auth client "Admin (Windows)" (Auth migration
  0038), redirect `alummahnow-admin://auth/callback` (MSIX protocol, or registered per user in
  unpackaged builds). One instance: the callback is redirected to the running app.
  `GET /api/me` on admin-api decides who is staff; everyone else sees "This app is for Al Ummah
  Now staff" and Sign out.
- **Windows Hello** on open and after 5 minutes minimised, hidden or with the session locked.
- **Step-up**: when admin-api answers `step_up_required`, the app asks, reopens sign-in with
  `prompt=login&max_age=900`, then retries once.
- **Queue**: Action needed / History, sorted by SLA deadline then risk; live search with
  suggestions; claim on open (3 minutes, renewed every 90 s); review pane with player, frames
  (flagged first), reasons and scores, metadata and channel history; Approve / Reject (reason) /
  Request changes (reason) / Escalate. Decisions apply at once; a 10-second Undo reverses them
  (the server sends the creator a correction). A decision already taken shows "Already approved
  by Name". Offline: the last text list stays, decisions are off.
- **Keys**: J/K or arrows, Enter, A, R, C, E, U or Ctrl+Z, S (show), Space, / or Ctrl+F, F5, Esc, ? (list).
- **Media rule** ("never cache or download on phone or native app ever, only encrypted stream"):
  HLS/DASH over TLS from the short-lived signed URL with an HTTP stack that neither reads nor
  writes a cache; frames are fetched into memory and decoded from an in-memory stream; no file,
  temp file, save, share, drag-out or OS media controls; signed URLs are never stored or logged.
  `WDA_EXCLUDEFROMCAPTURE` on the window; content is hidden while minimised. NSFW and minor items
  are blurred until an audited per-item "Show", and blur again on leaving, minimising or 60 s idle.
  Tests: `MediaNeverOnDiskTests` (loopback server, marker bytes, disk scan) and the CI scenario
  that streams a loopback HLS video and scans the disk for its bytes.
- **Push**: WNS. Registers `PUT /api/v1/devices/:installId` with `app: "admin"`,
  `provider: "wns"`. The server (AlUmmahNow #1286) sends the urgent toast with Open and Claim; a
  raw `review_clear` removes it. Off until the owner inputs below exist.
- **Language**: English by default, Arabic with full RTL (`admin.*` keys in `I18n/{en,ar}.json`).
  Light or Dark only.

## Owner inputs (not in this public repo)

| Secret / setting | What | Used by |
| --- | --- | --- |
| `ADMIN_WINDOWS_CERT_PFX_BASE64`, `ADMIN_WINDOWS_CERT_PASSWORD` | Code-signing certificate (PFX) whose subject matches the publisher | `release-admin-windows.yml` |
| `ADMIN_WINDOWS_PACKAGE_NAME`, `ADMIN_WINDOWS_PUBLISHER` | Partner Center app identity (Package/Identity/Name and Publisher `CN=…`) reserved for "Al Ummah Now Admin" | MSIX manifest, WNS channel |
| `WNS_PACKAGE_SID`, `WNS_CLIENT_SECRET` (AlUmmahNow processor) | Package SID and client secret of that identity | server WNS sender (#1286) |
| `ADMIN_RELEASE_BUCKET` | A private R2 bucket (internal channel); staff install from a signed link | release upload |

Until they exist: CI builds and tests everything, push shows "Alerts are off in this build", and
the release workflow stops at its gate with a warning. Releases are tagged
`admin-windows-v1.2.3` on main; nothing goes to GitHub Releases or workflow artifacts.
