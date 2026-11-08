# Administrator setup guide

This is the one-time, per-environment procedure.

## 1. Create the Worker environment

```
wrangler login
wrangler d1 create ts3-auth
```

Paste the returned `database_id` into `wrangler.jsonc`.

## 2. Choose and store the shared access password

Pick a strong password (12+ characters recommended; minimum 8 is enforced).

Generate an Argon2id hash of it locally:

```
node scripts\gen-password-hash.mjs
```

It prints only a hash like `$argon2id$v=19$m=19456,t=2,p=1$…$…`. **The plaintext password
is never written to disk by the script and never appears in this repository.**

Set it as a Worker secret:

```
wrangler secret put ADMIN_PASSWORD_HASH
```

Paste the hash at the prompt. From this point on the plaintext only exists in your head
and in the TeamSpeak server admin's password manager.

## 3. Apply migrations and deploy

```
wrangler d1 migrations apply ts3-auth --remote
wrangler deploy
```

## 4. Point desktop clients at the Worker

In the app: **Settings → auth server URL** → `https://teamser-auth-backend.<sub>.workers.dev`
→ **Apply**.

Log in with the shared password. The app stores a session in Windows DPAPI (CurrentUser
scope) so the password is not typed again.

## 5. Changing the shared password

Administrator operations are **step-up protected**: a live session alone is not enough —
every admin request must also prove the *current* shared password. Send it in the
`X-Admin-Password` header or as `current_password` in the JSON body.

Open a browser and:

```
POST https://<worker-url>/api/admin/change-password
Authorization: Bearer <your access token>
X-Admin-Password: the current shared password
Content-Type: application/json

{"password":"the new shared password"}
```

Then set the new hash as the Worker secret and **restart the Worker** (the new value takes
effect on the next request; the D1 hash is updated immediately):

```
node scripts\gen-password-hash.mjs   # for the new password
wrangler secret put ADMIN_PASSWORD_HASH
```

Changing the shared password **revokes every other computer's session** automatically.
The computer you used to make the change keeps working.

## 6. Revoking all authorizations

```
POST https://<worker-url>/api/admin/revoke-access
Authorization: Bearer <your access token>
X-Admin-Password: the current shared password
Content-Type: application/json

{}
```

This revokes every session except the caller's. After that, each computer must log in again
with the current shared password.

## 7. First-run bootstrap (no secret set yet)

If `ADMIN_PASSWORD_HASH` is absent, `POST /api/auth/login` returns `503 not_configured`
and login is impossible. That is intentional: an unconfigured Worker never accepts
passwords. See `src/Backend/src/index.ts` → `ensureConfigured`.

## 8. Rotating after a suspected leak

1. `POST /api/admin/revoke-access` (with `X-Admin-Password`) — kills every session.
2. Generate a new hash, `wrangler secret put ADMIN_PASSWORD_HASH`.
3. `wrangler d1 migrations apply ts3-auth --remote` if you added migrations.
4. Tell every user the new shared password.
On each Windows machine, clear the local session: **Settings → remove local access**.

---

## Notes

- The Worker authenticates only. It has no endpoint that starts, stops, or sends data to a
  TeamSpeak server. There is deliberately no remote command execution.
- Cloudflare cannot relay UDP 9987 voice traffic. It is an authentication backend only.
