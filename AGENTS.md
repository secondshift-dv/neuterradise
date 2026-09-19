# NeuTerradise Agent Instructions

## Audit and remediation authority

`DEEP_AUDIT_FINAL.md` is the canonical audit ledger and remediation specification for the frozen Stage 1–12 baseline.

- Follow the remediation order R0 → R8 defined in Section 22.
- Finding IDs are immutable. X27–X29 remain RESERVED and must never be repurposed.
- Do not open Stage 13 for the frozen baseline.
- A newly discovered manifestation of an existing root cause stays under its existing X finding.
- Allocate X74+ only for a genuinely distinct root cause outside X01–X73.
- Before editing for a finding, read its finding body, Section 4B trace target, dependency cluster, and current callers.
- Do not mark a finding SOURCE-CLOSED until the root cause and required regression guard are committed.
- Later phase references marked integration/revalidation companion do not authorize duplicate implementation.
- R8 is executable evidence work and remains subject to the explicit execution restriction below.

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
