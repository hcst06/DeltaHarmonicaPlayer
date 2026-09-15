import { build } from "esbuild";
import { copyFile, mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const output = resolve(here, "../../app/DeltaHarmonicaPlayer/AudioTranscriber");

await mkdir(resolve(output, "model"), { recursive: true });

await build({
  entryPoints: [resolve(here, "src/index.js")],
  outfile: resolve(output, "app.js"),
  bundle: true,
  minify: true,
  sourcemap: false,
  format: "iife",
  target: "chrome100",
  legalComments: "none",
});

await Promise.all([
  copyFile(resolve(here, "public/index.html"), resolve(output, "index.html")),
  copyFile(
    resolve(here, "node_modules/@spotify/basic-pitch/model/model.json"),
    resolve(output, "model/model.json"),
  ),
  copyFile(
    resolve(here, "node_modules/@spotify/basic-pitch/model/group1-shard1of1.bin"),
    resolve(output, "model/group1-shard1of1.bin"),
  ),
]);

console.log(`Audio transcriber assets written to ${output}`);
