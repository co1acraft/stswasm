global using IkvmClassLoaderDll = (string[] Prefixes, string Name);
global using IkvmClassLoaderTransformer = System.Func<(string[] Classes, System.Type Visitor)>;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using IKVM.Runtime;

// ClassWriter.COMPUTE_FRAMES re-derives stack-map frames, which calls getCommonSuperClass to merge
// reference types at control-flow joins. The default implementation loads both classes via the
// classloader to find their common ancestor — but during incremental class loading a referenced MC
// type is often not defined yet, so the load throws and the whole transform aborts the class load
// (e.g. SealLeaves on WorldBorder/class_2784). Falling back to java/lang/Object on any resolution
// failure keeps frame computation alive (Object is a valid, if wider, merge result), so a transform
// never kills a class load over a not-yet-loadable type.
internal class SafeClassWriter : org.objectweb.asm.ClassWriter
{
	public SafeClassWriter(org.objectweb.asm.ClassReader reader, int flags) : base(reader, flags) { }

	protected override string getCommonSuperClass(string type1, string type2)
	{
		try { return base.getCommonSuperClass(type1, type2); }
		catch (Exception) { return "java/lang/Object"; }
	}
}

class AssemblyURLConnection : java.net.JarURLConnection
{
	private readonly java.net.URL JarUrl;

	public AssemblyURLConnection(java.net.URL url) : base(new("jar:" + url + "!/"))
	{
		JarUrl = url;
	}

    public override java.net.URL getJarFileURL() => JarUrl;
	public override java.util.jar.JarFile getJarFile() => null;
    public override java.util.jar.JarEntry getJarEntry() => null;
    public override void connect() { }
}

class AssemblyURLStreamHandler : java.net.URLStreamHandler
{
	protected override java.net.URLConnection openConnection(java.net.URL url)
	{
		var name = url.getHost();
		var asm = $"/dlls/{name}.dll";
		return new AssemblyURLConnection(new("file", "", asm));
	}
}

class IkvmURLStreamHandlerFactory : java.net.URLStreamHandlerFactory
{
	public java.net.URLStreamHandler createURLStreamHandler(String protocol)
	{
		if (protocol == "assembly:")
			return new AssemblyURLStreamHandler();

		return null;
	}
}

internal sealed class IkvmClassLoader : java.net.URLClassLoader
{
	static IkvmClassLoader()
	{
		java.net.URL.setURLStreamHandlerFactory(new IkvmURLStreamHandlerFactory());
	}

	[DllImport("Emscripten")]
	private static extern void classloader_debug([MarshalAs(UnmanagedType.LPUTF8Str)] string log);
	[DllImport("Emscripten")]
	private static extern void classloader_set_mono_assembly_filename(IntPtr assembly, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename);

    private readonly java.lang.ClassLoader systemLoader;
    private readonly List<(string Name, string[] Prefixes, java.lang.ClassLoader Loader)> dllLoaders;
	private readonly List<(string[] Classes, System.Type Visitor)> classTransformers; 

	public static IkvmClassLoader LatestInstance;

	// Per-class load tracing is extremely chatty (thousands of lines forwarded across the worker
	// boundary to console) and dominates STS boot time. Off unless STSWASM_CL_VERBOSE=1 (set via
	// main.js withEnvironmentVariable, so it is available before the first class load).
	internal static readonly bool Verbose = Environment.GetEnvironmentVariable("STSWASM_CL_VERBOSE") == "1";

    public IkvmClassLoader(string[] jars, IkvmClassLoaderDll[] dlls, IkvmClassLoaderTransformer[] transformers)
        : base((from jar in jars select new java.net.URL("file", "", jar)).ToArray(), null)
    {
        dllLoaders =
        [
            ("IkvmWasm", new[] { "cli.Ikvm" }, CreateAssemblyClassLoader("IkvmWasm", typeof(IkvmClassLoader).Assembly)),
            .. from dll in dlls
               select (dll.Name, dll.Prefixes, CreateAssemblyClassLoader(dll.Name, Assembly.Load(dll.Name))),
        ];
        systemLoader = java.lang.ClassLoader.getSystemClassLoader();

		classTransformers = new();
		foreach (var transformerInit in transformers)
		{
			var transformer = transformerInit();
			Type baseType = transformer.Visitor;
			while (baseType != typeof(System.Object))
			{
				baseType = baseType.BaseType;
				if (baseType == typeof(org.objectweb.asm.ClassVisitor))
					goto End;
			}
			throw new InvalidOperationException("transformer must inherit from ClassVisitor");
		End:
			classTransformers.Add(transformer);
		}

		LatestInstance = this;
    }

    private static java.lang.ClassLoader CreateAssemblyClassLoader(string name, Assembly assembly)
    {
		var ptr = (IntPtr)assembly.GetType().GetMethod("GetUnderlyingNativeHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(assembly, []);
		classloader_set_mono_assembly_filename(ptr, $"/dlls/{name}.dll");

        var context = typeof(JVM).GetProperty("Context", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (context is null)
        {
            throw new InvalidOperationException("IKVM runtime context is unavailable.");
        }

        var classLoaderFactory = context.GetType().GetProperty("AssemblyClassLoaderFactory", BindingFlags.Instance | BindingFlags.Public)?.GetValue(context);
        if (classLoaderFactory is null)
        {
            throw new InvalidOperationException("IKVM assembly class loader factory is unavailable.");
        }

        var loader = classLoaderFactory.GetType().GetMethod("FromAssembly", BindingFlags.Instance | BindingFlags.Public)?.Invoke(classLoaderFactory, [assembly]);
        if (loader is null)
        {
            throw new InvalidOperationException($"Failed creating IKVM class loader for assembly '{assembly.FullName}'.");
        }

        var javaClassLoader = loader.GetType().GetMethod("GetJavaClassLoader", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(loader, []);
        if (javaClassLoader is not java.lang.ClassLoader typedLoader)
        {
            throw new InvalidOperationException($"Failed obtaining Java class loader for assembly '{assembly.FullName}'.");
        }

        return typedLoader;
    }

	private void MaybeDefinePackage(string name)
	{
		int lastDot = name.LastIndexOf(".");
		if (lastDot < 0)
			return;

		string packageName = name[..lastDot];

		if (getPackage(packageName) == null)
		{
			definePackage(
				packageName,
				null, // specTitle
				null, // specVersion
				null, // specVendor
				null, // implTitle
				null, // implVersion
				null, // implVendor
				null  // sealBase
			);
		}
	}

	public byte[] TransformClassBytecode(string name, byte[] bytes)
	{
		foreach (var transformer in classTransformers)
		{
			// Match an exact class name, or a package prefix when the entry ends with '.'
			// (e.g. "com.codedisaster.steamworks." matches the whole steamworks4j package).
			if (!transformer.Classes.Any(x => name == x || (x.EndsWith(".") && name.StartsWith(x))))
				continue;

			classloader_debug($"[IkvmClassLoader] applying transformer {transformer.Visitor} to '{name}'");

			try
			{
				org.objectweb.asm.ClassReader reader = new(bytes);
				org.objectweb.asm.ClassWriter writer = new SafeClassWriter(reader, org.objectweb.asm.ClassWriter.COMPUTE_FRAMES | org.objectweb.asm.ClassWriter.COMPUTE_MAXS);
				var visitor = (org.objectweb.asm.ClassVisitor)Activator.CreateInstance(transformer.Visitor, [name, writer]);
				reader.accept(visitor, 0);

				bytes = writer.toByteArray();
			}
			catch (Exception)
			{
				Console.Error.WriteLine($"[IkvmClassLoader] transformer {transformer.Visitor} failed on '{name}'");
				throw;
			}
		}

		// Always-on: make log4j's no-arg getLogger() AOT-safe (see CallerSensitiveLoggerFix). Guarded
		// so a rewrite hiccup on some odd class can't break class loading.
		try
		{
			bytes = CallerSensitiveLoggerFix.Apply(bytes);
		}
		catch (Exception e)
		{
			classloader_debug($"[IkvmClassLoader] CallerSensitiveLoggerFix skipped '{name}': {e.Message}");
		}

		return bytes;
	}

    protected override java.lang.Class findClass(string name)
    {
        if (classTransformers.Count == 0)
        {
            return base.findClass(name);
        }

        var resourcePath = name.Replace('.', '/') + ".class";
        var url = findResource(resourcePath);
        if (url is null)
        {
            throw new java.lang.ClassNotFoundException(name);
        }

        byte[] bytes;
        var stream = url.openStream();
        try
        {
            var output = new java.io.ByteArrayOutputStream();
            var buffer = new byte[8192];
            int n;
            while ((n = stream.read(buffer)) > 0)
            {
                output.write(buffer, 0, n);
            }
            bytes = output.toByteArray();
        }
        finally
        {
            stream.close();
        }

		bytes = TransformClassBytecode(name, bytes);

		java.net.URL codeSourceUrl = url;
		var urlStr = url.toString();
		if (urlStr.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
		{
			var jarPath = urlStr["jar:".Length..];
			var bangSlash = jarPath.IndexOf("!/", StringComparison.Ordinal);
			if (bangSlash >= 0)
				jarPath = jarPath[..bangSlash];
			codeSourceUrl = new java.net.URL(jarPath);
		}
		java.security.CodeSource codeSource = new(codeSourceUrl, (java.security.cert.Certificate[])null);
		java.security.ProtectionDomain protectionDomain = new(codeSource, null, this, null);

		MaybeDefinePackage(name);
        var defined = defineClass(name, bytes, 0, bytes.Length, protectionDomain);
        return defined;
    }

	public override java.net.URL getResource(string name)
	{
		if (!name.EndsWith(".class"))
			return base.getResource(name);

		var klass = name[0..^6].Replace("/", ".");

		foreach (var (assemblyName, prefixes, _) in dllLoaders)
		{
			if (!prefixes.Any(x => klass.StartsWith(x)))
				continue;

            if (Verbose) classloader_debug($"[IkvmClassLoader] '{name}' resource loaded from asm {assemblyName}");
			return new java.net.URL("assembly:", assemblyName, name);
		}

		return base.getResource(name);
	}

	public override java.util.Enumeration getResources(string name)
	{
		if (name.EndsWith(".class"))
		{
			var klass = name[0..^6].Replace("/", ".");

			foreach (var (assemblyName, prefixes, _) in dllLoaders)
			{
				if (!prefixes.Any(x => klass.StartsWith(x)))
					continue;

				if (Verbose) classloader_debug($"[IkvmClassLoader] '{name}' resources resolved from asm {assemblyName}");
				var urls = new java.util.Vector();
				urls.add(new java.net.URL("assembly:", assemblyName, name));
				return urls.elements();
			}
		}

		return base.getResources(name);
	}

    protected override java.lang.Class loadClass(string name, bool resolve)
    {
        lock (getClassLoadingLock(name))
        {
            string loadedFrom = "[already loaded]";
            java.lang.Class cls = findLoadedClass(name);
			bool fastpath = false;

            if (name.StartsWith("java.", StringComparison.Ordinal) || name.StartsWith("javax.", StringComparison.Ordinal) || name.StartsWith("sun.", StringComparison.Ordinal))
            {
                if (cls is null)
                {
                    cls = systemLoader.loadClass(name);
                }

                if (resolve)
                {
                    resolveClass(cls);
                }

                return cls;
            }

            if (!fastpath && cls is null)
            {
                foreach (var (assemblyName, prefixes, loader) in dllLoaders)
                {
					if (!prefixes.Any(x => name.StartsWith(x)))
						continue;

					// An AOT bundle owns a prefix (e.g. org.lwjgl.) but may not contain every class
					// under it: STS references LWJGL2-only types (org.lwjgl.opengl.Display) absent
					// from the LWJGL3 bundle. On a miss, fall through to the URLClassLoader (jars),
					// where a stub/alternate can be provided, instead of failing the whole load.
					try
					{
						cls = loader.loadClass(name);
						loadedFrom = $"assembly {assemblyName}";
						fastpath = true;
					}
					catch (java.lang.ClassNotFoundException)
					{
						if (Verbose) classloader_debug($"[IkvmClassLoader] '{name}' not in assembly {assemblyName}; falling back to jars");
					}
					break;
                }
            }

            if (!fastpath && cls is null)
            {
                try
                {
                    cls = base.loadClass(name, resolve);
                    loadedFrom = "URLClassLoader";
                    resolve = false;
                }
                catch (java.lang.ClassNotFoundException cnf)
                {
                    if (Verbose) Console.WriteLine($"[IkvmClassLoader] '{name}' miss in URLClassLoader: {cnf.getClass().getName()}: {cnf.getMessage()}");
                }
                catch (Exception ex)
                {
                    if (Verbose) Console.WriteLine($"[IkvmClassLoader] '{name}' EXCEPTION in URLClassLoader: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }

            if (cls is null)
            {
				cls = systemLoader.loadClass(name);
				loadedFrom = "system";
            }

            if (resolve)
            {
                resolveClass(cls);
            }

            if (Verbose) classloader_debug($"[IkvmClassLoader] '{name}' loaded from {loadedFrom}");
            return cls;
        }
    }
}
