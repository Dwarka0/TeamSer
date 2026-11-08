/**
 * Generates an Argon2id PHC hash for the shared access password.
 *
 * Usage:
 *   node scripts/gen-password-hash.mjs
 *   node scripts/gen-password-hash.mjs "my shared password"
 *
 * The password is read from argv ONLY for convenience on a trusted local machine.
 * Prefer the interactive prompt: it never lands in shell history.
 *
 * Output goes to stdout. Feed it to:
 *   wrangler secret put ADMIN_PASSWORD_HASH
 *
 * The plaintext is never written to disk by this script.
 */
import { argon2id } from "hash-wasm";
import { randomBytes } from "node:crypto";
import { createInterface } from "node:readline/promises";
import { stdin, stdout } from "node:process";

const MEMORY_KIB = 19456;
const ITERATIONS = 2;
const PARALLELISM = 1;

async function readPassword() {
  const fromArgs = process.argv[2];
  if (fromArgs) return fromArgs;

  const rl = createInterface({ input: stdin, output: stdout });
  try {
    const answer = await rl.question("Shared access password (input is echoed; use a private terminal): ");
    return answer;
  } finally {
    rl.close();
  }
}

const password = (await readPassword()).trim();
if (password.length < 8) {
  console.error("Password must be at least 8 characters.");
  process.exit(1);
}
if (password.length > 256) {
  console.error("Password must be at most 256 characters.");
  process.exit(1);
}

const hash = await argon2id({
  password,
  salt: randomBytes(16),
  parallelism: PARALLELISM,
  memorySize: MEMORY_KIB,
  iterations: ITERATIONS,
  hashLength: 32,
  outputType: "encoded",
});

// Only the hash is emitted. The plaintext is discarded when this process exits.
console.log(hash);
