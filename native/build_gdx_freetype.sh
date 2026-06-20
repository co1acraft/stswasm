#!/bin/bash
# Compile libGDX 1.9.5's gdx-freetype JNI wrapper to a WASM static archive. The wrapper only
# calls the standard FreeType C API (FT_*), which is already provided by the JDK's libfreetype.a
# (vendor/ikvm/libfreetype.a, linked by IkvmWasm.csproj) -- so we compile against the emscripten
# freetype-port HEADERS but do NOT bundle a second FreeType library.
set -e
EMSDK=${EMSDK:-$(cd "$(dirname "$0")/.." && pwd)/vendor/emsdk}
export EM_CONFIG=$EMSDK/emscripten/.emscripten
export PATH=$EMSDK/emscripten:$EMSDK/bin:$PATH

HERE="$(cd "$(dirname "$0")" && pwd)"
JNI="$HERE/gdxfreetype"
FT_INC="$EMSDK/emscripten/cache/sysroot/include/freetype2"
JNIH="$HERE/gdxjni/jni-headers"
OUT="$HERE/obj-freetype"
mkdir -p "$OUT"

[ -d "$FT_INC" ] || { echo "freetype headers missing at $FT_INC; run: embuilder build freetype"; exit 1; }

CFLAGS="-O3 -pthread -fno-exceptions"
INC="-I$JNI -I$FT_INC -I$JNIH -I$JNIH/linux"

echo "[cc] FreeType.cpp"
emcc -c $CFLAGS $INC "$JNI/com.badlogic.gdx.graphics.g2d.freetype.FreeType.cpp" -o "$OUT/FreeType.o"

echo "[ar] libgdxfreetype.a"
emar rcs "$HERE/libgdxfreetype.a" "$OUT"/*.o
"$EMSDK/bin/llvm-nm" --defined-only --extern-only "$HERE/libgdxfreetype.a" | grep -c ' T Java_' | xargs echo "exported Java_ symbols:"
echo "[done] $HERE/libgdxfreetype.a"
