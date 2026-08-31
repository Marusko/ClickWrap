# Update-check library

`ClickWrap.UpdateClient` — one project, one assembly, no package dependencies,
`net10.0-windows`.

It checks whether the server has a newer version, and can hand off to the app's installer to
apply it. It has no UI: how to tell the user about an update is each app's decision.

## The short version

```csharp
using ClickWrap;

var client = new UpdateClient("https://updates.example.com");

var update = await client.CheckForUpdateAsync("race-timer");   // version worked out for you
if (update is not null && UserAgreedToUpdate(update))
{
    InstalledApp.UpdateAndExit("race-timer");                  // launches update.exe, exits the app
}
```

Two calls, and neither needs the running version or a shutdown of your own.

## UpdateClient

| Member | Behaviour |
| --- | --- |
| `CheckForUpdateAsync(appId, ct)` | Uses `InstalledApp.GetCurrentVersion(appId)` as the running version. |
| `CheckForUpdateAsync(appId, currentVersion, ct)` | Compares against a version you supply. |
| `GetLatestAsync(appId, ct)` | Newest published version regardless of what is running. Used by the installer. |
| `ServerBaseUrl` | The base URL, trailing slash trimmed. |

Both check overloads return `UpdateInfo` (`LatestVersion`, `DownloadUrl`, `ReleaseNotes`) when
something newer exists, and `null` otherwise.

Pass your own `HttpClient` when you have a pooled one — only the single-argument constructor
creates and disposes one:

```csharp
var client = new UpdateClient(httpClientFactory.CreateClient(), "https://updates.example.com");
```

### What it returns and throws

| Situation | Result |
| --- | --- |
| Server has a newer version | `UpdateInfo` |
| Running version equals, or is newer than, latest | `null` |
| App id has no published versions (`404`) | `null` |
| `currentVersion` is not a version number | `ArgumentException` |
| Server unreachable, or any non-404 error status | `HttpRequestException` |

A `404` returns `null` rather than throwing, so a typo'd app id cannot crash a startup check.
Network failures **do** throw, so wrap the call when it runs at startup:

```csharp
try
{
    var update = await client.CheckForUpdateAsync(AppId);
    if (update is not null) ShowUpdateBanner(update);
}
catch (HttpRequestException)
{
    // Offline or the server is down. Not worth bothering the user about.
}
```

## InstalledApp

Reads what the installer recorded under `HKCU\Software\ClickWrap\{appId}`, and hands off to the
app's updater.

| Member | Behaviour |
| --- | --- |
| `GetCurrentVersion(appId)` | The version to compare against the server. See below. |
| `GetInstalledVersion(appId)` | What the installer last installed, or `null` if this app was not installed by it. |
| `GetInstallFolder(appId)` | Folder the publish output was extracted into, or `null`. |
| `GetUpdaterPath(appId)` | Path to `update.exe`, or `null` if not recorded or no longer on disk. |
| `StartUpdater(appId)` | Launches the updater and leaves the app running. `false` if there is none. |
| `UpdateAndExit(appId, exitCode)` | Launches the updater and exits the app. `false` if there is none. |
| `KeyPathFor(appId)`, `*ValueName` consts | The registry contract, shared with the installer. |

### How the current version is worked out

`GetCurrentVersion` prefers the version the installer recorded, and falls back to the entry
assembly's version.

The recorded value is preferred because it is the version actually pulled from the server. A
ClickOnce `ApplicationVersion` and an `AssemblyVersion` drift apart the moment one is bumped
without the other — and Visual Studio auto-increments the ClickOnce revision on publish, so this
is easy to do by accident. An app comparing a stale `AssemblyVersion` against the server would be
permanently convinced an update is available.

The assembly fallback covers a build that was never installed through ClickWrap, such as one
running from your dev machine.

### Applying the update

`UpdateAndExit` is the whole self-update in one call: it starts `update.exe` and ends the process.

**It returns `false` instead of exiting when there is no updater** — an app that was never
installed by ClickWrap, or whose install folder has been removed. The app keeps running, so a
missing updater can never strand a user in a closed app:

```csharp
if (!InstalledApp.UpdateAndExit(AppId))
{
    ShowMessage("Could not find the updater. Reinstall from the download link.");
}
```

On success it does not return.

The app **must** exit: `setup.exe` launches the app once it has updated it, so an app that stays
open ends up running beside a second, newer copy of itself
([clickonce.md](clickonce.md#an-update-applies-while-the-app-is-running)). That is the mistake
`UpdateAndExit` exists to prevent.

It exits the process directly, so unsaved state is not flushed and WPF's shutdown does not run.
When that matters, save first and use `StartUpdater`:

```csharp
if (InstalledApp.StartUpdater(AppId))
{
    SaveEverything();
    Application.Current.Shutdown();   // must still close
}
```

## Why this library is Windows-only

It targets `net10.0-windows` because reading the registration needs the registry. That is not a
real restriction: the whole system is built on ClickOnce, which is Windows-only, so a portable
build had nothing to be portable for.

The library still contains no install machinery — no download, no extraction, no folder writes.
It reads a registry key and starts a process. Everything that actually installs anything lives in
the installer exe. See [architecture.md](architecture.md).
