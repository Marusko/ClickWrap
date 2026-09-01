# Server

ASP.NET Core with a Blazor Server admin page and two API endpoints. Plain files on disk, no
database, no login.

## Storage layout

```
$CLICKWRAP_DATA/
  race-timer/
    3.3.0.1/
      app.zip          # zipped ClickOnce publish folder
      metadata.json
    3.4.0.0/
      app.zip
      metadata.json
  time-maker/
    1.6.0.0/
      ...
```

`metadata.json`:

```json
{
  "version": "3.4.0.0",
  "releaseNotes": "Fixed lap import.",
  "uploadedUtc": "2026-08-28T09:14:03Z",
  "sizeBytes": 8412331,
  "sha256": "9f2c…"
}
```

**Latest** is the highest version *folder name*, parsed with `System.Version` — so `3.10.0.0`
beats `3.9.0.0`, which string ordering would get wrong. Folders that do not parse as a version,
or that have no `app.zip`, are skipped. A version folder dropped in by hand works; without
`metadata.json` the size and date come from the file itself and there are no release notes.

## API

### `GET /api/apps/{appId}/latest`

```json
{
  "appId": "race-timer",
  "version": "3.4.0.0",
  "downloadUrl": "https://updates.example.com/api/apps/race-timer/versions/3.4.0.0/download",
  "releaseNotes": "Fixed lap import.",
  "uploadedUtc": "2026-08-28T09:14:03+00:00",
  "sizeBytes": 8412331,
  "sha256": "9f2c…"
}
```

`404` when the app has no versions. `400` when the app id is not a valid segment.

### `GET /api/apps/{appId}/versions/{version}/download`

Streams `app.zip` as `application/zip` with range requests enabled, so a large download can
resume rather than restart.

### `GET /health`

Plain text `Healthy` with `200`, or `Unhealthy` with `503` — which is all a container healthcheck
needs. It checks that the data folder is present, since an unmounted volume is the one failure
the server cannot recover from on its own and cannot otherwise detect. The rest of the app being
up is implied by the endpoint answering at all.

The image installs `curl` for this; wire it up from compose, for example:

```yaml
healthcheck:
  test: ["CMD", "curl", "-fsS", "http://localhost:8080/health"]
  interval: 30s
  timeout: 3s
  retries: 3
```

### Path safety

`appId` and `version` become path segments, so they are whitelisted against
`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` rather than sanitised. Anything else is rejected with `400`
before touching the disk.

## `/admin`

One page, built with [MudBlazor](https://mudblazor.com): pick an existing app or type a new id,
enter a version, optional release notes, choose the zip, upload. Success shows a snackbar;
validation failures show inline.

The MudBlazor theme lives in `Components/ClickWrapTheme.cs` and is built from the same palette as
the installer and the store launcher (accent `#2563EB`, surface `#FFFFFF`, background `#F3F4F6`),
so the admin UI reads as part of the same family of tools.

There is **no app-level auth by design** — this is expected to sit behind a Cloudflare Tunnel
with Cloudflare Access in front of `/admin`. Do not expose it directly.

Uploads stream to a staging folder and are hashed on the way through, so nothing is buffered
whole in memory. The zip is then checked for `setup.exe` and a `*.application` at its root
before anything moves into place. A rejected or interrupted upload leaves nothing behind, and a
half-written version is never visible to `/latest`.

Rejections you will actually hit:

| Cause | Message |
| --- | --- |
| Version already exists | `Version 3.4.0.0 of 'race-timer' already exists. Tick overwrite to replace it.` |
| Not a version number | `Enter a valid version number, for example 1.2.3.0.` |
| Zipped the folder, not its contents | `This does not look like a ClickOnce publish folder: setup.exe and *.application missing from the root of the zip.` |

That last one is worth the check: the installer runs `setup.exe` from the root of what it
extracts, so a wrongly-shaped zip would otherwise produce a broken install on every machine.

## Configuration

All from environment variables, so it drops into a container with no `appsettings.json`. These
are every variable the app itself reads — the names are declared once as constants on
`ServerOptions`.

| Variable | Default | Purpose |
| --- | --- | --- |
| `CLICKWRAP_DATA` | `/data` on Linux, `<app>/data` on Windows | Root of the app/version tree. The only state; put it on a volume. |
| `CLICKWRAP_PUBLIC_BASE_URL` | *(forwarded headers)* | Public origin, e.g. `https://updates.example.com`. Used to build `downloadUrl`. |
| `CLICKWRAP_MAX_UPLOAD_MB` | `512` | Upload size cap, in megabytes. Values that are not a positive integer fall back to the default. |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` | Addresses to bind. All interfaces, since it is reached through the tunnel, not loopback. |

Standard ASP.NET Core variables still work — `ASPNETCORE_ENVIRONMENT`,
`Logging__LogLevel__Default` and the rest — but nothing in ClickWrap requires them.

Every one of these can also be passed as a command-line argument, which is handy in development:

```bash
dotnet run --project src/ClickWrap.Server -- --CLICKWRAP_DATA /tmp/clickwrap --urls http://0.0.0.0:8090
```

### Startup output

The server prints what it resolved, so a misconfigured deployment is obvious in the logs:

```
--------------------- ClickWrap server starting v1.0.0 ---------------------
Current user: Spravca
Working directory: /app
Listening on: http://0.0.0.0:8080
Public base URL: https://updates.example.com
Data path: /data
Max upload: 512 MB
Apps published: 2 (7 versions)
```

With `CLICKWRAP_PUBLIC_BASE_URL` unset, that line says so and names the variable, because it is
the one setting that silently produces unreachable download URLs behind a tunnel.

**Set `CLICKWRAP_PUBLIC_BASE_URL` in production.** Without it the `downloadUrl` in `/latest` is
built from the inbound request host, which behind a tunnel is the tunnel's host, not yours —
installers would then be handed a URL they cannot reach.

TLS is terminated by Cloudflare, so there is no HTTPS redirection and no HSTS. `X-Forwarded-*`
headers are trusted from any proxy because `cloudflared` connects from inside the container
network and so is never in a known-proxy range.

## Running it locally

```bash
dotnet run --project src/ClickWrap.Server
```

Then open <http://localhost:8080/admin>. Data lands in
`src/ClickWrap.Server/bin/Debug/net10.0/data` unless `CLICKWRAP_DATA` says otherwise.
