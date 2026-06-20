#!/usr/bin/env python3
"""Inject the libGDX core JNI (gdx-platform / libgdx.a) into a copy of ikvmcraft's
statics.c so IKVM's JVM_LoadLibrary/JVM_FindLibraryEntry can resolve the
Java_com_badlogic_gdx_* symbols of our statically-linked native without dlopen.

Signatures come from the libGDX JNI headers (Apache-2.0). JNI ABI -> wasm C type:
  JNIEnv*/jclass/jobject/jstring/all arrays -> int (i32 pointer/handle)
  jint/jboolean/jbyte/jchar/jshort         -> int
  jlong                                    -> long long
  jfloat                                   -> float ; jdouble -> double ; void -> void
The declared signature matters: wasm call_indirect dispatches on it.
"""
import re, subprocess, sys
from pathlib import Path

JNI_DIR = Path("/tmp/stsbuild/libgdx-1.9.5/gdx/jni")
HEADERS = [
    "com.badlogic.gdx.utils.BufferUtils.h",
    "com.badlogic.gdx.math.Matrix4.h",
    "com.badlogic.gdx.graphics.g2d.Gdx2DPixmap.h",
    "com.badlogic.gdx.graphics.glutils.ETC1.h",
]
ARCHIVE = Path("/home/ubuntu/Documents/slaythespire/stswasm/native/libgdx.a")
LLVM_NM = "/home/ubuntu/Documents/ikvmcraft/statics/emsdk/bin/llvm-nm"
REG_PATH = "/tmp/lwjgl/libgdx64.so"

TYPE = {
    "void": "void", "jint": "int", "jlong": "long long", "jfloat": "float",
    "jdouble": "double", "jboolean": "int", "jbyte": "int", "jchar": "int",
    "jshort": "int", "jsize": "int",
}
def cty(t):
    t = t.strip()
    if t in TYPE:
        return TYPE[t]
    # JNIEnv*, jclass, jobject, jstring, jXxxArray, etc. are all i32 handles
    return "int"

decl_re = re.compile(
    r"JNIEXPORT\s+(?P<ret>[\w]+)\s+JNICALL\s+(?P<name>Java_[\w]+)\s*\((?P<args>[^)]*)\)",
    re.S,
)

def parse_headers():
    sigs = {}
    for h in HEADERS:
        text = (JNI_DIR / h).read_text()
        for m in decl_re.finditer(text):
            ret = cty(m.group("ret"))
            args = [a for a in (x.strip() for x in m.group("args").split(",")) if a]
            # strip param names if any (headers usually have none)
            ctypes = []
            for a in args:
                # take the type tokens before an optional identifier
                a2 = a.replace("*", " * ")
                toks = a2.split()
                # JNIEnv * / jclass / jobject etc -> map whole thing
                base = toks[0]
                ctypes.append(cty(base if base != "JNIEnv" else "JNIEnv"))
            sigs[m.group("name")] = (ret, ctypes)
    return sigs

def archive_symbols():
    out = subprocess.check_output([LLVM_NM, "--defined-only", "--extern-only", str(ARCHIVE)],
                                  text=True)
    syms = []
    for line in out.splitlines():
        parts = line.split()
        if len(parts) >= 3 and parts[1] == "T" and parts[2].startswith("Java_"):
            syms.append(parts[2])
    return sorted(set(syms))

def main():
    sigs = parse_headers()
    syms = archive_symbols()
    decls, table = [], []
    missing = []
    for s in syms:
        if s in sigs:
            ret, args = sigs[s]
        else:
            missing.append(s)
            ret, args = "int", ["int", "int"]  # safe fallback: (JNIEnv*, jclass)
        argstr = ", ".join(args) if args else "void"
        decls.append(f"extern {ret} {s}({argstr});")
        table.append(f'    {{ "{s}", (void*){s} }},')
    if missing:
        print(f"[gen] WARN: {len(missing)} symbols not in headers (fallback sig): {missing[:5]}...",
              file=sys.stderr)

    block = []
    block.append("\n/* === libGDX core JNI (gdx-platform), statically linked from native/libgdx.a === */")
    block.append('#pragma clang diagnostic push')
    block.append('#pragma clang diagnostic ignored "-Wstrict-prototypes"')
    block.extend(decls)
    block.append("static const jvm_symbol_entry_t _syms_libgdx_so[] = {")
    block.extend(table)
    block.append("    { NULL, NULL }")
    block.append("};")
    block.append('#pragma clang diagnostic pop')
    print(f"[gen] {len(syms)} gdx symbols, {len(missing)} fallback", file=sys.stderr)
    return "\n".join(block)

if __name__ == "__main__":
    print(main())
