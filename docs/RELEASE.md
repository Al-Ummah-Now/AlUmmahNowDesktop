# Release and store infrastructure

**Rule:** releases are built and uploaded **only by CI** (`.github/workflows/release.yml`) from a `v1.2.3` tag
(full release, production defaults) or `v1.2.3-preview.N` (pre-release, QA defaults) on `main`. Never upload to a
store by hand; the one exception is the very first Microsoft Store submission, which Partner Center requires to be
made in its web UI (step 2.4 below).

## What a release tag does today

Nothing fails because an owner value is missing: that step is skipped with a `::notice::` naming what is missing, and
the run stays green. The MSIX identity version is the tag's `X.Y.Z` plus `.0` (`v1.4.2` → `1.4.2.0`, stamped by
`scripts/stamp-version.py`; the Store needs the fourth part to be 0).

| Job / step | Does | Needs (else skipped with a notice) |
|---|---|---|
| `windows` → MSI, zip | per-machine MSI (WiX) and portable zip, both unsigned, into the GitHub release | — |
| `windows` → MSIX | MSIX of the viewer and Studio, artifact `msix-AlUmmahNow-Windows` / `msix-AlUmmahNow-Studio-Windows` (unsigned; this is the file the Store takes) | always built; uses the Partner Center identity when `WINDOWS_PUBLISHER` + `WINDOWS_VIEWER_PACKAGE_NAME` / `WINDOWS_STUDIO_PACKAGE_NAME` are set, a placeholder identity otherwise |
| `windows` → signed MSIX | `AlUmmahNow-Windows-<v>-x64.msix` / `AlUmmahNow-Studio-Windows-<v>-x64.msix` signed with your certificate (publisher = the certificate's subject) into the GitHub release | `WINDOWS_CERT_PFX_BASE64`, `WINDOWS_CERT_PASSWORD` |
| `msstore` | per app: uploads the MSIX to a **draft** Partner Center submission and writes the listing (`store/microsoft/<app>/<lang>/`) into it; submits for certification only when `MSSTORE_COMMIT=true` | full release tag + `MSSTORE_TENANT_ID`, `MSSTORE_SELLER_ID`, `MSSTORE_CLIENT_ID`, `MSSTORE_CLIENT_SECRET` + `MSSTORE_VIEWER_PRODUCT_ID` / `MSSTORE_STUDIO_PRODUCT_ID` + the identity values above |
| `linux` | `.deb` and Flatpak bundle into the GitHub release | — |
| `tizen` | TPK signed with the Tizen SDK's test certificate (`-unsigned.tpk`, for developer TVs) into the GitHub release | — |
| `tizen` → Samsung-signed TPK | `AlUmmahNow-Tizen-<v>.tpk` signed with your Samsung author + distributor certificates | `TIZEN_AUTHOR_P12_BASE64`, `TIZEN_AUTHOR_PASSWORD`, `TIZEN_DISTRIBUTOR_P12_BASE64`, `TIZEN_DISTRIBUTOR_PASSWORD` |
| `release` | draft GitHub release (pre-release for `-preview` tags) with everything above and `SHA256SUMS.txt` | — |

Pull requests that touch packaging and manual runs are dry runs: every package is built and the listing files are
checked, nothing is released or uploaded.

## Store listing files

Microsoft Store listing per app and language, checked in, read by the `msstore` job through
`scripts/store-listing.py`:

```
store/microsoft/<viewer|studio>/<en-us|ar-sa>/
  title.txt               ≤ 256 (must equal the name reserved in Partner Center)
  short-description.txt   ≤ 1,000
  description.txt         ≤ 10,000
  features.txt            one per line, ≤ 20 lines, ≤ 200 each
  keywords.txt            search terms, one per line, ≤ 7, ≤ 30 each, ≤ 21 words in all
  release-notes.txt       ≤ 1,500 ("What's new in this version")
```

`tests/AlUmmahNow.App.Tests/StoreListingTests.cs` (runs in CI) enforces those limits, en-us / ar-sa parity, the
copy-tone words in `CLAUDE.md`, and that the package manifests declare both languages. The privacy policy URL comes
from the repository variable `MSSTORE_PRIVACY_POLICY_URL`.

**Screenshots:** Actions → **Store screenshots** → Run workflow → download the `store-screenshots` artifact: viewer
home in en / ar × dark / light, Studio upload, videos and analytics pages in en / ar, all 1920×1080. They are taken of
the real WinUI apps on the runner's desktop in demo mode (fixture catalog `src/AlUmmahNow.App/Demo/DemoCatalogClient.cs`,
canned Studio accounts `src/AlUmmahNow.Studio/Demo/DemoBackend.cs`), never with QA or production accounts.
Partner Center has no screenshot upload in the CLI flow we use: upload them once in the listing page (they stay
across submissions).

**Privacy and data-collection answers**, drawn from the code with a source file for each:
[`store/microsoft/privacy.md`](../store/microsoft/privacy.md).

## Package identities

| App | Partner Center name to reserve | MSIX placeholder identity (no owner values) | Flatpak / deb id |
|---|---|---|---|
| Viewer | Al Ummah Now | `AlUmmahNow.Viewer.Dev`, `CN=AlUmmahNowDev` | `net.alummahnow.AlUmmahNow` / `alummahnow` |
| Studio | Al Ummah Now Studio | `AlUmmahNow.Studio.Dev`, `CN=AlUmmahNowDev` | `net.alummahnow.Studio` / `alummahnow-studio` |
| TV (Tizen) | — (Samsung Seller Office) | package `net.alummahnow.tv` | — |
| Admin | never in the Store; see `docs/ADMIN_WINDOWS.md` | `AlUmmahNow.Admin.Dev` | — |

## Owner checklist

Everything is built; these are the only things to paste or click. Each unlocks its step on the next tag. Secrets go
in GitHub → repo **Settings → Secrets and variables → Actions → Secrets**; values marked *variable* go in the
**Variables** tab of the same page (they are not secret and show in logs).

### 1. Partner Center app identities (unlocks Store-identity MSIX)

1. [Partner Center](https://partner.microsoft.com/dashboard) → Apps and games → **New product → MSIX or PWA app** →
   reserve **Al Ummah Now**; again for **Al Ummah Now Studio**.
2. For each app: Product management → **Product identity**. Copy:

| Partner Center field | Paste into | Kind |
|---|---|---|
| Package/Identity/Publisher (`CN=…`, same for both apps) | `WINDOWS_PUBLISHER` | variable |
| Package/Identity/PublisherDisplayName (if not "Al Ummah Now") | `WINDOWS_PUBLISHER_DISPLAY_NAME` | variable |
| Package/Identity/Name of Al Ummah Now | `WINDOWS_VIEWER_PACKAGE_NAME` | variable |
| Package/Identity/Name of Al Ummah Now Studio | `WINDOWS_STUDIO_PACKAGE_NAME` | variable |
| Store ID (12 characters, `9N…`) of Al Ummah Now | `MSSTORE_VIEWER_PRODUCT_ID` | variable |
| Store ID of Al Ummah Now Studio | `MSSTORE_STUDIO_PRODUCT_ID` | variable |

### 2. Microsoft Store API access (unlocks the draft submission upload)

1. Partner Center → ⚙ Account settings → **User management → Microsoft Entra applications** → *Add Microsoft Entra
   application* → create a new one → role **Manager** (or Developer). Then on that application → **Add new key**.
2. Paste:

| Value | Secret |
|---|---|
| Tenant ID (Account settings → Organization profile → Tenant, or the application page) | `MSSTORE_TENANT_ID` |
| Seller ID (Account settings → Legal info → Developer → Seller ID) | `MSSTORE_SELLER_ID` |
| Client ID of the Entra application | `MSSTORE_CLIENT_ID` |
| The key's value (shown once; renew before it expires, max 2 years) | `MSSTORE_CLIENT_SECRET` |

3. Variable `MSSTORE_PRIVACY_POLICY_URL` = the public privacy policy page (the www CMS page). Required, because both
   apps transmit personal information (see `store/microsoft/privacy.md`).
4. **First submission by hand** (Partner Center requires it before the API works): push a full release tag, download
   the `msix-AlUmmahNow-Windows` and `msix-AlUmmahNow-Studio-Windows` artifacts from the run, then in each product →
   **Start your submission**: Pricing (Free, markets), Properties (category **Photo & video**; privacy answers from
   `store/microsoft/privacy.md`), Age ratings (IARC questionnaire, answers in `store/microsoft/age-rating.md`, re-checked at every release), Packages (upload the
   MSIX), Store listings en-us and ar-sa (paste from `store/microsoft/<app>/<lang>/`, screenshots from the
   **Store screenshots** workflow),
   Submission options (justify `runFullTrust` with the sentence in `privacy.md`). Submit.
5. From the next full release tag, CI uploads each package and listing into a draft. Review it in Partner Center and
   **Submit to the Store**, or set variable `MSSTORE_COMMIT` = `true` to have CI submit it.

### 3. Code-signing certificate (unlocks signed MSIX downloads)

Not needed for the Store (the Store signs the package). Needed so a downloaded MSIX installs without Developer Mode:

- An OV or EV code-signing certificate exported as a password-protected PFX (EV/HSM-only certificates cannot be
  exported: then use Azure Trusted Signing, which needs a different signing step; ask for it when you have one).
- `base64 -i codesign.pfx | pbcopy` → secret `WINDOWS_CERT_PFX_BASE64`; its password → `WINDOWS_CERT_PASSWORD`.
- CI writes the certificate's subject into the sideload MSIX as its publisher, so no other value is needed. The same
  PFX can also serve `ADMIN_WINDOWS_CERT_PFX_BASE64` if the Admin identity uses the same subject.

### 4. Samsung TV (unlocks the Samsung-signed TPK)

1. Samsung Seller Office account (seller.samsungapps.com) with TV app distribution.
2. Tizen Studio → Certificate Manager → **Samsung** certificate profile (TV): author certificate (`author.p12`) and
   distributor certificate (`distributor.p12`, privilege *Partner* or *Public*); add a test TV's DUID for on-device
   testing.
3. Secrets: `base64 -i author.p12` → `TIZEN_AUTHOR_P12_BASE64`, its password → `TIZEN_AUTHOR_PASSWORD`;
   `base64 -i distributor.p12` → `TIZEN_DISTRIBUTOR_P12_BASE64`, its password → `TIZEN_DISTRIBUTOR_PASSWORD`.
4. Seller Office has no upload API: upload `AlUmmahNow-Tizen-<v>.tpk` from the GitHub release in Seller Office →
   Applications → **Create app** (TV), with the privacy policy URL from step 2.3.

### 5. Not automated (no plan in `docs/PLAN.md` yet)

- **Flathub**: needs a manifest that builds from source with generated NuGet sources (#15), an AppStream metainfo
  file, and a pull request to `flathub/flathub` plus domain verification of `alummahnow.net` for the
  `net.alummahnow.*` ids. The Flatpak bundle in each release is for direct install only.
- **Snap**, **winget**: not planned. winget would need the signed MSI first.

## How the apps learn of a release

Wiki epic AUN-APP-UPDATES. The viewer and Studio on Windows and Linux ask `GET /api/v1/app-config` (through Core) about
30 s after launch, every `checkHours`, and on Settings → Updates → Check now. They tell of a new version in a bar, or in a
screen when theirs is below the minimum an admin set. The screen waits for a running upload, encode or watched live stream.

- They download and install nothing. The only link is compiled in (`UpdateInstalls.DownloadPage`: this repo's releases
  page) and opens in the browser; nothing in an answer can change it.
- They carry the repo's root public key and the root-signed key list (`src/AlUmmahNow.CoreBridge.Native/UpdateKeys`,
  signed byte for byte, see `.gitattributes`), so they tell only of releases the server has verified (`signed`).
- Only builds of `release.yml` check (it alone passes `AunProd`). The Microsoft Store copy does not: the Store updates it.
- All of it is behind the server's `appUpdates` switch. Off: no bar, no screen, no Updates section.

## Keep it true

When a feature changes what the apps send (analytics, crash reports, push, comments, new uploads), update
`store/microsoft/privacy.md` and the listing files in the same PR and tell the owner to update Partner Center.
