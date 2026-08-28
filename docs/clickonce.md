# ClickOnce behaviour reference

Everything on this page was verified by publishing a real WPF app and running the full
install / update / migrate / uninstall cycle on Windows 11 with .NET 10. None of it is
inferred from documentation. The rest of the design follows from these facts, so if you are
about to change something load-bearing, start here.

## Re-running setup.exe from the same folder updates in place

This is the assumption the whole installer rests on.

| What was done | What happened |
| --- | --- |
| Install v1.0.0.0 from a fixed folder | Installs, one Add/Remove Programs entry |
| Wipe the folder, extract v1.0.1.0, re-run `setup.exe` | **Updates in place.** No prompts, ~2s, app relaunches on the new version |
| Check Add/Remove Programs | **Same** registry key, `DisplayVersion` bumped. No duplicate entry |
| Re-run `setup.exe` at the same version | No error — just launches the app |
| Uninstall afterwards | Entry gone, store files gone. The maintenance dialog also offers a rollback |

The folder was wiped completely before extracting, leaving only the new version's
`Application Files\`. ClickOnce did not need the previous version's files to be present, so
"delete folder, extract new zip, run `setup.exe`" is safe.

## The install folder can never change

Running `setup.exe` for an app already installed from a different path fails outright:

> **Cannot Start Application** — You cannot start application X from this location because it
> is already installed from a different location.

Nothing installs, nothing updates, the app does not launch. This is why `installFolder` is
fixed per app and why `onExistingInstall` exists — see [installer.md](installer.md).

## deploymentProvider is not a way around that

A `<deploymentProvider>` in the deployment manifest keys the subscription to a URL rather than
a folder, which would remove the fixed-folder constraint. It is not usable here:

- It is only emitted when `UpdateEnabled=true`.
- It arrives together with a `<subscription><beforeApplicationStartup/></subscription>`, so
  ClickOnce would contact that URL before every single app launch.

Since this system serves zips rather than a live ClickOnce endpoint, that check would fail on
every start. Publish with `UpdateEnabled=false`.

## An update applies while the app is running

Applying v2.0.1.0 while v2.0.0.0 was running produced no error and no file-in-use dialog —
ClickOnce writes the new version into a *new* store folder, so the running instance keeps its
files. The result was both versions running at once:

```
CoTest 2.0.0.0   <- the instance that was already open
CoTest 2.0.1.0   <- launched by setup.exe after updating
```

`setup.exe` always launches the app when it finishes. Anything that triggers an update from
inside the app must therefore close the app itself. See "Self-update" in
[installer.md](installer.md).

## ClickOnce uninstall only removes what ClickOnce owns

After uninstalling through Add/Remove Programs:

| | |
| --- | --- |
| Add/Remove Programs entry | removed |
| ClickOnce store files | removed |
| The install folder (`setup.exe`, `update.exe`, `Application Files\`) | **left behind** |
| `HKCU\Software\ClickWrap\{appId}` | **left behind** |

There is no hook into ClickOnce uninstall, so the installer cleans up opportunistically
instead — see "Orphan cleanup" in [installer.md](installer.md).

## Manifests are unsigned, and should stay that way

Published with `SignManifests=false`, the deployment identity carries
`PublicKeyToken=0000000000000000`. That token is stable across builds, so there is no
signing-certificate rotation to break app identity.

**Do not start signing manifests for an app that has already shipped.** Signing changes the
ClickOnce identity, which means every existing user gets a second, side-by-side install rather
than an update.

The cost of unsigned manifests is a one-time prompt on first install:

> Publisher cannot be verified. Are you sure you want to install this application?

Updates after that are silent.

## dotnet publish cannot produce ClickOnce output

```
error MSB4803: The task "GenerateBootstrapper" is not supported on the .NET Core version of
MSBuild. Please use the .NET Framework version of MSBuild.
```

Publish from Visual Studio, or drive the .NET Framework MSBuild directly:

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" YourApp.csproj -restore -t:Publish -p:PublishProfile=ClickOnceProfile
```

## There is no ApplicationDeployment API on modern .NET

`System.Deployment.Application.ApplicationDeployment` — the classic in-process self-update API —
does not exist on .NET Core / .NET 5+. A .NET 10 ClickOnce app cannot check or apply its own
updates through the framework. That absence is the reason this project exists.
