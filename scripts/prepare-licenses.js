
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const app = fileURLToPath(new URL("../src-tauri/", import.meta.url));
const notices = join(app, "generated-licenses");
mkdirSync(notices, { recursive: true });

if (process.argv[2] !== "--rust-only") {
  execFileSync("cargo", [
    "about", "generate", "--locked", "--fail", "--features", "custom-protocol",
    "--output-file", join(notices, "THIRD-PARTY.txt"), "about.hbs",
  ], { cwd: app, stdio: "inherit" });
}

const sysroot = execFileSync("rustc", ["--print", "sysroot"], { cwd: app, encoding: "utf8" }).trim();
copyFileSync(join(sysroot, "share", "doc", "rust", "COPYRIGHT-library.html"), join(notices, "RUST-STD.html"));
