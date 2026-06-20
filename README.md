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
> `desktop-1.0.jar`. See [Prerequisites](#prerequisites). This is an unofficial,
> non-commercial technical experiment and is not affiliated with or endorsed by
> Mega Crit.

---

## How it works

```
 Slay the Spire desktop-1.0.jar (your copy) ─┐
 libGDX 1.9.5 + LWJGL3 + freetype  ──────────┤
                                             │  IKVM (ahead-of-time, in ikvmcraft)
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

| Path                | What it is                                                                 |
| ------------------- | -------------------------------------------------------------------------- |
| `build.sh`          | Build orchestration (`native`, `launcher`, `publish`, `deploy`, `serve`…). |
| `loader/`           | .NET/IKVM wasm host: C# (`IkvmWasm.cs`, `ClassLoader.cs`, …) + C (`icalls.c`, `Emscripten.c`) + `IkvmWasm.csproj`. |
| `launcher/`         | Java shims (`WasmLauncher`, GDX/LWJGL stubs) → built into `stswasm-launcher.jar`. |
| `native/`           | libGDX core + `gdx-freetype` JNI sources and Emscripten build scripts → `*.a`. |
| `frontend/`         | `server.mjs` (static host) + `public/` (`index.html`, `main.js`).          |
| `jars/`             | Vendored third-party libs needed to compile the launcher (libGDX, LWJGL, ASM). |
| `statics/`          | **Symlink** to the sibling [`ikvmcraft`](#the-ikvmcraft-dependency) project (toolchains + IKVM image). Not committed. |

Generated output (`loader/bin`, `loader/obj`, `launcher/out`, `native/obj*`,
`native/*.a`, `loader/statics.c`, `frontend/public/{_framework,image,jars}`) is
**not** tracked — see `.gitignore`. Everything is reproducible via `build.sh`.

## Prerequisites

You need a Linux environment with:

- **JDK 17** (the launcher is compiled with `--release 8` against IKVM's Java 8 ABI)
- **.NET 10 SDK** with the WebAssembly workload (`Microsoft.NET.Sdk.WebAssembly`)
- **Python 3** (native static-registry generators)
- **Node.js** (the dev server, `frontend/server.mjs`)
- **Emscripten** — provided by `ikvmcraft`'s `statics/emsdk`
- A **Chromium-based browser** with SharedArrayBuffer (cross-origin isolation) and
  JSPI support; the runtime uses pthreads + JSPI.

### The `ikvmcraft` dependency

This project is a downstream of a sibling project, **`ikvmcraft`**, which provides
the IKVM-on-wasm toolchain. `statics/` is a symlink into it and supplies:

- the .NET wasm runtime pack and `emsdk`,
- the IKVM Java image and native libs (`statics/ikvm/lib*.a`, `image/`),
- SDL/mojoAL headers, `libglfw3.a`, `liblwjgl3.a`, `libopenal.a`, `SDL3.a`, …,
- `ikvmc_lwjgl3-3.2.2.dll` and `lib_emscripten_glfw3.js`.

Check out and build `ikvmcraft` next to this repo, then create the symlink:

```sh
# expected layout:  <parent>/ikvmcraft  and  <parent>/slaythespire/stswasm
ln -s ../ikvmcraft/statics statics
```

> [!NOTE]
> **Hardcoded paths.** `native/inject_statics.py` and `build.sh`'s defaults assume
> `ikvmcraft` and the game live at specific absolute paths
> (`/home/ubuntu/Documents/ikvmcraft`, `…/slaythespire/Slay.the.Spire.v2.3.4`).
> Adjust those paths, or override via the `STS_DIR`, `STS_JAR`, `JAVA_HOME`, and
> `EMSDK` environment variables, to match your machine.

### Supplying the game

Point the build at your own copy of Slay the Spire's `desktop-1.0.jar`:

```sh
export STS_DIR=/path/to/Slay.the.Spire        # dir containing desktop-1.0.jar
export STS_JAR=$STS_DIR/desktop-1.0.jar
```

## Build & run

`build.sh` runs one or more targets in order. Typical first build:

```sh
export JAVA_HOME=/path/to/jdk-17
export STS_JAR=/path/to/desktop-1.0.jar

./build.sh native     # compile libGDX + freetype JNI -> native/*.a, regen loader/statics.c
./build.sh launcher   # javac launcher/src        -> jars/stswasm-launcher.jar
./build.sh publish    # dotnet publish loader      -> _framework wasm runtime
./build.sh deploy     # stage _framework + IKVM image + jars into frontend/public, patch JS
./build.sh sts-jar    # symlink your desktop-1.0.jar into frontend/public/jars
./build.sh serve      # node frontend/server.mjs   -> http://localhost:8088
```

Then open:

- <http://localhost:8088/?app=gdxtest> — libGDX validation app (default), no game needed
- <http://localhost:8088/?app=sts> — Slay the Spire (requires the `sts-jar` step)

Common shortcuts:

```sh
./build.sh all            # = launcher + publish + deploy  (the usual edit/rebuild loop)
AOT=true OPT=true ./build.sh all   # optional AOT + wasm-opt (default is interpreter-only)
./build.sh clean          # remove loader/bin,obj — REQUIRED after editing native C sources
```

Run `./build.sh` with no arguments to print the built-in help.

`PORT=<n> ./build.sh serve` changes the listen port (default `8088`). The server
sets the COOP/COEP headers required for SharedArrayBuffer and supports HTTP byte
ranges (the wasmfs fetch backend reads jars/assets in ranges).

## Known limitations

- Heavy, research-grade build with absolute paths baked into a couple of scripts
  (see the note above); it is not yet a clean `git clone && build`.
- Requires the external `ikvmcraft` toolchain and a user-supplied game jar.
- Browser support is narrow (needs SAB + threads + JSPI); expect Chromium.

## Licensing & attribution

- **Slay the Spire** and all of its assets are © Mega Crit and are **not** included
  in this repository. You must own the game to use this project.
- Vendored third-party libraries under `jars/` are redistributed under their own
  licenses: libGDX (Apache-2.0), LWJGL (BSD-3-Clause), ASM (BSD-3-Clause).
- IKVM is used under its respective license (see the `ikvmcraft` project).
- The original code in this repository (the `loader/`, `launcher/`, `native/`, and
  `frontend/` sources and `build.sh`) has no license file yet — **add a `LICENSE`
  before publishing** if you intend others to reuse it.
