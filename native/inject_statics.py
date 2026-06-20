#!/usr/bin/env python3
"""Produce stswasm/loader/statics.c = ikvmcraft's full registry + the libGDX core JNI block
(libgdx.a) + the gdx-freetype JNI block (libgdxfreetype.a), with registry entries for both native
markers and the library count bumped accordingly."""
import subprocess, sys, re
from pathlib import Path

HERE = Path(__file__).resolve().parent
BASE = Path("/home/ubuntu/Documents/ikvmcraft/loader/statics.c")
OUT = Path("/home/ubuntu/Documents/slaythespire/stswasm/loader/statics.c")

# (generator script, marker path, symbol-table identifier)
BLOCKS = [
    (HERE / "gen_gdx_statics.py",      "/tmp/lwjgl/libgdx64.so",          "_syms_libgdx_so"),
    (HERE / "gen_freetype_statics.py", "/tmp/lwjgl/libgdx-freetype64.so", "_syms_libgdxfreetype_so"),
]

src = BASE.read_text()
marker = "/* Registry of statically-linked libraries."
assert marker in src, "registry comment not found"
sentinel = "    { NULL, NULL }  /* sentinel */\n};\n\nconst int jvm_static_libs_count ="
assert sentinel in src, "master-array sentinel not found"

reg_lines = ""
for gen, path, syms in BLOCKS:
    block = subprocess.check_output(["python3", str(gen)], text=True)
    src = src.replace(marker, block + "\n\n" + marker, 1)
    reg_lines += f'    {{ "{path}", {syms} }},\n'

src = src.replace(sentinel, reg_lines + sentinel, 1)

m = re.search(r"const int jvm_static_libs_count = (\d+);", src)
assert m, "count not found"
n = int(m.group(1))
src = src.replace(m.group(0), f"const int jvm_static_libs_count = {n + len(BLOCKS)};", 1)

OUT.write_text(src)
print(f"[inject] wrote {OUT} (+{len(BLOCKS)} libs, count {n} -> {n + len(BLOCKS)})", file=sys.stderr)
