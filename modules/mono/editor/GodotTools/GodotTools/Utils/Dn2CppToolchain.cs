using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Godot;
using GodotTools.Internals;

namespace GodotTools.Utils
{
    /// <summary>
    /// Locates the self-contained dn2cpp export toolchain that ships next to the
    /// editor in <c>GodotSharp/Dn2Cpp/</c>, the sibling of <c>GodotSharp/Tools/</c>
    /// this assembly is loaded from. The bundle carries a native transpiler CLI,
    /// the runtime sources it compiles against, and a pinned managed framework
    /// closure, so the export backend needs nothing else on the machine beyond a
    /// C++ toolchain.
    /// </summary>
    public sealed class Dn2CppToolchain
    {
        /// <summary>
        /// Editor setting pointing at a toolchain layout directory to use instead
        /// of the bundled one. This is the dn2cpp developer loop: re-package the
        /// layout in the dn2cpp tree and re-export, with no editor rebuild.
        /// </summary>
        public const string ToolchainPathSetting = "dotnet/export/dn2cpp_toolchain_path";

        /// <summary>
        /// Editor setting pointing at an Emscripten SDK layout to use instead of
        /// the bundled one, and ahead of any SDK on PATH.
        /// </summary>
        public const string EmsdkPathSetting = "dotnet/export/dn2cpp_emsdk_path";

        /// <summary>
        /// Editor setting pointing at a writable Emscripten cache directory. The
        /// bundled SDK carries a baked, frozen cache, so a link that needs a
        /// system-library variant nothing baked (a <c>-pthread</c> build, say)
        /// fails rather than building it; this is the way out.
        /// </summary>
        public const string EmsdkCachePathSetting = "dotnet/export/dn2cpp_emsdk_cache_path";

        private Dn2CppToolchain(string rootDir, string source)
        {
            RootDir = rootDir;
            Source = source;
        }

        /// <summary>Root of the toolchain layout.</summary>
        public string RootDir { get; }

        /// <summary>Human-readable description of where <see cref="RootDir"/> came from.</summary>
        public string Source { get; }

        /// <summary>The self-hosted native transpiler CLI. Runs without a .NET runtime.</summary>
        public string Dn2CppExe => Path.Combine(RootDir, "bin", OS.IsWindows ? "dn2cpp.exe" : "dn2cpp");

        /// <summary>
        /// The managed support shim, which defines the types the transpiler
        /// synthesizes — notably the <c>SZArrayEnumerable&lt;T&gt;</c> behind
        /// <c>((IEnumerable&lt;T&gt;)array).GetEnumerator()</c>.
        /// <para>The transpiler auto-references it from <c>AppContext.BaseDirectory</c>,
        /// which a self-hosted native binary answers with its own directory — so a
        /// bundle that ships it beside the CLI resolves it without help. We pass it
        /// explicitly with <c>-r</c> anyway: an explicit path cannot be defeated by a
        /// relocated or symlinked binary. A transpile that needs the shim and cannot
        /// find it fails outright (<c>Compilation.RequireShimType</c>) rather than
        /// emitting a program that links, exports the entry point, and dies in
        /// <c>dn2cpp_resolve_interface</c> the first time it enumerates an
        /// array — so its presence is validated, never assumed.</para>
        /// </summary>
        public string RuntimeShim => Path.Combine(RootDir, "bin", "Dn2Cpp.Runtime.dll");

        /// <summary>
        /// The pinned <c>System.Private.CoreLib.dll</c>. The transpiler's
        /// <c>--auto-ref</c> resolves the rest of the game's framework references
        /// from the directory that holds it, so the whole closure ships alongside.
        /// </summary>
        public string CoreLibRef => Path.Combine(RootDir, "ref", "System.Private.CoreLib.dll");

        /// <summary>
        /// The POSIX-flavour framework a cross-target export transpiles against.
        /// Staged beside <c>ref/</c> only by a bundle built on a host whose own
        /// framework cannot serve one — i.e. a Windows host; see
        /// <see cref="NeedsCrossCoreLib"/>.
        /// </summary>
        public string CrossCoreLibRef => Path.Combine(RootDir, "ref-posix", "System.Private.CoreLib.dll");

        /// <summary>
        /// The CoreLib the transpile of an export to <paramref name="godotPlatform"/>
        /// must reference.
        /// </summary>
        public string CoreLibRefFor(string godotPlatform) =>
            NeedsCrossCoreLib(godotPlatform) ? CrossCoreLibRef : CoreLibRef;

        /// <summary>
        /// Whether an export to <paramref name="godotPlatform"/> needs the
        /// POSIX-flavour framework rather than the bundle's host one. The invariant:
        /// <b>on a Windows host, every target but Windows itself needs the POSIX
        /// flavour</b> — so it is written as the complement of the one host-native
        /// target, never as a list of cross-targets. A list is a thing a newly
        /// supported platform can be forgotten from; iOS and macOS were already
        /// missing from one, and that was harmless only because
        /// <c>VerifyAppleSdk</c> happens to refuse an Apple target on a Windows host
        /// first — safety by accident of ordering, which the complement does not
        /// depend on.
        /// <para>What the transpiler consumes is the CoreLib's IL, so the flavour
        /// decides which native libraries the emitted P/Invokes name. A Windows
        /// framework names kernel32/ntdll (the real, unintercepted
        /// <c>FileStream</c>) and ole32 (COM, behind <c>Guid.NewGuid</c>); no
        /// cross-target sysroot — the NDK's, Emscripten's, an Apple SDK's — has any
        /// of them, and the failure lands in the linker naming a library rather than
        /// a flavour. On a POSIX host the question does not arise: that framework's
        /// <c>Interop.Sys</c> is what bionic and Emscripten already answer, which is
        /// why this went unnoticed while the lane was macOS-only.</para>
        /// <para>The publish RID stays the HOST's either way — that is a deliberate
        /// rule (a browser-wasm publish would need the wasm-tools workload and a
        /// Mono-flavoured CoreLib, i.e. it would change the very IL we transpile).
        /// This corrects only the FRAMEWORK the transpile references, which is the
        /// part the RID was never choosing well.</para>
        /// </summary>
        public static bool NeedsCrossCoreLib(string godotPlatform) =>
            OS.IsWindows && godotPlatform != OS.Platforms.Windows;

        /// <summary>CMake source dir for the runtime the generated C++ links against.</summary>
        public string RuntimeDir => Path.Combine(RootDir, "runtime");

        /// <summary>
        /// The bundled Emscripten SDK a Web export cross-compiles through. Only a
        /// bundle packaged on a host that had an SDK carries one, so its absence
        /// is normal and never a broken layout — see <see cref="TryValidate"/>.
        /// </summary>
        public string EmsdkDir => Path.Combine(RootDir, "emsdk");

        /// <summary>The wrapper that hands cmake Emscripten's own toolchain file.</summary>
        public string EmsdkEmcmake => EmsdkEmcmakeIn(EmsdkDir);

        /// <summary>
        /// The SDK's own config. It is written relative to <c>$CFGDIR</c>, so the
        /// bundle stays relocatable; every tool under the SDK is pointed at it
        /// through <c>EM_CONFIG</c>.
        /// </summary>
        public string EmsdkConfig => EmsdkConfigIn(EmsdkDir);

        /// <summary>Whether this bundle carries a usable Emscripten SDK.</summary>
        public bool HasEmsdk => IsEmsdkLayout(EmsdkDir);

        /// <summary>
        /// <c>emcmake</c> under an arbitrary SDK root — the editor setting names
        /// one this class knows nothing else about. The frontend's spelling is
        /// the SDK's, not the OS's: an <c>emsdk install</c> tree ships
        /// <c>.bat</c> wrappers, the bundled Windows SDK ships native
        /// <c>.exe</c> launchers, a POSIX SDK bare scripts — take what exists.
        /// </summary>
        public static string EmsdkEmcmakeIn(string emsdkDir)
        {
            string emscriptenDir = Path.Combine(emsdkDir, "emscripten");
            foreach (string name in new[] { "emcmake.bat", "emcmake.exe", "emcmake" })
            {
                string candidate = Path.Combine(emscriptenDir, name);
                if (System.IO.File.Exists(candidate))
                    return candidate;
            }

            // No frontend at all (IsEmsdkLayout refuses such a root); name the
            // OS's usual spelling so the error message stays concrete.
            return Path.Combine(emscriptenDir, OS.IsWindows ? "emcmake.bat" : "emcmake");
        }

        /// <summary>The SDK config under an arbitrary SDK root.</summary>
        public static string EmsdkConfigIn(string emsdkDir) =>
            Path.Combine(emsdkDir, "emscripten", ".emscripten");

        /// <summary>
        /// Whether <paramref name="emsdkDir"/> is an SDK this backend can drive.
        /// The config is asked for as well as the wrapper: without one, emcc falls
        /// back to the user's <c>~/.emscripten</c> and compiles through another
        /// SDK's LLVM.
        /// </summary>
        public static bool IsEmsdkLayout(string emsdkDir) =>
            System.IO.File.Exists(EmsdkEmcmakeIn(emsdkDir))
            && System.IO.File.Exists(EmsdkConfigIn(emsdkDir));

        /// <summary>The dn2cpp-to-fork version contract: commit, package version, godot pin, content hash.</summary>
        public string ManifestPath => Path.Combine(RootDir, "manifest.json");

        /// <summary>
        /// The editor's own <c>GodotSharp/</c> directory, independent of the
        /// toolchain (which may be overridden to point outside the editor).
        /// Derived from the engine's own <c>GodotSharp/Tools</c> path rather than
        /// from this assembly's location: the editor loads GodotTools from a
        /// stream into a collectible load context, so its <c>Assembly.Location</c>
        /// is empty.
        /// </summary>
        public static string EditorGodotSharpDir =>
            Path.GetFullPath(Path.Combine(GodotSharpDirs.DataEditorToolsDir, ".."));

        /// <summary>
        /// The GodotSharp assembly the transpiler binds the game's engine calls
        /// against. Taking the editor's own copy makes the emitted C++ agree with
        /// the engine and the export templates by construction.
        /// </summary>
        public static string EditorGodotSharpAssembly =>
            Path.Combine(EditorGodotSharpDir, "Api", "Release", "GodotSharp.dll");

        private static string DefaultRootDir => Path.Combine(EditorGodotSharpDir, "Dn2Cpp");

        /// <summary>
        /// Resolves the toolchain, preferring the editor-setting override over the
        /// bundled layout. Returns <see langword="false"/> with an actionable
        /// <paramref name="error"/> when neither is a usable layout.
        /// </summary>
        public static bool TryResolve([NotNullWhen(true)] out Dn2CppToolchain? toolchain, out string error)
        {
            string overridePath = GetSettingOverride();
            if (!string.IsNullOrEmpty(overridePath))
            {
                var overrideToolchain = new Dn2CppToolchain(Path.GetFullPath(overridePath),
                    $"editor setting '{ToolchainPathSetting}'");
                if (!overrideToolchain.TryValidate(out error))
                {
                    toolchain = null;
                    return false;
                }

                toolchain = overrideToolchain;
                return true;
            }

            var bundled = new Dn2CppToolchain(DefaultRootDir, "editor bundle");
            if (!bundled.TryValidate(out error))
            {
                error = $"{error}\nThis editor build does not ship the dn2cpp toolchain. Either rebuild it with " +
                    $"'build_assemblies.py --bundle-dn2cpp <layout|tar>', or point the editor setting " +
                    $"'{ToolchainPathSetting}' at a layout produced by dn2cpp's 'dist/package-toolchain.sh'.";
                toolchain = null;
                return false;
            }

            toolchain = bundled;
            return true;
        }

        private bool TryValidate(out string error)
        {
            // The Emscripten SDK is deliberately not required here: only a Web
            // export needs one, and demanding it would refuse every other target
            // on a bundle packaged where no SDK was.
            foreach (string required in new[]
                     { Dn2CppExe, RuntimeShim, CoreLibRef, Path.Combine(RuntimeDir, "CMakeLists.txt") })
            {
                if (!System.IO.File.Exists(required))
                {
                    error = $"dn2cpp toolchain at '{RootDir}' ({Source}) is incomplete: missing '{required}'.";
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        /// <summary>
        /// One-line summary of <c>manifest.json</c> for the export log, or a
        /// placeholder when the manifest is missing or unreadable. The manifest is
        /// informational — a bundle without one still transpiles.
        /// </summary>
        public string DescribeManifest()
        {
            try
            {
                var parsed = Json.ParseString(System.IO.File.ReadAllText(ManifestPath));
                if (parsed.VariantType != Variant.Type.Dictionary)
                    return "manifest.json: not a JSON object";

                var manifest = parsed.AsGodotDictionary();

                string Field(string key) =>
                    manifest.TryGetValue(key, out Variant value) ? value.AsString() : "?";

                return $"dn2cpp {Field("package_version")} (commit {Field("dn2cpp_commit")}, " +
                    $"godot pin {Field("godot_pin")}, corelib {Field("corelib_framework")}, " +
                    $"content {Field("content_hash")})";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"manifest.json: unreadable ({e.Message})";
            }
        }

        private static string GetSettingOverride() => GetEditorSetting(ToolchainPathSetting);

        /// <summary>
        /// An editor setting as a string, empty when unset or when there is no
        /// editor (the assembly is also loaded outside one).
        /// </summary>
        public static string GetEditorSetting(string name)
        {
            try
            {
                EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
                if (settings is null || !settings.HasSetting(name))
                    return string.Empty;

                return settings.GetSetting(name).AsString();
            }
            catch (InvalidOperationException)
            {
                // No editor singleton (the assembly is loaded outside the editor).
                return string.Empty;
            }
        }
    }
}
