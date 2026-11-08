# Test report

Run date: 2026-11-08

One command runs the whole suite:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test-all.ps1
```

---

## Desktop (xUnit)

**Result: 63 passed, 0 failed, 0 skipped** — `LTS.Tests.dll` (net10.0-windows)

| Area | Tests | What is actually asserted |
|---|---|---|
| ServerQuery parser | 13 | OK/non-OK error lines, records, `\|`-separated records, escape/unescape round-trip, `\p` not split as record separator, truncated and empty responses reported as errors, multiple replies, values containing `=` |
| Network | 12 | RFC 1918 classification incl. boundary values, RFC 6598 CGNAT classification incl. boundaries, invalid IP handling, unreachable and unset public-IP endpoints return an error and **no** address, connection address formatting |
| DPAPI secret store | 8 | Session round-trip, **plaintext token absent from the on-disk blob**, entropy file also protected, clear removes, query password round-trip + obfuscated on disk, overwrite, corrupt blob returns null instead of throwing |
| Auth client | 8 | Not-configured reported without throwing, unreachable backend → `Network`, validate/logout tolerant of network failure, trimmed base URL, access-token expiry with and without clock skew, refresh expiry |
| Process management | 17 | Missing directory, unset path, directory without an executable, valid directory, `ts3server_win64.exe` detection, preference of win64 over the plain name, correct working directory on start, **start twice returns the same PID (no duplicate)**, force-kill when the process ignores signals, stop when not running, crash flips state to `Offline`, unrelated executables never matched, readiness probe timeout |

The process tests run against `tests/MockServer`, a small console program that behaves like
`ts3server.exe`: it honours Ctrl+C for graceful shutdown, supports `--hang` (ignore all
signals), `--crash`, `--quick-exit`, and opens a configurable listener so readiness
probing is exercised. The mock is copied into a scratch directory **as `ts3server.exe`**
so path detection runs through the production code path.

---

## Cloudflare Worker (Vitest)

**Result: 46 passed, 0 failed** — plus `tsc --noEmit` clean under
`exactOptionalPropertyTypes`, `noUncheckedIndexedAccess` and `noUnusedLocals`.

| Area | Tests | What is actually asserted |
|---|---|---|
| Password hashing | 6 | PHC/argon2id output that never contains the plaintext, correct password verifies, wrong password and empty password rejected, salts differ for identical input, malformed/non-argon2 stored values return `false` without throwing, unicode passwords |
| Tokens | 5 | 200 tokens all unique and URL-safe, length and charset bounds, deterministic hash, hash ≠ token, 64 hex chars |
| Constant-time compare | 1 | equal, unequal, different length, empty |
| Rate limiting | 3 | allows exactly up to the limit then blocks, buckets are isolated per identifier **and** per scope, raw identifier never stored in D1 |
| Login | 6 | health is public, wrong password → 401 `invalid_credentials`, correct password → full session, response never contains the password or the argon2 hash, two computers get independent sessions |
| Session lifecycle | 8 | validate accepts a fresh token, rejects a missing token, rejects a forged token, logout revokes, refresh rotates **and** the old refresh token is rejected on replay, expired access → `expired`, expired refresh → `unauthorized` |
| Admin | 12 | **step-up: a session alone gets 403 for status/change-password/revoke-access**, wrong proof is rejected and the password is not rotated, status reports config and session count with the correct proof, **change-password rotates the hash and revokes every other session while the caller keeps working**, too-short password rejected while the current password stays valid, revoke-access kills other sessions, admin endpoints all require a session even with a correct proof |
| Bootstrap | 2 | no hash and no secret → 503 `not_configured`, a non-argon2 secret is not trusted as configured |
| Validation & limits | 5 | non-JSON → 400, empty password → 400, over-long password → 400, repeated failures → 429, a rate-limited request cannot be used to confirm whether a password is correct |
| CORS | 5 | allowlisted origin echoed, foreign origin gets no CORS header, foreign preflight → 403, allowlisted preflight → 204, `no-store` + `nosniff` always set |
| Misc | 2 | unknown route → clean 404 with no stack trace |

These tests drive the **real Hono app** (`src/Backend/src/index.ts`) through a small
in-memory D1 double that implements exactly the statement shapes the Worker issues and
throws on anything else, so a refactor cannot silently change behaviour.

---

## Build

| Check | Result |
|---|---|
| `dotnet build -c Release` (Desktop + Core) | succeeds, 0 warnings, 0 errors |
| `dotnet build -c Debug` | succeeds |
| `dotnet test -c Release` | 63 / 63 pass |
| `npm run typecheck` | 0 errors |
| `npm test` | 46 / 46 pass |
| `npx wrangler deploy --dry-run` | succeeds; Worker bundle 114.81 KiB (gzip 31.96 KiB), D1 and all `vars` bindings resolved |

## Published EXE

```
dotnet publish -c Release -r win-x64 --self-contained true
```

Output: `D:\teamser\publish\win-x64\TeamSer.exe`

- single file, 139,888,785 bytes (133.4 MB)
- 1 file in the output directory, no separate runtime files
- HTTP smoke test: launched, stayed running (not exited after 4 s), terminated cleanly on
  stop

---

## Not tested, and why

Stated plainly rather than implied:

| Claim | Status | Reason |
|---|---|---|
| Start/stop/restart against a real TeamSpeak 3 server | **not tested** | No official TeamSpeak binary was available in this environment, and downloading/redistributing one is not something to do without the admin's acceptance of the licence. Tested against `MockServer` instead. |
| ServerQuery commands against a live server | **not tested** | Same reason. Command syntax follows the documented protocol and the parser is tested against fixtures, but real field names and responses were not observed. |
| Live Cloudflare deployment | **not tested** | No Cloudflare credentials. `wrangler deploy --dry-run` was the furthest step taken. |
| Windows Firewall detection | **not implemented** | No reliable, non-mutating way to determine rule state was found; the UI states requirements textually rather than guessing. |
| UDP 9987 reachability | **not tested** | Requires a second network peer. The app explicitly does not infer reachability from having a public IP. |
| CGNAT warning | **logic tested** (12 cases) | The live ISP path cannot be reproduced here. |

To close the TeamSpeak gap: install the official server, run one Start → Stop → Restart
cycle, confirm the dashboard shows a real connected-client count, and confirm the logs tab
shows a real `.log` entry.
