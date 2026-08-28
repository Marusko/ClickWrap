# ClickWrap

A small system wrapped around ClickOnce: a server that hosts each app's zipped ClickOnce publish
output, a library apps use to check for updates, and a wrapper installer that downloads the zip
and runs `setup.exe`.

ClickOnce stays the actual install and update mechanism. Nothing here replaces it — and uninstall
remains ClickOnce's job, in Add/Remove Programs.

```
src/ClickWrap.Server/         Blazor Server admin page + two API endpoints, files on disk
src/ClickWrap.UpdateClient/   one assembly: HTTP call + version compare, no UI
src/ClickWrap.Installer/      WPF exe, one self-contained build per app
```

## Docs

| | |
| --- | --- |
| [Developer guide](docs/guide.md) | Setup, and how to ship an app or a new version end to end. **Start here.** |
| [Architecture](docs/architecture.md) | How the three pieces fit, and where state lives. |
| [Server](docs/server.md) | Storage layout, API, `/admin`, configuration. |
| [Update-check library](docs/client.md) | API, return values, version comparison. |
| [Installer](docs/installer.md) | `install.yaml`, existing-install policy, self-update, per-app builds. |
| [ClickOnce behaviour](docs/clickonce.md) | The verified facts the whole design rests on. |

## Quick start

```bash
dotnet build ClickWrap.slnx
dotnet run --project src/ClickWrap.Server
```

Upload a zipped publish folder at <http://localhost:8080/admin>, add
`src/ClickWrap.Installer/apps/{appId}.yaml`, then:

```bash
pwsh ./build/publish-installers.ps1 -App race-timer
```

`out/race-timer/RaceTimerSetup.exe` is the single file you distribute. Running it again is how
updates are applied, so there is no separate updater to ship.

## The three things most likely to bite you

- **`installFolder` is permanent.** ClickOnce refuses to update an app from a folder other than
  the one it was installed from. Change it after an app ships and updates break for everyone who
  has it.
- **Do not start signing manifests** for an app that has already shipped. It changes the ClickOnce
  identity, and every existing user gets a duplicate side-by-side install instead of an update.
- **Set `CLICKWRAP_PUBLIC_BASE_URL`** on the server. Behind a Cloudflare Tunnel the inbound host
  is not the public one, so without it installers are handed download URLs they cannot reach.
