# Step 7 — Mobile Security Hardening: skipped, no target

**Status:** skipped. RediensIAM has no mobile component to harden.

## How that was established

Repository root holds `deploy/`, `docs/`, `frontend/`, `sdk/`, `src/`, `tests/` and build files —
no mobile project directory.

A search for mobile project markers across the tree (depth 3, excluding `node_modules`) returned
nothing:

```
find . -maxdepth 3 \( -name "*.xcodeproj" -o -name "build.gradle*" -o -name "Podfile" \
  -o -name "pubspec.yaml" -o -name "capacitor.config.*" \) -not -path "./node_modules/*"
→ no results
```

And no frontend or SDK package declares a mobile runtime:

```
grep -l "react-native\|expo\|capacitor\|cordova" frontend/*/package.json sdk/typescript/*/package.json
→ no results
```

The three client surfaces are all web: `frontend/admin` and `frontend/login` (React SPAs) and
`sdk/typescript/rediensiam-web` (browser SDK). They are covered by step 6.

## What this step would have covered, and where it actually lands

The pipeline's step 7 items each have a real analogue on the web surface. None is dropped; they
belong to other steps:

| Step 7 item | Where it lands for this system |
|---|---|
| Certificate pinning | Not applicable to browsers (pinning is the CA/HSTS story) — TLS posture is R-02/R-15, step 9 |
| Biometric authentication | WebAuthn/FIDO2 — step 8 (auth enhancement) |
| Encrypted local storage | Step 1 confirmed no token is written to `localStorage`/`sessionStorage`; the browser SDK holds tokens in memory only |
| Code obfuscation (ProGuard/R8) | No equivalent security control for a public SPA bundle |
| Anti-tampering, root/jailbreak detection | No client-integrity assumption exists to protect — every privileged decision is re-made server-side |
| Secure IPC | No IPC surface; the boundary is HTTP, covered by step 6 and the CSP work |

## If a mobile client is added later

The token contract is the thing that would need re-reading first. `ext.roles` now carries tenant
roles as `{project_id}/{name}` (changed in step 4), and a mobile app must not treat locally
decoded claims as authorisation — same rule as the browser SDK, for the same reason. See
`docs/INTEGRATION.md` and `sdk/README.md`.

A native app would also need its own OAuth2 client. It must be a **public PKCE** client, and the
`sa_` and `client_` id prefixes are reserved.
