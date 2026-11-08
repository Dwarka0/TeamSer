import { Hono, type Context } from "hono";
import type { Env } from "./env.js";
import { SETTING_ADMIN_PASSWORD_HASH } from "./env.js";
import { prune } from "./services/prune.js";
import { hashPassword, newToken, sha256Hex, verifyPassword } from "./services/password.js";
import { checkRateLimit } from "./services/ratelimit.js";
import type { SessionRow } from "./env.js";

type Vars = { session: SessionRow };

const app = new Hono<{ Bindings: Env; Variables: Vars }>();

/* ------------------------------------------------------------------ helpers */

const now = () => Math.floor(Date.now() / 1000);
const int = (value: string | undefined, fallback: number) => {
  const n = Number(value);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : fallback;
};

function jsonError(c: Context, status: number, error: string, message: string) {
  return c.json({ error, message }, status as never);
}

/**
 * CORS: browsers send Origin; the desktop client does not.
 * Requests with no Origin are allowed (non-browser client), everything else must
 * match the allowlist exactly. No wildcard with credentials.
 */
function resolveOrigin(env: Env, origin: string | undefined): string | null {
  if (!origin) return null;
  const allowed = (env.ALLOWED_ORIGINS ?? "")
    .split(",")
    .map((o) => o.trim())
    .filter(Boolean);
  return allowed.includes(origin) ? origin : null;
}

app.use("*", async (c, next) => {
  const origin = c.req.header("Origin");
  const allowed = resolveOrigin(c.env, origin);

  if (origin && !allowed) {
    // A browser origin that is not allowlisted gets no CORS headers and is rejected
    // on preflight. Simple requests still execute but the browser withholds the body.
    if (c.req.method === "OPTIONS") return c.body(null, 403);
  }

  await next();

  if (allowed) {
    c.header("Access-Control-Allow-Origin", allowed);
    c.header("Vary", "Origin");
    c.header("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
    c.header("Access-Control-Allow-Headers", "Authorization,Content-Type");
    c.header("Access-Control-Max-Age", "600");
  }
  c.header("X-Content-Type-Options", "nosniff");
  c.header("Cache-Control", "no-store");
});

app.options("*", (c) => c.body(null, 204));

/* --------------------------------------------------------------- middleware */

function bearer(c: Context<{ Bindings: Env; Variables: Vars }>): string | null {
  const header = c.req.header("Authorization") ?? "";
  const match = /^Bearer\s+(.+)$/i.exec(header.trim());
  return match?.[1] ?? null;
}

async function findSessionByAccess(db: D1Database, token: string): Promise<SessionRow | null> {
  const hash = await sha256Hex(token);
  return db
    .prepare("SELECT * FROM sessions WHERE token_hash = ? AND revoked_at IS NULL")
    .bind(hash)
    .first<SessionRow>();
}

/** Verifies the access token and rejects expired or revoked sessions. */
async function requireSession(c: Context<{ Bindings: Env; Variables: Vars }>) {
  const token = bearer(c);
  if (!token) return { ok: false as const, response: jsonError(c, 401, "unauthorized", "Missing bearer token.") };

  const session = await findSessionByAccess(c.env.DB, token);
  if (!session) return { ok: false as const, response: jsonError(c, 401, "unauthorized", "Unknown or revoked session.") };
  if (session.access_expires_at <= now())
    return { ok: false as const, response: jsonError(c, 401, "expired", "Access token expired.") };

  await c.env.DB.prepare("UPDATE sessions SET last_seen_at = ? WHERE id = ?").bind(now(), session.id).run();
  c.set("session", session);
  return { ok: true as const, session };
}

async function getSetting(db: D1Database, key: string): Promise<string | null> {
  const row = await db.prepare("SELECT value FROM settings WHERE key = ?").bind(key).first<{ value: string }>();
  return row?.value ?? null;
}

async function setSetting(db: D1Database, key: string, value: string): Promise<void> {
  await db
    .prepare(
      `INSERT INTO settings (key, value) VALUES (?, ?)
       ON CONFLICT (key) DO UPDATE SET value = excluded.value`
    )
    .bind(key, value)
    .run();
}

/**
 * First-run bootstrap: ADMIN_PASSWORD_HASH is a Worker secret holding an argon2id PHC
 * string produced offline by scripts/gen-password-hash.mjs. It is copied into D1 once,
 * then the secret can be removed. No plaintext password exists in code or config.
 */
async function ensureConfigured(env: Env): Promise<string | null> {
  const stored = await getSetting(env.DB, SETTING_ADMIN_PASSWORD_HASH);
  if (stored) return stored;

  const seed = env.ADMIN_PASSWORD_HASH;
  if (!seed || !seed.startsWith("$argon2")) return null;

  await setSetting(env.DB, SETTING_ADMIN_PASSWORD_HASH, seed);
  await env.DB.prepare("INSERT INTO audit_log (at, event, detail) VALUES (?, ?, ?)")
    .bind(now(), "bootstrap_seed", "admin password hash seeded from secret")
    .run();
  return seed;
}

async function issueSession(
  env: Env,
  label: string | null
): Promise<{ access: string; refresh: string; accessExpiresAt: number; refreshExpiresAt: number }> {
  const access = newToken();
  const refresh = newToken();
  const createdAt = now();
  const accessTtl = int(env.ACCESS_TOKEN_TTL, 900);
  const refreshTtl = int(env.REFRESH_TOKEN_TTL, 604800);

  await env.DB
    .prepare(
      `INSERT INTO sessions
       (id, token_hash, refresh_hash, created_at, access_expires_at, refresh_expires_at, client_label)
       VALUES (?, ?, ?, ?, ?, ?, ?)`
    )
    .bind(
      crypto.randomUUID(),
      await sha256Hex(access),
      await sha256Hex(refresh),
      createdAt,
      createdAt + accessTtl,
      createdAt + refreshTtl,
      label
    )
    .run();

  return {
    access,
    refresh,
    accessExpiresAt: createdAt + accessTtl,
    refreshExpiresAt: createdAt + refreshTtl,
  };
}

function sessionPayload(s: { access: string; refresh: string; accessExpiresAt: number; refreshExpiresAt: number }) {
  return {
    access_token: s.access,
    refresh_token: s.refresh,
    access_expires_at: new Date(s.accessExpiresAt * 1000).toISOString(),
    refresh_expires_at: new Date(s.refreshExpiresAt * 1000).toISOString(),
  };
}

/**
 * Administrator operations.
 *
 * A session alone is NOT sufficient â€” every computer holding the shared password would
 * otherwise be an implicit admin and could silently rotate or revoke credentials for
 * everyone else. These endpoints additionally require the CURRENT shared password to be
 * proved (step-up). "Administrator" therefore means: holds a valid session AND still
 * knows the current shared password. After a rotation, a machine that was not updated
 * cannot perform admin operations even with a live session.
 *
 * Admin routes are also rate limited, because they are the most valuable targets for
 * an attacker who has stolen a session token.
 *
 * The proof is read from the `X-Admin-Password` header, or from `current_password` in the
 * body. `password` is reserved for the NEW value, so the two never collide.
 */
async function proof(
  c: Context<{ Bindings: Env; Variables: Vars }>,
  body: { password?: unknown; current_password?: unknown }
): Promise<string> {
  const header = c.req.header("X-Admin-Password");
  if (header) return header;
  if (typeof body.current_password === "string") return body.current_password;
  return "";
}

async function requireAdmin(
  c: Context<{ Bindings: Env; Variables: Vars }>,
  providedPassword: string
) {
  const ip = c.req.header("CF-Connecting-IP") ?? "unknown";
  const allowed = await checkRateLimit(c.env.DB, "admin", ip, 60, 20);
  if (!allowed) return { ok: false as const, response: jsonError(c, 429, "rate_limited", "Too many admin attempts.") };

  const session = await requireSession(c);
  if (!session.ok) return { ok: false as const, response: session.response };

  if (providedPassword.length === 0)
    return { ok: false as const, response: jsonError(c, 403, "admin_confirmation_required", "Administrator operation requires the current shared password.") };

  const storedHash = await getSetting(c.env.DB, SETTING_ADMIN_PASSWORD_HASH);
  if (!storedHash) return { ok: false as const, response: jsonError(c, 503, "not_configured", "Server has no access password configured.") };

  const verified = await verifyPassword(providedPassword, storedHash);
  if (!verified) {
    await c.env.DB.prepare("INSERT INTO audit_log (at, event, detail) VALUES (?, ?, ?)")
      .bind(now(), "admin_denied", null)
      .run();
    return { ok: false as const, response: jsonError(c, 403, "forbidden", "Shared password did not match.") };
  }

  return { ok: true as const, session: session.session, db: c.env.DB };
}

/* ------------------------------------------------------------------- routes */

app.get("/health", (c) => c.json({ ok: true }));

app.post("/api/auth/login", async (c) => {
  let body: { password?: unknown };
  try {
    body = await c.req.json<{ password?: unknown }>();
  } catch {
    return jsonError(c, 400, "invalid_request", "Body must be JSON.");
  }

  const password = typeof body.password === "string" ? body.password : "";
  if (password.length === 0 || password.length > 256)
    return jsonError(c, 400, "invalid_request", "Password is required (max 256 chars).");

  const ip = c.req.header("CF-Connecting-IP") ?? "unknown";
  const allowed = await checkRateLimit(
    c.env.DB,
    "login",
    ip,
    int(c.env.RATE_WINDOW, 60),
    int(c.env.RATE_MAX_LOGIN, 10)
  );
  if (!allowed) return jsonError(c, 429, "rate_limited", "Too many attempts. Try again later.");

  const storedHash = await ensureConfigured(c.env);
  if (!storedHash)
    return jsonError(c, 503, "not_configured", "Server has no access password configured yet.");

  const ok = await verifyPassword(password, storedHash);
  if (!ok) {
    await c.env.DB.prepare("INSERT INTO audit_log (at, event, detail) VALUES (?, ?, ?)")
      .bind(now(), "login_failed", null)
      .run();
    return jsonError(c, 401, "invalid_credentials", "Invalid access password.");
  }

  const label = (c.req.header("X-Client-Label") ?? "").slice(0, 64) || null;
  const session = await issueSession(c.env, label);
  await c.env.DB.prepare("INSERT INTO audit_log (at, event, detail) VALUES (?, ?, ?)")
    .bind(now(), "login_ok", null)
    .run();

  return c.json({ session: sessionPayload(session) });
});

app.post("/api/auth/refresh", async (c) => {
  let body: { refresh_token?: unknown };
  try {
    body = await c.req.json<{ refresh_token?: unknown }>();
  } catch {
    return jsonError(c, 400, "invalid_request", "Body must be JSON.");
  }

  // Rate limited: refresh is the endpoint an attacker would replay a stolen token at.
  const ip = c.req.header("CF-Connecting-IP") ?? "unknown";
  const allowed = await checkRateLimit(c.env.DB, "refresh", ip, int(c.env.RATE_WINDOW, 60), 30);
  if (!allowed) return jsonError(c, 429, "rate_limited", "Too many attempts. Try again later.");

  const raw = typeof body.refresh_token === "string" ? body.refresh_token : "";
  if (!raw) return jsonError(c, 401, "unauthorized", "Refresh token required.");

  const hash = await sha256Hex(raw);
  const session = await c.env.DB
    .prepare("SELECT * FROM sessions WHERE refresh_hash = ? AND revoked_at IS NULL")
    .bind(hash)
    .first<SessionRow>();

  if (!session) return jsonError(c, 401, "unauthorized", "Unknown or revoked session.");
  if (session.refresh_expires_at <= now()) return jsonError(c, 401, "expired", "Refresh token expired.");

  // Rotate both tokens on every refresh: a stolen refresh token is single-use.
  const access = newToken();
  const refresh = newToken();
  const createdAt = now();
  const accessTtl = int(c.env.ACCESS_TOKEN_TTL, 900);
  const refreshTtl = int(c.env.REFRESH_TOKEN_TTL, 604800);

  await c.env.DB
    .prepare(
      `UPDATE sessions SET token_hash = ?, refresh_hash = ?, access_expires_at = ?,
       refresh_expires_at = ?, last_seen_at = ? WHERE id = ?`
    )
    .bind(
      await sha256Hex(access),
      await sha256Hex(refresh),
      createdAt + accessTtl,
      createdAt + refreshTtl,
      createdAt,
      session.id
    )
    .run();

  return c.json({
    session: sessionPayload({
      access,
      refresh,
      accessExpiresAt: createdAt + accessTtl,
      refreshExpiresAt: createdAt + refreshTtl,
    }),
  });
});

app.post("/api/auth/logout", async (c) => {
  const token = bearer(c);
  if (!token) return jsonError(c, 401, "unauthorized", "Missing bearer token.");

  const hash = await sha256Hex(token);
  await c.env.DB
    .prepare("UPDATE sessions SET revoked_at = ? WHERE token_hash = ? AND revoked_at IS NULL")
    .bind(now(), hash)
    .run();

  return c.json({ ok: true });
});

app.get("/api/auth/validate", async (c) => {
  // Rate limited: this endpoint is unauthenticated-facing (rejects forged tokens) and
  // performs a D1 lookup, so it is the cheapest way to burn request quota.
  const ip = c.req.header("CF-Connecting-IP") ?? "unknown";
  const allowed = await checkRateLimit(c.env.DB, "validate", ip, int(c.env.RATE_WINDOW, 60), 60);
  if (!allowed) return jsonError(c, 429, "rate_limited", "Too many attempts. Try again later.");

  const result = await requireSession(c);
  if (!result.ok) return result.response;

  // Return fresh token material so the client can keep a rolling session without
  // re-prompting the user. Reuses the existing row (no new session).
  const access = newToken();
  const createdAt = now();
  const accessTtl = int(c.env.ACCESS_TOKEN_TTL, 900);

  await c.env.DB
    .prepare("UPDATE sessions SET token_hash = ?, access_expires_at = ?, last_seen_at = ? WHERE id = ?")
    .bind(await sha256Hex(access), createdAt + accessTtl, createdAt, result.session.id)
    .run();

  return c.json({
    ok: true,
    session: {
      access_token: access,
      // The refresh token is not re-issued here; it stays valid until its own expiry.
      refresh_token: "",
      access_expires_at: new Date((createdAt + accessTtl) * 1000).toISOString(),
      refresh_expires_at: new Date(result.session.refresh_expires_at * 1000).toISOString(),
    },
  });
});

/* -------------------------------------------------------------------- admin */

/* -------------------------------------------------------------------- admin */

app.get("/api/admin/status", async (c) => {
  const result = await requireAdmin(c, c.req.header("X-Admin-Password") ?? "");
  if (!result.ok) return result.response;

  const configured = (await getSetting(c.env.DB, SETTING_ADMIN_PASSWORD_HASH)) !== null;
  const sessions = await c.env.DB
    .prepare("SELECT COUNT(*) AS n FROM sessions WHERE revoked_at IS NULL AND refresh_expires_at > ?")
    .bind(now())
    .first<{ n: number }>();

  return c.json({
    configured,
    active_sessions: sessions?.n ?? 0,
  });
});

app.post("/api/admin/change-password", async (c) => {
  let body: { password?: unknown; current_password?: unknown };
  try {
    body = await c.req.json<{ password?: unknown; current_password?: unknown }>();
  } catch {
    return jsonError(c, 400, "invalid_request", "Body must be JSON.");
  }

  // Prove the caller still knows the CURRENT shared password before accepting a change.
  const admin = await requireAdmin(c, await proof(c, body));
  if (!admin.ok) return admin.response;

  const password = typeof body.password === "string" ? body.password : "";
  if (password.length < 8 || password.length > 256)
    return jsonError(c, 400, "invalid_request", "Password must be 8-256 characters.");

  const hash = await hashPassword(password);
  await setSetting(c.env.DB, SETTING_ADMIN_PASSWORD_HASH, hash);

  // Rotating the shared password invalidates every other computer's session.
  await c.env.DB
    .prepare("UPDATE sessions SET revoked_at = ? WHERE revoked_at IS NULL AND id != ?")
    .bind(now(), admin.session.id)
    .run();

  await c.env.DB.prepare("INSERT INTO audit_log (at, event, detail) VALUES (?, ?, ?)")
    .bind(now(), "password_changed", "all other sessions revoked")
    .run();

  return c.json({ ok: true, revoked_others: true });
});

app.post("/api/admin/revoke-access", async (c) => {
  let body: { current_password?: unknown };
  try {
    body = await c.req.json<{ current_password?: unknown }>();
  } catch {
    return jsonError(c, 400, "invalid_request", "Body must be JSON.");
  }

  const admin = await requireAdmin(c, await proof(c, body));
  if (!admin.ok) return admin.response;

  // Revoke every session except the caller's, so the admin is not locked out mid-request.
  await c.env.DB
    .prepare("UPDATE sessions SET revoked_at = ? WHERE revoked_at IS NULL AND id != ?")
    .bind(now(), admin.session.id)
    .run();

  await c.env.DB.prepare("INSERT INTO audit_log (at, event, detail) VALUES (?, ?, ?)")
    .bind(now(), "revoke_all", "all other sessions revoked")
    .run();

  return c.json({ ok: true });
});

/* -------------------------------------------------------------------- errors */

app.notFound((c) => jsonError(c, 404, "not_found", "No such endpoint."));

app.onError((err, c) => {
  // Never leak stack traces or DB errors to the client.
  console.error("unhandled", err instanceof Error ? err.message : "unknown");
  return jsonError(c, 500, "server_error", "Internal error.");
});

export default {
  fetch: app.fetch,
  /**
   * Scheduled handler for the retention job configured in wrangler.jsonc:
   *   crons = ["0 3 * * *"]
   * This is the only place data is deleted without an explicit user action.
   */
  scheduled: async (_event: ScheduledController, env: Env, _ctx: ExecutionContext) => {
    await prune(env.DB);
  },
};

/** Tests drive the Hono app directly via app.request(). */
export { app };