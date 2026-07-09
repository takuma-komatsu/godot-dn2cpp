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

        /// <summary>CMake source dir for the runtime the generated C++ links against.</summary>
        public string RuntimeDir => Path.Combine(RootDir, "runtime");

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

        private static string GetSettingOverride()
        {
            try
            {
                EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
                if (settings is null || !settings.HasSetting(ToolchainPathSetting))
                    return string.Empty;

                return settings.GetSetting(ToolchainPathSetting).AsString();
            }
            catch (InvalidOperationException)
            {
                // No editor singleton (the assembly is loaded outside the editor).
                return string.Empty;
            }
        }
    }
}
