-- 0001_init.sql
-- Access-management schema only. No audio, no server control, no arbitrary commands.
-- All secrets are stored as hashes; nothing recoverable is persisted.

PRAGMA foreign_keys = ON;

-- Single-row-ish settings table. key/value keeps migrations cheap.
CREATE TABLE IF NOT EXISTS settings (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

-- Shared access password. One active row at a time; revoked rows are kept for audit.
CREATE TABLE IF NOT EXISTS access_passwords (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  password_hash TEXT NOT NULL,
  created_at   INTEGER NOT NULL,
  revoked_at   INTEGER
);

-- Sessions issued to individual computers. Token stored as SHA-256 hash only.
CREATE TABLE IF NOT EXISTS sessions (
  id            TEXT PRIMARY KEY,              -- random session id
  token_hash    TEXT NOT NULL UNIQUE,          -- sha256(access token)
  refresh_hash  TEXT NOT NULL UNIQUE,          -- sha256(refresh token)
  created_at    INTEGER NOT NULL,
  access_expires_at  INTEGER NOT NULL,
  refresh_expires_at INTEGER NOT NULL,
  revoked_at    INTEGER,
  last_seen_at  INTEGER,
  client_label  TEXT                           -- non-identifying label, optional
);

CREATE INDEX IF NOT EXISTS idx_sessions_token ON sessions(token_hash);
CREATE INDEX IF NOT EXISTS idx_sessions_refresh ON sessions(refresh_hash);
CREATE INDEX IF NOT EXISTS idx_sessions_revoked ON sessions(revoked_at);

-- Rate-limit table. keyed by a hashed IP + bucket so raw IPs are not stored.
CREATE TABLE IF NOT EXISTS rate_limits (
  bucket     TEXT NOT NULL,                    -- e.g. "login:<hash>"
  window_start INTEGER NOT NULL,
  count      INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (bucket, window_start)
);

CREATE INDEX IF NOT EXISTS idx_rate_window ON rate_limits(window_start);

-- Audit trail for security-relevant events. Never contains secrets.
CREATE TABLE IF NOT EXISTS audit_log (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  at        INTEGER NOT NULL,
  event     TEXT NOT NULL,
  detail    TEXT
);

CREATE INDEX IF NOT EXISTS idx_audit_at ON audit_log(at);
