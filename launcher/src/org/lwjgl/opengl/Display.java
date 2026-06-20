package org.lwjgl.opengl;

/**
 * Minimal stub of LWJGL2's {@code Display} for Slay the Spire on WASM.
 *
 * <p>STS runs on the LWJGL3 backend (no LWJGL2 {@code Display}), but
 * {@code com.megacrit.cardcrawl.helpers.controller.CInputHelper.initializeIfAble()} calls
 * {@link #isActive()} before its try/catch. Providing this stub (resolved from the launcher jar via
 * the IkvmClassLoader's jar fallback) lets controller init proceed; any further controller issues
 * are caught by CInputHelper itself. The browser canvas is treated as always active/created.
 */
public final class Display {
    private Display() {}

    public static boolean isActive() { return true; }
    public static boolean isCreated() { return true; }
    public static boolean isVisible() { return true; }
    public static boolean isCloseRequested() { return false; }
}
