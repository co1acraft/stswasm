package stswasm;

import com.badlogic.gdx.utils.GdxNativesLoader;

/**
 * Binds the libGDX core native (gdx-platform: BufferUtils, Matrix4, Gdx2DPixmap, ETC1).
 *
 * <p>In the WASM port that native is linked statically (native/libgdx.a) and registered in the
 * JVM static-library table (loader/statics.c) under the marker path {@code /tmp/lwjgl/libgdx64.so}.
 * We {@link System#load} that marker so IKVM resolves the {@code Java_com_badlogic_gdx_*} symbols,
 * then set {@link GdxNativesLoader#disableNativesLoading} so libGDX does not try to CRC-extract and
 * {@code dlopen} the real .so from the jar (which cannot work here).
 *
 * <p>Crucially this MUST be called from a class on the application classpath (loaded by the
 * IkvmClassLoader), not from the C# loader: JNI scopes a loaded native library to the calling
 * class's defining classloader, and the gdx classes live in this same classloader.
 */
public final class Natives {
    private static boolean bound = false;

    private Natives() {}

    public static synchronized void bind() {
        if (bound) return;
        // gdx core (gdx-platform): native/libgdx.a, registered as /tmp/lwjgl/libgdx64.so.
        System.load("/tmp/lwjgl/libgdx64.so");
        GdxNativesLoader.disableNativesLoading = true;
        // gdx-freetype: native/libgdxfreetype.a, registered as /tmp/lwjgl/libgdx-freetype64.so.
        // setLoaded("gdx-freetype") makes FreeType's static SharedLibraryLoader.load(...) a no-op
        // (otherwise it would extract libgdx-freetype64.so from the jar and dlopen it).
        System.load("/tmp/lwjgl/libgdx-freetype64.so");
        com.badlogic.gdx.utils.SharedLibraryLoader.setLoaded("gdx-freetype");
        bound = true;
        System.out.println("[stswasm] gdx + gdx-freetype natives bound");
    }
}
