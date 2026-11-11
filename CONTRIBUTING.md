# Contributing

Thanks for helping make Beauty Browser better.

## Before you start

For anything larger than a small fix, open an issue first so we can agree on the approach. Beauty Browser aims to
stay simple: fewer options done well.

## Development

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. Build and run:

   ```powershell
   dotnet build BeautyBrowser.slnx
   dotnet run --project src/BeautyBrowser
   ```

Only one instance can run at a time, since Chromium cannot share a profile. Close any installed Beauty Browser before
running a development build.

## Guidelines

- Code, comments and commit messages are in English.
- Keep comments few and technical: explain why, not what.
- User-facing text goes through `L.T("English", "Español")` in C# or `{local:T En='…', Es='…'}` in XAML.
- The UI only talks to `BeautyKit`, never to CefSharp directly.
- Follow the style in `.editorconfig`.

## Branches

| Branch | Purpose |
| --- | --- |
| `main` | Released versions only. Every merge is tagged (`v0.1.1`, `v0.2.0`…). |
| `develop` | Integration branch for the next release. Pull requests target it. |
| `feat/…`, `fix/…`, `docs/…` | One change each, branched from `develop` and deleted after merging. |
| `release/x.y.z` | Final polish of a release, branched from `develop` and merged into `main` and `develop`. |
| `hotfix/x.y.z` | Urgent fixes to a release, branched from `main` and merged into `main` and `develop`. |

## Releases

1. Raise `<Version>` in `version.props` (the app and the installer both take it from there).
2. Merge into `main` and push.
3. Run `powershell -ExecutionPolicy Bypass -File tools\publish-release.ps1`. It builds the installer, tags `vX.Y.Z`
   and publishes the GitHub release with `BeautyBrowserSetup.exe` and its `.sha256`.

Installed copies find the new release within a few hours and update themselves when they close. Releases are built
locally rather than on GitHub Actions because they ship CEF with proprietary codecs (see the README).

## Commits and pull requests

- Branch from `develop` (`feat/…`, `fix/…`, `docs/…`) and open the pull request against it.
- Use [Conventional Commits](https://www.conventionalcommits.org): `feat(sidebar): …`, `fix(kit): …`, `docs: …`.
- Keep pull requests focused on one change and describe how you tested it.
