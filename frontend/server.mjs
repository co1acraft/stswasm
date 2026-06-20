// Minimal static server with the headers the .NET wasm runtime needs:
//   - COOP/COEP for SharedArrayBuffer (pthreads)
//   - byte-range support (wasmfs fetch backend reads jars/assets in ranges)
import http from "node:http";
import { createReadStream, statSync } from "node:fs";
import { extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(fileURLToPath(new URL(".", import.meta.url)), "public");
const PORT = process.env.PORT ? +process.env.PORT : 8088;

const MIME = {
  ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript",
  ".wasm": "application/wasm", ".dll": "application/octet-stream",
  ".dat": "application/octet-stream", ".json": "application/json",
  ".jar": "application/java-archive", ".so": "application/octet-stream",
  ".css": "text/css", ".pdb": "application/octet-stream", ".png": "image/png",
  ".ogg": "audio/ogg", ".ttf": "font/ttf", ".otf": "font/otf", ".fnt": "text/plain",
  ".atlas": "text/plain", ".txt": "text/plain", ".properties": "text/plain",
};

http.createServer((req, res) => {
  res.setHeader("Cross-Origin-Opener-Policy", "same-origin");
  res.setHeader("Cross-Origin-Embedder-Policy", "require-corp");
  res.setHeader("Cross-Origin-Resource-Policy", "cross-origin");

  let urlPath = decodeURIComponent((req.url || "/").split("?")[0]);
  if (urlPath === "/") urlPath = "/index.html";
  const filePath = join(ROOT, normalize(urlPath).replace(/^(\.\.[/\\])+/, ""));

  let st;
  try { st = statSync(filePath); } catch { res.statusCode = 404; return res.end("not found"); }
  if (st.isDirectory()) { res.statusCode = 404; return res.end("dir"); }

  res.setHeader("Content-Type", MIME[extname(filePath)] || "application/octet-stream");
  res.setHeader("Accept-Ranges", "bytes");

  const range = req.headers.range;
  if (range) {
    const m = /bytes=(\d*)-(\d*)/.exec(range);
    let start = m && m[1] ? +m[1] : 0;
    let end = m && m[2] ? +m[2] : st.size - 1;
    if (start > end || end >= st.size) end = st.size - 1;
    res.statusCode = 206;
    res.setHeader("Content-Range", `bytes ${start}-${end}/${st.size}`);
    res.setHeader("Content-Length", end - start + 1);
    createReadStream(filePath, { start, end }).pipe(res);
  } else {
    res.setHeader("Content-Length", st.size);
    createReadStream(filePath).pipe(res);
  }
}).listen(PORT, () => console.log(`stswasm serving ${ROOT} at http://localhost:${PORT}`));
