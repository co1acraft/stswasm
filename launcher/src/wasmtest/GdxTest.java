package wasmtest;

import com.badlogic.gdx.ApplicationAdapter;
import com.badlogic.gdx.Gdx;
import com.badlogic.gdx.graphics.GL20;
import com.badlogic.gdx.graphics.Texture;
import com.badlogic.gdx.graphics.Pixmap;
import com.badlogic.gdx.graphics.Color;
import com.badlogic.gdx.graphics.g2d.SpriteBatch;

/**
 * Minimal libGDX validation app. Exercises the parts of the pipeline that must
 * work before Slay the Spire can: GL context + clear (gl4es->WebGL), the gdx
 * native (Pixmap image decode / BufferUtils / Matrix4 via SpriteBatch's
 * projection), and texture upload + a textured draw.
 */
public class GdxTest extends ApplicationAdapter {
    private SpriteBatch batch;
    private Texture texture;
    private float t = 0f;

    @Override
    public void create() {
        System.out.println("[GdxTest] create(): GL_VERSION=" + Gdx.gl.glGetString(GL20.GL_VERSION));
        // Build a Pixmap in code (exercises gdx2d native newPixmap/setPixel/fill).
        Pixmap pm = new Pixmap(64, 64, Pixmap.Format.RGBA8888);
        pm.setColor(Color.WHITE);
        pm.fill();
        pm.setColor(Color.RED);
        pm.fillCircle(32, 32, 24);
        texture = new Texture(pm); // exercises BufferUtils + GL texture upload
        pm.dispose();
        batch = new SpriteBatch(); // exercises Matrix4 native (projection) + shaders
    }

    @Override
    public void render() {
        t += Gdx.graphics.getDeltaTime();
        float r = (float) (0.5 + 0.5 * Math.sin(t));
        Gdx.gl.glClearColor(r, 0.3f, 0.6f, 1f);
        Gdx.gl.glClear(GL20.GL_COLOR_BUFFER_BIT);
        batch.begin();
        batch.draw(texture, 100 + 50f * (float) Math.cos(t), 100 + 50f * (float) Math.sin(t));
        batch.end();
    }

    @Override
    public void dispose() {
        if (batch != null) batch.dispose();
        if (texture != null) texture.dispose();
    }
}
