import { copyFile, mkdir, readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const file = (path) => resolve(root, path);
const { version } = JSON.parse(await readFile(file("package.json"), "utf8"));
const project = await readFile(file("src/VpnLedger.csproj"), "utf8");
if (!project.includes(`<Version>${version}</Version>`)) throw new Error("网站版本与 Windows 项目不一致");
await mkdir(file("dist"), { recursive: true });
const html = await readFile(file("site/index.html"), "utf8");
await writeFile(file("dist/index.html"), html.replaceAll("{{version}}", version));
for (const name of ["style.css", "preview.js"]) await copyFile(file(`site/${name}`), file(`dist/${name}`));
for (const name of ["dashboard.png", "dashboard-dark.png", "history.png"]) {
  await copyFile(file(`docs/${name}`), file(`dist/${name}`));
}
await copyFile(file("src/ledger.ico"), file("dist/favicon.ico"));
