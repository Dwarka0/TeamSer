# TeamSer

A Windows desktop application that starts and stops a **local TeamSpeak 3 Server**, with
access control handled by a **Cloudflare Worker + D1** backend.

> **Cloudflare never carries voice traffic.** All audio, file transfer and ServerQuery
> traffic goes between clients and the locally running TeamSpeak 3 server process. The
> Worker's only job is to validate the shared access password.

---

## Architecture

```
┌───────────────────────┐        HTTPS (auth only)      ┌──────────────────────┐
│  TeamSer.exe (WPF)    │ ─────────────────────────────▶ │ Cloudflare Worker     │
│  .NET 10, C#          │        session tokens         │ (TypeScript + Hono)   │
│  starts/stops and     │ ◀───────────────────────────── │ D1: sessions, hashes, │
│  monitors ts3server   │        Argon2id verify        │      audit, ratelimit │
└──────────┬────────────┘                               └──────────────────────┘
           │ process + ServerQuery (localhost TCP 10011)
           ▼
    ts3server_win64.exe  ◀── voice UDP 9987 / files TCP 30033 ── TeamSpeak clients
```

- **Desktop (`src/Desktop`)** — WPF shell (views, view models, dialogs).
- **Core (`src/Core`)** — testable shared logic: auth client, process management,
  ServerQuery client/parser, ServerStatus polling, network diagnostics, settings, DPAPI
  secret store.
- **Backend (`src/Backend`)** — Cloudflare Worker (TypeScript + Hono) with D1.
- **Database (`database/migrations`)** — D1 SQL migrations.

## Requirements

- **Build:** Windows 10/11 x64 with the .NET 10 SDK; Node ≥ 18 with npm.
- **Run (EXE):** Windows 10/11 x64. The published binary is self-contained — no .NET
  runtime install needed.
- **Backend:** a Cloudflare account (free plan is sufficient).
- **TeamSpeak 3 Server**: the official Windows binary from
  https://teamspeak.com/en/downloads/#server (not included in this repository; do not
  redistribute it).

## How the access model works

You share **one access password** with every computer that is allowed to manage the
server. The desktop app exchanges it for a session (short-lived access token + rotating
refresh token) at the Worker; the plaintext password is never stored on either side.
Only a logged-in client sees the dashboard, and the server process is still managed
entirely locally.

## Build and run (desktop)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Output: `publish\win-x64\TeamSer.exe` — single-file, self-contained. Copy it anywhere and
double-click. First run shows the login screen. If SmartScreen complains, the binary is
unsigned; use *More info → Run anyway* or code-sign it yourself.

For debug builds: `dotnet build src\Desktop\Desktop.csproj -c Debug`.

## Install the official TeamSpeak 3 Server

1. Download the Windows server package from the official page above and unzip it, e.g.
   to `C:\teamspeak3-server_win64`.
2. Run `ts3server_win64.exe` once yourself to accept the licence and read the console
   output (one-time `serveradmin` password / admin token).
3. In TeamSer: **Settings → server installation folder** and pick that folder.
4. In TeamSer: **Settings → save ServerQuery password** and store the `serveradmin`
   password so client counts, capacity, and uptime can be read.
5. Do this first run while testing; TeamSer then manages start/stop from then on.

The free non-commercial licence allows **1 virtual server, 32 slots**; TeamSer does not
change that.

## Configure the Cloudflare Worker

```powershell
cd src\Backend
npm install

# 1. Authentication (manual, uses your own account)
wrangler login

# 2. Create the D1 database and copy its id into wrangler.jsonc
wrangler d1 create ts3-auth

# 3. Apply migrations
wrangler d1 migrations apply ts3-auth --remote

# 4. Seed the shared access password (Argon2id hash, never plaintext)
node ..\..\scripts\gen-password-hash.mjs
wrangler secret put ADMIN_PASSWORD_HASH

# 5. Deploy
wrangler deploy
```

`scripts\deploy.ps1` automates steps 3–5 and **intentionally refuses to run** while
`wrangler.jsonc` still contains the `REPLACE_WITH_D1_DATABASE_ID` placeholder, or when
wrangler is not authenticated. Replace the placeholder first.

## Configure D1

Migrations in `database/migrations/` create the tables used by the Worker:

- `settings` — server-side configuration (the current password hash)
- `sessions` — session records; only SHA-256 token **hashes**, never tokens
- `audit_log` — append-only security events
- `rate_limits` — sliding-window buckets keyed by `sha256(caller id)`

Also set these vars in `wrangler.jsonc` before deploying:

| Var | Meaning | Default |
|---|---|---|
| `ALLOWED_ORIGINS` | comma-separated browser origins | `https://team.local` (placeholder) |
| `ACCESS_TOKEN_TTL` | access-token lifetime, seconds | `900` |
| `REFRESH_TOKEN_TTL` | refresh-token lifetime, seconds | `604800` |
| `RATE_WINDOW` / `RATE_MAX_LOGIN` | login sliding window / max attempts | `60` / `10` |

## Administrator operations

A live session alone is **not** an admin credential. Changing the shared password or
revoking everyone's access requires the current password again (step-up), sent as the
`X-Admin-Password` header or `current_password` body field:

```
POST https://<worker>/api/admin/change-password
Authorization: Bearer <access token>
X-Admin-Password: <current shared password>
Content-Type: application/json
{"password":"the new shared password"}
```

Rotate without lockout: `POST /api/admin/revoke-access` takes the same proof. See
`docs/ADMIN_SETUP.md` for the complete runbook.

## Security

- Access password stored only as an **Argon2id** PHC hash in D1 — never in plaintext,
  never in the repository.
- Tokens are 256-bit random values; D1 stores SHA-256 hashes only. **Refresh tokens
  rotate** on every refresh, so a stolen one is single-use.
- **Step-up auth** on all admin endpoints (current password required again).
- **Rate limiting** on login, refresh, validate, and admin scopes; buckets are keyed by
  `sha256(ip)` so no raw IP is ever recorded.
- Data retention: a daily cron prunes expired sessions (>30 d), audit entries (>90 d),
  and stale rate-limit buckets.
- Login failures use constant-time comparison and responses never reveal whether a
  password was close.
- Desktop secrets (auth session tokens, ServerQuery password) are stored with **Windows
  DPAPI** (CurrentUser scope) plus an application-scope entropy file that is itself
  DPAPI-protected.
- ServerQuery values are escaped per the documented protocol before being sent, so a
  password containing `|`, `\`, `\n`, `=` or spaces cannot inject additional commands.
- The Worker exposes **no** command execution, no remote control, and no audio relay;
  Cloudflare physically cannot carry UDP voice traffic.
- Process identity is enforced by exact executable path, never by process name; the
  force-kill is scoped to exactly the process TeamSer started.

Full details: `docs/SECURITY.md`.

## Known limitations

- **CGNAT** — most consumer ISPs in some regions put users behind CGNAT. TeamSer
  detects a public address in `100.64.0.0/10` and warns explicitly: **no port-forwarding
  change will make the server reachable from outside**. Options: ask the ISP for a real
  public IP, use a VPS UDP relay you run yourself, or use a VPN/overlay.
- **Firewall** — TeamSer never modifies Windows Firewall or router settings. UDP 9987
  and TCP 30033 must be opened manually (commands in `docs/DEPLOYMENT.md`).
- **TeamSpeak compatibility not yet verified against a live server** — ServerQuery
  command syntax follows the documented protocol and parser/unit tests cover it, but no
  official TeamSpeak binary was available in the build environment to test Start/Stop or
  client counting against. Run one verified cycle before trusting it in production.
- Free TeamSpeak licence: 1 virtual server, 32 slots.
- The published EXE is not code-signed.

## Run the tests

```powershell
# Everything (desktop + backend)
powershell -ExecutionPolicy Bypass -File scripts\test-all.ps1

# Or individually:
dotnet test tests\Desktop.Tests\LTS.Tests.csproj -c Debug   # 63 tests
cd src\Backend; npm run typecheck; npm test                  # 46 tests
npx wrangler deploy --dry-run --outdir .wrangler-dry         # bundle check, no deploy
```

## Documentation

| Doc | What it covers |
|---|---|
| `docs/DEPLOYMENT.md` | publishing the EXE, Worker deployment, firewall, port forwarding, CGNAT |
| `docs/ADMIN_SETUP.md` | one-time Worker provisioning, password rotation, revocation runbook |
| `docs/USER_GUIDE.md` | every screen and button |
| `docs/TROUBLESHOOTING.md` | known failure modes and fixes |
| `docs/SECURITY.md` | threat model, proven-by-test guarantees, residual risks |
| `docs/TESTING.md` | what each test actually asserts, plus what stays untested |

## Licence note

TeamSpeak 3 Server is proprietary software by TeamSpeak Systems GmbH and is **not**
distributed with this project. Download it yourself from the official site and accept
its licence. This repository contains only TeamSer's own source code.
