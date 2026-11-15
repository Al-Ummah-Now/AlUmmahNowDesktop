# What the owner must provide

Nothing here is committed to this public repo. Each secret goes into GitHub Actions secrets, each variable into
Actions variables. Step-by-step owner checklist: [`docs/RELEASE.md`](RELEASE.md#owner-checklist).

| Target | Needed | Used for |
|---|---|---|
| Microsoft Store | Partner Center account, reserved names "Al Ummah Now" and "Al Ummah Now Studio"; variables `WINDOWS_PUBLISHER`, `WINDOWS_VIEWER_PACKAGE_NAME`, `WINDOWS_STUDIO_PACKAGE_NAME`, `MSSTORE_VIEWER_PRODUCT_ID`, `MSSTORE_STUDIO_PRODUCT_ID`, `MSSTORE_PRIVACY_POLICY_URL` (optional `WINDOWS_PUBLISHER_DISPLAY_NAME`, `MSSTORE_COMMIT`); secrets `MSSTORE_TENANT_ID`, `MSSTORE_SELLER_ID`, `MSSTORE_CLIENT_ID`, `MSSTORE_CLIENT_SECRET`; the first submission made in Partner Center | Store-identity MSIX and CI draft submissions with the listing in `store/microsoft/`; no certificate needed for Store builds |
| Direct downloads (MSIX) | OV code-signing certificate as a PFX: secrets `WINDOWS_CERT_PFX_BASE64`, `WINDOWS_CERT_PASSWORD` | Signed MSIX in the GitHub release |
| Direct downloads (MSI, zip) and winget | Code-signing usable from CI for the MSI and exe: an Azure Trusted Signing account (identity validation, a certificate profile, and a service principal's tenant id, client id and secret as Actions secrets) **or** an OV/EV certificate in a cloud HSM (DigiCert KeyLocker, SSL.com eSigner) | Signed MSI and exe, so SmartScreen and Smart App Control stop warning (not wired yet: needs the account first) |
| Flathub | Agreed app ID (proposed `net.alummahnow.AlUmmahNow`, `net.alummahnow.Studio`) and domain verification | Flathub submission (needs a from-source manifest first, #15) |
| Samsung | Samsung Seller Office account, Samsung author and distributor certificates as secrets `TIZEN_AUTHOR_P12_BASE64`, `TIZEN_AUTHOR_PASSWORD`, `TIZEN_DISTRIBUTOR_P12_BASE64`, `TIZEN_DISTRIBUTOR_PASSWORD`, DUID of a test TV | Signed TPK and on-device testing |
| Backend (Epic 9) | Registered native OAuth client IDs and device-flow client | Sign-in |
| Design (Epic 10) | Published Fanous token packages; font decision D6 | Replacing placeholder tokens |
| Core | AlUmmahNowCore NuGet release | Replacing `CoreBridge/Temporary` |
