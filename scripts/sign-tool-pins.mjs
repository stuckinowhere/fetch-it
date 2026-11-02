#!/usr/bin/env node
// Sign scripts/tool-pins.json with the ECDSA P-256 private key.
//   node scripts/sign-tool-pins.mjs --key <private-key.pem>
//   TOOL_PINS_SIGNING_KEY='-----BEGIN PRIVATE KEY-----...' node scripts/sign-tool-pins.mjs
// Writes scripts/tool-pins.json.sig: base64 of the IEEE P1363 (r||s) signature
// over the exact bytes of tool-pins.json, which is what the app verifies.
import { createPrivateKey, sign } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const pinsPath = join(here, "tool-pins.json");
const sigPath = pinsPath + ".sig";

const keyArg = process.argv.indexOf("--key");
const pem = keyArg >= 0
  ? readFileSync(process.argv[keyArg + 1], "utf8")
  : process.env.TOOL_PINS_SIGNING_KEY;
if (!pem) {
  console.error("no key: pass --key <private-key.pem> or set TOOL_PINS_SIGNING_KEY");
  process.exit(2);
}

const data = readFileSync(pinsPath);
const signature = sign("sha256", data, { key: createPrivateKey(pem), dsaEncoding: "ieee-p1363" });
writeFileSync(sigPath, signature.toString("base64") + "\n");
console.log(`signed ${pinsPath} -> ${sigPath}`);
