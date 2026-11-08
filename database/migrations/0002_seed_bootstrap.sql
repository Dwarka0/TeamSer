-- 0002_seed_bootstrap.sql
-- First-time administrator setup.
--
-- The hash of the initial shared password is produced by scripts/gen-password-hash.mjs and
-- supplied as the ADMIN_PASSWORD_HASH secret. No plaintext password is stored here or anywhere.
--
-- The bootstrap session is created by the Worker on first POST /api/auth/login once the
-- settings table has been seeded by the deploy script.

-- Placeholder row so the migrate step proves the settings table exists and is writable.
-- Session tokens and password hashes are written by the Worker, not by migration SQL.
INSERT OR IGNORE INTO settings (key, value)
VALUES ('schema_version', '2');
