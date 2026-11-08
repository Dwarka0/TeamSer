/**
 * Data retention.
 *
 * Every table here grows over time and none of them has a natural end:
 *   sessions  - old revoked/expired rows are never referenced again
 *   audit_log - append-only by design
 *   rate_limits - keyed by window, so rows age out immediately
 * Left alone, D1 storage is consumed indefinitely. `prune` removes rows that can no
 * longer affect any decision. Called from the Worker's scheduled trigger.
 */

/** How long a revoked/expired session is kept as an audit breadcrumb (days). */
const SESSION_RETENTION_DAYS = 30;
/** Audit log retention (days). Keep generous; entries are tiny and non-secret. */
const AUDIT_RETENTION_DAYS = 90;

export async function prune(db: D1Database): Promise<void> {
  const now = () => Math.floor(Date.now() / 1000);
  const nowSeconds = now();
  const sessionCutoff = nowSeconds - SESSION_RETENTION_DAYS * 86400;
  const auditCutoff = nowSeconds - AUDIT_RETENTION_DAYS * 86400;

  // Sessions that are revoked, or whose refresh token already expired long enough ago.
  await db
    .prepare(
      "DELETE FROM sessions WHERE (revoked_at IS NOT NULL AND revoked_at < ?) OR refresh_expires_at < ?"
    )
    .bind(sessionCutoff, sessionCutoff)
    .run();

  await db.prepare("DELETE FROM audit_log WHERE at < ?").bind(auditCutoff).run();

  await db.prepare("DELETE FROM rate_limits WHERE window_start < ?").bind(nowSeconds).run();
}
