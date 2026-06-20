# stswasm — Slay the Spire in the browser (WebAssembly)

`stswasm` runs the desktop Java build of **Slay the Spire** entirely client-side in
a web browser by compiling it to WebAssembly. The game's Java bytecode is run on
the .NET runtime via **IKVM** (Java → .NET), and the whole thing — IKVM, the .NET
runtime, and the native libGDX/LWJGL/FreeType code — is compiled to WASM with
**Emscripten** and Mono's wasm backend.

> [!IMPORTANT]
> **This repository contains no game assets and no copy of Slay the Spire.**
> Slay the Spire is © [Mega Crit](https://www.megacrit.com/). To build or run this
> project you must supply your own legally-purchased copy of the game's
> `desktop-1.0.jar`. See [Supplying the game](#supplying-the-game). This is an
> unofficial, non-commercial technical experiment and is not affiliated with or
> endorsed by Mega Crit.

---

## How it works

```
 Slay the Spire desktop-1.0.jar (your copy) ─┐
 libGDX 1.9.5 + LWJGL3 + freetype  ──────────┤
                                             │  IKVM (Java → .NET; prebuilt in vendor/)
                                             ▼
                              .NET assemblies (IKVM.Java, ikvmc_* bundles)
                                             │  dotnet publish (Mono wasm)
                                             ▼
   native libGDX/LWJGL/FreeType JNI ──────►  WebAssembly + JS glue (_framework/)
   (Emscripten, native/*.a)                   │  node static server (COOP/COEP)
                                              ▼
                                      runs in the browser
```

The browser entry point (`frontend/public/main.js`) boots the .NET wasm runtime,
mounts the IKVM Java home + class library, then calls into `IkvmWasm.PreInit` /
`IkvmWasm.RunSts` to start either a libGDX self-test or the game.

## Repository layout

| Path                | What it is                                                                  |
| ------------------- | --------------------------------------------------------------------------- |
| `build.sh`          | Build orchestration (`native`, `launcher`, `publish`, `deploy`, `serve`…).  |
| `loader/`           | .NET/IKVM wasm host: C# (`IkvmWasm.cs`, `ClassLoader.cs`, …) + C (`icalls.c`, `Emscripten.c`) + `IkvmWasm.csproj`. |
| `launcher/`         | Java shims (`WasmLauncher`, GDX/LWJGL stubs) → built into `stswasm-launcher.jar`. |
| `native/`           | libGDX core + `gdx-freetype` JNI sources + Emscripten build scripts → `libgdx.a`, `libgdxfreetype.a`. |
| `frontend/`         | `server.mjs` (static host) + `public/` (`index.html`, `main.js`).           |
| `jars/`             | Vendored third-party libs to compile the launcher (libGDX, LWJGL, ASM).     |
| `vendor/`           | **Prebuilt artifacts we link against** (from the `ikvmcraft` toolchain): the IKVM runtime + Java image, IKVM-compiled bundles, and the native `.a` libraries. Committed so no external checkout is required. |

This project is **self-contained except for two large external toolchains** — the
Emscripten SDK and the .NET wasm runtime pack — which are too big to commit (the
Emscripten SDK alone contains files over GitHub's 100 MB limit). They are
configured by environment variable and default to `vendor/emsdk` and
`vendor/dotnet`. See [Toolchain setup](#toolchain-setup).

Generated output (`loader/bin`, `loader/obj`, `launcher/out`, `native/obj*`,
`frontend/public/{_framework,image,jars}`) is not tracked — see `.gitignore`.
The small prebuilt outputs `native/libgdx.a`, `native/libgdxfreetype.a` and the
generated `loader/statics.c` **are** committed, so you can `publish` without
re-running the heavy `native` target.

## Prerequisites

- **.NET 10 SDK** with the WebAssembly workload (`Microsoft.NET.Sdk.WebAssembly`)
- **JDK 17** (only to build the launcher jar; compiled with `--release 8` for IKVM's Java 8 ABI)
- **Node.js** (the dev server, `frontend/server.mjs`)
- **Python 3** (only to regenerate native registries via `./build.sh native`)
- **Emscripten SDK** and the **.NET wasm runtime pack** — external, see below
- A **Chromium-based browser** with SharedArrayBuffer (cross-origin isolation) and
  JSPI support; the runtime uses pthreads + JSPI.

### Toolchain setup

Two toolchains are not committed. Provide each by either placing it under `vendor/`
or pointing an environment variable at an existing copy:

| Toolchain            | Env var            | Default location |
| -------------------- | ------------------ | ---------------- |
| Emscripten SDK       | `EMSDK`            | `vendor/emsdk`   |
| .NET wasm runtime pack | `DOTNET_WASM_PACK` | `vendor/dotnet`  |

```sh
# Option A: point at existing copies
export EMSDK=/path/to/emsdk
export DOTNET_WASM_PACK=/path/to/microsoft.netcore.app.runtime.mono.browser-wasm

# Option B: drop them in-repo (gitignored)
ln -s /path/to/emsdk           vendor/emsdk
ln -s /path/to/dotnet-wasm-pack vendor/dotnet
```

> [!NOTE]
> These are the specific builds produced by the upstream [`ikvmcraft`](#) toolchain;
> arbitrary stock releases of Emscripten / the runtime pack may not match the
> prebuilt `.a` libraries in `vendor/`.

### Supplying the game

Provide your own copy of Slay the Spire's `desktop-1.0.jar`, either at the default
location or via `STS_JAR`:

```sh
cp /path/to/desktop-1.0.jar ./game/desktop-1.0.jar   # default (game/ is gitignored)
# or:
export STS_JAR=/path/to/desktop-1.0.jar
```

## Build & run

`build.sh` runs one or more targets in order. With the toolchains and game jar in
place, a typical full build is:

```sh
./build.sh launcher publish deploy sts-jar serve
```

Then open:

- <http://localhost:8088/?app=gdxtest> — libGDX validation app (default), no game needed at runtime
- <http://localhost:8088/?app=sts> — Slay the Spire (requires the `sts-jar` step)

### Targets

| Target     | Does                                                                            |
| ---------- | ------------------------------------------------------------------------------- |
| `native`   | Compile libGDX + freetype JNI → `native/*.a` and regenerate `loader/statics.c`. **Optional** — outputs are committed. Needs `EMSDK` + the libGDX 1.9.5 source (`GDX_JNI_DIR`). |
| `launcher` | `javac launcher/src` → `jars/stswasm-launcher.jar`. Needs a JDK (and the game jar, since `WasmLauncher` references it). |
| `publish`  | `dotnet publish loader` → the `_framework` wasm runtime. Needs `EMSDK` + `DOTNET_WASM_PACK`. |
| `deploy`   | Stage `_framework` + the IKVM image + jars into `frontend/public`, patch JS.    |
| `sts-jar`  | Symlink your `desktop-1.0.jar` into `frontend/public/jars`.                      |
| `all`      | `launcher` + `publish` + `deploy`.                                              |
| `serve`    | `node frontend/server.mjs` → http://localhost:8088 (override with `PORT`).      |
| `clean`    | Remove `loader/bin,obj` — **required** after editing native C sources.          |

`AOT=true OPT=true ./build.sh all` enables AOT + wasm-opt (default is interpreter-only).
Run `./build.sh` with no arguments to print the built-in help.

### Environment variables

| Var                  | Purpose                                   | Default                      |
| -------------------- | ----------------------------------------- | ---------------------------- |
| `EMSDK`              | Emscripten SDK dir                        | `vendor/emsdk`               |
| `DOTNET_WASM_PACK`   | .NET wasm runtime pack dir                | `vendor/dotnet`              |
| `JAVA_HOME`          | JDK 17 install                            | `javac`/`jar` on `PATH`      |
| `STS_JAR`            | your `desktop-1.0.jar`                     | `game/desktop-1.0.jar`       |
| `PORT`               | dev server port                           | `8088`                       |
| `GDX_JNI_DIR`        | libGDX 1.9.5 `gdx/jni` source (`native` only) | *(unset; required for `native`)* |
| `IKVMCRAFT_STATICS_C`| base `statics.c` registry (`native` only) | `vendor/ikvmcraft-statics.c` |

No build script or project file contains a machine-specific absolute path; every
external input is one of the variables above.

## Known limitations

- Building the `launcher` currently requires the game jar, because `WasmLauncher`
  references the game's main class.
- The Emscripten SDK and .NET wasm runtime pack are version-specific to the
  `ikvmcraft` toolchain that produced `vendor/`'s `.a` libraries.
- Browser support is narrow (needs SAB + threads + JSPI); expect Chromium.

## Licensing & attribution

- **Slay the Spire** and all of its assets are © Mega Crit and are **not** included
  in this repository. You must own the game to use this project.
- `jars/` and `vendor/` redistribute third-party components under their own
  upstream licenses, including libGDX (Apache-2.0), LWJGL (BSD-3-Clause),
  ASM (BSD-3-Clause), SDL (zlib), FreeType (FTL/GPLv2), and IKVM. Consult each
  upstream project for exact terms before redistributing.
- The original code in this repository (`loader/`, `launcher/`, `native/`,
  `frontend/`, `build.sh`) has no license file yet — **add a `LICENSE`** before
  publishing if you intend others to reuse it.
```
