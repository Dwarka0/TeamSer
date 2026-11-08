# Troubleshooting

## Login

Error messages from the app are localized (Persian); the quoted text below is exactly what
the app shows.

**"ارتباط با سرور تأیید ممکن نشد."** — the Worker is unreachable. Check:
- the URL in **Settings → auth server URL** (scheme must be `https://`)
- internet connectivity
- `wrangler tail` on the Worker side for incoming requests

**"خطای سرور تأیید: … (503)"** — `ADMIN_PASSWORD_HASH` is not set. Run
`node scripts\gen-password-hash.mjs` and `wrangler secret put ADMIN_PASSWORD_HASH`.

**Login refuses after several attempts** — local lockout is 5 minutes after 5 failures.
The backend also rate-limits per IP (`RATE_MAX_LOGIN`, default 10/min). Wait, then retry.

**Login works but the dashboard stays offline** — that is correct behaviour;
authentication and server state are independent. Start the server from the dashboard.

## Server won't start

**"Server directory is not configured."** — pick the folder in Settings.

**"No TeamSpeak server executable was found…"** — the folder must contain
`ts3server_win64.exe`, `ts3server_win32.exe`, or `ts3server.exe`.

**Starts, then goes to Error with "ServerQuery is unreachable"** — the process is alive
but TCP 10011 does not answer. Common causes:
- the server is still initialising (raise `StartupTimeoutSeconds` in `config.json`)
- ServerQuery is bound to a different port (`ts3server.ini` / `query_port`)
- the server refused to start because the licence prompt was never accepted, or because
  port 9987 is already taken by another instance

Look in the **Logs** tab first. TeamSpeak writes the actual reason there.

**"یک نمونه از سرور از قبل در حال اجراست."** — duplicate prevention. Stop the existing
process first. If it was started manually, stop it from its console window.

## ServerQuery authentication

**Client count is blank / warning shows "ServerQuery login failed"** — wrong `serveradmin`
password, or the account is still the one-time token rather than a password. Open the
server console, read the generated admin token, and use it once as the password;
TeamSpeak then asks you to set a real one.

## Stopping

**Stop takes the full 15 seconds** — the server ignored Ctrl+C and was force-terminated.
Data files are not touched, but the server did not shut down cleanly. Check the logs for
why the console handler did not run.

**"سرور خاموش نشد"** — the process survived both graceful shutdown and `Kill()`. It is
likely elevated or protected. Stop it manually from an elevated console.

## Network

**Friends cannot connect**
1. Is UDP 9987 forwarded to this machine?
2. Is a Windows Firewall rule present for UDP 9987?
3. Does the public address fall in `100.64.0.0/10`? If yes, you are behind CGNAT and port
   forwarding cannot work. See `docs/DEPLOYMENT.md` → Router / port forwarding.
4. A public IP alone is not proof of reachability. Ask a friend to actually connect.

**Public IP shows "آدرس عمومی یافت نشد"** — the lookup endpoint is blocked or offline.
Change `PublicIpEndpoint` in `config.json` to another HTTPS service that returns a bare
IPv4 body.

## Windowing / startup

**"برنامه از قبل در حال اجراست."** — a named mutex is held by another instance. Close the
other window, or reboot if a stale handle persists.

**SmartScreen blocks the EXE** — the binary is unsigned. Use "More info → Run anyway", or
sign it (see SECURITY.md).

## Data locations

| Data | Path |
|---|---|
| Settings | `%LOCALAPPDATA%\TeamSer\config.json` |
| Encrypted secrets | `%LOCALAPPDATA%\TeamSer\secrets.bin` |
| Entropy | `%LOCALAPPDATA%\TeamSer\entropy.bin` |
| Server logs | `<server dir>\logs\*.log` |
| Server database | `<server dir>\ts3server.sqlitedb` |

Deleting `secrets.bin` logs you out locally and forgets the ServerQuery password. It does
not delete the server data.
