// Headless boot smoke test: launches Chromium with software WebGL2 + JSPI, navigates to the
// app, and streams page console/exception output to stdout. Usage: node smoketest.mjs [app] [secs]
import { spawn } from "node:child_process";
import { rmSync, writeFileSync } from "node:fs";
const cleanup = () => { try { rmSync(PROF, { recursive: true, force: true }); } catch {} };

const APP = process.argv[2] || "gdxtest";
const SECS = +(process.argv[3] || 90);
const URL = `http://localhost:8088/?app=${APP}`;
const PROF = `/tmp/chrome-prof-${APP}-${process.pid}-${Date.now()}`;

const chrome = spawn("chromium", [
  "--headless=new", "--no-sandbox", "--disable-dev-shm-usage",
  "--use-gl=angle", "--use-angle=swiftshader-webgl", "--enable-unsafe-swiftshader",
  "--enable-experimental-webassembly-jspi",
  "--window-size=1280,720", "--hide-scrollbars",
  "--remote-debugging-port=9222", `--user-data-dir=${PROF}`,
  "--no-first-run", "--no-default-browser-check", "about:blank",
], { stdio: ["ignore", "inherit", "inherit"] });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function getJson(path) {
  const res = await fetch(`http://localhost:9222${path}`);
  return res.json();
}

let nextId = 1;
function send(ws, method, params = {}) {
  const id = nextId++;
  ws.send(JSON.stringify({ id, method, params }));
  return id;
}

function fmtArg(a) {
  if (a == null) return String(a);
  if (a.type === "string") return a.value;
  if (a.value !== undefined) return JSON.stringify(a.value);
  if (a.description) return a.description;
  if (a.preview?.properties) return "{" + a.preview.properties.map((p) => `${p.name}:${p.value}`).join(",") + "}";
  return a.type;
}

async function main() {
  // wait for devtools endpoint + a page target
  let target;
  for (let i = 0; i < 50; i++) {
    try {
      const list = await getJson("/json/list");
      target = list.find((t) => t.type === "page");
      if (target?.webSocketDebuggerUrl) break;
    } catch {}
    await sleep(200);
  }
  if (!target) { console.error("no devtools page target"); process.exit(2); }

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((r) => (ws.onopen = r));

  const pending = new Map();
  const sendAwait = (method, params) => new Promise((resolve) => { pending.set(send(ws, method, params), resolve); });

  ws.onmessage = (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id !== undefined && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id); return; }
    if (m.method === "Runtime.consoleAPICalled") {
      const a = (m.params.args || []).map(fmtArg).join(" ");
      console.log(`[console.${m.params.type}] ${a}`);
    } else if (m.method === "Log.entryAdded") {
      const e = m.params.entry;
      console.log(`[log.${e.level}] ${e.text}${e.url ? " @" + e.url : ""}`);
    } else if (m.method === "Runtime.exceptionThrown") {
      const d = m.params.exceptionDetails;
      console.log(`[exception] ${d.exception?.description || d.text}`);
    }
  };

  send(ws, "Runtime.enable");
  send(ws, "Log.enable");
  send(ws, "Page.enable");
  await sleep(150);
  console.log(`[smoketest] navigating to ${URL} for ${SECS}s`);
  send(ws, "Page.navigate", { url: URL });

  await sleep(SECS * 1000);
  try {
    // Race against a timeout: captureScreenshot blocks indefinitely if the render thread is pegged.
    const shot = await Promise.race([sendAwait("Page.captureScreenshot", { format: "png" }),
                                     new Promise((r) => setTimeout(() => r(null), 10000))]);
    if (shot?.data) { const f = `/tmp/shot-${APP}.png`; writeFileSync(f, Buffer.from(shot.data, "base64")); console.log(`[smoketest] screenshot -> ${f}`); }
    else console.log("[smoketest] screenshot timed out (render thread busy)");
  } catch (e) { console.log("[smoketest] screenshot failed: " + e); }
  console.log("[smoketest] done");
  try { chrome.kill("SIGKILL"); } catch {} cleanup();
  process.exit(0);
}
main().catch((e) => { console.error(e); try { chrome.kill("SIGKILL"); } catch {} cleanup(); process.exit(1); });
