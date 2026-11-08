export interface Env {
  DB: D1Database;
  ACCESS_TOKEN_TTL: string;
  REFRESH_TOKEN_TTL: string;
  ALLOWED_ORIGINS: string;
  RATE_WINDOW: string;
  RATE_MAX_LOGIN: string;
  /** Wrangler secret. Argon2id PHC string from scripts/gen-password-hash.mjs. */
  ADMIN_PASSWORD_HASH?: string;
}

/** Settings keys written by the bootstrap step, never derived from user input. */
export const SETTING_ADMIN_PASSWORD_HASH = "admin_password_hash";
export const SETTING_BOOTSTRAP_COMPLETE = "bootstrap_complete";

export interface SessionRow {
  id: string;
  token_hash: string;
  refresh_hash: string;
  created_at: number;
  access_expires_at: number;
  refresh_expires_at: number;
  revoked_at: number | null;
  last_seen_at: number | null;
  client_label: string | null;
}
