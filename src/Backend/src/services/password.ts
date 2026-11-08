import { argon2id, argon2Verify } from "hash-wasm";

/**
 * Password hashing.
 *
 * Argon2id via hash-wasm: it ships compiled WebAssembly, so it runs inside the Workers
 * isolate with no native dependency. Output is PHC format, so the stored value is
 * self-describing (algorithm, params, salt, digest) and parameters can be raised later.
 *
 * Trade-off stated plainly: WASM is slower and harder to audit than a native
 * implementation. It is chosen because Workers cannot load native modules, and the
 * hash is not on a hot path — slow is a feature here. Swap only these two functions
 * if the backend ever moves somewhere with native crypto.
 */
const MEMORY_KIB = 19456; // 19 MiB — RFC 9106 second recommended option
const ITERATIONS = 2;
const PARALLELISM = 1;

export async function hashPassword(plaintext: string): Promise<string> {
  return argon2id({
    password: plaintext,
    salt: randomBytes(16),
    parallelism: PARALLELISM,
    memorySize: MEMORY_KIB,
    iterations: ITERATIONS,
    hashLength: 32,
    outputType: "encoded",
  });
}

/** Constant-time by construction: argon2Verify compares digests internally. */
export async function verifyPassword(plaintext: string, storedPhc: string): Promise<boolean> {
  if (!storedPhc.startsWith("$argon2")) return false;
  try {
    return await argon2Verify({ password: plaintext, hash: storedPhc });
  } catch {
    return false;
  }
}

export function randomBytes(count: number): Uint8Array {
  return crypto.getRandomValues(new Uint8Array(count));
}

/** 256-bit URL-safe token. Only its hash is stored. */
export function newToken(): string {
  const bytes = randomBytes(32);
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Length-independent constant-time string comparison. */
export function safeEqual(a: string, b: string): boolean {
  const enc = new TextEncoder();
  const ab = enc.encode(a);
  const bb = enc.encode(b);
  const length = Math.max(ab.length, bb.length);
  let diff = ab.length ^ bb.length;
  for (let i = 0; i < length; i++) {
    diff |= (ab[i] ?? 0) ^ (bb[i] ?? 0);
  }
  return diff === 0;
}
