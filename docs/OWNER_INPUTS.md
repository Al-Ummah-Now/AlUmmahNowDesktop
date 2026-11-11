# What the owner must provide

Nothing here is committed to this public repo. Each secret goes into GitHub Actions secrets.

| Target | Needed | Used for |
|---|---|---|
| Microsoft Store | Partner Center account, reserved app name, Store association (publisher ID and package identity) | Store-signed MSIX; no certificate needed for Store builds |
| Direct downloads (MSI, zip) and winget | Code-signing certificate: an Azure Trusted Signing account (identity validation, a certificate profile, and a service principal's tenant id, client id and secret as Actions secrets) **or** an OV/EV certificate usable from CI (a cloud HSM such as DigiCert KeyLocker or SSL.com eSigner, with its credentials as secrets) | Signed MSI and exe, so SmartScreen and Smart App Control stop warning |
| Flathub | Agreed app ID (proposed `net.alummahnow.AlUmmahNow`, `net.alummahnow.Studio`) and domain verification | Flathub submission |
| Samsung | Samsung Seller Office account, Samsung author and distributor certificates, DUID of a test TV | Signed TPK and on-device testing |
| Backend (Epic 9) | Registered native OAuth client IDs and device-flow client | Sign-in |
| Design (Epic 10) | Published Fanous token packages; font decision D6 | Replacing placeholder tokens |
| Core | AlUmmahNowCore NuGet release | Replacing `CoreBridge/Temporary` |
