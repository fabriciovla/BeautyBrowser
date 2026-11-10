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

## Commits and pull requests

- Branch from `main` (`feat/…`, `fix/…`, `docs/…`).
- Use [Conventional Commits](https://www.conventionalcommits.org): `feat(sidebar): …`, `fix(kit): …`, `docs: …`.
- Keep pull requests focused on one change and describe how you tested it.
