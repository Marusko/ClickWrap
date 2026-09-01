# Installer

A WPF exe that wraps ClickOnce. One build per app, with that app's YAML embedded, so shipping an
app means shipping exactly one file.

The same exe installs and updates — running it again fetches the newest version and re-runs
`setup.exe`. Uninstall stays with ClickOnce in Add/Remove Programs; there is nothing to build
for it.

## What happens on run

1. Prune registrations for any ClickWrap app that has since been uninstalled.
2. Read the `install.yaml` embedded in the exe.
3. Run the pre-install steps.
4. Ask the server for the latest version and download the zip.
5. Read the deployment name (`*.application`) out of the zip and decide the target folder.
6. Clear the target folder and extract.
7. Copy itself in as `update.exe`.
8. Run `setup.exe` and wait for it.
9. Record where the app landed under `HKCU\Software\ClickWrap\{appId}`.

## install.yaml

```yaml
appId: race-timer
displayName: Race Timer
serverUrl: https://updates.example.com
installFolder: '%LOCALAPPDATA%\ClickWrap\race-timer'
onExistingInstall: adopt

preInstall:
  - type: createFolder
    path: '%LOCALAPPDATA%\RaceTimer\data'
  - type: downloadFile
    url: https://updates.example.com/extras/tracks.db
    path: '%LOCALAPPDATA%\RaceTimer\data\tracks.db'
    overwrite: false
```

| Key | Required | Notes |
| --- | --- | --- |
| `appId` | yes | Must match the id on the server. |
| `displayName` | no | Shown in the window. Falls back to `appId`. |
| `serverUrl` | yes | Absolute URL of the ClickWrap server. |
| `installFolder` | yes | Fixed folder the publish output is extracted into. Environment variables expand. |
| `onExistingInstall` | no | `adopt` (default) or `reinstall`. |
| `preInstall` | no | Steps run before `setup.exe`. |

Pre-install step types are `createFolder` (needs `path`) and `downloadFile` (needs `url` and
`path`, plus optional `overwrite`, default false). An unknown `type`, or a step missing a
required field, fails at startup with a message rather than silently doing nothing.

`%LOCALAPPDATA%\ClickWrap\{appId}` is the sensible default install folder: ClickOnce installs per
user anyway, so it needs no elevation.

### installFolder is permanent

ClickOnce refuses to update an app from a folder other than the one it was installed from — see
[clickonce.md](clickonce.md#the-install-folder-can-never-change). Once an app has shipped, changing
`installFolder` breaks updates for everyone who already has it. Treat it as immutable.

## onExistingInstall

When the app is already installed from a folder that is not `installFolder`:

| Value | Behaviour |
| --- | --- |
| `adopt` *(default)* | Update the app where it already lives, ignoring `installFolder` on that machine. No prompts, and the app keeps its ClickOnce data directory. |
| `reinstall` | Open the ClickOnce uninstall dialog and stop, so the app can be moved to `installFolder` on the next run. |

`adopt` is the default for two reasons: ClickOnce has **no silent uninstall**, so `reinstall`
cannot be automated and needs the user to pick "Remove the application" in a dialog; and
uninstalling discards the app's ClickOnce data directory.

Fresh installs always go to `installFolder`, so new machines land in the right place either way.

This matters for existing apps. Anything installed before ClickWrap is registered against
wherever it was installed from — typically a `Downloads` folder — and `adopt` keeps updating it
there rather than erroring.

## Self-update

The installer copies itself into the install folder as `update.exe` and records where it went:

```
HKCU\Software\ClickWrap\{appId}
    InstallFolder    C:\Users\me\AppData\Local\ClickWrap\race-timer
    Updater          C:\Users\me\AppData\Local\ClickWrap\race-timer\update.exe
    Version          3.4.0.0
    DeploymentName   RaceTimer.application
    Managed          1
```

The registry pointer exists because the app runs from the ClickOnce store, not the install
folder, and under `adopt` the install folder is not necessarily the one in `install.yaml`.

Apps do not read this key themselves — `ClickWrap.UpdateClient` wraps it, so the whole
self-update is one call:

```csharp
InstalledApp.UpdateAndExit("race-timer");   // starts update.exe, exits the app
```

It returns `false` rather than exiting when there is no updater, so a missing one cannot strand
the user in a closed app. See [client.md](client.md#applying-the-update) for the full API,
including `StartUpdater` for apps that need to save state before closing.

**The app must exit.** `setup.exe` always launches the app after updating, so one that stays open
ends up running beside a second, newer copy of itself
([clickonce.md](clickonce.md#an-update-applies-while-the-app-is-running)). Doing the exit inside
`UpdateAndExit` is what stops that being every app author's problem.

`Version` is written only after `setup.exe` succeeds, so it reflects a completed install. It is
also what `InstalledApp.GetCurrentVersion` compares against the server, which is more reliable
than an assembly version that can drift from the published `ApplicationVersion`.

`update.exe` sits in the folder the installer wipes on every run, and Windows locks a running
exe. The wipe deliberately skips the running executable; without that, an app-initiated update
would fail trying to delete its own updater mid-run.

## Orphan cleanup

ClickOnce uninstall removes only its own Add/Remove Programs entry and store files. The install
folder — roughly 62 MB of it being `update.exe` — and the registry key both survive, and there is
no hook into ClickOnce uninstall to prevent that.

So every installer run prunes **any** ClickWrap app, not just its own: for each registration
whose ClickOnce entry no longer exists, the folder and key are removed. Orphans are collected
opportunistically the next time you install or update anything.

Two safety rules, because this deletes directories:

- Only folders with `Managed=1` are deleted — a folder that was *adopted* (someone's `Downloads`
  folder) is never touched, only its registry key is dropped.
- Even then, the folder must still contain `update.exe` and a `*.application`, so a hand-edited
  or corrupt registry entry cannot take out an unrelated directory.

An app's own registration is never pruned by its own installer, because that run is about to
reinstall it anyway.

To clear one by hand:

```powershell
Remove-Item "$env:LOCALAPPDATA\ClickWrap\race-timer" -Recurse -Force
Remove-Item 'HKCU:\Software\ClickWrap\race-timer' -Recurse -Force
```

## Why self-contained, and why 62 MB

The installer runs *before* `setup.exe` has installed any runtime, so it cannot be
framework-dependent — on a clean machine it would not start. Self-contained single-file WPF with
compression is ~62 MB, which is the WPF + runtime floor rather than anything this project adds
(any WPF app published the same way lands at the same size).

Set in the csproj: `SelfContained`, `PublishSingleFile`,
`IncludeNativeLibrariesForSelfExtract`, `EnableCompressionInSingleFile`, plus `DebugType=none`
and `AllowedReferenceRelatedFileExtensions=none` so publish output really is one file.

## Building one exe per app

Add `src/ClickWrap.Installer/apps/{appId}.yaml`, then:

> Per-app configs are **gitignored**, because `serverUrl` is the address of your own deployment.
> `sample.yaml` is the one committed file and serves as the template — copy it. A fresh clone
> therefore builds only the sample until you add your own.

```bash
pwsh ./build/publish-installers.ps1 -App race-timer
```

Omit `-App` to build every config. Output goes to `out/{appId}/RaceTimerSetup.exe`.

Directly, if you prefer:

```bash
dotnet publish src/ClickWrap.Installer -c Release -p:ClickWrapApp=race-timer -p:InstallerAssemblyName=RaceTimerSetup -o out/race-timer
```

Two MSBuild traps, both already handled in the csproj and worth not re-discovering:

- **Never pass `-p:AssemblyName`.** It is a global property, so it also renames the referenced
  `ClickWrap.UpdateClient` project and restore fails with *Ambiguous project name*. Use
  `InstallerAssemblyName`, which only the installer csproj reads.
- **The YAML selector is `ClickWrapApp`, not `AppConfig`.** `AppConfig` is a built-in MSBuild
  property for `app.config`; using it produces
  `MSB3030: Could not copy the file "sample" because it was not found`.

A missing `apps/{name}.yaml` fails the build with a clear message rather than producing an exe
with no config in it.

## Styling

`Themes/ModernTheme.xaml` and its two helpers are copied verbatim from the store launcher
(`AppStore.Launcher/Themes/`), which copied them from TimeMaker, so all three read as one family
of tools. Nothing links the repositories — **keep them in sync by hand.**

The window follows the store's `ThemedDialog` shape: chromeless rounded surface, drop shadow,
and an icon badge that reflects the outcome — accent while working, green on success, amber when
paused for the user, red on failure.

`appicon.ico` is set as `<ApplicationIcon>`, so it is embedded in the exe itself and travels with
the single distributed file — and with the `update.exe` copied beside the installed app. It is
also included as a WPF `Resource` and set as the window `Icon`, which is what the taskbar shows.

The icon contains only 24x24 and 16x16 frames, so Windows upscales it for the 32px and 48px
views used in Explorer and on the desktop, and it looks slightly soft there. **Leave it that
way.** The icon is licensed from Axialis Software, whose terms forbid distributing a modified
version — and re-rendering it at larger sizes to bake into the exe would be exactly that. See
the credits in the [README](../README.md).
