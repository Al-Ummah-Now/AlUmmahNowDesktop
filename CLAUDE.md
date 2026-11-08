# AlUmmahNowDesktop

Native C# apps: Windows (WinUI 3), Linux (GTK 4 / libadwaita via Gir.Core), Samsung Tizen TV (NUI).
Viewer and Studio share one application layer. See `docs/PLAN.md`.

## Rules

- PUBLIC repo: never commit certificates, PFX files, API keys or `.env` files.
- `AlUmmahNow.App` and `AlUmmahNow.CoreBridge` stay netstandard2.0-compatible (Tizen).
- Every core call goes through a `CoreBridge` interface. Temporary implementations live in `Temporary/`.
- Every UI story is bilingual (Arabic default, English equal) with RTL verified. Strings live in
  `src/AlUmmahNow.App/I18n/{en,ar}.json`; a test enforces matching keys.
- Tests for everything in the shared layer. No UI framework types in shared projects.
- Small PRs into `main` with green CI. `main` needs the owner's approval: agents never merge and never use `--admin`.
- Never publish to a store by hand.
- Commit as `meywd` with the GitHub noreply address; never put a personal email in a command.
- Each platform has its own workflow file, so parallel branches do not conflict in CI.
- Out-of-scope findings become GitHub issues labelled `queue`.

## Commands

```
dotnet test tests/AlUmmahNow.App.Tests
```

WinUI builds need Windows and the Tizen workload needs its CLI, so use CI for those.
