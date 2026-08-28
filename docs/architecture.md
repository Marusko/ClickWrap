# Architecture

ClickWrap keeps ClickOnce as the actual install and update mechanism and wraps it in three
small pieces you control. Nothing here replaces ClickOnce; it feeds it.

```
  your build                    ClickWrap server                 a user's machine
 ────────────                  ──────────────────               ──────────────────

  MSBuild -t:Publish            /data/{appId}/{version}/          {installFolder}/
    │                             app.zip                           setup.exe
    │  zip the publish            metadata.json                     update.exe
    │  folder contents                  ▲                           CoTest.application
    ▼                                   │ upload via /admin         Application Files/
  app.zip ──────────────────────────────┘                              │
                                        │                              │ setup.exe
                                        │ GET /latest                  ▼
                                        │ GET /download            ClickOnce store
                                        │                          (%LOCALAPPDATA%\Apps\2.0)
                                   installer exe ───────────────────────┘
                                        ▲
                                        │ launches update.exe, then exits
                                   your WPF app
                                        │
                                        └── UpdateClient.CheckForUpdateAsync()
```

## The three pieces

| Project | What it is | Target |
| --- | --- | --- |
| `ClickWrap.Server` | Blazor Server admin page + two API endpoints. Files on disk, no database. | `net10.0` |
| `ClickWrap.UpdateClient` | One assembly, no dependencies. HTTP call + version compare. | `net10.0` |
| `ClickWrap.Installer` | WPF exe, self-contained, one build per app with its YAML embedded. | `net10.0-windows` |

## The split, and why it is worth keeping

Apps **check**. The installer **applies**.

`ClickWrap.UpdateClient` contains no download, extract, registry or process-launch code. An app
that references it can learn a newer version exists and nothing more. All the install machinery
lives in one exe instead of being duplicated into every app you ship.

This mirrors the separate `store` repo (Private App Store), which enforces the same rule through
project structure (`UpdateCheck.Wpf` → `UpdateCheck.Core`, neither containing an install API).
Keeping ClickWrap on the same rule means the two systems behave the same way from an app
author's point of view.

The library is deliberately `net10.0` rather than `net10.0-windows`. Adding self-update to it
would require the registry and process APIs, making it Windows-only and pulling install
machinery into every consumer. When an app wants a one-click update, it launches the installer
instead — see "Self-update" in [installer.md](installer.md).

## Data flow, end to end

1. You publish a ClickOnce build and zip the **contents** of the publish folder.
2. You upload that zip at `/admin` against an app id and version.
3. The server stores it as `{appId}/{version}/app.zip` plus a `metadata.json`.
4. An app calls `CheckForUpdateAsync` at startup and, if something is newer, shows its own
   notice — ClickWrap has no opinion about how.
5. The installer (or `update.exe` beside the app) asks for the latest version, downloads the
   zip, extracts it into the install folder, and runs `setup.exe`.
6. ClickOnce does the real install or update, and owns the uninstall entry.

## Where state lives

| State | Location | Owner |
| --- | --- | --- |
| Published versions | `$CLICKWRAP_DATA/{appId}/{version}/` | server |
| Latest-version answer | derived from folder names at request time | server |
| Extracted publish output | `installFolder` | installer |
| The updater | `installFolder\update.exe` | installer |
| Where an app was installed | `HKCU\Software\ClickWrap\{appId}` | installer |
| The installed app itself | `%LOCALAPPDATA%\Apps\2.0` + Add/Remove Programs | ClickOnce |

There is no database anywhere. The server's answer to "what is the latest version" is a
directory listing, so a version folder copied in by hand works exactly like an uploaded one.
