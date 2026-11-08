/**
 * Minimal in-memory D1 stand-in.
 *
 * It implements exactly the statement shapes the Worker issues, so the real routes can be
 * exercised end-to-end without a live database. Any statement outside the recognised
 * shapes throws loudly rather than silently no-op'ing, which keeps this honest as a test
 * double and prevents a refactor from silently changing behaviour.
 */
export type Row = Record<string, unknown>;

export class FakeD1 {
  private tables = new Map<string, Row[]>();

  constructor() {
    this.tables.set("settings", []);
    this.tables.set("sessions", []);
    this.tables.set("rate_limits", []);
    this.tables.set("audit_log", []);
  }

  prepare(sql: string) {
    return new FakeStatement(this, sql);
  }

  private get(name: string): Row[] {
    const rows = this.tables.get(name);
    if (!rows) throw new Error(`unknown table: ${name}`);
    return rows;
  }

  run(sql: string, params: unknown[]): void {
    const query = sql.replace(/\s+/g, " ").trim();

    if (/^INSERT INTO settings/i.test(query)) {
      const [key, value] = params as [string, string];
      const row = this.get("settings").find((r) => r.key === key);
      if (row) row.value = value;
      else this.get("settings").push({ key, value });
      return;
    }

    if (/^INSERT INTO sessions/i.test(query)) {
      const [id, token_hash, refresh_hash, created_at, access_expires_at, refresh_expires_at, client_label] =
        params as [string, string, string, number, number, number, string | null];
      this.get("sessions").push({
        id, token_hash, refresh_hash, created_at, access_expires_at, refresh_expires_at,
        client_label, revoked_at: null, last_seen_at: null,
      });
      return;
    }

    if (/^INSERT INTO rate_limits/i.test(query)) {
      const [bucket, window_start] = params as [string, number];
      const row = this.get("rate_limits").find((r) => r.bucket === bucket && r.window_start === window_start);
      if (row) row.count = Number(row.count) + 1;
      else this.get("rate_limits").push({ bucket, window_start, count: 1 });
      return;
    }

    if (/^INSERT INTO audit_log/i.test(query)) {
      this.get("audit_log").push({ at: params[0], event: params[1], detail: params[2] ?? null });
      return;
    }

    if (/^DELETE FROM rate_limits WHERE window_start < \?$/i.test(query)) {
      const cutoff = Number(params[0]);
      const rows = this.get("rate_limits");
      this.tables.set("rate_limits", rows.filter((r) => Number(r.window_start) >= cutoff));
      return;
    }

    throw new Error(`FakeD1 cannot execute: ${query}`);
  }

  select(sql: string, params: unknown[]): Row[] {
    const query = sql.replace(/\s+/g, " ").trim();

    if (/^SELECT count FROM rate_limits WHERE bucket = \? AND window_start = \?$/i.test(query)) {
      const [bucket, window_start] = params as [string, number];
      const row = this.get("rate_limits").find((r) => r.bucket === bucket && r.window_start === window_start);
      return row ? [{ count: row.count }] : [];
    }

    if (/^SELECT value FROM settings WHERE key = \?$/i.test(query)) {
      const row = this.get("settings").find((r) => r.key === params[0]);
      return row ? [{ value: row.value }] : [];
    }

    if (/^SELECT \* FROM sessions WHERE token_hash = \? AND revoked_at IS NULL$/i.test(query)) {
      const row = this.get("sessions").find((r) => r.token_hash === params[0] && r.revoked_at == null);
      return row ? [{ ...row }] : [];
    }

    if (/^SELECT \* FROM sessions WHERE refresh_hash = \? AND revoked_at IS NULL$/i.test(query)) {
      const row = this.get("sessions").find((r) => r.refresh_hash === params[0] && r.revoked_at == null);
      return row ? [{ ...row }] : [];
    }

    if (/^SELECT COUNT\(\*\) AS n FROM sessions WHERE revoked_at IS NULL AND refresh_expires_at > \?$/i.test(query)) {
      const cutoff = Number(params[0]);
      const n = this.get("sessions").filter((r) => r.revoked_at == null && Number(r.refresh_expires_at) > cutoff).length;
      return [{ n }];
    }

    throw new Error(`FakeD1 cannot execute: ${query}`);
  }

  update(sql: string, params: unknown[]): void {
    const query = sql.replace(/\s+/g, " ").trim();

    if (/^UPDATE sessions SET last_seen_at = \? WHERE id = \?$/i.test(query)) {
      const [last_seen_at, id] = params as [number, string];
      const row = this.get("sessions").find((r) => r.id === id);
      if (row) row.last_seen_at = last_seen_at;
      return;
    }

    if (/^UPDATE sessions SET token_hash = \?, refresh_hash = \?, access_expires_at = \?,\s*refresh_expires_at = \?,\s*last_seen_at = \? WHERE id = \?$/i.test(query)) {
      const [token_hash, refresh_hash, access_expires_at, refresh_expires_at, last_seen_at, id] = params as [string, string, number, number, number, string];
      const row = this.get("sessions").find((r) => r.id === id);
      if (row) Object.assign(row, { token_hash, refresh_hash, access_expires_at, refresh_expires_at, last_seen_at });
      return;
    }

    if (/^UPDATE sessions SET token_hash = \?, access_expires_at = \?, last_seen_at = \? WHERE id = \?$/i.test(query)) {
      const [token_hash, access_expires_at, last_seen_at, id] = params as [string, number, number, string];
      const row = this.get("sessions").find((r) => r.id === id);
      if (row) Object.assign(row, { token_hash, access_expires_at, last_seen_at });
      return;
    }

    if (/^UPDATE sessions SET revoked_at = \? WHERE token_hash = \? AND revoked_at IS NULL$/i.test(query)) {
      const [revoked_at, token_hash] = params as [number, string];
      for (const row of this.get("sessions")) {
        if (row.token_hash === token_hash && row.revoked_at == null) row.revoked_at = revoked_at;
      }
      return;
    }

    if (/^UPDATE sessions SET revoked_at = \? WHERE revoked_at IS NULL AND id != \?$/i.test(query)) {
      const [revoked_at, keep] = params as [number, string];
      for (const row of this.get("sessions")) {
        if (row.revoked_at == null && row.id !== keep) row.revoked_at = revoked_at;
      }
      return;
    }

    throw new Error(`FakeD1 cannot execute: ${query}`);
  }

  /** Test helper: raw row access, for assertions about persisted state. */
  table(name: string): Row[] {
    return this.get(name);
  }
}

class FakeStatement {
  constructor(private db: FakeD1, private sql: string) {}

  bind(...params: unknown[]) {
    return new BoundStatement(this.db, this.sql, params);
  }
}

class BoundStatement {
  constructor(private db: FakeD1, private sql: string, private params: unknown[]) {}

  private dispatch(): void {
    const q = this.sql.replace(/\s+/g, " ").trim();
    if (/^select/i.test(q)) this.db.select(this.sql, this.params);
    else if (/^update/i.test(q)) this.db.update(this.sql, this.params);
    else this.db.run(this.sql, this.params);
  }

  async first<T>(): Promise<T | null> {
    const q = this.sql.replace(/\s+/g, " ").trim();
    const rows = this.db.select(this.sql, this.params);
    void q;
    return rows.length > 0 ? (rows[0] as T) : null;
  }

  async run(): Promise<unknown> {
    this.dispatch();
    return { success: true };
  }

  async all<T>(): Promise<{ results: T[] }> {
    return { results: this.db.select(this.sql, this.params) as T[] };
  }
}
