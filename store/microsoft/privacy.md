# Privacy and data collection answers (desktop and TV)

Drawn from what the code really sends off the device (audited 2026-11-14). Every answer names the source
file it comes from. `StoreListingTests` fails if a cited file disappears, so a refactor flags this page for
review. Partner Center has no form to import; fill the questions below by hand (a few minutes per app).

## Shared facts

| Fact | Source |
|---|---|
| Every server address is `https://` (videos API, Auth, admin API, QA and production) | `src/AlUmmahNow.App/Services/AppEnvironment.cs` |
| No analytics, crash-reporting, advertising or telemetry SDK: the only packages are the UI toolkits, MVVM toolkit, JSON, DPAPI, D-Bus and our own core | `Directory.Packages.props` |
| Logs stay on the device (a file under the app's local data folder or standard error) and tokens, codes, emails and search text are scrubbed before writing | `src/AlUmmahNow.CoreBridge/Logging/LogScrubber.cs`, `src/AlUmmahNow.CoreBridge/Logging/Sinks.cs` |
| Sign-in happens in the system browser on the Auth site (PKCE, loopback redirect); the app never sees the password | `src/AlUmmahNow.CoreBridge/Temporary/PkceAuthSession.cs` |
| Tokens are stored encrypted for the current Windows user (DPAPI) or in the Linux keyring | `src/AlUmmahNow.CoreBridge.Native/CoreRuntime.cs`, `src/AlUmmahNow.Platform.Linux/LibSecretStore.cs` |
| Language, theme, watch folder and Studio's recent searches are saved in a local settings file only | `src/AlUmmahNow.App/Services/SettingsStore.cs` |
| No location, contacts, camera, microphone or other device capabilities: the packages ask only for `internetClient` (and `runFullTrust`, the Windows App SDK desktop model) | `src/AlUmmahNow.Windows/Package.appxmanifest`, `src/AlUmmahNow.Studio.Windows/Package.appxmanifest` |
| No data is sold or shared with third parties. Bunny and Cloudflare store and deliver media for us as service providers | — |

## What each app sends

| App | Data | When | Source |
|---|---|---|---|
| Viewer (Windows, Linux) | Public catalogue requests (lists, video details, subtitles) | always; no account needed | `src/AlUmmahNow.CoreBridge/Temporary/HttpCatalogClient.cs` |
| Viewer (Windows, Linux) | Account access token | only when the person signs in (optional) | `src/AlUmmahNow.CoreBridge/Temporary/PkceAuthSession.cs` |
| Studio (Windows, Linux) | Account access token | signed-in calls to the videos API | `src/AlUmmahNow.CoreBridge.Native/Studio/CoreStudioAccounts.cs`, `src/AlUmmahNow.CoreBridge.Native/Studio/CoreApiTransport.cs` |
| Studio | Video files | only the files the person picks, or puts in the watch folder they chose | `src/AlUmmahNow.Studio.Windows/UploadPage.cs`, `src/AlUmmahNow.CoreBridge.Native/Studio/CoreUploadService.cs` |
| Studio | Titles, descriptions, tags, category, language, playlists, schedules, delete and restore actions | when the person edits their videos | `src/AlUmmahNow.CoreBridge/Studio/StudioApiClient.cs` |
| Studio | Passkey (WebAuthn) responses to the Auth server | only when adding or using a passkey | `src/AlUmmahNow.CoreBridge/Passkeys/NativePasskeyClient.cs` |
| Studio | Reads the channel's analytics (nothing is sent beyond the request) | Analytics page | `src/AlUmmahNow.Studio/Analytics/AnalyticsViewModel.cs` |
| Viewer and Studio (Windows, Linux), not the Microsoft Store copy | Which app, system, processor, version and release channel is running, to check for a new version. No account, no device or install id. | about 30 s after launch, every few hours, and on "Check now" | `src/AlUmmahNow.CoreBridge.Native/CoreUpdateChecker.cs` |
| Viewer and Studio (Windows, our per-user setup only) | Downloads an update from this repo's GitHub releases. When one is refused: which app, its version, the release and the reason. No account, no device or install id. | "Install updates automatically", or Update is pressed | `src/AlUmmahNow.CoreBridge.Native/SelfUpdate/SelfUpdater.cs`, `src/AlUmmahNow.CoreBridge.Native/CoreUpdateChecker.cs` |
| TV (Tizen) | Public catalogue requests only; no sign-in | always | `src/AlUmmahNow.Tizen/Program.cs` |

Admin for Windows is staff-only and never goes to the Store (`docs/ADMIN_WINDOWS.md`).

## Partner Center answers (Microsoft Store, viewer and Studio)

| Where | Question | Viewer | Studio |
|---|---|---|---|
| Properties | Does this product access, collect or transmit personal information? | **Yes** (optional sign-in sends the account token) | **Yes** (account, uploads and video details) |
| Properties | Privacy policy URL | the URL in `MSSTORE_PRIVACY_POLICY_URL` (required because of the answer above) | same |
| Properties | Category | **Photo & video** | **Photo & video** |
| Properties | Product declarations: "This product has been tested to meet accessibility guidelines" | leave unticked until a Narrator pass is signed off | same |
| Age ratings (IARC) | Every question | see `store/microsoft/age-rating.md` | same |
| Submission options | Restricted capability `runFullTrust` justification | "Desktop app built with the Windows App SDK (WinUI 3); runFullTrust is the standard capability for packaged Win32 desktop apps." | same |

## Samsung Seller Office (TV)

The TV app collects nothing and has no account: privacy policy URL is still required by Samsung; use the same
`MSSTORE_PRIVACY_POLICY_URL` page.

## Keep it true

Adding analytics, crash reporting, push, comments or a new upload type changes these answers: update this page in
the same PR and tell the owner to update Partner Center.
