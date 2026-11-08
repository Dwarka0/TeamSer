import { beforeEach, describe, expect, it } from "vitest";
import { app } from "../src/index";
import { FakeD1 } from "./FakeD1";
import { hashPassword, sha256Hex } from "../src/services/password";

const env = (overrides: Record<string, unknown> = {}) => ({
  DB: new FakeD1() as unknown as D1Database,
  ACCESS_TOKEN_TTL: "900",
  REFRESH_TOKEN_TTL: "604800",
  ALLOWED_ORIGINS: "https://allowed.example",
  RATE_WINDOW: "60",
  RATE_MAX_LOGIN: "10",
  ...overrides,
});

const json = (body: unknown) => ({
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});

async function loginWith(password: string, bindings: ReturnType<typeof env>) {
  return app.request("/api/auth/login", json({ password }), bindings);
}

describe("auth flow", () => {
  let bindings: ReturnType<typeof env>;

  beforeEach(async () => {
    bindings = env({ ADMIN_PASSWORD_HASH: await hashPassword("shared-pass-123") });
  });

  it("health is public", async () => {
    const res = await app.request("/health", {}, bindings);
    expect(res.status).toBe(200);
  });

  it("rejects a wrong password with 401", async () => {
    const res = await loginWith("nope", bindings);
    expect(res.status).toBe(401);
    const body = (await res.json()) as { error: string };
    expect(body.error).toBe("invalid_credentials");
  });

  it("accepts the right password and returns a session", async () => {
    const res = await loginWith("shared-pass-123", bindings);
    expect(res.status).toBe(200);
    const body = (await res.json()) as {
      session: { access_token: string; refresh_token: string; access_expires_at: string; refresh_expires_at: string };
    };
    expect(body.session.access_token.length).toBeGreaterThan(20);
    expect(body.session.refresh_token.length).toBeGreaterThan(20);
    expect(new Date(body.session.access_expires_at).getTime()).toBeGreaterThan(Date.now());
  });

  it("never returns the password hash", async () => {
    const res = await loginWith("shared-pass-123", bindings);
    const text = await res.text();
    expect(text).not.toContain("$argon2");
    expect(text).not.toContain("shared-pass-123");
  });

  it("two computers log in independently", async () => {
    const a = (await (await loginWith("shared-pass-123", bindings)).json()) as { session: { access_token: string } };
    const b = (await (await loginWith("shared-pass-123", bindings)).json()) as { session: { access_token: string } };
    expect(a.session.access_token).not.toBe(b.session.access_token);
  });

  it("validate accepts a fresh token", async () => {
    const login = (await (await loginWith("shared-pass-123", bindings)).json()) as {
      session: { access_token: string };
    };
    const res = await app.request(
      "/api/auth/validate",
      { headers: { Authorization: `Bearer ${login.session.access_token}` } },
      bindings
    );
    expect(res.status).toBe(200);
  });

  it("validate rejects a missing token", async () => {
    const res = await app.request("/api/auth/validate", {}, bindings);
    expect(res.status).toBe(401);
  });

  it("validate rejects a forged token", async () => {
    const res = await app.request(
      "/api/auth/validate",
      { headers: { Authorization: "Bearer forged-token" } },
      bindings
    );
    expect(res.status).toBe(401);
  });

  it("logout revokes the session", async () => {
    const login = (await (await loginWith("shared-pass-123", bindings)).json()) as {
      session: { access_token: string };
    };
    const auth = { Authorization: `Bearer ${login.session.access_token}` };

    const out = await app.request("/api/auth/logout", { method: "POST", headers: auth }, bindings);
    expect(out.status).toBe(200);

    const after = await app.request("/api/auth/validate", { headers: auth }, bindings);
    expect(after.status).toBe(401);
  });

  it("refresh rotates both tokens and invalidates the old refresh token", async () => {
    const login = (await (await loginWith("shared-pass-123", bindings)).json()) as {
      session: { access_token: string; refresh_token: string };
    };

    const refreshed = await app.request(
      "/api/auth/refresh",
      json({ refresh_token: login.session.refresh_token }),
      bindings
    );
    expect(refreshed.status).toBe(200);

    const replay = await app.request(
      "/api/auth/refresh",
      json({ refresh_token: login.session.refresh_token }),
      bindings
    );
    expect(replay.status).toBe(401);
  });

  it("expired access token is rejected", async () => {
    // TTL of 0 is coerced to the default; instead seed a session that is already expired.
    const db = bindings.DB as unknown as FakeD1;
    const token = "expired-token-value";
    db.table("sessions").push({
      id: "s1",
      token_hash: await sha256Hex(token),
      refresh_hash: await sha256Hex("r"),
      created_at: 1,
      access_expires_at: 2,
      refresh_expires_at: 2,
      revoked_at: null,
      last_seen_at: null,
      client_label: null,
    });

    const res = await app.request(
      "/api/auth/validate",
      { headers: { Authorization: `Bearer ${token}` } },
      bindings
    );
    expect(res.status).toBe(401);
    const body = (await res.json()) as { error: string };
    expect(body.error).toBe("expired");
  });

  it("expired refresh token is rejected", async () => {
    const login = (await (await loginWith("shared-pass-123", bindings)).json()) as {
      session: { refresh_token: string };
    };
    const db = bindings.DB as unknown as FakeD1;
    for (const row of db.table("sessions")) row.refresh_expires_at = 1;

    const res = await app.request(
      "/api/auth/refresh",
      json({ refresh_token: login.session.refresh_token }),
      bindings
    );
    expect(res.status).toBe(401);
  });
});

describe("admin flow", () => {
  let bindings: ReturnType<typeof env>;

  beforeEach(async () => {
    bindings = env({ ADMIN_PASSWORD_HASH: await hashPassword("shared-pass-123") });
  });

  async function login(): Promise<string> {
    const res = await loginWith("shared-pass-123", bindings);
    const body = (await res.json()) as { session: { access_token: string } };
    return body.session.access_token;
  }

  it("status requires the current shared password", async () => {
    const token = await login();

    // Session alone is NOT enough: it must not be an implicit admin credential.
    const bare = await app.request(
      "/api/admin/status",
      { headers: { Authorization: `Bearer ${token}` } },
      bindings
    );
    expect(bare.status).toBe(403);

    // With the current password it works.
    const ok = await app.request(
      "/api/admin/status",
      { headers: { Authorization: `Bearer ${token}`, "X-Admin-Password": "shared-pass-123" } },
      bindings
    );
    expect(ok.status).toBe(200);
    const body = (await ok.json()) as { configured: boolean; active_sessions: number };
    expect(body.configured).toBe(true);
    expect(body.active_sessions).toBe(1);
  });

  it("status reports configuration and session count", async () => {
    const token = await login();
    const res = await app.request(
      "/api/admin/status",
      { headers: { Authorization: `Bearer ${token}`, "X-Admin-Password": "shared-pass-123" } },
      bindings
    );
    expect(res.status).toBe(200);
    const body = (await res.json()) as { configured: boolean; active_sessions: number };
    expect(body.configured).toBe(true);
    expect(body.active_sessions).toBe(1);
  });

  it("change-password rejects a session without password proof", async () => {
    const token = await login();
    const res = await app.request(
      "/api/admin/change-password",
      {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({ password: "brand-new-pass-456" }),
      },
      bindings
    );
    expect(res.status).toBe(403);
    // The password must not have been rotated by an unconfirmed request.
    expect((await loginWith("shared-pass-123", bindings)).status).toBe(200);
  });

  it("change-password rejects a wrong password proof", async () => {
    const token = await login();
    const res = await app.request(
      "/api/admin/change-password",
      {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({ password: "brand-new-pass-456", current_password: "not-the-password" }),
      },
      bindings
    );
    expect(res.status).toBe(403);
    expect((await loginWith("shared-pass-123", bindings)).status).toBe(200);
  });

  it("revoke-access rejects a session without password proof", async () => {
    const other = await login();
    const res = await app.request(
      "/api/admin/revoke-access",
      {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${other}` },
        body: JSON.stringify({}),
      },
      bindings
    );
    expect(res.status).toBe(403);
    // Other sessions must survive.
    const check = await app.request(
      "/api/auth/validate",
      { headers: { Authorization: `Bearer ${other}` } },
      bindings
    );
    expect(check.status).toBe(200);
  });

  it("change-password rotates the hash and revokes other sessions", async () => {
    const admin = await login();
    const other = await login();

    const res = await app.request(
      "/api/admin/change-password",
      {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${admin}` },
        body: JSON.stringify({ password: "brand-new-pass-456", current_password: "shared-pass-123" }),
      },
      bindings
    );
    expect(res.status).toBe(200);

    // old shared password no longer works
    expect((await loginWith("shared-pass-123", bindings)).status).toBe(401);
    // new one does
    expect((await loginWith("brand-new-pass-456", bindings)).status).toBe(200);

    // the other computer's session was revoked
    const otherRes = await app.request(
      "/api/auth/validate",
      { headers: { Authorization: `Bearer ${other}` } },
      bindings
    );
    expect(otherRes.status).toBe(401);

    // the admin keeps working
    const adminRes = await app.request(
      "/api/auth/validate",
      { headers: { Authorization: `Bearer ${admin}` } },
      bindings
    );
    expect(adminRes.status).toBe(200);
  });

  it("change-password rejects too-short passwords", async () => {
    const admin = await login();
    const res = await app.request(
      "/api/admin/change-password",
      {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${admin}` },
        body: JSON.stringify({ password: "short", current_password: "shared-pass-123" }),
      },
      bindings
    );
    expect(res.status).toBe(400);
    // The current password is still valid — only the NEW value was rejected.
    expect((await loginWith("shared-pass-123", bindings)).status).toBe(200);
  });

  it("revoke-access kills every other session", async () => {
    const admin = await login();
    const a = await login();
    const b = await login();

    const res = await app.request(
      "/api/admin/revoke-access",
      {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${admin}` },
        body: JSON.stringify({ current_password: "shared-pass-123" }),
      },
      bindings
    );
    expect(res.status).toBe(200);

    for (const token of [a, b]) {
      const check = await app.request(
        "/api/auth/validate",
        { headers: { Authorization: `Bearer ${token}` } },
        bindings
      );
      expect(check.status).toBe(401);
    }
  });

  it("admin endpoints require a session", async () => {
    for (const path of ["/api/admin/status", "/api/admin/revoke-access", "/api/admin/change-password"]) {
      const method = path.endsWith("status") ? "GET" : "POST";
      // Even WITH the correct proof, an unauthenticated caller gets 401: the session
      // check runs before the step-up proof check.
      const res = await app.request(
        path,
        {
          method,
          headers: method === "GET" ? { "X-Admin-Password": "shared-pass-123" } : {
            "Content-Type": "application/json",
            "X-Admin-Password": "shared-pass-123",
          },
          body: method === "POST" ? JSON.stringify({}) : undefined,
        },
        bindings
      );
      expect(res.status).toBe(401);
    }
  });
});

describe("bootstrap", () => {
  it("reports not_configured when no hash exists and no secret is set", async () => {
    const bindings = env();
    const res = await loginWith("anything", bindings);
    expect(res.status).toBe(503);
  });

  it("rejects a secret that is not an argon2 hash", async () => {
    const bindings = env({ ADMIN_PASSWORD_HASH: "plaintextsecret" });
    const res = await loginWith("plaintextsecret", bindings);
    expect(res.status).toBe(503);
  });
});

describe("input validation and limits", () => {
  it("rejects a non-JSON body", async () => {
    const bindings = env();
    const res = await app.request(
      "/api/auth/login",
      { method: "POST", headers: { "Content-Type": "application/json" }, body: "not json" },
      bindings
    );
    expect(res.status).toBe(400);
  });

  it("rejects an empty password", async () => {
    const bindings = env();
    const res = await loginWith("", bindings);
    expect(res.status).toBe(400);
  });

  it("rejects an over-long password", async () => {
    const bindings = env();
    const res = await loginWith("x".repeat(300), bindings);
    expect(res.status).toBe(400);
  });

  it("rate limits repeated failures", async () => {
    const bindings = env({
      ADMIN_PASSWORD_HASH: await hashPassword("shared-pass-123"),
      RATE_MAX_LOGIN: "2",
    });

    const attempt = () =>
      app.request(
        "/api/auth/login",
        json({ password: "wrong" }),
        bindings,
        // one stable client IP for the whole test
      );

    // CF-Connecting-IP is absent -> "unknown"; keep it stable and hit the same bucket.
    await attempt();
    await attempt();
    const third = await attempt();
    expect(third.status).toBe(429);
  });

  it("rate limit message does not reveal whether the password was right", async () => {
    const bindings = env({
      ADMIN_PASSWORD_HASH: await hashPassword("shared-pass-123"),
      RATE_MAX_LOGIN: "1",
    });
    await app.request("/api/auth/login", json({ password: "wrong" }), bindings);
    const blocked = await app.request("/api/auth/login", json({ password: "shared-pass-123" }), bindings);
    expect(blocked.status).toBe(429);
  });
});

describe("cors", () => {
  it("echoes an allowlisted origin", async () => {
    const bindings = env();
    const res = await app.request(
      "/health",
      { headers: { Origin: "https://allowed.example" } },
      bindings
    );
    expect(res.headers.get("Access-Control-Allow-Origin")).toBe("https://allowed.example");
  });

  it("does not echo a foreign origin", async () => {
    const bindings = env();
    const res = await app.request("/health", { headers: { Origin: "https://evil.example" } }, bindings);
    expect(res.headers.get("Access-Control-Allow-Origin")).toBeNull();
  });

  it("rejects preflight from a foreign origin", async () => {
    const bindings = env();
    const res = await app.request(
      "/api/auth/login",
      { method: "OPTIONS", headers: { Origin: "https://evil.example" } },
      bindings
    );
    expect(res.status).toBe(403);
  });

  it("allows preflight from an allowlisted origin", async () => {
    const bindings = env();
    const res = await app.request(
      "/api/auth/login",
      { method: "OPTIONS", headers: { Origin: "https://allowed.example" } },
      bindings
    );
    expect(res.status).toBe(204);
  });
});

describe("misc", () => {
  it("unknown route is a clean 404, not a stack trace", async () => {
    const res = await app.request("/api/does-not-exist", {}, env());
    expect(res.status).toBe(404);
    const text = await res.text();
    expect(text).not.toContain("at ");
  });

  it("sets no-store and nosniff on responses", async () => {
    const res = await app.request("/health", {}, env());
    expect(res.headers.get("Cache-Control")).toBe("no-store");
    expect(res.headers.get("X-Content-Type-Options")).toBe("nosniff");
  });
});
