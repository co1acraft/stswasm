using org.objectweb.asm;

// Slay the Spire is a Steam build: CardCrawlGame.create() unconditionally news up steamworks4j
// objects (SteamUtils, SteamController via SteamInputHelper, ...) whose JNI natives (libsteam_api /
// libsteamworks4j, 347 native methods) cannot exist in the browser. Rather than stub all of them in
// native code, this transform rewrites every `native` method in the com.codedisaster.steamworks.*
// classes into a concrete no-op that returns a default value. Net effect:
//   SteamAPI.isSteamRunning() -> false  (so SteamInputHelper disables itself gracefully)
//   new SteamUtils(...)        -> succeeds (createCallback returns 0; no UnsatisfiedLinkError)
// Wired via the IkvmClassLoader transformer list (prefix "com.codedisaster.steamworks.").
internal sealed class SteamDeNative : ClassVisitor
{
    // Instantiated by IkvmClassLoader as (className, writer).
    public SteamDeNative(string name, ClassVisitor next) : base(Opcodes.ASM9, next) { }

    public override MethodVisitor visitMethod(int access, string name, string descriptor, string signature, string[] exceptions)
    {
        if ((access & Opcodes.ACC_NATIVE) == 0)
            return base.visitMethod(access, name, descriptor, signature, exceptions);

        // Re-emit without ACC_NATIVE and give it a trivial body. Original native methods carry no
        // Code attribute, so we write the whole body here and return null (don't let the reader
        // drive a second visitEnd on it).
        var mv = base.visitMethod(access & ~Opcodes.ACC_NATIVE, name, descriptor, signature, exceptions);
        if (mv != null)
        {
            mv.visitCode();
            EmitDefaultReturn(mv, descriptor.Substring(descriptor.IndexOf(')') + 1)[0]);
            mv.visitMaxs(0, 0); // recomputed by COMPUTE_MAXS/COMPUTE_FRAMES
            mv.visitEnd();
        }
        return null;
    }

    private static void EmitDefaultReturn(MethodVisitor mv, char ret)
    {
        switch (ret)
        {
            case 'V': mv.visitInsn(Opcodes.RETURN); break;
            case 'J': mv.visitInsn(Opcodes.LCONST_0); mv.visitInsn(Opcodes.LRETURN); break;
            case 'F': mv.visitInsn(Opcodes.FCONST_0); mv.visitInsn(Opcodes.FRETURN); break;
            case 'D': mv.visitInsn(Opcodes.DCONST_0); mv.visitInsn(Opcodes.DRETURN); break;
            case 'L': case '[': mv.visitInsn(Opcodes.ACONST_NULL); mv.visitInsn(Opcodes.ARETURN); break;
            default: mv.visitInsn(Opcodes.ICONST_0); mv.visitInsn(Opcodes.IRETURN); break; // Z,B,C,S,I
        }
    }
}
