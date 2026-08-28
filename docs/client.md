# Update-check library

`ClickWrap.UpdateClient` — one project, one assembly, no package dependencies, `net10.0`.

It makes an HTTP call and compares two version numbers. It has no UI, does not download
anything, and cannot install anything. How to tell the user about an update is each app's
decision. See [architecture.md](architecture.md) for why that boundary is deliberate.

## Usage

```csharp
using ClickWrap;

var client = new UpdateClient("https://updates.example.com");
var update = await client.CheckForUpdateAsync("race-timer", "3.3.0.1");

if (update is not null)
{
    // update.LatestVersion, update.DownloadUrl, update.ReleaseNotes
}
```

Pass your own `HttpClient` when you have a pooled or pre-configured one:

```csharp
var client = new UpdateClient(httpClientFactory.CreateClient(), "https://updates.example.com");
```

Only the parameterless-`HttpClient` constructor creates and owns one; the overload above leaves
disposal to you.

## API

| Member | Behaviour |
| --- | --- |
| `CheckForUpdateAsync(appId, currentVersion, ct)` | `UpdateInfo` when the server has something newer, otherwise `null`. |
| `GetLatestAsync(appId, ct)` | The newest published version regardless of what is running, or `null` if the app has none. Used by the installer. |
| `ServerBaseUrl` | The base URL, trailing slash trimmed. |

`UpdateInfo` carries `LatestVersion`, `DownloadUrl` and `ReleaseNotes`.

## What it returns and throws

| Situation | Result |
| --- | --- |
| Server has a newer version | `UpdateInfo` |
| Running version equals latest | `null` |
| Running version is newer than the server's | `null` |
| App id has no published versions (`404`) | `null` |
| `currentVersion` is not a version number | `ArgumentException` |
| Server unreachable, or any non-404 error status | `HttpRequestException` |

A `404` returns `null` rather than throwing, so a typo'd app id cannot crash an app's startup
check. Network failures **do** throw, so wrap the call if you run it during startup:

```csharp
try
{
    var update = await client.CheckForUpdateAsync(AppId, CurrentVersion);
    if (update is not null) ShowUpdateBanner(update);
}
catch (HttpRequestException)
{
    // Offline or the server is down. Not worth bothering the user about.
}
```

## Version comparison

Versions are normalised to four components before comparing. `System.Version` treats `1.2.3` and
`1.2.3.0` as different — `Revision` is `-1` versus `0` — so an app reporting three components
would otherwise look permanently out of date. `"2.5"` and `"2.5.0.0"` compare equal here.

Get the running version from the assembly:

```csharp
var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
```

Whatever you use must match the `ApplicationVersion` you publish with, since that is what gets
uploaded as the version folder name.

## Triggering the update from the app

The library will not do this for you, but an app can hand off to its installer in a few lines.
That path, and why the app must shut itself down, is documented in
[installer.md](installer.md#self-update).
