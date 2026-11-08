# User guide

## First launch

1. Run `TeamSer.exe`.
2. Enter the **shared access password** you were given. It is checked online; the app
   then stores a session on this computer so you do not retype it every time.
3. If the backend is unreachable, the app says so and keeps your stored session instead
   of deleting it.

Five wrong passwords within a few minutes locks the login screen locally for 5 minutes.
The backend also rate-limits by IP.

## Choosing the server installation

**Settings → server installation folder**

Pick the folder containing `ts3server_win64.exe` (or `ts3server.exe` in older packages).
The app verifies the binary exists before accepting the folder.

If the app already found an installation in `C:\Program Files\ts3server3-server_win64`
or similar, it fills this in automatically.

## Dashboard

| Field | Meaning |
|---|---|
| Server status | Offline / Starting / Online / Stopping / Error |
| Connected users | connected voice clients, excluding ServerQuery clients |
| Remaining capacity | `virtualserver_maxclients` from the server |
| Uptime | uptime reported by the server |
| Installation status | whether the executable was found |

The status is **not** guessed from button clicks. The app checks that the process exists
*and* that ServerQuery answers.

## Controls

| Button | Behaviour |
|---|---|
| Start | starts the server, then waits until ServerQuery answers (timeout 60 s) |
| Stop | asks for confirmation, sends Ctrl+C, waits 15 s, force-terminates only if needed |
| Restart | stop + start, with the same confirmation |
| Refresh status | re-reads process and ServerQuery state |
| Server folder | opens the installation folder in Explorer |
| Logs folder | opens `<server>\logs` |

Duplicate protection: if a server process is already running for that exact executable,
the app reports it and refuses to start a second one.

## ServerQuery password

**Settings → save ServerQuery password**

Enter the `serveradmin` password shown in the server console on first run. It is stored
encrypted with Windows DPAPI and is never displayed again, logged, or written to config.

Without it, the app can still start and stop the server, but client counts, capacity, and
uptime will be unavailable and the status shows a ServerQuery warning.

## Network tab

- **Local address** — IPv4 on this machine
- **Public address** — resolved via the configured HTTPS endpoint
- **Voice port (UDP)** — default 9987
- **Voice address** — what to give friends

A public IP does **not** prove the server is reachable from outside. Two things must
still be true: the router forwards UDP 9987, and the ISP does not use CGNAT. The app
shows a CGNAT warning when the public address falls in `100.64.0.0/10`.

## Sharing the connection address

**Dashboard → copy connection address** copies e.g. `203.0.113.5:9987`.

Friends add that under **Connections → Connect** in the TeamSpeak 3 client.

## Logs tab

Shows the last 300 lines of the newest `.log` file in the server's `logs` folder. Useful
when a start attempt fails.

## Quitting

Closing the window does **not** stop the server. Stop it explicitly first if you want it
down. Only one instance of the app can run at a time.
