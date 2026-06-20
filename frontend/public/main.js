// Boot the IKVM/WASM runtime and launch either the libGDX validation app (?app=gdxtest,
// default) or Slay the Spire (?app=sts). Mirrors ikvmcraft's dotnet init: pthreads + JSPI,
// canvas wired to the emscripten GLFW backend, PreInit (mounts IKVM home + dlls), then RunSts.

const logEl = document.getElementById("log");
function uiLog(s, cls) {
  const d = document.createElement("div");
  if (cls) d.className = cls;
  d.textContent = s;
  logEl.appendChild(d);
  logEl.scrollTop = logEl.scrollHeight;
}
for (const k of ["log", "info", "warn", "error"]) {
  const orig = console[k].bind(console);
  console[k] = (...a) => { orig(...a); try { uiLog(a.join(" "), k === "log" ? "" : k); } catch {} };
}
window.addEventListener("error", (e) => uiLog("window.onerror: " + (e.error?.stack || e.message), "err"));
window.addEventListener("unhandledrejection", (e) => uiLog("unhandledrejection: " + (e.reason?.stack || e.reason), "err"));

const params = new URLSearchParams(location.search);
const app = params.get("app") || "gdxtest";

const APPS = {
  gdxtest: {
    jars: ["/jars/gdx-1.9.5.jar", "/jars/gdx-backend-lwjgl3-1.9.5.jar", "/jars/stswasm-launcher.jar"],
    main: "wasmtest.GdxTestLauncher",
  },
  sts: {
    jars: ["/jars/gdx-backend-lwjgl3-1.9.5.jar", "/jars/stswasm-launcher.jar", "/jars/desktop-1.0.jar"],
    main: "com.megacrit.cardcrawl.desktop.WasmLauncher",
  },
};

function getDlls(dotnet) {
  const config = dotnet.instance.config;
  const res = [...(config.resources?.coreAssembly || []), ...(config.resources?.assembly || [])];
  // .NET 10: resources may be objects {name, virtualPath} or a name->hash map per group.
  const out = [];
  for (const r of res) {
    if (typeof r === "string") out.push([r, r]);
    else if (r && r.name) out.push([r.name, r.virtualPath || r.name]);
  }
  if (out.length === 0 && config.resources?.assembly && !Array.isArray(config.resources.assembly)) {
    for (const name of Object.keys(config.resources.assembly)) out.push([name, name]);
  }
  return out;
}

async function main() {
  const canvas = document.getElementById("canvas");
  // emscripten proxy hackfix (ikvmcraft)
  globalThis.Atomics.waitAsync = undefined;

  uiLog(`[boot] app=${app}`);
  const { dotnet } = await import("/_framework/dotnet.js");

  const runtime = await dotnet
    .withConfig({ pthreadPoolInitialSize: 16 })
    .withModuleConfig({
      onRuntimeInitialized(Module) { globalThis.wasm = { Module, FS: Module.FS }; },
      canvas,
    })
    .withEnvironmentVariable("MONO_SLEEP_ABORT_LIMIT", "20000")
    .withEnvironmentVariable("MONO_GC_PARAMS", "nursery-size=128m")
    .withRuntimeOptions([
      "--jiterpreter-minimum-trace-value=10",
      "--jiterpreter-minimum-trace-hit-count=1000",
    ])
    .create();

  const config = runtime.getConfig();
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  runtime.Module.canvas = canvas;
  globalThis.wasm = { Module: runtime.Module, FS: runtime.Module.FS, runtime, config, exports, canvas };

  uiLog("[boot] runMain");
  await runtime.runMain();

  const dlls = getDlls(dotnet).map((x) => `${x[0]}|${x[1]}`);
  // Fetch-mount base: the document's directory URL with a trailing slash and NO query string.
  // (PreInit/RunSts concatenate "image"/"_framework/"/"jars" onto this.) Using location.href
  // directly breaks when a ?app=... query is present, e.g. http://host/?app=sts -> "...stsjars".
  const fetchBase = new URL(".", location.href).href;
  uiLog(`[boot] PreInit base=${fetchBase} (${dlls.length} dlls)`);
  await exports.IkvmWasm.PreInit(fetchBase, dlls, [
    ["org.lwjgl.util.Debug", "true"],
    ["java.awt.headless", "true"],
    // Force log4j to the lightweight simple logger; STS bundles full log4j-core, whose plugin/
    // Jackson-layout scan (hundreds of optional-class probes) is extremely slow in the interpreter.
    ["log4j2.loggerContextFactory", "org.apache.logging.log4j.simple.SimpleLoggerContextFactory"],
  ]);

  const sel = APPS[app];
  uiLog(`[boot] RunSts main=${sel.main}`);
  await exports.IkvmWasm.RunSts(sel.jars, sel.main);
  uiLog("[boot] returned from RunSts");
}

main().catch((e) => uiLog("[boot] FATAL " + (e?.stack || e), "err"));
