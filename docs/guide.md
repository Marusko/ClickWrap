# Developer guide

How to get set up, and how to do each of the things you will actually do.

## Prerequisites

| | Why |
| --- | --- |
| .NET 10 SDK | Everything builds against `net10.0` / `net10.0-windows`. |
| Visual Studio 2022/18 (or Build Tools) with the **ClickOnce Publishing** component | ClickOnce publish needs .NET Framework MSBuild; `dotnet publish` cannot do it. See [clickonce.md](clickonce.md#dotnet-publish-cannot-produce-clickonce-output). |
| PowerShell 7 (`pwsh`) | For `build/publish-installers.ps1`. Windows PowerShell also works. |
| Docker | Only for deploying the server. |

The solution is `ClickWrap.slnx` — the .NET 10 XML solution format, not a `.sln`.

```bash
dotnet build ClickWrap.slnx
```

## Run the server locally

```bash
dotnet run --project src/ClickWrap.Server
```

<http://localhost:8080> lists apps, `/admin` uploads a version. Data lands under
`src/ClickWrap.Server/bin/Debug/net10.0/data`; set `CLICKWRAP_DATA` to put it elsewhere.

`.claude/launch.json` starts the same server on **8090** instead, because `RaceView.Web.exe`
holds 8080 on this machine. Either port is fine — 8080 is only the default the container binds:

```bash
dotnet run --project src/ClickWrap.Server -- --urls http://0.0.0.0:8090
```

## Ship a new app, start to finish

### 1. Prepare the app's ClickOnce publish profile

In the app you want to distribute, `Properties/PublishProfiles/ClickOnceProfile.pubxml`:

```xml
<PropertyGroup>
  <PublishProtocol>ClickOnce</PublishProtocol>
  <PublishDir>bin\publish\</PublishDir>
  <PublishUrl>bin\publish\</PublishUrl>
  <Install>true</Install>
  <InstallFrom>Disk</InstallFrom>
  <UpdateEnabled>false</UpdateEnabled>
  <BootstrapperEnabled>true</BootstrapperEnabled>
  <SignManifests>false</SignManifests>
  <ProductName>Race Timer</ProductName>
  <ApplicationVersion>1.0.0.0</ApplicationVersion>
</PropertyGroup>
```

`UpdateEnabled=false` and `SignManifests=false` are not incidental — see
[clickonce.md](clickonce.md). `ProductName` becomes the Add/Remove Programs name.

### 2. Publish and zip

```bash
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" RaceTimer.csproj -restore -t:Publish -p:PublishProfile=ClickOnceProfile -p:ApplicationVersion=1.0.0.0
```

Zip the **contents** of `bin\publish` — `setup.exe` must be at the root of the archive, not
inside a folder. The server rejects the wrong shape at upload time, but it is easier to get
right the first time:

```powershell
Compress-Archive -Path bin\publish\* -DestinationPath race-timer-1.0.0.0.zip -Force
```

### 3. Upload

Open `/admin`, choose `<new app>`, enter the app id (`race-timer`), the version (`1.0.0.0`),
optional release notes, pick the zip, publish.

The **app id is permanent** — installers are built against it.

### 4. Create the installer config

`src/ClickWrap.Installer/apps/race-timer.yaml`:

```yaml
appId: race-timer
displayName: Race Timer
serverUrl: https://updates.example.com
installFolder: '%LOCALAPPDATA%\ClickWrap\race-timer'
onExistingInstall: adopt
preInstall: []
```

`installFolder` is permanent too. Changing it after the app has shipped breaks updates for
everyone who already has it.

### 5. Build the installer

```bash
pwsh ./build/publish-installers.ps1 -App race-timer
```

`out/race-timer/RaceTimerSetup.exe` — one file, ~62 MB. That is what you distribute.

### 6. Add the update check to the app

Reference `ClickWrap.UpdateClient`, then at startup:

```csharp
var client = new UpdateClient("https://updates.example.com");

try
{
    var update = await client.CheckForUpdateAsync("race-timer");
    if (update is not null) ShowUpdateBanner(update);
}
catch (HttpRequestException) { /* offline; ignore */ }
```

And behind the banner's button:

```csharp
if (!InstalledApp.UpdateAndExit("race-timer"))
{
    ShowMessage("Could not find the updater. Reinstall from the download link.");
}
```

You do not work out the running version and you do not shut the app down yourself — both are
handled, and both are easy to get wrong. See [client.md](client.md) for why, and for
`StartUpdater` if the app must save state before closing.

## Ship a new version of an existing app

1. Bump `ApplicationVersion` and publish.
2. Zip the publish folder contents.
3. Upload it at `/admin` against the same app id.

That is all. Users get it when they next run the installer, or when your app's update banner
sends them through `update.exe`. You do not rebuild or redistribute the installer for a normal
release — it always fetches the latest.

Rebuild the installer only when its own YAML changes, or when you change installer code.

## Migrating apps that predate ClickWrap

Apps installed before this system are registered against wherever they were installed from,
usually a `Downloads` folder. With the default `onExistingInstall: adopt`, the installer detects
that and keeps updating them in place — no error, no data loss, nothing for the user to do.

They stay in the old folder on that machine. Fresh installs on new machines go to
`installFolder`. If you genuinely need the folder moved, set `onExistingInstall: reinstall`, but
read the trade-off in [installer.md](installer.md#onexistinginstall) first — it cannot be
automated and it discards the app's ClickOnce data.

## Deploying the server

Bind `0.0.0.0` (the default), put it behind a Cloudflare Tunnel, and let Cloudflare Access
protect `/admin`. There is no app-level auth by design.

The Dockerfile lives in `src/ClickWrap.Server/`, with that folder as the build context. Stamp the
version into both the image label and the binary from one argument:

```bash
docker build --build-arg APP_VERSION=1.2.3 -t clickwrap:1.2.3 src/ClickWrap.Server
```

Set at minimum:

```
CLICKWRAP_DATA=/data
CLICKWRAP_PUBLIC_BASE_URL=https://updates.example.com
```

Point the compose healthcheck at `/health` — see [server.md](server.md#get-health).

Persist `/data` on a volume — it is the only state. `CLICKWRAP_PUBLIC_BASE_URL` is not optional
in production: without it, download URLs are built from the tunnel's hostname and installers
cannot reach them.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| *You cannot start application X from this location because it is already installed from a different location* | `installFolder` differs from where the app was installed. Should not happen with `adopt`; check the app's `UrlUpdateInfo` in Add/Remove Programs. |
| `MSB4803: GenerateBootstrapper is not supported` | You used `dotnet publish` for a ClickOnce build. Use MSBuild.exe. |
| `MSB3030: Could not copy the file "sample"` | You passed `-p:AppConfig=`. Use `-p:ClickWrapApp=`. |
| `Ambiguous project name` on restore | You passed `-p:AssemblyName=`. Use `-p:InstallerAssemblyName=`. |
| Installer says the server has no versions | App id mismatch between the YAML and `/admin`. |
| Download URL points at the wrong host | `CLICKWRAP_PUBLIC_BASE_URL` is not set. |
| A user has two copies of the app running | The app launched `update.exe` without shutting itself down. |
| Uninstalled app left a 62 MB folder behind | Expected; collected on the next installer run. See [installer.md](installer.md#orphan-cleanup). |
| "Publisher cannot be verified" on first install | Expected — manifests are unsigned. Do not fix this by signing them. |

## Testing changes to the installer

The installer touches the real ClickOnce store and Add/Remove Programs, so test it with a
throwaway app rather than a real one. A minimal WPF app published at two versions is enough to
cover install, update, adopt, self-update and uninstall. Verify against:

- Add/Remove Programs: `DisplayVersion` bumped, still exactly **one** entry
- `HKCU\Software\ClickWrap\{appId}`: `Version` matches
- the install folder: `update.exe` present, only the new `Application Files\` version
- after uninstall: entry gone, and the folder collected on the next installer run
