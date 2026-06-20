package wasmtest;

import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application;
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration;

public class GdxTestLauncher {
    public static void main(String[] args) {
        stswasm.Natives.bind();
        Lwjgl3ApplicationConfiguration config = new Lwjgl3ApplicationConfiguration();
        config.setTitle("gdxtest");
        config.setWindowedMode(1280, 720);
        config.setResizable(false);
        config.useVsync(true);
        new Lwjgl3Application(new GdxTest(), config);
    }
}
