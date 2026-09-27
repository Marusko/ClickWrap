# Installer

A WPF exe that wraps ClickOnce. One build per app, with that app's YAML embedded, so shipping an
app means shipping exactly one file.

The same exe installs and updates — running it again fetches the newest version and re-runs
`setup.exe` — and, with `--uninstall`, removes the app along with everything ClickOnce's own
uninstall leaves behind. See [Uninstall](#uninstall).

## What happens on run

1. Prune registrations for any ClickWrap app that has since been uninstalled.
2. Read the `install.yaml` embedded in the exe.
3. Run the pre-install steps.
4. Ask the server for the latest version and download the zip.
5. Read the deployment name (`*.application`) out of the zip and decide the target folder.
6. Clear the target folder and extract.
7. Copy itself in as `update.exe`.
8. Run `setup.exe` and wait for it.
9. Wait until Add/Remove Programs shows the new version. `setup.exe` exits before ClickOnce has
   installed anything — see [clickonce.md](clickonce.md#setupexe-returns-before-the-install-happens).
   If ClickOnce goes quiet without getting there (the security prompt was declined, or it showed
   an error), the install fails here and nothing is recorded.
10. Point the app's Add/Remove Programs *Uninstall* at `update.exe --uninstall` — see
    [Add/Remove Programs](#addremove-programs).
11. Record where the app landed under `HKCU\Software\ClickWrap\{appId}`.

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
| `uninstall` | no | The app's own data, for the uninstaller to offer to delete. See [Uninstall](#uninstall). |

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

## Uninstall

`update.exe --uninstall` (or `/uninstall`) runs the same exe as the uninstaller. It is not a
separate file: it already sits in the install folder, costs no extra 62 MB, and is always the
same build as the installer that put the app there. The original `RaceTimerSetup.exe` accepts the
switch too.

Three ways in, all the same flow:

- **Settings > Apps > Uninstall**, once the installer has hooked the entry — see
  [Add/Remove Programs](#addremove-programs).
- **From the app**, `InstalledApp.UninstallAndExit(appId)` — see
  [client.md](client.md#uninstalling). The app has to exit: files it holds open (logs, a
  database) cannot be deleted while it runs.
- **By hand**, `update.exe --uninstall` in the install folder.

1. Ask first. Unlike an install, this can delete the user's data, so the window waits for
   **Uninstall** and offers the data checkbox.
2. Open ClickOnce's own uninstall dialog, where the user picks *Remove the application from this
   computer*. There is no silent ClickOnce uninstall, so this click cannot be skipped.
3. Check the Add/Remove Programs entry has actually gone. The dialog closing proves nothing: the
   user may have cancelled, or restored the previous version instead. If the entry is still
   there, **nothing** is removed and the window says so.
4. Delete the install folder and `HKCU\Software\ClickWrap\{appId}` — the same rules as
   [orphan cleanup](#orphan-cleanup), just straight away.
5. If ticked, delete the app's data.
6. Remove whatever is still locked once the window closes (see below).

If ClickOnce has already removed the app on its own — the entry was not hooked — step 2 is
skipped and the rest still runs, so the uninstaller also doubles as "clean up after it".

### Add/Remove Programs

> **Unsupported by Microsoft.** Read the warning in the [README](../README.md#warning-the-installer-rewrites-the-apps-addremove-programs-entry)
> before relying on it.

ClickOnce's Add/Remove Programs entry runs its own dialog and nothing else, which is why its
uninstall leaves the install folder behind. So after every successful install or update, the
installer rewrites that entry's `UninstallString`:

```
before  rundll32.exe dfshim.dll,ShArpMaintain RaceTimer.application, Culture=…, PublicKeyToken=…
after   "C:\…\ClickWrap\race-timer\update.exe" --uninstall --clickonce rundll32.exe dfshim.dll,ShArpMaintain RaceTimer.application, Culture=…
```

ClickOnce's command stays on the end, after `--clickonce`, for two reasons:

- The uninstaller runs it from there for step 2 — it is the only record of the app's identity.
- Everything that recognises a ClickOnce entry looks for `ShArpMaintain` plus the deployment
  name, including installers built before the hook existed. Were the command dropped, their
  orphan cleanup would take a hooked app for uninstalled and delete its folder.

It is re-applied on every run, because ClickOnce rewrites the entry whenever it installs a
version — see [clickonce.md](clickonce.md#clickonce-rewrites-its-entry-on-every-update). *Restore
the application to its previous state* in its dialog presumably does the same, and nothing
re-hooks after that until the next installer run. It is only
applied when `update.exe` is actually on disk: a hook to a missing exe would leave Settings > Apps
unable to uninstall at all.

Set `uninstall.hookAddRemovePrograms: false` to opt an app out. The next installer run puts
ClickOnce's own command back.

### What it removes

| | |
| --- | --- |
| ClickOnce store + Add/Remove Programs entry | via ClickOnce's dialog |
| `HKCU\Software\ClickWrap\{appId}` | always (and `HKCU\Software\ClickWrap` once empty) |
| Install folder (`Managed=1`, **is** `installFolder`, still has `update.exe` + `*.application`) | always (and a parent `ClickWrap` folder once empty) |
| Any other recorded folder — adopted (`Managed=0`, e.g. `Downloads\Race Timer`), or not matching `installFolder` | only the `update.exe` this installer put there, and only while the folder still has `update.exe` + `*.application` |
| `uninstall.data`, `uninstall.registryKeys` | when the checkbox is ticked |

### The uninstall section

```yaml
uninstall:
  data:                       # folders or files; full paths, environment variables expand
    - '%LOCALAPPDATA%\TimeMaker'
    - '%APPDATA%\Trakster\logs'
  registryKeys:               # the app's own keys under HKCU\Software
    - 'HKCU\Software\TimeMaker'
  deleteData: false           # checkbox starts ticked?
  askAboutData: true          # show the checkbox at all?
  hookAddRemovePrograms: true # Settings > Apps runs this uninstaller
```

| Key | Default | Notes |
| --- | --- | --- |
| `data` | none | Folders are deleted with everything in them; files on their own. No wildcards. |
| `registryKeys` | none | `HKCU\Software\...` or `HKEY_CURRENT_USER\Software\...`, deleted with all subkeys. |
| `deleteData` | `false` | Starting state of *Also delete its settings, logs and other data*. |
| `askAboutData` | `true` | `false` hides the checkbox and applies `deleteData` without asking. The window still lists what it is deleting. |
| `hookAddRemovePrograms` | `true` | Point Settings > Apps > Uninstall at this uninstaller. Unsupported by Microsoft; see [Add/Remove Programs](#addremove-programs). |

Omit the section when the app keeps no data — the checkbox then does not appear, and
Settings > Apps is still hooked. `hookAddRemovePrograms` is the one key that affects installs and
updates: they are what apply or remove the hook.

Because the uninstaller deletes whole folders, entries are checked at startup and a bad one fails
the installer with a message, as a bad `preInstall` step does:

- `data` must be a full path and must not be a drive root, a Windows or user folder
  (`%LOCALAPPDATA%`, `Documents`, `%TEMP%`, …), or anything above one (`C:\Users`,
  `%APPDATA%\Microsoft`).
- `registryKeys` must be at least `HKCU\Software\{Vendor}`, and never `Microsoft`, `Classes`,
  `Policies`, `Wow6432Node` or `ClickWrap`.

Items that cannot be deleted — typically a log file the app still has open — do not stop the
rest. The window lists them afterwards so they can be removed by hand.

### Deleting its own folder

The uninstaller is usually `update.exe` inside the very folder it removes, and Windows locks a
running exe. So it deletes everything it can immediately, and when its window closes hands the
remainder to a hidden `cmd.exe` that retries once a second for up to a minute. Paths are passed
through environment variables rather than the command line, so spaces, `&` and non-ASCII
characters in a user's profile path need no quoting of their own.

### Security

The uninstaller runs as the logged-in user, never elevated, so it can only ever delete what that
user could delete by hand. Within that, it trusts two sources differently:

- **The embedded YAML is trusted.** `uninstall.data` and `uninstall.registryKeys` are whatever
  the exe was built with. The startup checks catch mistakes — a missing subfolder, a key one level
  too high — but they are **not a security boundary**: whoever builds the exe decides what it
  deletes, exactly as whoever writes an app decides what it does. A malicious exe would not need
  ClickWrap to delete files, and ClickWrap is open source, so the checks could simply be removed.
  The defence against a malicious installer is the user not running one — see below.
- **The registry is not trusted.** Anything running as the user can edit HKCU, so values read
  from it are only acted on within limits the exe itself sets:
  - The install folder is deleted only if it **is** the configured `installFolder`. A
    registration pointing anywhere else costs that folder its `update.exe` at most.
  - The ClickOnce command is run only if it starts with `rundll32.exe dfshim.dll,ShArpMaintain`,
    and only its identity part is used, so a tampered Add/Remove Programs entry cannot make the
    uninstaller load another DLL. The hook refuses to wrap such an entry, too.
  - `rundll32`, `cmd` and `ping` are started by full System32 path, never by bare name — a bare
    name is looked for first in the current or the exe's own folder.

**Sign the installer exe.** It is what users actually have to trust, and an Authenticode
signature is how they can tell yours from someone else's with the same name. It also keeps
SmartScreen and antivirus quieter — Kaspersky, for one, puts unsigned exes in a restricted group.
This is separate from the ClickOnce manifests, which must stay unsigned (see
[clickonce.md](clickonce.md#manifests-are-unsigned-and-should-stay-that-way)): signing the wrapper
exe does not change the app's ClickOnce identity. `update.exe` is a copy of the installer, so it
carries the same signature.

## Orphan cleanup

ClickOnce uninstall removes only its own Add/Remove Programs entry and store files. The install
folder — roughly 62 MB of it being `update.exe` — and the registry key both survive, and there is
no hook into ClickOnce uninstall to prevent that. The [uninstaller](#uninstall) avoids this, and
[hooking Add/Remove Programs](#addremove-programs) routes Settings > Apps through it, but an
unhooked entry (opted out, or restored by ClickOnce) still bypasses it.

So every installer run prunes **any** ClickWrap app, not just its own: for each registration
whose ClickOnce entry no longer exists, the folder and key are removed. Orphans are collected
opportunistically the next time you install or update anything.

Two safety rules, because this deletes directories:

- Only folders with `Managed=1` are deleted — a folder that was *adopted* (someone's `Downloads`
  folder) is never touched, only its registry key is dropped.
- Even then, the folder must still contain `update.exe` and a `*.application`, and must not be,
  or contain, a Windows or user folder, so a hand-edited or corrupt registry entry cannot take out
  an unrelated directory. (The uninstaller goes further and pins the folder to its own
  `installFolder`; pruning cleans up after *other* apps, whose configs it does not have.)

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
