# Age rating answers (Microsoft Store IARC, Samsung Seller Office)

Owner decision E20-D1 (13 and over, everywhere), wiki
[E20 §6](https://github.com/Al-Ummah-Now/AlUmmahNowWiki/blob/main/epics/E20-system-parental-controls.md). Family Safety filters
Store apps by this rating. Typed by hand in Partner Center → Age ratings (one questionnaire per app) and re-checked at every
release; Store policy 11.11 requires them to stay current. `StoreListingTests` checks that every question is answered for both
apps and that the cited files exist.

| Question | Viewer | Studio | Source |
|---|---|---|---|
| IARC category | Entertainment: video streaming | Utility: content creation | `store/microsoft/viewer/en-us/description.txt`, `store/microsoft/studio/en-us/description.txt` |
| Users interact or exchange content | No | Yes | `src/AlUmmahNow.Windows/Package.appxmanifest`, `src/AlUmmahNow.Studio.Windows/UploadPage.cs` |
| Shares the user's location | No | No | `src/AlUmmahNow.Windows/Package.appxmanifest`, `src/AlUmmahNow.Studio.Windows/Package.appxmanifest` |
| Shares personal information with third parties | No | No | `store/microsoft/privacy.md` |
| Digital purchases | No | No | `Directory.Packages.props` |
| Unrestricted internet access | No | No | `src/AlUmmahNow.CoreBridge/Temporary/PkceAuthSession.cs` |
| Violence | Infrequent | No | `store/microsoft/age-rating.md` |
| Graphic violence | No | No | `store/microsoft/age-rating.md` |
| Sexual content | No | No | `store/microsoft/age-rating.md` |
| Language | No | No | `store/microsoft/age-rating.md` |
| Controlled substances | References | No | `store/microsoft/age-rating.md` |
| Gambling | No | No | `store/microsoft/age-rating.md` |
| Directed at children under 13 | No | No | `store/microsoft/age-rating.md` |

Infrequent violence and references: creators' talks and news can touch them. Videos above that are age-restricted and need a
signed-in 18+ account. Expected result: Teen / PEGI 12; keep what IARC returns. **The viewer's "Users interact" becomes Yes in
the PR that adds comments, chat or posts**, and policy 11.12 then needs the Terms, in-app reporting and respect for the user's
parental settings. Listings never say "for kids" or "for children" in either language.

Direct downloads (MSI, MSIX, zip) have no rating, so a parent limits them with Family Safety app limits; the parents' guide
says so. Linux has no store rating to answer. Admin is never in the Store.

## Samsung Seller Office (TV)

Age rating 12/13 (the local equivalent of Teen); no purchases, no user interaction, no location, no ads.
