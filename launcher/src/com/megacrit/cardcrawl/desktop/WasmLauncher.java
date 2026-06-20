package com.megacrit.cardcrawl.desktop;

import com.badlogic.gdx.Files;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;
import com.megacrit.cardcrawl.core.CardCrawlGame;

/**
 * Browser/WASM launcher for Slay the Spire. Mirrors the original
 * {@code DesktopLauncher.main} setup but targets libGDX's LWJGL3 backend
 * (the one the WASM GLFW/gl4es/OpenAL natives implement) instead of LWJGL2.
 *
 * Original (decompiled) sequence:
 *   cfg.title = "Slay the Spire"; cfg.resizable = false; loadSettings(cfg);
 *   new LwjglApplication(new CardCrawlGame(cfg.preferencesDirectory), cfg);
 */
public class WasmLauncher {
    // libGDX LwjglApplicationConfiguration.preferencesDirectory default.
    private static final String PREFS_DIR = ".prefs/";

    public static void main(String[] args) {
        stswasm.Natives.bind();
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("Slay the Spire");
        // STS renders at a 1920x1080 virtual resolution and letterboxes.
        config.setWindowedMode(1920, 1080);
        config.setResizable(false);
        config.useVsync(true);
        config.setPreferencesConfig(PREFS_DIR, Files.FileType.External);

        CardCrawlGame game = new CardCrawlGame(PREFS_DIR);
        new Lwjgl3Application(game, config);
    }
}
