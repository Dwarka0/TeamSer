import { describe, expect, it } from "vitest";
import { hashPassword, newToken, safeEqual, sha256Hex, verifyPassword } from "../src/services/password";
import { FakeD1 } from "./FakeD1";

describe("password hashing", () => {
  it("produces a PHC argon2id string, never the plaintext", async () => {
    const hash = await hashPassword("correct horse battery staple");
    expect(hash.startsWith("$argon2id$")).toBe(true);
    expect(hash).not.toContain("correct horse battery staple");
  });

  it("verifies the right password", async () => {
    const hash = await hashPassword("s3cret-access");
    expect(await verifyPassword("s3cret-access", hash)).toBe(true);
  });

  it("rejects the wrong password", async () => {
    const hash = await hashPassword("s3cret-access");
    expect(await verifyPassword("s3cret-acces", hash)).toBe(false);
    expect(await verifyPassword("", hash)).toBe(false);
  });

  it("salts, so identical passwords hash differently", async () => {
    const [a, b] = await Promise.all([hashPassword("same"), hashPassword("same")]);
    expect(a).not.toBe(b);
  });

  it("rejects a non-argon2 or malformed stored value without throwing", async () => {
    expect(await verifyPassword("x", "plaintext")).toBe(false);
    expect(await verifyPassword("x", "$argon2id$garbage")).toBe(false);
    expect(await verifyPassword("x", "")).toBe(false);
  });

  it("accepts unicode passwords", async () => {
    const hash = await hashPassword("رمز-عبور-۱۲۳");
    expect(await verifyPassword("رمز-عبور-۱۲۳", hash)).toBe(true);
  });
});

describe("tokens", () => {
  it("are long, unique and url-safe", () => {
    const tokens = new Set(Array.from({ length: 200 }, () => newToken()));
    expect(tokens.size).toBe(200);
    for (const t of tokens) {
      expect(t.length).toBeGreaterThanOrEqual(40);
      expect(t).toMatch(/^[A-Za-z0-9_-]+$/);
    }
  });

  it("hash deterministically and never equal the token", async () => {
    const token = newToken();
    expect(await sha256Hex(token)).toBe(await sha256Hex(token));
    expect(await sha256Hex(token)).not.toBe(token);
    expect((await sha256Hex(token)).length).toBe(64);
  });
});

describe("safeEqual", () => {
  it("compares equal and unequal strings", () => {
    expect(safeEqual("abc", "abc")).toBe(true);
    expect(safeEqual("abc", "abd")).toBe(false);
    expect(safeEqual("abc", "abcd")).toBe(false);
    expect(safeEqual("", "")).toBe(true);
  });
});

describe("rate limiter", () => {
  it("allows up to the limit then blocks", async () => {
    const { checkRateLimit } = await import("../src/services/ratelimit");
    const db = new FakeD1() as unknown as D1Database;

    for (let i = 0; i < 3; i++) {
      expect(await checkRateLimit(db, "login", "1.2.3.4", 60, 3)).toBe(true);
    }
    expect(await checkRateLimit(db, "login", "1.2.3.4", 60, 3)).toBe(false);
  });

  it("isolates buckets per identifier and per scope", async () => {
    const { checkRateLimit } = await import("../src/services/ratelimit");
    const db = new FakeD1() as unknown as D1Database;

    expect(await checkRateLimit(db, "login", "10.0.0.1", 60, 1)).toBe(true);
    expect(await checkRateLimit(db, "login", "10.0.0.1", 60, 1)).toBe(false);
    // different IP
    expect(await checkRateLimit(db, "login", "10.0.0.2", 60, 1)).toBe(true);
    // different scope
    expect(await checkRateLimit(db, "admin", "10.0.0.1", 60, 1)).toBe(true);
  });

  it("never stores the raw identifier", async () => {
    const { checkRateLimit } = await import("../src/services/ratelimit");
    const fake = new FakeD1();
    await checkRateLimit(fake as unknown as D1Database, "login", "203.0.113.9", 60, 10);
    const buckets = fake.table("rate_limits").map((r) => String(r.bucket));
    expect(buckets.length).toBe(1);
    expect(buckets[0]).not.toContain("203.0.113.9");
  });
});
