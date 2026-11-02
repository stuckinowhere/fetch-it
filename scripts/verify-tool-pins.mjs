#!/usr/bin/env node
// Verify scripts/tool-pins.json against scripts/tool-pins.json.sig with the
// public key in scripts/tool-pins.pub. Exit 1 on any failure. CI runs this so
// an unsigned pin change never reaches main (clients reject it anyway).
import { createPublicKey, verify } from "node:crypto";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const pinsPath = join(here, "tool-pins.json");

let data, sig, pub;
try {
  data = readFileSync(pinsPath);
  sig = Buffer.from(readFileSync(pinsPath + ".sig", "utf8").trim(), "base64");
  pub = Buffer.from(readFileSync(join(here, "tool-pins.pub"), "utf8").trim(), "base64");
} catch (err) {
  console.error(`tool-pins: missing input (${err.message})`);
  process.exit(1);
}

const key = createPublicKey({ key: pub, format: "der", type: "spki" });
const ok = sig.length === 64 && verify("sha256", data, { key, dsaEncoding: "ieee-p1363" }, sig);
if (!ok) {
  console.error("tool-pins: signature does NOT match scripts/tool-pins.json. Run scripts/sign-tool-pins.mjs with the signing key.");
  process.exit(1);
}

const table = JSON.parse(data.toString("utf8"));
const prefixes = [
  "https://github.com/yt-dlp/yt-dlp/releases/download/",
  "https://github.com/GyanD/codexffmpeg/releases/download/",
  "https://codeberg.org/mikf/gallery-dl/releases/download/"
];
for (const name of ["ytDlp", "ffmpegZip", "galleryDlExe"]) {
  const url = table[name]?.url ?? "";
  if (!prefixes.some((p) => url.startsWith(p))) {
    console.error(`tool-pins: ${name}.url is outside the allowed release paths: ${url}`);
    process.exit(1);
  }
}
if (!Number.isInteger(table.version) || table.version < 1) {
  console.error("tool-pins: version must be a positive integer");
  process.exit(1);
}
console.log(`tool-pins: signature OK (version ${table.version})`);
