# Deployment guide

## Desktop application

### Requirements

- Windows 10/11 x64
- .NET 10 SDK installed to build; none required to run the published EXE

### Build

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Output: `publish\win-x64\TeamSer.exe` (self-contained, single file).

To copy the EXE to another computer, take the whole `publish\win-x64` folder or just the
EXE — it has no runtime dependency.

### Run

Double-click `TeamSer.exe`. First run shows the login screen.

If Windows SmartScreen appears, it is because the binary is not code-signed. Choose
"More info" → "Run anyway", or sign the binary before distributing (see SECURITY.md).

---

## Cloudflare Worker

### Prerequisites

- A Cloudflare account you control
- `wrangler` (already present in this environment: v4.100.0)
- `node` (present: v26.3.0)

### Steps

1. **Authenticate** — this is a manual step that requires your own account:

   ```
   wrangler login
   ```

2. **Create the D1 database** — this also requires your account:

   ```
   wrangler d1 create ts3-auth
   ```

   Copy the printed `database_id` into `wrangler.jsonc`, replacing
   `REPLACE_WITH_D1_DATABASE_ID`.

3. **Apply migrations**:

   ```
   wrangler d1 migrations apply ts3-auth --remote
   ```

4. **Seed the access password** — never stored in plaintext:

   ```
   node scripts\gen-password-hash.mjs
   ```

   Paste the printed hash into:

   ```
   wrangler secret put ADMIN_PASSWORD_HASH
   ```

5. **Deploy**:

   ```
   wrangler deploy
   ```

### Using the helper script

```powershell
# Migrations only (stops with a warning if the D1 id is still the placeholder)
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1

# Migrations + deploy
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Deploy
```

`scripts\deploy.ps1` intentionally refuses to deploy when the database id is still the
placeholder, and refuses when wrangler is not authenticated. It cannot and will not
invent your Cloudflare credentials.

### After deploying

- Record the Worker URL: `https://teamser-auth-backend.<subdomain>.workers.dev`
- Paste it into the desktop app under **Settings → auth server URL → Apply**.
- If you change `ALLOWED_ORIGINS` in `wrangler.jsonc`, redeploy.

---

## Firewall

The desktop app never modifies Windows Firewall rules. If the server is reachable only
from this machine, no rules are needed. To let others connect:

```
netsh advfirewall firewall add rule name="TeamSpeak Voice (UDP 9987)" dir=in action=allow protocol=UDP localport=9987
netsh advfirewall firewall add rule name="TeamSpeak File Transfer (TCP 30033)" dir=in action=allow protocol=TCP localport=30033
```

Only open **ServerQuery TCP 10011** to loopback (`127.0.0.1`), never to the network.

---

## Router / port forwarding

External clients need UDP 9987 forwarded to this machine.

If the network diagnostic tab reports **CGNAT** (public address in `100.64.0.0/10`), the
ISP performs the NAT and no port-forwarding change on your side will work. Alternatives:

- a VPS acting as a UDP relay (you must run and secure it yourself)
- a hosted TeamSpeak provider instead of a self-hosted one
- a VPN/overlay that gives you a routable address

Do not spend time trying to forward ports through CGNAT; it will not work.

---

## Not deployed by this project

No Cloudflare account was used while preparing this repository. Deployment requires your
credentials and your explicit action.
