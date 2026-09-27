# ClickWrap

A small system wrapped around ClickOnce: a server that hosts each app's zipped ClickOnce publish
output, a library apps use to check for updates, and a wrapper installer that downloads the zip
and runs `setup.exe`.

ClickOnce stays the actual install and update mechanism. Nothing here replaces it — the
uninstaller (`update.exe --uninstall`) runs ClickOnce's own uninstall, then removes what that
leaves behind: the install folder, the registry record and, if the user ticks it, the app's data.
Settings > Apps runs that uninstaller too, which means rewriting an entry ClickOnce owns — read
[the warning below](#warning-the-installer-rewrites-the-apps-addremove-programs-entry).

```
src/ClickWrap.Server/         Blazor Server admin page + two API endpoints, files on disk
src/ClickWrap.UpdateClient/   one assembly: version check + hand-off to the installer, no UI
src/ClickWrap.Installer/      WPF exe, one self-contained build per app
```

From an app's point of view the whole thing is two calls:

```csharp
var update = await client.CheckForUpdateAsync("race-timer");   // version worked out for you
if (update is not null && userSaidYes)
    InstalledApp.UpdateAndExit("race-timer");                  // updates and closes the app
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

## Warning: the installer rewrites the app's Add/Remove Programs entry

> [!WARNING]
> **ClickWrap changes a Windows registry entry that ClickOnce owns, in an unsupported way.**
> If an app's `update.exe` goes missing, that app can no longer be uninstalled from
> Settings > Apps until its installer is run again. Read this section before shipping to machines
> you do not control.

After every install or update the installer points the app's *Uninstall* in Settings > Apps at
`update.exe --uninstall`, so removing the app there also deletes its install folder, its
registration and (if ticked) its data. Microsoft does not document or support editing that entry.
It works on Windows 11 with .NET 10 as tested, but nothing guarantees a future Windows or
ClickOnce update keeps it working.

What you are accepting:

- **If `update.exe` is gone, Settings > Apps cannot uninstall the app.** Deleting the install
  folder by hand, or a cleaner or antivirus removing `update.exe`, leaves an Uninstall button
  that fails. Recover by running the app's installer again (it restores `update.exe` and the
  entry), or run ClickOnce directly — the original command is kept on the end of the entry's
  `UninstallString`, after `--clickonce`:

  ```powershell
  Get-ChildItem HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall |
      Get-ItemProperty | Where-Object DisplayName -eq 'Race Timer' | Select-Object UninstallString
  ```

- **ClickOnce puts its own entry back** every time it installs a version — verified on update, and
  expected for *Restore the application to its previous state* in its dialog. Updates go through
  the installer, which re-hooks within a second. After a restore, though, Uninstall runs ClickOnce
  alone, as if ClickWrap were not there, until the next installer run hooks it again.
- **Installers built before this change** still recognise a hooked entry — the ClickOnce command
  is kept in it precisely so their orphan cleanup cannot mistake the app for uninstalled — but
  their `onExistingInstall: reinstall` cannot open the ClickOnce dialog from a hooked entry.
  Rebuild every app's installer rather than mixing versions on one machine.
- **The hook is per machine and per user**, re-applied by every run of the installer.

To opt an app out, set `uninstall.hookAddRemovePrograms: false` in its YAML; the next installer run
puts ClickOnce's own command back. See [installer.md](docs/installer.md#addremove-programs).

## The three things most likely to bite you

- **`installFolder` is permanent.** ClickOnce refuses to update an app from a folder other than
  the one it was installed from. Change it after an app ships and updates break for everyone who
  has it.
- **Do not start signing manifests** for an app that has already shipped. It changes the ClickOnce
  identity, and every existing user gets a duplicate side-by-side install instead of an update.
- **Set `CLICKWRAP_PUBLIC_BASE_URL`** on the server. Behind a Cloudflare Tunnel the inbound host
  is not the public one, so without it installers are handed download URLs they cannot reach.
- **Scope Cloudflare Access to the whole host, with a bypass for `/api/*`.** Protecting `/admin`
  alone does not work: Blazor routes to it client-side over the SignalR circuit, so Access never
  sees a request to challenge. See [server.md](docs/server.md#protect-the-whole-host-not-just-admin).

## Credits

Icons courtesy of [Axialis Software](https://www.axialis.com).

The icon is used unmodified, as its licence requires: it ships with the 16x16 and 24x24 frames it
was generated with, and must not be re-rendered at other sizes for distribution. That is why the
installer's icon looks slightly soft at Explorer's 32px and 48px views — it is deliberate, not an
oversight.
