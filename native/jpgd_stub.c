/* Stub replacement for libGDX's jpgd (NanoJPEG/jpeg-decoder) C shim.
 *
 * gdx2d_load() tries stb_image first and only falls back to jpgd when stb fails.
 * The bundled stb_image supports baseline + progressive JPEG, so for our assets
 * stb handles every .jpg. The real jpgd pulls in setjmp/longjmp, which the mono
 * wasm runtime link does not provide; stubbing it keeps the gdx native free of
 * setjmp so it links cleanly. If stb ever fails on an exotic JPEG, load returns
 * NULL and the failure reason below is reported (instead of a hard link error).
 */
#include <stddef.h>

const char *jpgd_failure_reason(void) {
    return "jpgd disabled in wasm build (stb_image decodes jpeg)";
}

unsigned char *jpgd_decompress_jpeg_image_from_memory(
        const unsigned char *pSrc_data, int src_data_size,
        int *width, int *height, int *actual_comps, int req_comps) {
    (void)pSrc_data; (void)src_data_size;
    if (width) *width = 0;
    if (height) *height = 0;
    if (actual_comps) *actual_comps = 0;
    (void)req_comps;
    return NULL;
}
