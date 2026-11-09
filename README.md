# Image Digest Advisor

Windows desktop app (WinUI 3, .NET 10) that tells you whether a pinned Docker image digest is outdated, and suggests an updated `repo:tag@sha256:…` reference.

- Compares your digest with the image the same tag points to today ("tag moved").
- Finds newer **patch / minor / major** versions in the same variant (`1.27.2-alpine3.20` → `1.27.4-alpine3.21`, never `-bookworm`).
- Flags images older than a configurable age (default 90 days).
- Shows created date, age, platform, size, layers, tags, index and platform digests, and OCI labels (version, source, revision).
- Works **without Docker** and **without login** for public images on Docker Hub, MCR, GHCR, Quay and any OCI v2 registry.

## Solution layout

| Project | Purpose |
|---|---|
| `src/ImageDigestAdvisor.Core` | All logic: reference parsing, OCI registry client (token auth, caching), Docker Hub tag API, credential chain, analyzer. No UI dependency. |
| `src/ImageDigestAdvisor.App` | WinUI 3 app (MVVM with CommunityToolkit.Mvvm). Single-project MSIX, also builds unpackaged. |
| `tests/ImageDigestAdvisor.Core.Tests` | MSTest tests against an in-memory fake registry. They make no network calls. |

### How credentials are resolved

1. Accounts saved in the app (Windows Credential Manager, `ImageDigestAdvisor:<registry>`).
2. `%USERPROFILE%\.docker\config.json` (or `%DOCKER_CONFIG%`). This includes credential helpers such as `docker-credential-desktop`, but only if they are installed. A missing file or helper is skipped.
3. Anonymous.

If saved credentials are rejected, the app falls back to anonymous access so public images keep working.

### Rate limits

Docker Hub limits anonymous pulls per IP. The app keeps usage low:

- Tags are resolved with HEAD requests, which don't count against the limit.
- Tag→digest lists come from the Docker Hub web API.
- Manifests and configs are cached on disk by digest, forever (content-addressed), under `%LOCALAPPDATA%\ImageDigestAdvisor\cache`.
- The remaining limit is shown after each Docker Hub check.

## Build and run

Requires the .NET 10 SDK. Visual Studio 2026 with the *WinUI application development* workload is recommended for debugging.

```powershell
dotnet test --project tests\ImageDigestAdvisor.Core.Tests
dotnet build src\ImageDigestAdvisor.App -p:Platform=x64 -p:Unpackaged=true
.\src\ImageDigestAdvisor.App\bin\unpackaged\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ImageDigestAdvisor.exe
```

Unpackaged builds go to `bin\unpackaged\` and `obj\unpackaged\`, separate from packaged (MSIX) builds. The two modes produce different `resources.pri` files, so they must not share an output folder.

In Visual Studio, choose the launch profile **ImageDigestAdvisor (Package)** (MSIX) or **(Unpackaged)**. The unpackaged profile needs the build property `Unpackaged=true`.

## Distribute

**To colleagues (zip, no installer, no certificate):**

```powershell
dotnet publish src\ImageDigestAdvisor.App -c Release -p:Platform=x64 -p:Unpackaged=true
# zip src\ImageDigestAdvisor.App\bin\publish\x64\ and share it; run ImageDigestAdvisor.exe
```

**Microsoft Store (MSIX):**

1. Reserve the app name in Partner Center.
2. In Visual Studio: *Project › Publish › Associate App with the Store*. This updates `Package.appxmanifest` Identity/Publisher.
3. *Publish › Create App Packages* (Store), then upload the `.msixupload`.
4. The listing needs a privacy policy URL. The app's behaviour is described under *Settings › About*: it only contacts the checked registries and hub.docker.com, stores credentials locally, and collects no telemetry.

A local test package can be built with:

```powershell
dotnet publish src\ImageDigestAdvisor.App -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageDir=bin\msix\ -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxBundle=Never
```

The package is unsigned. To sideload it, sign it with a certificate trusted on the target machine.

## Known limitations

- Docker Hub's web API returns only the 1,000 most recently updated tags anonymously. For older digests, the tag is found through the version the image reports about itself, such as `NGINX_VERSION` or the `org.opencontainers.image.version` label.
- On registries without a digest-aware tag API, a bare digest (no tag) is matched by probing up to 150 tags with HEAD requests. Entering `repo:tag@digest` is always exact.
- If an app crashes at startup, it writes `%LOCALAPPDATA%\ImageDigestAdvisor\crash.log`.
