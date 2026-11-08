import { sha256Hex } from "./password";

/**
 * Sliding-window rate limiter backed by D1.
 *
 * Keyed by "<scope>:<sha256(identifier)>": the raw IP or password is never written to
 * D1, so a leaked database does not reveal caller identities.
 *
 * Returns false when the caller has exhausted this window's budget.
 */
export async function checkRateLimit(
  db: D1Database,
  scope: string,
  identifier: string,
  windowSeconds: number,
  maxAttempts: number
): Promise<boolean> {
  const key = `${scope}:${await sha256Hex(identifier)}`;
  const now = Math.floor(Date.now() / 1000);
  const window = Math.max(1, windowSeconds);
  const windowStart = now - (now % window);

  const existing = await db
    .prepare("SELECT count FROM rate_limits WHERE bucket = ? AND window_start = ?")
    .bind(key, windowStart)
    .first<{ count: number }>();

  if (existing && existing.count >= maxAttempts) return false;

  await db
    .prepare(
      `INSERT INTO rate_limits (bucket, window_start, count) VALUES (?, ?, 1)
       ON CONFLICT (bucket, window_start) DO UPDATE SET count = count + 1`
    )
    .bind(key, windowStart)
    .run();

  return true;
}

/** Remove buckets older than the retention horizon, so the table stays bounded. */
export async function pruneRateLimits(db: D1Database, beforeUnixSeconds: number): Promise<void> {
  await db.prepare("DELETE FROM rate_limits WHERE window_start < ?").bind(beforeUnixSeconds).run();
}
