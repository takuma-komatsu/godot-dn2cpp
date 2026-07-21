using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Godot;
using GodotTools.Internals;
using GodotTools.Utils;
using Directory = System.IO.Directory;
using File = System.IO.File;
using OS = GodotTools.Utils.OS;
using Path = System.IO.Path;

namespace GodotTools.Export
{
    /// <summary>
    /// The dn2cpp export backend. Takes the published game IL and produces the
    /// same artifact shape a NativeAOT publish does — a shared library exporting
    /// <c>godotsharp_game_main_init</c>, which the engine's
    /// <c>try_load_native_aot_library</c> opens from
    /// <c>data_{project}_{platform}_{arch}/</c>. Nothing in the engine or in the
    /// export templates knows dn2cpp exists.
    ///
    /// Everything the two steps need — a native transpiler, the runtime sources,
    /// a pinned framework closure — travels in the toolchain bundle; the host only
    /// supplies a C++ toolchain.
    /// </summary>
    internal sealed class Dn2CppExporter : IDisposable
    {
        private const int RequiredCMakeMajor = 3;
        private const int RequiredCMakeMinor = 20;

        /// <summary>
        /// The minimum iOS version the compiled library declares. 16.3 is the
        /// floor for float <c>std::to_chars</c> in libc++, which the bundled
        /// runtime's number formatting is built on.
        /// </summary>
        private const string IOSDeploymentTarget = "16.3";

        /// <summary>
        /// The Android API level the compiled library declares, and the ABI it is
        /// built for. API 24 is the runtime's own floor (the bionic PAL is written
        /// to it), well below Godot's; arm64-v8a is the one ABI a current device
        /// needs.
        /// </summary>
        private const string AndroidPlatform = "android-24";
        private const string AndroidAbi = "arm64-v8a";

        /// <summary>
        /// Project setting (PackedStringArray) appended verbatim to the transpiler
        /// invocation, e.g. ["--pinvoke-module", "my_native_lib"] for a game
        /// binding an external native library through a referenced assembly.
        /// </summary>
        private const string ExtraTranspileArgsSetting = "dotnet/dn2cpp/extra_transpile_args";

        /// <summary>
        /// Project setting (PackedStringArray) of extra link options for the
        /// drop-in build, passed to the configure as
        /// <c>-DDN2CPP_APP_LINK_FLAGS</c> (space-joined; the runtime's CMake
        /// applies them via <c>target_link_options</c>) — e.g. ["-L/path/to/libs"].
        /// </summary>
        private const string ExtraLinkFlagsSetting = "dotnet/dn2cpp/extra_link_flags";

        /// <summary>
        /// Project setting (PackedStringArray) of extra link inputs for the
        /// drop-in build, passed to the configure as
        /// <c>-DDN2CPP_APP_LINK_LIBS</c> (space-joined; the runtime's CMake
        /// applies them via <c>target_link_libraries</c>) — library tokens or
        /// full archive paths, e.g. a binding SDK's static library.
        /// </summary>
        private const string ExtraLinkLibsSetting = "dotnet/dn2cpp/extra_link_libs";

        /// <summary>
        /// Project setting (PackedStringArray) of native library paths staged
        /// beside the drop-in, so the platform exporter packages them the same
        /// way — into the APK's lib/&lt;abi&gt;/ on Android, next to the data
        /// directory elsewhere — e.g. a binding SDK's shared objects the
        /// drop-in links against or dlopens.
        /// </summary>
        private const string ExtraSharedObjectsSetting = "dotnet/dn2cpp/extra_shared_objects";

        /// <summary>
        /// The one architecture the Web platform has. It never reaches a compiler —
        /// Emscripten decides what wasm it emits — but it is the name the export and
        /// the engine agree on, so it is what the drop-in is keyed on.
        /// </summary>
        private const string WebArch = "wasm32";

        /// <summary>
        /// The cmake versions — half-open ranges, [First, PastLast) — on which
        /// Emscripten's own CMake platform module sets
        /// <c>TARGET_SUPPORTS_SHARED_LIBS</c> to <see langword="false"/>.
        /// </summary>
        private static readonly (Version First, Version PastLast)[] CMakeVersionsWithoutWasmSharedLibs =
        {
            (new Version(4, 2, 0), new Version(4, 2, 6)),
            (new Version(4, 3, 0), new Version(4, 3, 3)),
        };

        /// <summary>How much of the tool output to quote back in an error message.</summary>
        private const int LogTailLines = 30;

        private readonly Dn2CppToolchain _toolchain;
        private readonly string _cmakeExe;
        private readonly string _godotPlatform;
        private readonly string? _androidNdkRoot;
        private readonly string? _emcmakeExe;
        private readonly string _logPath;
        private readonly StreamWriter _log;
        private readonly Queue<string> _logTail = new Queue<string>();

        private Dn2CppExporter(Dn2CppToolchain toolchain, string cmakeExe, string godotPlatform,
            string? androidNdkRoot, string? emcmakeExe)
        {
            _toolchain = toolchain;
            _cmakeExe = cmakeExe;
            _godotPlatform = godotPlatform;
            _androidNdkRoot = androidNdkRoot;
            _emcmakeExe = emcmakeExe;

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string logsDir = Path.Combine(GodotSharpDirs.ProjectBaseOutputPath, "dn2cpp", "logs");
            Directory.CreateDirectory(logsDir);
            _logPath = Path.Combine(logsDir, $"export-{timestamp}.log");
            _log = new StreamWriter(_logPath, append: false) { AutoFlush = true };

            LogLine($"toolchain: {toolchain.RootDir} ({toolchain.Source})");
            LogLine($"manifest:  {toolchain.DescribeManifest()}");
            GD.Print($"dn2cpp: using the toolchain at {toolchain.RootDir} ({toolchain.Source})");
            GD.Print($"dn2cpp: {toolchain.DescribeManifest()}");
            GD.Print($"dn2cpp: export log: {_logPath}");
        }

        /// <summary>
        /// Checks everything that can fail before a minutes-long publish runs: the
        /// target the backend can serve, the host C++ toolchain, and the bundle.
        /// Throws with an actionable message — <c>_ExportBegin</c> surfaces it
        /// through <c>AddMessage(Error, …)</c>.
        /// </summary>
        /// <param name="archs">
        /// The architectures the caller is about to publish and package, not the
        /// preset's feature set. A feature naming an architecture does not make it
        /// a target, and a target is what this backend has to be able to build.
        /// </param>
        public static Dn2CppExporter Create(string godotPlatform, IReadOnlyCollection<string> archs)
        {
            if (godotPlatform == OS.Platforms.MacOS || godotPlatform == OS.Platforms.Windows)
            {
                // The two host-compiled desktop targets. The bundle compiles the
                // game with the host's own C++ compiler (clang++ on macOS, cl.exe
                // under the Ninja generator on Windows), so the export machine's
                // architecture is the only one it can target — cross-architecture
                // export is not supported on either, and the checks are identical.
                string hostArch = GetHostArchitecture();

                if (archs.Count == 0)
                {
                    throw new NotSupportedException(
                        "The dn2cpp export backend needs a target architecture, and this preset selects none. " +
                        $"Set 'binary_format/architecture' to '{hostArch}'.");
                }

                if (archs.Count > 1)
                {
                    throw new NotSupportedException(
                        $"The dn2cpp export backend compiles the game for the host architecture ({hostArch}) and " +
                        $"cannot cross-compile, so it cannot serve a preset targeting {DescribeArchs(archs)}. Set " +
                        $"'binary_format/architecture' to '{hostArch}' ('universal' selects two), and keep " +
                        "architecture names out of 'custom_features'.");
                }

                string arch = archs.First();
                if (arch != hostArch)
                {
                    throw new NotSupportedException(
                        $"The dn2cpp export backend compiles the game for the host architecture ({hostArch}); this " +
                        $"preset targets '{arch}'. Cross-architecture export is not supported yet.");
                }
            }
            else if (godotPlatform == OS.Platforms.iOS)
            {
                // clang cross-targets iOS through -arch and a sysroot, so the host
                // architecture is no constraint here — but arm64 is the only iOS
                // device architecture there is, so it is all the backend builds.
                if (archs.Count != 1 || archs.First() != "arm64")
                {
                    throw new NotSupportedException(
                        "The dn2cpp export backend supports only the arm64 device architecture on iOS" +
                        (archs.Count == 0
                            ? ", and this preset selects none. "
                            : $", and this preset targets {DescribeArchs(archs)}. ") +
                        "Set 'binary_format/architecture' to 'arm64'.");
                }
            }
            else if (godotPlatform == OS.Platforms.Android)
            {
                // The NDK cross-targets Android through its own CMake toolchain
                // file, so the host architecture is no constraint here — but
                // arm64-v8a is the only ABI the backend builds, and the only one
                // a current device needs.
                if (archs.Count != 1 || archs.First() != "arm64")
                {
                    throw new NotSupportedException(
                        "The dn2cpp export backend supports only the arm64 (arm64-v8a) architecture on Android" +
                        (archs.Count == 0
                            ? ", and this preset selects none. "
                            : $", and this preset targets {DescribeArchs(archs)}. ") +
                        "Enable 'architectures/arm64-v8a' alone in the preset.");
                }
            }
            else if (godotPlatform == OS.Platforms.Web)
            {
                // Emscripten cross-targets wasm through its own CMake toolchain
                // file, so the host architecture is no constraint here — and wasm32
                // is the only architecture the Web platform has, so a preset naming
                // anything else named it itself.
                if (archs.Count != 1 || archs.First() != WebArch)
                {
                    throw new NotSupportedException(
                        $"The dn2cpp export backend supports only the {WebArch} architecture on Web" +
                        (archs.Count == 0
                            ? ", and this preset selects none. "
                            : $", and this preset targets {DescribeArchs(archs)}. ") +
                        "Keep architecture names out of 'custom_features'.");
                }
            }
            else
            {
                throw new NotSupportedException(
                    $"The dn2cpp export backend supports Windows, macOS, iOS, Android and Web only for now, not " +
                    $"'{godotPlatform}'. Switch 'dotnet/export_backend' to 'Host Runtime' or 'NativeAOT' for this " +
                    "preset.");
            }

            var missingTools = new List<string>();
            string? cmakeExe = OS.PathWhich("cmake");
            if (cmakeExe is null)
                missingTools.Add($"cmake ({RequiredCMakeMajor}.{RequiredCMakeMinor} or newer)");
            if (OS.PathWhich("ninja") is null)
                missingTools.Add("ninja");
            // The C++ compiler this looks for is the HOST's, and which name that is
            // belongs to the host rather than to the export target: the configure
            // below spells no compiler at all, so cmake picks the platform default —
            // cl.exe under the Ninja generator on Windows, clang++ on macOS. Naming
            // one unconditionally is what made a Windows host refuse a Web or Android
            // export it was perfectly able to serve: those two cross-compile through
            // Emscripten and the NDK, which bring their own compilers, and neither
            // needs the host one to be spelled clang++.
            if (HostCxxCompiler() is null)
                missingTools.Add(OS.IsWindows ? "cl.exe or clang++" : "clang++ (Xcode Command Line Tools)");

            if (missingTools.Count > 0)
            {
                throw new NotSupportedException(
                    "The dn2cpp export backend needs a C++ toolchain that is not on PATH: " +
                    $"{string.Join(", ", missingTools)}.\n" +
                    (OS.IsWindows
                        // The Windows counterpart of the Finder-PATH trap below, and a
                        // sharper one: MSVC is not installed onto PATH at all. cl.exe
                        // becomes visible only inside a shell that has run vcvarsall,
                        // so an editor started from Explorer — or from an ordinary
                        // terminal — cannot see a complete Visual Studio install.
                        ? "Install the Visual Studio C++ workload plus cmake and ninja, then start the editor " +
                          "from a Developer Command Prompt (or any shell that has run vcvarsall): cl.exe is not " +
                          "on PATH otherwise, however complete the install is."
                        : "Install it with 'xcode-select --install' and 'brew install cmake ninja', then restart " +
                          "the editor. An editor launched from Finder inherits a minimal PATH, so Homebrew tools " +
                          "can be invisible to it even when a terminal finds them."));
            }

            Version cmakeVersion = VerifyCMakeVersion(cmakeExe!);

            if (godotPlatform == OS.Platforms.iOS)
            {
                // The Xcode Command Line Tools carry the macOS SDK only; probing
                // both iOS SDKs up front turns what would be a cryptic mid-build
                // clang failure into an actionable refusal.
                VerifyAppleSdk("iphoneos");
                VerifyAppleSdk("iphonesimulator");
            }

            if (godotPlatform == OS.Platforms.Web)
                VerifyCMakeCanBuildWasmSharedLibs(cmakeExe!, cmakeVersion);

            // Resolved here, for the same reason: an absent NDK is a refusal
            // before the publish, not a cmake error twenty minutes in.
            string? androidNdkRoot = godotPlatform == OS.Platforms.Android ? ResolveAndroidNdk() : null;
            string? emcmakeExe = godotPlatform == OS.Platforms.Web ? ResolveEmscripten() : null;

            if (!Dn2CppToolchain.TryResolve(out Dn2CppToolchain? toolchain, out string toolchainError))
                throw new NotSupportedException(toolchainError);

            return new Dn2CppExporter(toolchain, cmakeExe!, godotPlatform, androidNdkRoot, emcmakeExe);
        }

        /// <summary>
        /// Transpiles and compiles the published game assembly into a drop-in
        /// library, and returns a directory holding exactly that library — the
        /// caller packages its contents into the project data directory.
        /// </summary>
        /// <remarks>
        /// The directory holds nothing else, except native libraries the project
        /// declares through <see cref="ExtraSharedObjectsSetting"/>. In particular
        /// never a managed runtime: the engine only reaches
        /// <c>try_load_native_aot_library</c> when it finds no hostfxr and no
        /// coreclr next to the game, so shipping the publish directory's runtime
        /// alongside would route the exported game straight back to the .NET host.
        /// </remarks>
        public string BuildDropIn(string publishOutputDir, string assemblyName, string buildConfig,
            string runtimeIdentifier, string arch)
        {
            // Create refuses any target set the backend cannot build, but it sees
            // one publish config and the caller loops over every architecture of
            // every one of them, so each combination is re-checked here. iOS
            // libraries are cross-compiled, so there the check is that the
            // architecture agrees with the runtime identifier it is built under;
            // the Web's runtime identifier encodes no target architecture at all,
            // so there it is the platform that answers; everywhere else the library
            // is host-compiled, and a foreign architecture reaching here would stage
            // a library into another architecture's data directory, where the engine
            // would load it on no machine at all.
            bool targetsIOS = runtimeIdentifier.StartsWith("ios", StringComparison.Ordinal);
            // Android publishes under either RID family: android-* by default,
            // linux-bionic-* when 'dotnet/android_use_linux_bionic' is on.
            bool targetsAndroid = runtimeIdentifier.StartsWith("android-", StringComparison.Ordinal)
                || runtimeIdentifier.StartsWith("linux-bionic-", StringComparison.Ordinal);
            // The Web is the one target whose runtime identifier says nothing about
            // what is being built: the publish runs under the HOST's RID, on purpose
            // (see the export plugin), and the wasm is Emscripten's doing rather than
            // the RID's. So it is read off the platform Create was given, which is
            // the only place that still knows.
            bool targetsWeb = _godotPlatform == OS.Platforms.Web;
            // Windows, like macOS, is host-compiled and falls into the host-arch
            // check below; it is named here only for the CMake output-naming rule,
            // which is Windows's own: a SHARED target is <name>.dll with NO 'lib'
            // prefix, and the engine opens exactly <assembly>.dll.
            bool targetsWindows = _godotPlatform == OS.Platforms.Windows;
            if (targetsWeb)
            {
                if (arch != WebArch)
                {
                    throw new InvalidOperationException(
                        $"The dn2cpp export backend compiles the Web game for '{WebArch}', but the export is " +
                        $"packaging '{arch}'.");
                }
            }
            else if (targetsIOS || targetsAndroid)
            {
                string ridArch = arch switch
                {
                    "arm64" => "arm64",
                    "x86_64" => "x64",
                    _ => arch,
                };
                if (!runtimeIdentifier.EndsWith($"-{ridArch}", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The dn2cpp export backend is building '{runtimeIdentifier}', but the export is " +
                        $"packaging '{arch}'.");
                }
            }
            else
            {
                string hostArch = GetHostArchitecture();
                if (arch != hostArch)
                {
                    throw new InvalidOperationException(
                        $"The dn2cpp export backend compiles for '{hostArch}', but the export is packaging '{arch}'.");
                }
            }

            string workDir = Path.Combine(MonoDataDir, "dn2cpp");
            // The Godot platform is part of the slot, not just the build config and
            // the runtime identifier. The Web builds under the host's RID, so a macOS
            // export and a Web export of one project would otherwise land in the same
            // build directory — and one directory cannot hold both a native CMakeCache
            // and an Emscripten one. cmake either refuses the changed compiler outright
            // or, worse, reuses the cached one and builds the wrong thing.
            string slot = $"{_godotPlatform}-{buildConfig}-{runtimeIdentifier}";
            string ilDir = Path.Combine(workDir, "il", slot);
            string genDir = Path.Combine(workDir, "gen", slot);
            string buildDir = Path.Combine(workDir, "build", slot);
            string stageDir = Path.Combine(workDir, "stage", slot);

            // The transpiler's --auto-ref resolves the game's framework references
            // from the directory of the first passed assembly that holds a
            // System.Private.CoreLib.dll. Copying the game IL to a directory of its
            // own keeps that directory the bundle's pinned ref/, whatever the
            // publish left behind (a self-contained publish drops its own CoreLib
            // next to the game assembly).
            RecreateDirectory(ilDir);
            string gameAssembly = Path.Combine(ilDir, $"{assemblyName}.dll");
            File.Copy(Path.Combine(publishOutputDir, $"{assemblyName}.dll"), gameAssembly, overwrite: true);

            GD.Print($"dn2cpp: transpiling {assemblyName}.dll to C++...");
            RecreateDirectory(genDir);
            var transpileArgs = new List<string>
            {
                gameAssembly,
                "--dotnet-module",
                // Ordered before any publish-directory reference so the pinned
                // framework closure wins the --auto-ref search described above.
                "-r", _toolchain.CoreLibRef,
                // The editor's own GodotSharp makes the emitted engine calls agree
                // with the engine and the export templates by construction.
                "-r", Dn2CppToolchain.EditorGodotSharpAssembly,
            };
            foreach (string dependency in FindManagedDependencies(publishOutputDir, assemblyName))
            {
                transpileArgs.Add("-r");
                transpileArgs.Add(dependency);
            }
            // Last, mirroring where the transpiler appends the copy it auto-references
            // from its own directory: the module order decides which module wins a
            // type-name tie. Passing it explicitly is belt and braces — see RuntimeShim.
            transpileArgs.Add("-r");
            transpileArgs.Add(_toolchain.RuntimeShim);
            transpileArgs.Add("--auto-ref");
            // Web only, and not an optimization: without it the game does not load at all.
            // The Web target links the generated C++ as an Emscripten wasm SIDE MODULE, and
            // a side module's __wasm_apply_data_relocs — one i32.store per pointer that lives
            // in static data — is a single function, which V8 caps at 7,654,321 bytes. The
            // reflection member tables are ~75% of that function's body, and they carried it
            // to 9,031,961 bytes: over the ceiling, so the browser refused to instantiate the
            // module. Trimming them lands it around 4.9 MB. No other target has a per-function
            // ceiling, and the trim is not free — it costs reflection over framework types the
            // program was not seen to name (a stripped type throws a PlatformNotSupportedException
            // naming itself and '--reflection-root', rather than answering an empty member list) —
            // so nothing but the Web pays for it.
            if (targetsWeb)
            {
                transpileArgs.Add("--trim-reflection");
                // Second Web-only size lever (dn2cpp SZ-12), same relocation budget as
                // above: the real GodotSharp's Godot.Constructors..cctor roots every
                // engine-wrapper class through its 955 registry lambdas — ~69% of a
                // small game's type-infos and thousands of never-called wrapper bodies,
                // with methtab_Godot_Constructors___c alone contributing 5,730 of the
                // side module's data relocations. Under this flag only the wrappers the
                // game actually names stay; every other registry lambda is redirected
                // to the nearest named ancestor's, so the 955-key registry (and its
                // loud missing-name throw) is preserved and an unnamed class's engine
                // object is wrapped as its nearest named ancestor — correct for every
                // cast/is the game can express, with GetType().Name-style string
                // reflection over never-named wrappers as the documented residue (the
                // same constraint bucket as --trim-reflection; '--godot-class-root
                // <Godot.Full.Name>' is the escape hatch for a class only ever named
                // from data).
                transpileArgs.Add("--trim-godot-classes");
            }
            // Project-declared extra transpiler arguments (the
            // "dotnet/dn2cpp/extra_transpile_args" project setting, a
            // PackedStringArray): the route for a per-project flag the exporter has
            // no dedicated knob for — e.g. "--pinvoke-module my_native_lib" when
            // the game binds an external native library through a referenced
            // binding assembly.
            // A PROJECT setting, deliberately not an environment variable: the flags
            // change the C++ a successful transpile emits, and they belong to the
            // game, versioned with it, visible in its project.godot.
            if (ProjectSettings.HasSetting(ExtraTranspileArgsSetting))
            {
                string[] extraArgs = ProjectSettings.GetSetting(ExtraTranspileArgsSetting).AsStringArray();
                if (extraArgs.Length > 0)
                {
                    GD.Print($"dn2cpp: extra transpile args (project setting): {string.Join(' ', extraArgs)}");
                    transpileArgs.AddRange(extraArgs);
                }
            }
            transpileArgs.Add("-o");
            transpileArgs.Add(genDir);
            RunTool(_toolchain.Dn2CppExe, transpileArgs, "transpiling the game assembly");

            // The build directory persists across exports so the runtime and the
            // vendored third-party sources are compiled once; only the regenerated
            // C++ is rebuilt on a re-export.
            //
            // No CMAKE_BUILD_TYPE: runtime/CMakeLists.txt pins its own -O2 per
            // target, so a build type would only add -g (Debug) or -DNDEBUG
            // (Release) on top, and NDEBUG would silently disable the runtime's
            // assertions that every dn2cpp gate runs with.
            GD.Print($"dn2cpp: compiling the drop-in library ({slot})...");
            Directory.CreateDirectory(buildDir);
            // A CMake target name is not a free-form string — it may hold only
            // [A-Za-z0-9_.+-], and a game's assembly name may hold anything a file
            // name may ("Squash the Creeps (3D)"). Passing one straight through
            // fails the configure outright ("reserved or not valid"), so the target
            // is built under a sanitized name and staged under the real one below:
            // what the engine dlopens is the name of the STAGED file, and nothing
            // downstream ever sees the target's.
            string targetName = SanitizeCMakeTarget(assemblyName);
            var configureArgs = new List<string>
            {
                "-S", _toolchain.RuntimeDir,
                "-B", buildDir,
                "-G", "Ninja",
                "-DDN2CPP_DOTNET_MODULE=ON",
                $"-DDN2CPP_APP_DIR={genDir}",
                $"-DDN2CPP_APP_NAME={targetName}",
            };
            if (targetsIOS)
            {
                // Retarget the host clang at the device or simulator SDK. The arch
                // parameter carries Godot's architecture names (arm64, x86_64),
                // which are exactly clang's -arch spellings.
                string sysroot = runtimeIdentifier.StartsWith("iossimulator-", StringComparison.Ordinal)
                    ? "iphonesimulator"
                    : "iphoneos";
                configureArgs.Add("-DCMAKE_SYSTEM_NAME=iOS");
                configureArgs.Add($"-DCMAKE_OSX_SYSROOT={sysroot}");
                configureArgs.Add($"-DCMAKE_OSX_ARCHITECTURES={arch}");
                configureArgs.Add($"-DCMAKE_OSX_DEPLOYMENT_TARGET={IOSDeploymentTarget}");
            }
            else if (targetsAndroid)
            {
                // The NDK ships its own toolchain file — it selects the bionic
                // sysroot, the target triple and the API-level defines together,
                // which is why nothing here spells a compiler.
                configureArgs.Add($"-DCMAKE_TOOLCHAIN_FILE={_androidNdkRoot}/build/cmake/android.toolchain.cmake");
                configureArgs.Add($"-DANDROID_ABI={AndroidAbi}");
                configureArgs.Add($"-DANDROID_PLATFORM={AndroidPlatform}");
            }

            // Project-declared extra native link inputs (PackedStringArray
            // settings, like the transpile knob above; res:// and user:// entries
            // are globalized). Both defines are passed on EVERY configure, empty
            // when unset: the build directory persists across exports and cmake
            // never resets a cached variable, so an emptied setting must
            // overwrite the cache rather than leave the previous export's flags
            // armed.
            string extraLinkFlags = string.Join(' ', GetPathListSetting(ExtraLinkFlagsSetting));
            string extraLinkLibs = string.Join(' ', GetPathListSetting(ExtraLinkLibsSetting));
            if (extraLinkFlags.Length > 0)
                GD.Print($"dn2cpp: extra link flags (project setting): {extraLinkFlags}");
            if (extraLinkLibs.Length > 0)
                GD.Print($"dn2cpp: extra link libs (project setting): {extraLinkLibs}");
            configureArgs.Add($"-DDN2CPP_APP_LINK_FLAGS={extraLinkFlags}");
            configureArgs.Add($"-DDN2CPP_APP_LINK_LIBS={extraLinkLibs}");

            if (targetsWeb)
            {
                // emcmake runs cmake with Emscripten's own CMake toolchain file
                // injected, which is what selects em++, the wasm target and the
                // side-module link together — the same reason the Android arm above
                // spells no compiler either. Only the configure is wrapped: the
                // cache it writes carries the toolchain into every later build.
                var emcmakeArgs = new List<string> { _cmakeExe };
                emcmakeArgs.AddRange(configureArgs);
                RunTool(_emcmakeExe!, emcmakeArgs, "configuring the native build");
            }
            else
            {
                RunTool(_cmakeExe, configureArgs, "configuring the native build");
            }

            RunTool(_cmakeExe, new List<string> { "--build", buildDir }, "compiling the drop-in library");

            // What CMake names a SHARED target's output is the platform's rule, not
            // one convention, and the three Unix-family targets already needed three
            // different answers. On Apple the linker writes lib<name>.dylib and the
            // engine opens <name>.dylib — the name a NativeAOT publish produces — so
            // the lib prefix is dropped when staging. On Android it opens the bare
            // soname lib<name>.so and lets the linker find it in the APK's lib/<abi>/,
            // so there the prefix is the whole point: keep it.
            //
            // On the Web the prefix goes, for a third reason. Emscripten names the
            // side module lib<name>.so like any other SHARED target, but the engine
            // asks for <name>.so: there is no web branch in
            // try_load_native_aot_library, so the Web takes the UNIX_ENABLED one.
            // That name is never opened as a path — OS_Web strips the directory and
            // dlopens the bare file name, which resolves out of the loader's registry
            // of libraries it preloaded before main(), keyed on the file name the Web
            // exporter copied next to index.html. That is the staged file's name. So
            // the staged file must be exactly <assembly>.so: on this platform the
            // name is not a convention, it is the entire lookup.
            //
            // Windows is the fourth: MSVC/lld write <name>.dll with NO 'lib' prefix
            // at all, and the engine's WINDOWS_ENABLED branch opens <assembly>.dll
            // from the data dir — so both the built name and the staged name carry no
            // prefix, unlike every Unix case above.
            string builtLibrary = targetsWindows
                ? Path.Combine(buildDir, $"{targetName}.dll")
                : Path.Combine(buildDir, $"lib{targetName}.{(targetsAndroid || targetsWeb ? "so" : "dylib")}");
            if (!File.Exists(builtLibrary))
            {
                throw new InvalidOperationException(
                    $"The dn2cpp native build produced no '{builtLibrary}'.\nLog: {_logPath}");
            }

            RecreateDirectory(stageDir);
            string stagedName = targetsWindows ? $"{assemblyName}.dll"
                : targetsAndroid ? $"lib{assemblyName}.so"
                : targetsWeb ? $"{assemblyName}.so"
                : $"{assemblyName}.dylib";
            string stagedLibrary = Path.Combine(stageDir, stagedName);
            File.Copy(builtLibrary, stagedLibrary, overwrite: true);

            if (targetsIOS)
            {
                // The backend-agnostic iOS packaging tail lipos {assembly}.dylib
                // out of every publish directory and wraps the results into the
                // _aot.xcframework the exported app embeds, so the library also
                // goes where a NativeAOT publish would have left it.
                File.Copy(builtLibrary, Path.Combine(publishOutputDir, $"{assemblyName}.dylib"), overwrite: true);
            }

            LogLine($"staged {stagedLibrary}");
            GD.Print($"dn2cpp: staged {stagedLibrary}");

            // Project-declared extra native libraries, staged beside the drop-in
            // so the platform exporter packages them by the same rules it
            // packages the drop-in itself (Android tags shared objects into the
            // APK's lib/<abi>/; the Web copies them next to index.html and
            // preloads them). A missing path is an error, not a skip: a typo
            // silently shipping a game without its native library would surface
            // as a dlopen failure on a player's machine, the one place this
            // diagnostic cannot reach anybody.
            foreach (string sharedObject in GetPathListSetting(ExtraSharedObjectsSetting))
            {
                if (!File.Exists(sharedObject))
                {
                    throw new InvalidOperationException(
                        $"The '{ExtraSharedObjectsSetting}' project setting names '{sharedObject}', which does " +
                        "not exist.");
                }

                string stagedSharedObject = Path.Combine(stageDir, Path.GetFileName(sharedObject));
                File.Copy(sharedObject, stagedSharedObject, overwrite: true);
                LogLine($"staged {stagedSharedObject}");
                GD.Print($"dn2cpp: staged extra shared object {stagedSharedObject}");
            }

            return stageDir;
        }

        /// <summary>
        /// A PackedStringArray project setting read as a list, with res:// and
        /// user:// entries globalized to filesystem paths. Empty when the
        /// setting is absent.
        /// </summary>
        private static string[] GetPathListSetting(string settingName)
        {
            if (!ProjectSettings.HasSetting(settingName))
                return Array.Empty<string>();

            return ProjectSettings.GetSetting(settingName).AsStringArray()
                .Select(entry =>
                    entry.StartsWith("res://", StringComparison.Ordinal)
                    || entry.StartsWith("user://", StringComparison.Ordinal)
                        ? ProjectSettings.GlobalizePath(entry)
                        : entry)
                .ToArray();
        }

        /// <summary>
        /// The game's own managed dependencies: everything in the publish output
        /// that the bundle's framework closure does not already define, minus the
        /// game assembly and the Godot assemblies passed explicitly. Sorted, because
        /// the order the transpiler loads modules in decides the names it mangles.
        /// </summary>
        private IEnumerable<string> FindManagedDependencies(string publishOutputDir, string assemblyName)
        {
            var dependencies = new List<string>();

            foreach (string candidate in Directory.GetFiles(publishOutputDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(candidate);

                if (name == $"{assemblyName}.dll")
                    continue;
                if (name.StartsWith("GodotSharp", StringComparison.Ordinal) || name == "GodotPlugins.dll")
                    continue;
                if (File.Exists(Path.Combine(_toolchain.RootDir, "ref", name)))
                    continue;
                // A framework-dependent Windows publish drops NATIVE runtime DLLs
                // into the output that a macOS/Linux publish leaves in the shared
                // framework — hostfxr.dll always, and the diagnostics pair
                // (mscordaccore_*.dll, mscordbi.dll) with debug symbols. They are
                // not managed assemblies, they are not in the bundle's ref/ closure,
                // and passing one to the transpiler's -r makes it throw "PE image
                // does not have metadata" and abort the whole export. The .dll glob
                // cannot tell them from managed ones by name, so the test is on the
                // file: skip anything without a CLI metadata header. macOS output is
                // unchanged — its publish dir holds only managed assemblies, which
                // all pass.
                if (!IsManagedAssembly(candidate))
                    continue;

                dependencies.Add(candidate);
            }

            dependencies.Sort(StringComparer.Ordinal);
            return dependencies;
        }

        /// <summary>
        /// True when <paramref name="path"/> is a managed assembly (has a CLI
        /// metadata header). <c>AssemblyName.GetAssemblyName</c> reads the identity
        /// out of the metadata and throws <see cref="BadImageFormatException"/> for a
        /// native image — the BCL-standard managed/native discriminator. It opens,
        /// reads and closes; it does not load the assembly into the process.
        /// </summary>
        private static bool IsManagedAssembly(string path)
        {
            try
            {
                System.Reflection.AssemblyName.GetAssemblyName(path);
                return true;
            }
            catch (BadImageFormatException)
            {
                return false;
            }
            catch (System.IO.FileLoadException)
            {
                // Readable but e.g. already loaded elsewhere — it IS managed, so
                // keep it; a genuinely broken reference fails loudly in the transpiler.
                return true;
            }
        }

        /// <summary>The project's <c>.godot/mono</c> directory, where the persistent build tree lives.</summary>
        private static string MonoDataDir =>
            Path.GetFullPath(Path.Combine(GodotSharpDirs.ProjectBaseOutputPath, "..", ".."));

        /// <summary>The target architectures, ordered, for an error message.</summary>
        private static string DescribeArchs(IEnumerable<string> archs)
        {
            return string.Join(", ", archs.OrderBy(a => a, StringComparer.Ordinal));
        }

        private static string GetHostArchitecture()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x86_64",
                var other => throw new NotSupportedException(
                    $"The dn2cpp export backend does not support the host architecture '{other}'."),
            };
        }

        /// <summary>
        /// The host C++ compiler cmake will pick for a native build, or
        /// <see langword="null"/> when there is none on PATH. The order matches
        /// cmake's own default search under the Ninja generator, so what this
        /// finds is what the configure will use — asking for a compiler cmake
        /// would not have chosen turns a working host into a refusal, and missing
        /// one cmake WOULD have chosen turns an actionable refusal into a configure
        /// error many minutes into the export.
        /// </summary>
        private static string? HostCxxCompiler()
        {
            if (OS.IsWindows)
                return OS.PathWhich("cl") ?? OS.PathWhich("clang++");

            return OS.PathWhich("clang++");
        }

        /// <summary>
        /// The NDK the Android cross-build compiles through: whatever the
        /// environment names (ANDROID_NDK_ROOT / ANDROID_NDK_HOME — what a
        /// terminal-launched editor and every CI script already set), else the
        /// newest NDK installed under the Android SDK the export itself uses.
        /// The check is the toolchain file cmake is actually handed, not the
        /// directory: an SDK with an `ndk/` holding a half-removed version would
        /// otherwise resolve to a path that fails at configure time.
        /// </summary>
        private static string ResolveAndroidNdk()
        {
            var probed = new List<string>();

            foreach (string name in new[] { "ANDROID_NDK_ROOT", "ANDROID_NDK_HOME" })
            {
                string? fromEnv = System.Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(fromEnv))
                    continue;
                if (HasNdkToolchainFile(fromEnv))
                    return fromEnv;
                probed.Add($"{name}={fromEnv}");
            }

            string sdkPath = GetAndroidSdkPath();
            if (sdkPath.Length > 0)
            {
                string ndkParent = Path.Combine(sdkPath, "ndk");
                if (Directory.Exists(ndkParent))
                {
                    // Newest first: the directory names are NDK versions, and
                    // ordinal-descending puts the highest one at the front (they
                    // are zero-padded and equal-width, so no version parse is owed).
                    foreach (string candidate in Directory.GetDirectories(ndkParent)
                                 .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
                    {
                        if (HasNdkToolchainFile(candidate))
                            return candidate;
                    }
                }
                probed.Add($"the editor's Android SDK ({ndkParent})");
            }

            throw new NotSupportedException(
                "The dn2cpp export backend cross-compiles for Android through the NDK's own CMake toolchain " +
                "file, and no NDK was found" +
                (probed.Count > 0 ? $" (looked at: {string.Join("; ", probed)})" : "") + ".\n" +
                "Install one from the Android SDK manager, then either set ANDROID_NDK_ROOT or point " +
                "'export/android/android_sdk_path' (Editor Settings) at the SDK that holds it.");
        }

        private static bool HasNdkToolchainFile(string ndkRoot) =>
            File.Exists(Path.Combine(ndkRoot, "build", "cmake", "android.toolchain.cmake"));

        /// <summary>
        /// The Emscripten SDK the Web cross-build compiles through, resolved to the
        /// <c>emcmake</c> the configure runs under. <c>em++</c> is probed beside it
        /// because emcmake alone proves nothing: it is a thin wrapper that would
        /// hand cmake a toolchain file naming a compiler that is not there, and the
        /// failure would land in cmake's compiler check with nothing pointing at the
        /// SDK.
        /// </summary>
        private static string ResolveEmscripten()
        {
            string? emcmakeExe = OS.PathWhich("emcmake");

            var missingTools = new List<string>();
            if (emcmakeExe is null)
                missingTools.Add("emcmake");
            if (OS.PathWhich("em++") is null)
                missingTools.Add("em++");

            if (missingTools.Count > 0)
            {
                throw new NotSupportedException(
                    "The dn2cpp export backend compiles the game for the Web with Emscripten, which is not on " +
                    $"PATH: {string.Join(", ", missingTools)}.\n" +
                    "Install the Emscripten SDK ('brew install emscripten', or emsdk's own installer), activate " +
                    "it in the environment the editor is launched from ('source /path/to/emsdk/emsdk_env.sh'), " +
                    "and restart the editor. An editor launched from Finder inherits a minimal PATH, so an SDK a " +
                    "terminal finds can be invisible to it.");
            }

            return emcmakeExe!;
        }

        /// <summary>
        /// The assembly name, reduced to the character set CMake allows in a target
        /// name (<c>[A-Za-z0-9_.+-]</c>). Only the build target is renamed — the
        /// library that ships keeps the assembly's own name, which is the one the
        /// engine opens.
        /// </summary>
        private static string SanitizeCMakeTarget(string assemblyName)
        {
            var sanitized = new StringBuilder(assemblyName.Length);
            foreach (char c in assemblyName)
            {
                sanitized.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '+' or '-' ? c : '_');
            }

            return sanitized.ToString();
        }

        /// <summary>
        /// The Android SDK the editor's own Android export is configured with —
        /// the same one the NDK is expected to live under.
        /// </summary>
        private static string GetAndroidSdkPath()
        {
            try
            {
                EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
                if (settings is null || !settings.HasSetting("export/android/android_sdk_path"))
                    return string.Empty;

                return settings.GetSetting("export/android/android_sdk_path").AsString();
            }
            catch (InvalidOperationException)
            {
                // No editor singleton (the assembly is loaded outside the editor).
                return string.Empty;
            }
        }

        /// <summary>
        /// Verifies that xcrun resolves the named Apple SDK to a directory that
        /// exists. The Xcode Command Line Tools ship the macOS SDK only, so the
        /// iOS device and simulator SDKs are what distinguish a full Xcode.
        /// </summary>
        private static void VerifyAppleSdk(string sdk)
        {
            string sdkPath;
            try
            {
                sdkPath = CaptureToolOutput("xcrun", "--sdk", sdk, "--show-sdk-path").Trim();
            }
            catch (Exception)
            {
                sdkPath = string.Empty;
            }

            if (sdkPath.Length == 0 || !Directory.Exists(sdkPath))
            {
                throw new NotSupportedException(
                    $"The dn2cpp export backend requires full Xcode to target iOS (xcrun --sdk {sdk} failed). " +
                    "Install Xcode, select it with 'sudo xcode-select --switch /Applications/Xcode.app', and " +
                    "restart the editor.");
            }
        }

        /// <summary>
        /// Verifies the cmake floor every target shares, and hands back the version
        /// it read — the Web has a second, narrower question to ask of it.
        /// </summary>
        private static Version VerifyCMakeVersion(string cmakeExe)
        {
            string firstLine = CaptureToolOutput(cmakeExe, "--version").Split('\n').FirstOrDefault() ?? string.Empty;

            // "cmake version 3.31.6" (a suffix such as "-rc1" may follow).
            string[] words = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Version? version = null;
            if (words.Length >= 3)
                _ = Version.TryParse(words[2].Split('-')[0], out version);

            if (version is null)
            {
                throw new NotSupportedException(
                    $"Could not determine the version of cmake at '{cmakeExe}' (it reported: {firstLine.Trim()}). " +
                    $"The dn2cpp export backend needs {RequiredCMakeMajor}.{RequiredCMakeMinor} or newer.");
            }

            if (version < new Version(RequiredCMakeMajor, RequiredCMakeMinor))
            {
                throw new NotSupportedException(
                    $"The dn2cpp export backend needs cmake {RequiredCMakeMajor}.{RequiredCMakeMinor} or newer, " +
                    $"but '{cmakeExe}' is {version}.");
            }

            return version;
        }

        /// <summary>
        /// Refuses the cmake versions on which an Emscripten SHARED library is not a
        /// shared library. Emscripten's CMake platform module turns
        /// <c>TARGET_SUPPORTS_SHARED_LIBS</c> off on two bands, and on those cmake
        /// quietly builds <c>add_library(... SHARED ...)</c> as a static archive: the
        /// configure succeeds, the build succeeds, and the side module the whole Web
        /// lane exists to produce is simply never written. The only symptom is a
        /// missing file at staging time, a long way from its cause — so the version
        /// is refused before anything is transpiled or compiled.
        /// </summary>
        private static void VerifyCMakeCanBuildWasmSharedLibs(string cmakeExe, Version version)
        {
            foreach ((Version first, Version pastLast) in CMakeVersionsWithoutWasmSharedLibs)
            {
                if (version >= first && version < pastLast)
                {
                    // The bands are disjoint and ascending, so the last one's upper
                    // bound is the floor that clears every one of them.
                    Version clearsAll = CMakeVersionsWithoutWasmSharedLibs[^1].PastLast;

                    throw new NotSupportedException(
                        $"cmake {version} ('{cmakeExe}') cannot build the WebAssembly side module a C# Web export " +
                        "is. On cmake " +
                        string.Join(" and ", CMakeVersionsWithoutWasmSharedLibs
                            .Select(band => $"[{band.First}, {band.PastLast})")) +
                        ", Emscripten's CMake platform module reports that the target does not support shared " +
                        "libraries, so a shared library is silently built as a static archive instead — and the " +
                        "drop-in the engine has to load is never produced. Install a cmake outside those ranges " +
                        $"({clearsAll} or newer clears all of them).");
                }
            }
        }

        /// <summary>
        /// Runs a tool and returns its standard output. Standard error is left
        /// attached to the editor's: reading only one of two redirected pipes
        /// deadlocks once the other fills.
        /// </summary>
        private static string CaptureToolOutput(string exe, params string[] args)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                },
            };

            foreach (string arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }

        private void RunTool(string exe, List<string> args, string step)
        {
            // The tail quotes the step that failed, so it starts at that step's
            // command line. The parameterless WaitForExit below drains the previous
            // process's asynchronous readers before returning, so nothing of it can
            // still arrive here.
            lock (_logTail)
                _logTail.Clear();

            LogLine($"$ {exe} {string.Join(' ', args)}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };

            foreach (string arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.OutputDataReceived += (_, e) => LogLine(e.Data);
            process.ErrorDataReceived += (_, e) => LogLine(e.Data);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode == 0)
                return;

            var message = new StringBuilder();
            message.Append(CultureInfo.InvariantCulture, $"dn2cpp export failed while {step} (exit code {process.ExitCode}).\n");
            message.Append(CultureInfo.InvariantCulture, $"Full log: {_logPath}\n\n");
            lock (_logTail)
            {
                foreach (string line in _logTail)
                    message.Append(line).Append('\n');
            }

            throw new InvalidOperationException(message.ToString());
        }

        private void LogLine(string? line)
        {
            if (line is null)
                return;

            lock (_logTail)
            {
                _log.WriteLine(line);
                _logTail.Enqueue(line);
                if (_logTail.Count > LogTailLines)
                    _logTail.Dequeue();
            }
        }

        private static void RecreateDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            Directory.CreateDirectory(path);
        }

        public void Dispose()
        {
            _log.Dispose();
        }
    }
}
