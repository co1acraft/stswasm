using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;

struct Dll
{
    public string RealName;
    public string MappedName;
}

static partial class IkvmWasm
{
    [DllImport("Emscripten")]
    static internal extern void ikvm_gl_init();

    // Base URL the page was served from (ends with '/'); used to fetch-mount jars at run time.
    private static string FetchBase;

    internal static void Main()
    {
        Console.WriteLine("stswasm loader :3");
    }

    public static string[][] ConvertJSObjectToStringArray(JSObject jsObject)
    {
        var outerLength = jsObject.GetPropertyAsInt32("length");
        var result = new string[outerLength][];
        for (int i = 0; i < outerLength; i++)
        {
            using var innerArray = jsObject.GetPropertyAsJSObject(i.ToString());
            var innerLength = innerArray!.GetPropertyAsInt32("length");
            result[i] = new string[innerLength];
            for (int j = 0; j < innerLength; j++)
                result[i][j] = innerArray.GetPropertyAsString(j.ToString()) ?? string.Empty;
        }
        return result;
    }

    private static void MountDlls(string root, string[] rawDlls)
    {
        IEnumerable<Dll> dlls = rawDlls.Select(x =>
        {
            var split = x.Split('|');
            return new Dll() { RealName = split[0], MappedName = split[1] };
        });

        Directory.CreateDirectory("/dlls");
        Emscripten.MountFetch(1, root + "_framework/", "/fetchdlls/");
        foreach (var dll in dlls)
        {
            Emscripten.MountFetchFile(1, $"/fetchdlls/{dll.RealName}");
            File.CreateSymbolicLink($"/dlls/{dll.MappedName}", $"/fetchdlls/{dll.RealName}");
        }
    }

    // Empty marker files so System.load(path) hits the static-lib registry (statics.c) instead of
    // dlopen. The lwjgl set mirrors ikvmcraft; libgdx64.so is the libGDX core JNI (gdx-platform)
    // we compiled to WASM (native/libgdx.a) and registered under this path.
    private static void WriteNativeMarkers()
    {
        Directory.CreateDirectory("/tmp/lwjgl");
        foreach (var so in new[] {
            "liblwjgl.so", "liblwjgl_opengl.so", "liblwjgl_stb.so", "liblwjgl_tinyfd.so",
            "libffi.so", "libglfw.so", "libopenal.so", "libGL.so.1",
            "libgdx64.so", "libgdx-freetype64.so",
        })
            File.WriteAllText($"/tmp/lwjgl/{so}", "");
    }

    [JSExport]
    internal static Task PreInit(string fetchbase, string[] rawDlls, JSObject props)
    {
        try
        {
            FetchBase = fetchbase;
            Emscripten.InstallThreadLogForwarder();
            Emscripten.MountOpfs();

            // IKVM Java home (rt classes, lib data, awt/fontmanager natives).
            Emscripten.MountFetch(0, fetchbase + "/image", "/ikvm");
            Emscripten.MountFetchDir(0, "/ikvm/bin");
            foreach (var f in new[] { "libzip.so", "libnio.so", "libnet.so", "libmanagement.so",
                                      "libawt.so", "libfontmanager.so", "libmlib_image.so",
                                      "liblcms.so", "libjpeg.so" })
                Emscripten.MountFetchFile(0, $"/ikvm/bin/{f}");
            Emscripten.MountFetchDir(0, "/ikvm/lib");
            foreach (var f in new[] { "currency.data", "tzdb.dat", "content-types.properties",
                                      "logging.properties" })
                Emscripten.MountFetchFile(0, $"/ikvm/lib/{f}");

            MountDlls(fetchbase, rawDlls);

            ikvm_gl_init();

            WriteNativeMarkers();

            File.WriteAllText("/ikvm.properties", "ikvm.home=/ikvm");

            // -- ikvm initializes after this point --
            var bootstrapDlls = IkvmcManifest.LoadEmbedded().AlwaysReplaceDlls();
            java.lang.Thread.currentThread().setContextClassLoader(new IkvmClassLoader([], bootstrapDlls, []));

            java.lang.System.setProperty("org.lwjgl.system.allocator", "system");
            java.lang.System.setProperty("org.lwjgl.system.SharedLibraryExtractPath", "/tmp/lwjgl");
            java.lang.System.setProperty("org.lwjgl.librarypath", "/tmp/lwjgl");
            java.lang.System.setProperty("log4j2.contextSelector",
                "org.apache.logging.log4j.core.selector.BasicContextSelector");
            // STS is fine headless-AWT; keeps font/Toolkit init from probing a display.
            java.lang.System.setProperty("java.awt.headless", "true");

            foreach (var prop in ConvertJSObjectToStringArray(props))
            {
                Console.WriteLine($"-D{prop[0]}={prop[1]}");
                java.lang.System.setProperty(prop[0], prop[1]);
            }

            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            ExceptionLogging.WriteException(e, "Error in PreInit()!");
            return Task.FromException(e);
        }
    }

    /// <summary>
    /// Launch Slay the Spire. jars = JIT classpath (gdx-backend-lwjgl3, the WasmLauncher jar, and the
    /// STS fat jar which itself supplies libGDX core + all other deps). The lwjgl3 + asm bundles are
    /// AOT and served by prefix from the embedded manifest. mainClass defaults to the WASM launcher.
    /// </summary>
    [JSExport]
    internal static Task RunSts(string[] jars, string mainClass)
    {
        try
        {
            mainClass ??= "com.megacrit.cardcrawl.desktop.WasmLauncher";
            Console.WriteLine($"[STS] classpath: {string.Join(", ", jars)}");
            Console.WriteLine($"[STS] main: {mainClass}");

            // Fetch-mount each jar into the WASMFS so the URLClassLoader can open it as file://.
            // A jar at FS "/jars/x.jar" is served from "<FetchBase>jars/x.jar".
            Emscripten.MountFetch(2, FetchBase + "jars", "/jars");
            foreach (var jar in jars)
            {
                Emscripten.MountFetchFile(2, jar);
                Console.WriteLine($"[STS] mounted {jar}");
            }

            var dlls = IkvmcManifest.LoadEmbedded().AlwaysReplaceDlls();
            // Neutralize steamworks4j: rewrite its native methods to no-ops (no Steam in the browser).
            IkvmClassLoaderTransformer[] transformers =
            [
                () => (["com.codedisaster.steamworks."], typeof(SteamDeNative)),
            ];
            var loader = new IkvmClassLoader(jars, dlls, transformers);
            java.lang.Thread.currentThread().setContextClassLoader(loader);

            java.lang.System.setProperty("java.library.path", "/tmp/lwjgl");
            java.lang.System.setProperty("java.class.path", string.Join(":", jars));

            // NOTE: the libGDX core native (/tmp/lwjgl/libgdx64.so, our native/libgdx.a) is bound by
            // the Java launcher (stswasm.Natives.bind()), NOT here. JNI associates a System.load'd
            // library with the *caller's* defining classloader; loading it from C# (cli.IkvmWasm,
            // bootstrap loader) makes Gdx2DPixmap/etc. -- defined by this IkvmClassLoader -- fail to
            // resolve their natives (UnsatisfiedLinkError). Loading it from a class on the app
            // classpath fixes the scoping, mirroring how LWJGL loads its own natives.

            var klass = java.lang.Class.forName(mainClass, true, loader);
            var stringArrayClass = java.lang.Class.forName("[Ljava.lang.String;");
            var main = klass.getMethod("main", new[] { stringArrayClass });
            main.invoke(null, new object[] { Array.Empty<string>() });
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            ExceptionLogging.WriteException(e, "[STS] Run failed");
            return Task.FromException(e);
        }
    }
}
