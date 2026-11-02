#!/usr/bin/env node
// Generate the ECDSA P-256 key pair that signs scripts/tool-pins.json.
//   node scripts/new-tool-pins-key.mjs <private-key.pem>
// Writes the private key (PKCS#8 PEM) to the given path OUTSIDE the repo and
// the public key (SPKI, base64) to scripts/tool-pins.pub. Keep the private key
// in a password manager and in the TOOL_PINS_SIGNING_KEY GitHub secret.
import { generateKeyPairSync } from "node:crypto";
import { writeFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const out = process.argv[2];
if (!out) {
  console.error("usage: node scripts/new-tool-pins-key.mjs <private-key.pem>");
  process.exit(2);
}
if (existsSync(out)) {
  console.error(`refusing to overwrite ${out}`);
  process.exit(2);
}

const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
writeFileSync(out, privateKey.export({ type: "pkcs8", format: "pem" }), { mode: 0o600 });
const spki = publicKey.export({ type: "spki", format: "der" }).toString("base64");
const pub = join(dirname(fileURLToPath(import.meta.url)), "tool-pins.pub");
writeFileSync(pub, spki + "\n");
console.log(`private key: ${out}`);
console.log(`public key:  ${pub}`);
