#!/bin/bash
# Compile libGDX 1.9.5's core JNI (gdx-platform: BufferUtils, Matrix4, Gdx2DPixmap
# image decode, ETC1) to a WASM static archive. jpgd is replaced with a stub so the
# archive has no setjmp/longjmp dependency (stb_image decodes JPEG).
set -e
EMSDK=${EMSDK:-/home/ubuntu/Documents/ikvmcraft/statics/emsdk}
export EM_CONFIG=$EMSDK/emscripten/.emscripten
export EM_FROZEN_CACHE=
export PATH=$EMSDK/emscripten:$EMSDK/bin:$PATH

HERE="$(cd "$(dirname "$0")" && pwd)"
JNI="$HERE/gdxjni"
OUT="$HERE/obj"
mkdir -p "$OUT"

CFLAGS="-O3 -pthread -fno-exceptions"
INC="-I$JNI -I$JNI/jni-headers -I$JNI/jni-headers/linux -I$JNI/gdx2d -I$JNI/etc1"

cc() { echo "[cc] $1"; emcc -c $CFLAGS $INC "$JNI/$1" -o "$OUT/$2"; }

cc com.badlogic.gdx.utils.BufferUtils.cpp        BufferUtils.o
cc com.badlogic.gdx.math.Matrix4.cpp             Matrix4.o
cc com.badlogic.gdx.graphics.g2d.Gdx2DPixmap.cpp Gdx2DPixmap.o
cc com.badlogic.gdx.graphics.glutils.ETC1.cpp    ETC1.o
cc gdx2d/gdx2d.c                                  gdx2d.o
cc etc1/etc1_utils.cpp                            etc1_utils.o
echo "[cc] jpgd_stub.c"; emcc -c $CFLAGS $INC "$HERE/jpgd_stub.c" -o "$OUT/jpgd_stub.o"

echo "[ar] libgdx.a"
emar rcs "$HERE/libgdx.a" "$OUT"/*.o
"$EMSDK/bin/llvm-nm" --defined-only --extern-only "$HERE/libgdx.a" | grep -c ' T Java_' | xargs echo "exported Java_ symbols:"
echo "[done] $HERE/libgdx.a"
