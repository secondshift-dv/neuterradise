# NeuTerradise Agent Instructions

Canonical build:

```powershell
pwsh ./scripts/build.ps1
```

Strictly offline build:

```powershell
pwsh ./scripts/build.ps1 -Offline
```

Create or refresh the versioned GitHub Draft Release from the current clean `origin/main`:

```powershell
pwsh ./scripts/release.ps1
```

Publish the verified Draft Release explicitly:

```powershell
pwsh ./scripts/release.ps1 -Publish
```

Rules:

- Do not create alternate build, package, release, or update paths.
- Do not purge NuGet or NeuTerradise dependency caches.
- Do not change dependency versions to make a build pass.
- Do not commit generated `dist/` or `.cache/` content.
- Do not touch the Vault during build, package, release, or update work.
- Do not launch the application or run tests unless explicitly requested.
