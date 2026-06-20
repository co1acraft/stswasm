#!/usr/bin/env bash
# stswasm build orchestration.
#
# Pipeline (mirrors ikvmcraft's Makefile `build` target, adapted for Slay the Spire):
#   native   -> compile libGDX core JNI to native/libgdx.a + regenerate loader/statics.c
#   launcher -> javac launcher/src -> jars/stswasm-launcher.jar
#   publish  -> dotnet publish loader/IkvmWasm.csproj (-> _framework wasm runtime)
#   deploy   -> copy _framework + IKVM image + jars into frontend/public, then patch JS
#   sts-jar  -> stage the 368MB Slay the Spire desktop-1.0.jar into frontend/public/jars
#   all      -> launcher + publish + deploy   (the usual edit/rebuild loop)
#   serve    -> run the static server (COOP/COEP + byte-range)
#
# Usage:  ./build.sh <target> [more targets...]      e.g.  ./build.sh launcher publish deploy
#         AOT=true OPT=true ./build.sh all            (optional; default interpreter-only build)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT"

# --- configurable toolchain locations -----------------------------------------------------------
JAVA_HOME="${JAVA_HOME:-/tmp/stsbuild/jdk-17.0.10+7}"
EMSDK="${EMSDK:-$ROOT/statics/emsdk}"
STS_DIR="${STS_DIR:-/home/ubuntu/Documents/slaythespire/Slay.the.Spire.v2.3.4}"
STS_JAR="${STS_JAR:-$STS_DIR/desktop-1.0.jar}"
AOT="${AOT:-false}"
OPT="${OPT:-false}"

JAVAC="$JAVA_HOME/bin/javac"
JAR="$JAVA_HOME/bin/jar"
PUBLISH_DIR="$ROOT/loader/bin/Release/net10.0/publish/wwwroot/_framework"
PUBLIC="$ROOT/frontend/public"
# libGDX jars the launcher compiles against (core + lwjgl3 backend).
GDX_CP="$ROOT/jars/gdx-1.9.5.jar:$ROOT/jars/gdx-backend-lwjgl3-1.9.5.jar"

say() { printf '\n\033[1;36m[build:%s]\033[0m %s\n' "$1" "$2"; }
die() { printf '\n\033[1;31m[build:error]\033[0m %s\n' "$1" >&2; exit 1; }

# --- targets -------------------------------------------------------------------------------------

target_native() {
  say native "compiling libGDX core JNI -> native/libgdx.a"
  EMSDK="$EMSDK" bash "$ROOT/native/build_gdx_native.sh"
  say native "compiling gdx-freetype JNI -> native/libgdxfreetype.a"
  EMSDK="$EMSDK" bash "$ROOT/native/build_gdx_freetype.sh"
  say native "regenerating loader/statics.c (ikvmcraft registry + gdx + freetype blocks)"
  python3 "$ROOT/native/inject_statics.py"
}

target_launcher() {
  [ -x "$JAVAC" ] || die "javac not found at $JAVAC (set JAVA_HOME)"
  say launcher "javac launcher/src -> launcher/out"
  rm -rf "$ROOT/launcher/out"
  mkdir -p "$ROOT/launcher/out"
  mapfile -t SRCS < <(find "$ROOT/launcher/src" -name '*.java')
  # WasmLauncher references com.megacrit.cardcrawl.core.CardCrawlGame from the STS fat jar.
  local cp="$GDX_CP"
  [ -f "$STS_JAR" ] && cp="$cp:$STS_JAR" || say launcher "WARN: $STS_JAR missing; WasmLauncher will not compile"
  # IKVM presents as Java 8 (class file v52); JDK17 default (v61) -> UnsupportedClassVersionError.
  "$JAVAC" --release 8 -cp "$cp" -d "$ROOT/launcher/out" "${SRCS[@]}"
  # Bundle non-.java resources (e.g. build.properties that overrides STS's distributor=steam).
  [ -d "$ROOT/launcher/resources" ] && cp -r "$ROOT/launcher/resources/." "$ROOT/launcher/out/"
  say launcher "jar -> jars/stswasm-launcher.jar"
  "$JAR" --create --file "$ROOT/jars/stswasm-launcher.jar" -C "$ROOT/launcher/out" .
}

target_publish() {
  command -v dotnet >/dev/null || die "dotnet not on PATH"
  say publish "dotnet publish loader/IkvmWasm.csproj (AOT=$AOT OPT=$OPT)"
  dotnet publish "$ROOT/loader/IkvmWasm.csproj" -c Release \
    -p:IkvmWasmEnableAot="$AOT" -p:IkvmWasmEnableWasmOpt="$OPT" \
    --nodereuse:false -v n
}

# Apply the two post-publish JS fixes (identical to ikvmcraft's Makefile). Idempotent.
target_patch() {
  say patch "applying dotnet/emscripten JS fixes to $PUBLIC/_framework"
  # Mono ULeb 32768 -> 65535 (dotnet emits a too-small max; breaks large methods).
  sed -i 's/this.appendULeb(32768)/this.appendULeb(65535)/' "$PUBLIC"/_framework/dotnet.runtime.*.js
  # Emscripten OffscreenCanvas: transfer the ".canvas" element to the render pthread.
  sed -i 's/var offscreenCanvases \?= \?{};/var offscreenCanvases={};if(globalThis.window\&\&!window.TRANSFERRED_CANVAS){transferredCanvasNames=[".canvas"];window.TRANSFERRED_CANVAS=true;}/' \
    "$PUBLIC"/_framework/dotnet.native.*.js
  grep -ql 'appendULeb(65535)'   "$PUBLIC"/_framework/dotnet.runtime.*.js || die "ULeb patch did not apply"
  grep -ql 'TRANSFERRED_CANVAS'  "$PUBLIC"/_framework/dotnet.native.*.js  || die "OffscreenCanvas patch did not apply"
}

target_deploy() {
  [ -d "$PUBLISH_DIR" ] || die "no publish output ($PUBLISH_DIR); run ./build.sh publish first"
  say deploy "copying _framework -> frontend/public/_framework"
  rm -rf "$PUBLIC/_framework"
  cp -r "$PUBLISH_DIR" "$PUBLIC/_framework"
  if [ ! -d "$PUBLIC/image" ] || [ "${FORCE_IMAGE:-0}" = "1" ]; then
    say deploy "copying IKVM Java image -> frontend/public/image"
    rm -rf "$PUBLIC/image"
    cp -r "$ROOT/statics/ikvm/image" "$PUBLIC/image"
  else
    say deploy "frontend/public/image present (FORCE_IMAGE=1 to refresh)"
  fi
  say deploy "copying classpath jars -> frontend/public/jars"
  mkdir -p "$PUBLIC/jars"
  cp -f "$ROOT/jars/gdx-1.9.5.jar" "$ROOT/jars/gdx-backend-lwjgl3-1.9.5.jar" \
        "$ROOT/jars/stswasm-launcher.jar" "$PUBLIC/jars/"
  target_patch
}

# Stage the Slay the Spire fat jar (368MB; contains libGDX core + all game assets).
# Symlinked (not copied) to avoid duplicating 368MB; the node server follows symlinks.
target_sts_jar() {
  [ -f "$STS_JAR" ] || die "STS jar not found at $STS_JAR (set STS_JAR)"
  mkdir -p "$PUBLIC/jars"
  say sts-jar "linking desktop-1.0.jar -> frontend/public/jars"
  ln -sf "$STS_JAR" "$PUBLIC/jars/desktop-1.0.jar"
  ls -lL "$PUBLIC/jars/desktop-1.0.jar" | sed 's/^/  /'
}

# Remove dotnet build state. REQUIRED after editing native sources (icalls.c, statics.c,
# Emscripten.c): the wasm m2n trampoline table (wasm_m2n_invoke.g.h -> object) is not always
# recompiled incrementally when icalls.cs changes, so the relinked wasm can keep a stale table.
target_clean() {
  say clean "removing loader/bin loader/obj"
  rm -rf "$ROOT/loader/bin" "$ROOT/loader/obj"
}

target_all()   { target_launcher; target_publish; target_deploy; }
target_serve() { say serve "node frontend/server.mjs"; exec node "$ROOT/frontend/server.mjs"; }

# --- dispatch ------------------------------------------------------------------------------------
[ $# -gt 0 ] || { grep -E '^#( |$)' "$0" | sed 's/^# \?//'; exit 0; }
for t in "$@"; do
  case "$t" in
    clean)    target_clean ;;
    native)   target_native ;;
    launcher) target_launcher ;;
    publish)  target_publish ;;
    deploy)   target_deploy ;;
    patch)    target_patch ;;
    sts-jar)  target_sts_jar ;;
    all)      target_all ;;
    serve)    target_serve ;;
    *) die "unknown target: $t (try: clean native launcher publish deploy patch sts-jar all serve)" ;;
  esac
done
say done "targets: $*"
