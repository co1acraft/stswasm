#!/usr/bin/env python3
"""Emit the libGDX gdx-freetype JNI symbol block for statics.c (see gen_gdx_statics.py).

Signatures come from the jnigen header (machine-generated, javah-style). JNI ABI -> wasm C type
is identical to the gdx-core generator. Registered under /tmp/lwjgl/libgdx-freetype64.so, which the
Java launcher System.load's (and SharedLibraryLoader.setLoaded("gdx-freetype") so libGDX does not
try to extract+dlopen the real .so)."""
import re, subprocess, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
HEADER = HERE / "gdxfreetype" / "com.badlogic.gdx.graphics.g2d.freetype.FreeType.h"
ARCHIVE = HERE / "libgdxfreetype.a"
LLVM_NM = "/home/ubuntu/Documents/ikvmcraft/statics/emsdk/bin/llvm-nm"

TYPE = {
    "void": "void", "jint": "int", "jlong": "long long", "jfloat": "float",
    "jdouble": "double", "jboolean": "int", "jbyte": "int", "jchar": "int",
    "jshort": "int", "jsize": "int",
}
def cty(t):
    t = t.strip()
    return TYPE.get(t, "int")  # JNIEnv*, jclass, jobject, jXxxArray, jstring -> i32 handle

decl_re = re.compile(
    r"JNIEXPORT\s+(?P<ret>[\w]+)\s+JNICALL\s+(?P<name>Java_[\w]+)\s*\((?P<args>[^)]*)\)", re.S)

def parse_header():
    sigs = {}
    for m in decl_re.finditer(HEADER.read_text()):
        ret = cty(m.group("ret"))
        ctypes = []
        for a in (x.strip() for x in m.group("args").split(",")):
            if not a:
                continue
            ctypes.append(cty(a.replace("*", " * ").split()[0]))
        sigs[m.group("name")] = (ret, ctypes)
    return sigs

def archive_symbols():
    out = subprocess.check_output([LLVM_NM, "--defined-only", "--extern-only", str(ARCHIVE)], text=True)
    syms = [p[2] for p in (l.split() for l in out.splitlines())
            if len(p) >= 3 and p[1] == "T" and p[2].startswith("Java_")]
    return sorted(set(syms))

def main():
    sigs, syms = parse_header(), archive_symbols()
    decls, table, missing = [], [], []
    for s in syms:
        ret, args = sigs.get(s, (None, None))
        if ret is None:
            missing.append(s); ret, args = "int", ["int", "int"]
        decls.append(f"extern {ret} {s}({', '.join(args) if args else 'void'});")
        table.append(f'    {{ "{s}", (void*){s} }},')
    if missing:
        print(f"[gen-ft] WARN {len(missing)} symbols not in header (fallback): {missing[:5]}...", file=sys.stderr)
    block = ["\n/* === libGDX gdx-freetype JNI, statically linked from native/libgdxfreetype.a === */",
             '#pragma clang diagnostic push', '#pragma clang diagnostic ignored "-Wstrict-prototypes"',
             *decls, "static const jvm_symbol_entry_t _syms_libgdxfreetype_so[] = {", *table,
             "    { NULL, NULL }", "};", '#pragma clang diagnostic pop']
    print(f"[gen-ft] {len(syms)} freetype symbols, {len(missing)} fallback", file=sys.stderr)
    return "\n".join(block)

if __name__ == "__main__":
    print(main())
