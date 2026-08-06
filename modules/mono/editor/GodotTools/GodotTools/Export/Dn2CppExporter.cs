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
        /// The floor emcc.py asserts on the interpreter its launcher starts.
        /// </summary>
        private const int RequiredPythonMajor = 3;
        private const int RequiredPythonMinor = 10;

        /// <summary>
        /// The floor emcc asserts on the node its JS tools run under. This is
        /// emcc's own number and not the version a toolchain bundle pins: the fork
        /// holds no bundle-specific number.
        /// </summary>
        private const int RequiredNodeMajor = 18;

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
        // dn2cpp's gates/expected/buildtools-pin.txt is the cross-repo counterpart:
        // a pin inside these bands ships a cmake this exporter refuses.
        private static readonly (Version First, Version PastLast)[] CMakeVersionsWithoutWasmSharedLibs =
        {
            (new Version(4, 2, 0), new Version(4, 2, 6)),
            (new Version(4, 3, 0), new Version(4, 3, 3)),
        };

        /// <summary>How much of the tool output to quote back in an error message.</summary>
        private const int LogTailLines = 30;

        private readonly Dn2CppToolchain _toolchain;
        private readonly string _cmakeExe;
        private readonly string _ninjaExe;
        private readonly string _godotPlatform;
        private readonly string? _androidNdkRoot;
        private readonly EmscriptenSdk? _emscripten;
        private readonly Dn2CppMsvcEnvironment? _msvc;

        /// <summary>
        /// The environment overlay every tool this export runs is given, a null
        /// VALUE meaning "remove"; null when there is nothing to overlay.
        /// </summary>
        private readonly Dictionary<string, string?>? _toolEnv;
        private readonly string _logPath;
        private readonly StreamWriter _log;
        private readonly Queue<string> _logTail = new Queue<string>();

        /// <summary>
        /// The build configs this export has already transpiled — one transpile
        /// each, its output shared by every runtime-identifier slot of that config.
        /// The build config alone is the key because an exporter serves one Godot
        /// platform.
        /// </summary>
        /// <remarks>
        /// The transpiler's inputs — the game IL, the bundle's pinned framework
        /// closure, the platform's flags — are a function of the export TARGET and
        /// never of the runtime identifier: what a RID decides is the native
        /// compile, which still runs per slot. So a second transpile would emit a
        /// byte-identical tree at a per-slot path, and on iOS's three slots that is
        /// two thirds of the one step whose cost scales with the game.
        /// </remarks>
        private readonly HashSet<string> _transpiled = new HashSet<string>();

        /// <summary>
        /// The slot layout of <c>.godot/mono/dn2cpp</c>, recorded in the work dir as
        /// <c>layout.txt</c>. Bump it whenever a slot's NAME changes: 2 is GE-6's
        /// per-config <c>il/</c> and <c>gen/</c>, 1 the per-RID ones before it (and
        /// an unmarked work dir is 1 or older).
        /// </summary>
        private const int WorkDirLayout = 2;

        /// <summary>The directories under the work dir this exporter writes, and the only ones it deletes.</summary>
        private static readonly string[] WorkDirTrees = { "il", "gen", "build", "stage" };

        /// <summary>
        /// How many export logs the project keeps, this export's included. One per
        /// export, no other writer, and nothing ever removed them.
        /// </summary>
        private const int LogGenerations = 20;

        private bool _workDirPruned;

        private Dn2CppExporter(Dn2CppToolchain toolchain, string cmakeExe, string ninjaExe, string godotPlatform,
            string? androidNdkRoot, EmscriptenSdk? emscripten, Dn2CppMsvcEnvironment? msvc)
        {
            _toolchain = toolchain;
            _cmakeExe = cmakeExe;
            _ninjaExe = ninjaExe;
            _godotPlatform = godotPlatform;
            _androidNdkRoot = androidNdkRoot;
            _emscripten = emscripten;
            _msvc = msvc;
            _toolEnv = emscripten?.Env ?? msvc?.Env;

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string logsDir = Path.Combine(GodotSharpDirs.ProjectBaseOutputPath, "dn2cpp", "logs");
            Directory.CreateDirectory(logsDir);
            _logPath = Path.Combine(logsDir, $"export-{timestamp}.log");
            _log = new StreamWriter(_logPath, append: false) { AutoFlush = true };
            PruneExportLogs(logsDir);

            LogLine($"toolchain: {toolchain.RootDir} ({toolchain.Source})");
            LogLine($"manifest:  {toolchain.DescribeManifest()}");
            LogLine($"cmake:     {cmakeExe}");
            LogLine($"ninja:     {ninjaExe}");
            GD.Print($"dn2cpp: using the toolchain at {toolchain.RootDir} ({toolchain.Source})");
            GD.Print($"dn2cpp: {toolchain.DescribeManifest()}");
            if (emscripten is not null)
            {
                LogLine($"emscripten: {emscripten.Version} ({emscripten.Origin})");
                GD.Print($"dn2cpp: emscripten {emscripten.Version} ({emscripten.Origin})");
            }

            if (msvc is not null)
            {
                LogLine($"msvc:      {msvc.Origin}");
                GD.Print($"dn2cpp: msvc {msvc.Origin}");
            }
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
                // under the Ninja generator on Windows), so the export machine is
                // the only machine it can target — neither its operating system nor
                // its architecture may differ, and the checks are identical.
                //
                // The OS is asked FIRST, because the architecture test alone lets a
                // foreign host straight through: a Linux x86_64 or an Intel macOS
                // host exporting Windows/x86_64 matches on the architecture name and
                // fails on nothing else, so the export runs the whole publish, the
                // transpile and the native compile — minutes — produces a
                // lib<target>.so or .dylib, and dies on a bare "produced no
                // '<assembly>.dll'" that names no cause. Asking the OS first also
                // makes the message the right one: a wrong-OS export is not a
                // cross-architecture refusal.
                if (godotPlatform == OS.Platforms.Windows ? !OS.IsWindows : !OS.IsMacOS)
                {
                    string targetName = godotPlatform == OS.Platforms.Windows ? "Windows" : "macOS";

                    throw new NotSupportedException(
                        "The dn2cpp export backend compiles the game with the host's own C++ compiler, so an " +
                        $"export to {targetName} has to run on {targetName}; this host is not one. Export from a " +
                        $"{targetName} machine, or switch 'dotnet/export_backend' to 'Host Runtime' or 'NativeAOT' " +
                        "for this preset.");
                }

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

            // Ahead of the tool search below, which asks the bundle for a cmake and
            // a ninja before it falls back to PATH.
            if (!Dn2CppToolchain.TryResolve(out Dn2CppToolchain? toolchain, out string toolchainError))
                throw new NotSupportedException(toolchainError);

            var missingTools = new List<string>();
            string? cmakeExe = ResolveCMake(toolchain);
            if (cmakeExe is null)
                missingTools.Add($"cmake ({RequiredCMakeMajor}.{RequiredCMakeMinor} or newer)");
            string? ninjaExe = ResolveNinja(toolchain);
            if (ninjaExe is null)
                missingTools.Add("ninja");
            // The C++ compiler this looks for is the HOST's, and which name that is
            // belongs to the host rather than to the export target: the configure
            // below spells no compiler at all, so cmake picks the platform default —
            // cl.exe under the Ninja generator on Windows, clang++ on macOS. Naming
            // one unconditionally is what made a Windows host refuse a Web or Android
            // export it was perfectly able to serve: those two cross-compile through
            // Emscripten and the NDK, which bring their own compilers, and neither
            // needs the host one to be spelled clang++.
            //
            // Nor does either need a host compiler to EXIST. Emscripten and the NDK
            // each hand cmake a toolchain file naming their own, so no host compiler
            // is invoked anywhere in those two builds — and on Windows, demanding one
            // regardless is not a harmless extra check: MSVC is not on PATH, so the
            // demand refuses a Web export on a box carrying a complete emsdk and
            // points its user at a Visual Studio install the export would never
            // touch. The import below is gated on the same question for that reason.
            bool needsHostCxx = godotPlatform != OS.Platforms.Web && godotPlatform != OS.Platforms.Android;

            // A complete Visual Studio install is invisible until vcvarsall has run,
            // so the editor runs it rather than demanding it was launched from a
            // Developer Command Prompt. Skipped when the environment already carries
            // one: that build works, and replacing its toolset is not this code's
            // call.
            Dn2CppMsvcEnvironment? msvc = null;
            string? msvcFailure = null;
            if (needsHostCxx && OS.IsWindows && !Dn2CppMsvcEnvironment.AlreadyInitialized())
            {
                GD.Print("dn2cpp: importing the Visual Studio C++ environment (this takes a few seconds)");
                msvc = Dn2CppMsvcEnvironment.Import(out msvcFailure);
            }

            bool missingHostCxx = needsHostCxx && HostCxxCompiler(msvc) is null;
            if (missingHostCxx)
            {
                missingTools.Add(OS.IsWindows ? "cl.exe or clang++"
                    : OS.IsMacOS ? "clang++ (Xcode Command Line Tools)"
                    : "clang++ or g++");
            }

            // Emscripten's compiler driver and its wasm tools run on node. A
            // toolchain bundle's SDK carries a pinned one, so the host only has to
            // supply node for an SDK that does not — the question is asked of the
            // SDK this export would use, never of the bundle as a whole. Asked here
            // so a Web export refuses before the publish rather than in the middle
            // of the link.
            string? preflightEmsdkDir = godotPlatform == OS.Platforms.Web ? PreflightEmsdkDir(toolchain) : null;
            bool needsNode = godotPlatform == OS.Platforms.Web
                && !(preflightEmsdkDir is not null && Dn2CppToolchain.HasEmsdkNode(preflightEmsdkDir))
                && OS.PathWhich("node") is null;
            if (needsNode)
                missingTools.Add($"node (Node.js {RequiredNodeMajor} or newer)");

            if (missingTools.Count > 0)
            {
                // One remedy per miss, because the misses do not coincide: a Web or
                // an Android export needs no host compiler at all, so a cmake-only
                // miss there must not send its user to Visual Studio for a compiler
                // that export would never invoke.
                var remedy = new StringBuilder();

                if (cmakeExe is null || ninjaExe is null)
                {
                    // Three arms, not two, and the third is not hypothetical: a host
                    // that is neither Windows nor macOS reaches this backend for the
                    // two cross-compiled targets (Web, Android), which need cmake and
                    // ninja like every other target does. Told to 'brew install', a
                    // Linux user is being sent to a package manager their machine does
                    // not have.
                    remedy.Append(OS.IsWindows
                        ? "Install cmake and ninja (e.g. 'winget install Kitware.CMake Ninja-build.Ninja'), then " +
                          "restart the editor from a shell that has them on PATH."
                        : OS.IsMacOS
                            ? "Install them with 'brew install cmake ninja', then restart the editor. An editor " +
                              "launched from Finder inherits a minimal PATH, so Homebrew tools can be invisible " +
                              "to it even when a terminal finds them."
                            : "Install them with your distribution's package manager (e.g. 'apt install cmake " +
                              "ninja-build'), then restart the editor from a shell that has them on PATH.");
                    remedy.Append(CultureInfo.InvariantCulture,
                        $"\nA toolchain bundle packaged with build tools carries its own pair and needs none of " +
                        $"that; the editor settings '{Dn2CppToolchain.CMakePathSetting}' and " +
                        $"'{Dn2CppToolchain.NinjaPathSetting}' name the two executables directly.");
                }

                if (missingHostCxx)
                {
                    if (remedy.Length > 0)
                        remedy.Append('\n');

                    remedy.Append(OS.IsWindows
                        // The Windows counterpart of the Finder-PATH trap above, and a
                        // sharper one: MSVC is not installed onto PATH at all. So the
                        // miss here is not "no compiler" but "no install the editor's
                        // own search reached", and naming the search's failure is what
                        // separates a machine without Visual Studio from one that put
                        // it somewhere the search does not look.
                        ? "Install the Visual Studio C++ workload (the standalone Build Tools carry it too), " +
                          "then restart the editor. cl.exe is never on PATH, however complete the install is, " +
                          "so the editor finds one through 'Microsoft Visual Studio\\Installer\\vswhere.exe' " +
                          "under %ProgramFiles(x86)% and runs vcvarsall itself — a Developer Command Prompt is " +
                          "not needed. That search found nothing here: " + msvcFailure + ". An install it " +
                          "cannot reach is still served by starting the editor from a Developer Command Prompt."
                        : OS.IsMacOS
                            ? "Install the C++ compiler with 'xcode-select --install', then restart the editor."
                            : "Install a C++ compiler with your distribution's package manager (e.g. 'apt " +
                              "install clang'), then restart the editor from a shell that has it on PATH.");
                }

                if (needsNode)
                {
                    if (remedy.Length > 0)
                        remedy.Append('\n');

                    remedy.Append(CultureInfo.InvariantCulture,
                        $"The Emscripten SDK this export would compile through carries no node of its own, so " +
                        $"it is one on PATH or one the editor setting '{Dn2CppToolchain.EmsdkPathSetting}' " +
                        $"names.\n{NodeRemedy()}");
                }

                throw new NotSupportedException(
                    "The dn2cpp export backend is missing tools it cannot build without: " +
                    $"{string.Join(", ", missingTools)}.\n" + remedy);
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

            // Resolved before the publish for the refusals' sake: an absent NDK is
            // a refusal now, not a cmake error twenty minutes in. The Emscripten SDK
            // is asked of the bundle first, so the toolchain has to be resolved
            // before it.
            string? androidNdkRoot = godotPlatform == OS.Platforms.Android ? ResolveAndroidNdk() : null;
            EmscriptenSdk? emscripten = godotPlatform == OS.Platforms.Web ? ResolveEmscripten(toolchain) : null;

            // Verified after resolution rather than listed among the missing tools
            // above: which interpreter and which node emcc starts are properties of
            // the resolved SDK, and the usual miss is one that is present and too
            // old.
            if (emscripten is not null)
            {
                VerifyEmscriptenPython(emscripten);
                VerifyEmscriptenNode(emscripten);
            }

            // A cross-target export off a Windows host transpiles the POSIX-flavour
            // framework, not the bundle's host one (Dn2CppToolchain.NeedsCrossCoreLib
            // says why). Checked here rather than at the -r, because the alternative
            // is not a missing file: the transpile SUCCEEDS against the Windows
            // framework and the failure surfaces as a linker complaining about
            // -lkernel32, or as an ole32 import no Emscripten link can satisfy —
            // neither of which names a CoreLib flavour, an export backend, or this
            // sentence.
            if (Dn2CppToolchain.NeedsCrossCoreLib(godotPlatform)
                && !File.Exists(toolchain.CrossCoreLibRef))
            {
                throw new NotSupportedException(
                    $"The dn2cpp toolchain at '{toolchain.RootDir}' ({toolchain.Source}) carries no " +
                    $"POSIX framework, which an export to '{godotPlatform}' from a Windows host needs: " +
                    $"'{toolchain.CrossCoreLibRef}' is absent.\n" +
                    "Rebuild the toolchain with dn2cpp's 'dist/package-toolchain.sh' on this host — it " +
                    "stages that framework from a linux-x64 runtime pack, and says how to fetch one if " +
                    "none is installed.");
            }

            return new Dn2CppExporter(toolchain, cmakeExe!, ninjaExe!, godotPlatform, androidNdkRoot, emscripten,
                msvc);
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
            PruneWorkDirGenerations(workDir);
            // The Godot platform is part of the slot, not just the build config and
            // the runtime identifier. The Web builds under the host's RID, so a macOS
            // export and a Web export of one project would otherwise land in the same
            // build directory — and one directory cannot hold both a native CMakeCache
            // and an Emscripten one. cmake either refuses the changed compiler outright
            // or, worse, reuses the cached one and builds the wrong thing.
            string slot = $"{_godotPlatform}-{buildConfig}-{runtimeIdentifier}";
            // The transpile's own slot, one level coarser: see _transpiled.
            string ilSlot = $"{_godotPlatform}-{buildConfig}";
            string ilDir = Path.Combine(workDir, "il", ilSlot);
            string genDir = Path.Combine(workDir, "gen", ilSlot);
            string buildDir = Path.Combine(workDir, "build", slot);
            string stageDir = Path.Combine(workDir, "stage", slot);

            if (_transpiled.Add(buildConfig))
            {
                Transpile(publishOutputDir, assemblyName, ilDir, genDir);
            }
            else
            {
                GD.Print($"dn2cpp: reusing the C++ transpiled for {buildConfig} ({genDir})");
                LogLine($"reusing the C++ transpiled for {buildConfig}: {genDir}");
            }

            // The build directory persists across exports so the runtime and the
            // vendored third-party sources are compiled once; only the regenerated
            // C++ is rebuilt on a re-export.
            //
            // ...but the slot names the export TARGET, and the source tree the
            // persistent cache was configured from is not part of it. So the cache
            // has to be asked whether it still describes this toolchain before the
            // configure trusts it.
            ResetStaleBuildCache(buildDir, slot);
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
                // The generator would find a ninja on PATH, which is where a bundled
                // one is not. A -D rather than an injected PATH because the cache
                // records it, and ResetStaleBuildCache reads it back — typed, or the
                // entry lands as UNINITIALIZED where cmake's own is a FILEPATH.
                $"-DCMAKE_MAKE_PROGRAM:FILEPATH={CMakePath(_ninjaExe)}",
                "-DDN2CPP_DOTNET_MODULE=ON",
                $"-DDN2CPP_APP_DIR={CMakePath(genDir)}",
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
                configureArgs.Add("-DCMAKE_TOOLCHAIN_FILE=" +
                    CMakePath(Path.Combine(_androidNdkRoot!, "build", "cmake", "android.toolchain.cmake")));
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
                RunTool(_emscripten!.EmcmakeExe, emcmakeArgs, "configuring the native build");
            }
            else
            {
                RunTool(_cmakeExe, configureArgs, "configuring the native build");
            }

            RunTool(_cmakeExe, new List<string> { "--build", buildDir }, "compiling the drop-in library");

            return StageBuiltLibrary(buildDir, stageDir, targetName, assemblyName,
                targetsWindows, targetsAndroid, targetsWeb);
        }

        /// <summary>
        /// Transpiles the published game assembly into <paramref name="genDir"/>.
        /// Runs once per build config — see <see cref="_transpiled"/>.
        /// </summary>
        private void Transpile(string publishOutputDir, string assemblyName, string ilDir, string genDir)
        {
            bool targetsWeb = _godotPlatform == OS.Platforms.Web;

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
                // Which framework is a function of the EXPORT TARGET, not of the
                // host: a cross-compiled target needs the POSIX flavour, because the
                // CoreLib's IL is what decides the native libraries the emitted
                // P/Invokes name (Dn2CppToolchain.CoreLibRefFor). Create() has
                // already refused the export if the one this needs is not staged.
                "-r", _toolchain.CoreLibRefFor(_godotPlatform),
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
        }

        /// <summary>
        /// Copies the library the native build produced into a staging directory of
        /// its own, under the name the engine opens, and returns that directory.
        /// </summary>
        private string StageBuiltLibrary(string buildDir, string stageDir, string targetName,
            string assemblyName, bool targetsWindows, bool targetsAndroid, bool targetsWeb)
        {
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
            // Windows is the fourth, and the only one whose BUILT name is not decided
            // by the platform alone: the no-'lib'-prefix rule is the MSVC ABI's, not
            // Windows's. cmake sets CMAKE_SHARED_LIBRARY_PREFIX from the toolchain,
            // so cl.exe and clang-cl write <name>.dll while an MSYS2/MinGW clang++ —
            // which HostCxxCompiler explicitly falls back to — writes lib<name>.dll.
            // Assuming one of the two makes the other fail as the same causeless
            // "produced no <name>.dll", so both are probed. Accepting either is safe
            // because nothing downstream reads the built name: what the engine opens
            // is the STAGED file, which is <assembly>.dll (no prefix, the engine's
            // WINDOWS_ENABLED branch) whichever candidate was found.
            string builtLibrary;
            if (targetsWindows)
            {
                string msvcNamed = Path.Combine(buildDir, $"{targetName}.dll");
                string mingwNamed = Path.Combine(buildDir, $"lib{targetName}.dll");
                builtLibrary = File.Exists(msvcNamed) ? msvcNamed : mingwNamed;
                if (!File.Exists(builtLibrary))
                {
                    throw new InvalidOperationException(
                        $"The dn2cpp native build produced neither '{msvcNamed}' nor '{mingwNamed}'.\n" +
                        $"Log: {_logPath}");
                }
            }
            else
            {
                builtLibrary = Path.Combine(buildDir,
                    $"lib{targetName}.{(targetsAndroid || targetsWeb ? "so" : "dylib")}");
                if (!File.Exists(builtLibrary))
                {
                    throw new InvalidOperationException(
                        $"The dn2cpp native build produced no '{builtLibrary}'.\nLog: {_logPath}");
                }
            }

            RecreateDirectory(stageDir);
            string stagedName = targetsWindows ? $"{assemblyName}.dll"
                : targetsAndroid ? $"lib{assemblyName}.so"
                : targetsWeb ? $"{assemblyName}.so"
                : $"{assemblyName}.dylib";
            string stagedLibrary = Path.Combine(stageDir, stagedName);
            File.Copy(builtLibrary, stagedLibrary, overwrite: true);

            // Nothing copies it into the publish directory as well. The iOS
            // packaging tail lipos one {assembly}.dylib per entry of the export's
            // output-path list, and under this backend those entries ARE these
            // staging directories: the publish directory is shared by every slot
            // of the export, so a dylib written there would be one file for three
            // architectures (ExportPlugin, dn2CppPublishDir).
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

            // The set excluded here must be the set REFERENCED above, and which one
            // that is depends on the target: a cross-target export off a Windows host
            // passes the ref-posix/ closure (Dn2CppToolchain.CoreLibRefFor). Probing a
            // hard-coded ref/ agrees with it only for as long as the two directories
            // hold the same assembly names — an invariant nothing enforces and no
            // failure would announce, since the symptom is a framework assembly passed
            // twice under two flavours. So the directory is derived from the reference
            // itself.
            string frameworkRefDir = Path.GetDirectoryName(_toolchain.CoreLibRefFor(_godotPlatform))!;

            foreach (string candidate in Directory.GetFiles(publishOutputDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(candidate);

                if (name == $"{assemblyName}.dll")
                    continue;
                if (name.StartsWith("GodotSharp", StringComparison.Ordinal) || name == "GodotPlugins.dll")
                    continue;
                if (File.Exists(Path.Combine(frameworkRefDir, name)))
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
                //
                // The skip is LOGGED, never silent. A truncated or otherwise corrupt
                // *managed* assembly reads as native by the same test, and dropping
                // it without a word resurfaces minutes later as a transpiler error
                // about a type that cannot be resolved — a diagnostic pointing at the
                // wrong file entirely. The log line is the only place the two cases
                // are distinguishable.
                if (!IsManagedAssembly(candidate))
                {
                    LogLine($"skipping '{name}': not a managed assembly (no CLI metadata header)");
                    continue;
                }

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
        /// <para>The catches are deliberate and exhaustive over what this call is
        /// documented to throw: only the two that carry an ANSWER are turned into one,
        /// and everything else is a failure to ask the question at all.</para>
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
            catch (Exception e) when (e is System.Security.SecurityException
                                          or ArgumentException
                                          or System.IO.IOException)
            {
                // An unreadable, inaccessible or vanished file: the probe learned
                // nothing, so neither answer is available. Left to escape, it reaches
                // _ExportBegin's catch-all, which shows the raw Message — "Security
                // error." or "Access to the path is denied." — naming neither the file
                // nor the export step, because that handler prints Message and nothing
                // else. NotSupportedException is the file's contract for a refusal the
                // user can act on (bad input, not a broken invariant — those are the
                // InvalidOperationExceptions above), so the diagnostic is built here
                // where the file name is still in hand.
                throw new NotSupportedException(
                    $"The dn2cpp export backend could not read '{path}' from the publish output to decide whether " +
                    $"it is a managed assembly: {e.Message}\n" +
                    "Delete the publish output and export again; if it persists, check the file's permissions.", e);
            }
        }

        /// <summary>
        /// A path in the one spelling CMake uses for paths everywhere: forward
        /// slashes. Applied to the path-valued <c>-D</c> arguments, which are built
        /// here rather than taken verbatim — a Windows path concatenated with a
        /// literal tail yields the mixed <c>C:\…\ndk\27.0/build/cmake/…</c>, which
        /// cmake accepts but then carries into its cache, its logs and its error
        /// messages, where the two conventions in one path read as a defect. The
        /// project-declared link settings are deliberately NOT run through this: they
        /// are pass-through user text, not paths this method built, and a backslash
        /// in a link flag may be an escape rather than a separator.
        /// </summary>
        private static string CMakePath(string path) => path.Replace('\\', '/');

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
        private static string? HostCxxCompiler(Dn2CppMsvcEnvironment? msvc)
        {
            // The imported cl was found on the PATH the overlay installs, which is
            // the PATH the configure runs under; OS.PathWhich can only see this
            // process's, and the editor's own environment is deliberately untouched.
            if (OS.IsWindows)
                return msvc?.ClExe ?? OS.PathWhich("cl") ?? OS.PathWhich("clang++");

            // clang++ first because it is cmake's own first choice and the only
            // compiler a macOS host has; g++ after it because on Linux it is the
            // usual one, and a host carrying g++ alone is a host cmake would have
            // configured happily — refusing it would turn a working machine into a
            // refusal, which is exactly the failure this probe exists to avoid.
            return OS.PathWhich("clang++") ?? OS.PathWhich("g++");
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
        /// The Emscripten SDK a Web export cross-compiles through: the
        /// <c>emcmake</c> the configure runs under, the environment every tool
        /// under it inherits, and where it was found.
        /// </summary>
        private sealed class EmscriptenSdk
        {
            public EmscriptenSdk(string emcmakeExe, Dictionary<string, string?>? env, string origin, string version,
                string? emsdkDir)
            {
                EmcmakeExe = emcmakeExe;
                Env = env;
                Origin = origin;
                Version = version;
                EmsdkDir = emsdkDir;
            }

            public string EmcmakeExe { get; }

            /// <summary>
            /// Environment overlay for every tool this export runs, a null VALUE
            /// meaning "remove". Null for an SDK taken from PATH, which is
            /// configured by whoever put it there — see <see cref="ResolveEmscripten"/>.
            /// </summary>
            public Dictionary<string, string?>? Env { get; }

            public string Origin { get; }

            public string Version { get; }

            /// <summary>
            /// The SDK's root, or null for one taken from PATH — where this backend
            /// found a frontend and knows nothing of the layout behind it.
            /// </summary>
            public string? EmsdkDir { get; }
        }

        /// <summary>
        /// Variables an activated emsdk exports into the shell. An editor started
        /// from such a shell inherits them, and each one would redirect the bundled
        /// SDK's compiler driver at that other SDK's LLVM, binaryen, cache, node
        /// or python —
        /// so a bundled SDK runs with all of them removed rather than merely
        /// out-ranked.
        /// </summary>
        private static readonly string[] ActivatedEmsdkVars =
        {
            "EM_CACHE", "EM_LLVM_ROOT", "EM_BINARYEN_ROOT", "EM_FROZEN_CACHE",
            "EMSDK", "EMSDK_NODE", "EM_NODE_JS", "EMSDK_PYTHON", "EMSCRIPTEN",
        };

        /// <summary>
        /// Resolves the cmake the build configures with: an editor setting naming
        /// one, else the bundle's own, else PATH — <see cref="ResolveEmscripten"/>'s
        /// three arms, and the bundle outranks PATH for its reason. Null means the
        /// PATH arm found nothing, which is the only arm that can come up empty.
        /// </summary>
        private static string? ResolveCMake(Dn2CppToolchain toolchain) =>
            ResolveBuildTool("cmake", Dn2CppToolchain.CMakePathSetting,
                toolchain.HasBuildTools ? toolchain.BundledCMake : null);

        /// <summary>
        /// Resolves ninja, <see cref="ResolveCMake"/>'s twin. Until the build
        /// program was passed by path this was a mere probe — cmake found ninja
        /// itself, from PATH, which is exactly what a bundled ninja is not on.
        /// </summary>
        private static string? ResolveNinja(Dn2CppToolchain toolchain) =>
            ResolveBuildTool("ninja", Dn2CppToolchain.NinjaPathSetting,
                toolchain.HasBuildTools ? toolchain.BundledNinja : null);

        private static string? ResolveBuildTool(string tool, string setting, string? bundled)
        {
            string overridePath = Dn2CppToolchain.GetEditorSetting(setting);
            if (overridePath.Length > 0)
            {
                string exe = Path.GetFullPath(overridePath);
                if (!File.Exists(exe))
                {
                    throw new NotSupportedException(
                        $"The editor setting '{setting}' points at '{exe}', which does not exist.\n" +
                        $"Point it at a {tool} executable, or clear it to use the bundled {tool} — else the one " +
                        "on PATH.");
                }

                return Resolved(tool, exe, $"editor setting '{setting}'");
            }

            if (bundled is not null)
                return Resolved(tool, bundled, "bundled");

            string? onPath = OS.PathWhich(tool);

            return onPath is null ? null : Resolved(tool, onPath, "PATH");
        }

        /// <summary>
        /// Names the arm that answered, for the reason the Emscripten origin is
        /// printed: three arms mean the tool that ran is not the tool a reader of
        /// the log would assume, and a failing build is diagnosed from this line.
        /// </summary>
        private static string Resolved(string tool, string exe, string origin)
        {
            GD.Print($"dn2cpp: {tool} {exe} ({origin})");

            return exe;
        }

        /// <summary>
        /// The SDK root a Web export would use, as far as it is knowable before the
        /// tool checks run: <see cref="ResolveEmscripten"/>'s first two arms, minus
        /// their validation and their environment overlay. Null for the PATH arm,
        /// whose root this backend never computes.
        /// <para>Deliberately not <see cref="ResolveEmscripten"/> itself, which
        /// verifies the python and builds the overlay: running it this early would
        /// reorder the refusals the preflight exists to produce.</para>
        /// </summary>
        private static string? PreflightEmsdkDir(Dn2CppToolchain toolchain)
        {
            string overridePath = Dn2CppToolchain.GetEditorSetting(Dn2CppToolchain.EmsdkPathSetting);
            if (overridePath.Length > 0)
                return Path.GetFullPath(overridePath);

            return toolchain.HasEmsdk ? toolchain.EmsdkDir : null;
        }

        /// <summary>
        /// Resolves the SDK the Web build compiles through: an editor setting
        /// naming one, else the bundle's own, else PATH. The bundle outranks PATH
        /// because it is the SDK this editor's runtime was packaged against, and
        /// only the first two carry a config this backend can point every tool at.
        /// </summary>
        private static EmscriptenSdk ResolveEmscripten(Dn2CppToolchain toolchain)
        {
            string overridePath = Dn2CppToolchain.GetEditorSetting(Dn2CppToolchain.EmsdkPathSetting);
            if (overridePath.Length > 0)
            {
                string emsdkDir = Path.GetFullPath(overridePath);
                if (!Dn2CppToolchain.IsEmsdkLayout(emsdkDir))
                {
                    throw new NotSupportedException(
                        $"The editor setting '{Dn2CppToolchain.EmsdkPathSetting}' points at '{emsdkDir}', which is " +
                        "not an Emscripten SDK: it must hold " +
                        $"'{Dn2CppToolchain.EmsdkEmcmakeIn(emsdkDir)}' and " +
                        $"'{Dn2CppToolchain.EmsdkConfigIn(emsdkDir)}'.\n" +
                        "Point it at an emsdk's active SDK directory, or clear it to use the bundled SDK.");
                }

                return BundledEmscripten(emsdkDir,
                    $"bundled: {emsdkDir}, editor setting '{Dn2CppToolchain.EmsdkPathSetting}'");
            }

            if (toolchain.HasEmsdk)
                return BundledEmscripten(toolchain.EmsdkDir, $"bundled: {toolchain.EmsdkDir}");

            // No SDK travelled with this bundle, so the host has to supply one.
            // em++ is probed beside emcmake because emcmake alone proves nothing:
            // it is a thin wrapper that would hand cmake a toolchain file naming a
            // compiler that is not there, and the failure would land in cmake's
            // compiler check with nothing pointing at the SDK.
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
                    $"PATH: {string.Join(", ", missingTools)}. This toolchain bundle carries no SDK either " +
                    $"('{toolchain.EmsdkDir}' is not one), which is what a bundle packaged on a host that had " +
                    "one would ship.\n" +
                    "Install the Emscripten SDK ('brew install emscripten', or emsdk's own installer), activate " +
                    "it in the environment the editor is launched from ('source /path/to/emsdk/emsdk_env.sh'), " +
                    "and restart the editor. An editor launched from Finder inherits a minimal PATH, so an SDK a " +
                    "terminal finds can be invisible to it; the editor setting " +
                    $"'{Dn2CppToolchain.EmsdkPathSetting}' names one directly.");
            }

            // Nothing is injected into the environment of an SDK taken from PATH:
            // it is the environment that selected that SDK, and overwriting
            // EM_CONFIG or PATH under it would change a build that already works.
            return new EmscriptenSdk(emcmakeExe!, null, $"PATH: {emcmakeExe}",
                ReadEmscriptenVersion(Path.GetDirectoryName(emcmakeExe!) ?? string.Empty), null);
        }

        private static EmscriptenSdk BundledEmscripten(string emsdkDir, string origin)
        {
            string emscriptenDir = Path.Combine(emsdkDir, "emscripten");
            string path = System.Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            var env = new Dictionary<string, string?>();
            foreach (string activated in ActivatedEmsdkVars)
                env[activated] = null;

            env["EM_CONFIG"] = Dn2CppToolchain.EmsdkConfigIn(emsdkDir);

            // The SDK's own node is deliberately absent from this PATH: the config
            // names it, and that is the single authority on which node emcc starts.
            // On PATH it would reach the dotnet publish and the cmake children too.
            env["PATH"] = path.Length > 0 ? emscriptenDir + Path.PathSeparator + path : emscriptenDir;

            // Resolving the bundled python is the environment's job: the SDK's
            // native frontends take EMSDK_PYTHON over any PATH search.
            string bundledPython = Path.Combine(emsdkDir, "python", "python.exe");
            if (File.Exists(bundledPython))
                env["EMSDK_PYTHON"] = bundledPython;

            // The bundled cache is baked and frozen, so a link needing a
            // system-library variant nothing baked fails instead of building one.
            // A writable cache is the way out, and un-freezing it is half of it.
            string cachePath = Dn2CppToolchain.GetEditorSetting(Dn2CppToolchain.EmsdkCachePathSetting);
            if (cachePath.Length > 0)
            {
                env["EM_CACHE"] = Path.GetFullPath(cachePath);
                env["EM_FROZEN_CACHE"] = "0";
            }

            return new EmscriptenSdk(Dn2CppToolchain.EmsdkEmcmakeIn(emsdkDir), env, origin,
                ReadEmscriptenVersion(emscriptenDir), emsdkDir);
        }

        /// <summary>
        /// The SDK's version, read from the file it states it in rather than by
        /// running emcc — which would want node and a warm cache before the export
        /// has decided it can run at all.
        /// </summary>
        private static string ReadEmscriptenVersion(string emscriptenDir)
        {
            try
            {
                string versionFile = Path.Combine(emscriptenDir, "emscripten-version.txt");

                return File.Exists(versionFile)
                    ? File.ReadAllText(versionFile).Trim().Trim('"')
                    : "unknown version";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return "unknown version";
            }
        }

        /// <summary>
        /// The interpreter emcc will start, and the arm that answered. Mirrors the
        /// launchers exactly — EMSDK_PYTHON first, then <c>python3</c> and
        /// <c>python</c> from the sh script, or <c>python.exe</c> alone from
        /// pylauncher.exe. Probing a name emcc does not look for would be a check
        /// that passes while the link fails.
        /// </summary>
        private static (string? Exe, string Origin) ResolveEmscriptenPython(EmscriptenSdk sdk)
        {
            // The overlay is the authority on EMSDK_PYTHON for an SDK that has one:
            // it either names the staged interpreter or REMOVES an inherited one,
            // and reading the process environment gets that wrong in both
            // directions. A PATH SDK has no overlay and keeps what it inherited.
            string? fromEnv = sdk.Env is not null
                ? (sdk.Env.TryGetValue("EMSDK_PYTHON", out string? overlaid) ? overlaid : null)
                : System.Environment.GetEnvironmentVariable("EMSDK_PYTHON");

            if (!string.IsNullOrEmpty(fromEnv))
                return (fromEnv, "EMSDK_PYTHON");

            if (OS.IsWindows)
                return (OS.PathWhich("python"), "PATH");

            return (OS.PathWhich("python3") ?? OS.PathWhich("python"), "PATH");
        }

        /// <summary>
        /// Verifies the python emcc runs on. Every Emscripten entry point is a
        /// launcher over a python script whose first statement asserts the version,
        /// and the SDK ships an interpreter on Windows alone — so everywhere else
        /// emcc gets whatever the host happens to have, and a host with a too-old
        /// one fails in the middle of the link instead of here.
        /// </summary>
        private static void VerifyEmscriptenPython(EmscriptenSdk sdk)
        {
            (string? exe, string origin) = ResolveEmscriptenPython(sdk);
            string required = $"{RequiredPythonMajor}.{RequiredPythonMinor}";

            if (exe is null)
            {
                throw new NotSupportedException(
                    "The dn2cpp export backend compiles the game for the Web with Emscripten, whose emcc is a " +
                    $"launcher over python {required} or newer — and there is none: " +
                    $"{(OS.IsWindows ? "'python'" : "neither 'python3' nor 'python'")} is on PATH, and the " +
                    $"Emscripten SDK in use ({sdk.Origin}) carries no interpreter of its own.\n" + PythonRemedy());
            }

            // Run it: presence is not the question. macOS answers 'python3' with an
            // Xcode stub that reports 3.9.6, which emcc refuses.
            string reported;
            try
            {
                reported = CaptureToolOutput(exe, "-E", "-c",
                    "import sys; print('%d.%d.%d' % sys.version_info[:3])").Trim();
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                          or IOException)
            {
                reported = string.Empty;
            }

            if (!Version.TryParse(reported, out Version? version))
            {
                throw new NotSupportedException(
                    $"Could not determine the version of the python emcc runs, '{exe}' ({origin}) — it reported: " +
                    $"{reported}. The dn2cpp export backend needs python {required} or newer to export for the " +
                    "Web.\n" + PythonRemedy());
            }

            if (version < new Version(RequiredPythonMajor, RequiredPythonMinor))
            {
                throw new NotSupportedException(
                    "The dn2cpp export backend compiles the game for the Web with Emscripten, whose emcc needs " +
                    $"python {required} or newer, but the interpreter it would run — '{exe}' ({origin}) — is " +
                    $"{version}.\n" + PythonRemedy());
            }

            // Printed for the reason the cmake and Emscripten origins are: three
            // arms mean the interpreter that ran is not the one a reader would
            // assume, and it is the only proof this check ran at all.
            GD.Print($"dn2cpp: python {exe} ({version}, {origin})");
        }

        private static string PythonRemedy()
        {
            string required = $"{RequiredPythonMajor}.{RequiredPythonMinor}";

            if (OS.IsMacOS)
            {
                // Worth spelling out: a user who "has python3" is being refused, and
                // the thing they have is not a python — /usr/bin/python3 is the Xcode
                // command-line stub, the same binary as /usr/bin/clang++, and it
                // answers 3.9.6 forever.
                return "macOS ships no python of its own — '/usr/bin/python3' is an Xcode command-line stub " +
                    "stuck at 3.9, and emcc will not run on it. Install a real one with 'brew install python3' " +
                    "and restart the editor. An editor launched from Finder inherits a minimal PATH, so a " +
                    "Homebrew python a terminal finds can be invisible to it.";
            }

            return OS.IsWindows
                ? $"Install python {required} or newer (e.g. 'winget install Python.Python.3.12'), then restart " +
                  "the editor from a shell that has it on PATH."
                : $"Install python {required} or newer with your distribution's package manager (e.g. 'apt " +
                  "install python3'), then restart the editor from a shell that has it on PATH.";
        }

        /// <summary>
        /// The node emcc will start, and the arm that answered. The SDK's own node
        /// is COMPUTED from the layout rather than read out of <c>.emscripten</c>,
        /// the flavour <see cref="ResolveEmscriptenPython"/> takes with
        /// <c>EMSDK_PYTHON</c>.
        /// </summary>
        private static (string? Exe, string Origin) ResolveEmscriptenNode(EmscriptenSdk sdk)
        {
            // The overlay is the authority for an SDK that has one, exactly as it is
            // for EMSDK_PYTHON: it either names a node or REMOVES an inherited one,
            // and the process environment gets that wrong in both directions.
            string? fromEnv = sdk.Env is not null
                ? (sdk.Env.TryGetValue("EM_NODE_JS", out string? overlaid) ? overlaid : null)
                : System.Environment.GetEnvironmentVariable("EM_NODE_JS");

            if (!string.IsNullOrEmpty(fromEnv))
                return (fromEnv, "EM_NODE_JS");

            if (sdk.EmsdkDir is not null && Dn2CppToolchain.HasEmsdkNode(sdk.EmsdkDir))
                return (Dn2CppToolchain.EmsdkNodeIn(sdk.EmsdkDir), "bundled");

            return (OS.PathWhich("node"), "PATH");
        }

        /// <summary>
        /// Verifies the node emcc runs its JS tools on. Every link starts one — the
        /// driver shells out to compiler.mjs and to <c>node --check</c> — so a node
        /// that is absent or too old fails in the middle of the link rather than
        /// here.
        /// </summary>
        private static void VerifyEmscriptenNode(EmscriptenSdk sdk)
        {
            (string? exe, string origin) = ResolveEmscriptenNode(sdk);

            if (exe is null)
            {
                throw new NotSupportedException(
                    "The dn2cpp export backend compiles the game for the Web with Emscripten, whose every link " +
                    $"runs node {RequiredNodeMajor} or newer — and there is none: 'node' is not on PATH, and the " +
                    $"Emscripten SDK in use ({sdk.Origin}) carries none of its own.\n" + NodeRemedy());
            }

            // Run it: presence is not the question, and a distribution's 'node' can
            // be years behind what emcc's JS asks of it.
            string reported;
            try
            {
                reported = CaptureToolOutput(exe, "--version").Trim().TrimStart('v');
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                          or IOException)
            {
                reported = string.Empty;
            }

            if (!Version.TryParse(reported, out Version? version))
            {
                throw new NotSupportedException(
                    $"Could not determine the version of the node emcc runs, '{exe}' ({origin}) — it reported: " +
                    $"{reported}. The dn2cpp export backend needs node {RequiredNodeMajor} or newer to export " +
                    "for the Web.\n" + NodeRemedy());
            }

            if (version.Major < RequiredNodeMajor)
            {
                throw new NotSupportedException(
                    "The dn2cpp export backend compiles the game for the Web with Emscripten, whose links need " +
                    $"node {RequiredNodeMajor} or newer, but the one they would run — '{exe}' ({origin}) — is " +
                    $"{version}.\n" + NodeRemedy());
            }

            // Printed for the reason the python and cmake origins are: three arms
            // mean the node that ran is not the one a reader would assume, and it is
            // the only proof this check ran at all.
            GD.Print($"dn2cpp: node {exe} ({version}, {origin})");
        }

        private static string NodeRemedy() =>
            $"Install Node.js {RequiredNodeMajor} or newer (e.g. 'brew install node', 'apt install nodejs', or " +
            "nodejs.org) and restart the editor. A toolchain bundle's Emscripten SDK carries a pinned node and " +
            "needs none of that.";

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

            // Applied to every step, not to the configure alone: cmake runs ninja
            // and ninja runs the compiler, and it is emcc that reads EM_CONFIG,
            // cl.exe that reads INCLUDE and link.exe that reads LIB — cmake bakes
            // none of them into the Ninja files. One overlay serves both because
            // they exclude each other: emsdk is Web's, MSVC every target but Web
            // and Android.
            if (_toolEnv is { } toolEnv)
            {
                foreach ((string name, string? value) in toolEnv)
                {
                    if (value is null)
                        process.StartInfo.Environment.Remove(name);
                    else
                        process.StartInfo.Environment[name] = value;
                }
            }

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

            bool frozenCache = false;
            lock (_logTail)
            {
                foreach (string line in _logTail)
                {
                    frozenCache |= line.Contains("FROZEN_CACHE", StringComparison.Ordinal);
                    message.Append(line).Append('\n');
                }
            }

            // Emscripten's own message names the variable and no way out of it: the
            // bundled SDK's cache is baked for the flags this backend passes, and a
            // build that adds others (-pthread) needs a variant nothing baked.
            if (frozenCache && _emscripten?.Env is not null)
            {
                message.Append(CultureInfo.InvariantCulture,
                    $"\nThe bundled Emscripten SDK carries a read-only, pre-built cache, and this build needs a " +
                    $"system-library variant it does not hold. Set the editor setting " +
                    $"'{Dn2CppToolchain.EmsdkCachePathSetting}' to a writable directory and export again — the " +
                    $"missing variant is built there once.\n");
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

        /// <summary>
        /// Discard the work-dir trees an earlier slot layout wrote, once per export.
        /// </summary>
        /// <remarks>
        /// <para>Every directory under the work dir is named for the export target it
        /// serves, and that naming has changed once already: GE-6 made <c>il/</c> and
        /// <c>gen/</c> per-config where they had been per-RID, orphaning three ~90 MB
        /// trees in every iOS project. Nothing collected them, and nothing could — the
        /// four <see cref="RecreateDirectory"/> sites each clear the slot they are
        /// about to rewrite, so by construction they never reach a name no export
        /// computes any more.</para>
        /// <para>Hence a RECORDED generation rather than an inferred one. Deciding
        /// from a directory's name whether some export could still produce it would be
        /// a second, drifting expression of the slot spelling whose failure direction
        /// is deleting a live cache; an integer answers the narrower question "this
        /// tree was written by a layout I do not know", whose remedy is the same
        /// whatever that layout was. Being wrong costs one rebuild: <c>il/</c>,
        /// <c>gen/</c> and <c>stage/</c> are recreated by every export in any case, and
        /// <c>build/</c> is a compile cache the next export refills.</para>
        /// </remarks>
        private void PruneWorkDirGenerations(string workDir)
        {
            if (_workDirPruned)
                return;
            _workDirPruned = true;

            Directory.CreateDirectory(workDir);
            string marker = Path.Combine(workDir, "layout.txt");
            string current = WorkDirLayout.ToString(CultureInfo.InvariantCulture);
            string? recorded = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (string.Equals(recorded, current, StringComparison.Ordinal))
                return;

            long reclaimed = 0;
            var removed = new List<string>();
            foreach (string tree in WorkDirTrees)
            {
                // Every path deleted here is a name spelled above joined to the work
                // dir this exporter computed — never one read back out of the tree.
                string path = Path.Combine(workDir, tree);
                if (!Directory.Exists(path))
                    continue;
                reclaimed += DirectorySize(path);
                Directory.Delete(path, recursive: true);
                removed.Add(tree);
            }

            if (removed.Count > 0)
            {
                string what = $"work dir written by slot layout {recorded ?? "<unmarked>"}, this editor " +
                    $"writes {current} — removed {string.Join(", ", removed)} ({reclaimed / (1024 * 1024)} MB) " +
                    $"under {workDir}";
                GD.Print($"dn2cpp: {what}");
                LogLine(what);
            }

            File.WriteAllText(marker, current + "\n");
        }

        /// <summary>
        /// Keep the newest <see cref="LogGenerations"/> export logs and delete the rest.
        /// </summary>
        /// <remarks>
        /// <para>A COUNT, not an age: what makes a log worth keeping is being one of
        /// the last few exports, and a project exported once a quarter would keep
        /// nothing to compare against under any age that bounds a project exported
        /// hourly. Newest is decided by an ordinal sort of the NAMES — the timestamp
        /// this constructor spells sorts that way by construction, whereas a file's
        /// mtime is rewritten by copying the project.</para>
        /// <para>Every path deleted is a name this same constructor spelled, under the
        /// directory it computed; nothing else writes there. A prune that cannot run
        /// is not an export failure — the logs are a diagnostic.</para>
        /// </remarks>
        private void PruneExportLogs(string logsDir)
        {
            try
            {
                foreach (string old in Directory.GetFiles(logsDir, "export-*.log")
                             // Windows matches a pattern against the 8.3 short name as
                             // well, so the shape is re-tested on the real one.
                             .Where(f => Path.GetFileName(f).StartsWith("export-", StringComparison.Ordinal)
                                 && Path.GetFileName(f).EndsWith(".log", StringComparison.Ordinal))
                             .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                             .Skip(LogGenerations)
                             .ToList())
                {
                    File.Delete(old);
                    LogLine($"removed the superseded export log {Path.GetFileName(old)}");
                }
            }
            catch (IOException e)
            {
                LogLine($"could not prune the export logs under {logsDir}: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                LogLine($"could not prune the export logs under {logsDir}: {e.Message}");
            }
        }

        /// <summary>Bytes held by a directory tree, for the reclaimed-space report.</summary>
        private static long DirectorySize(string path)
        {
            long total = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // A size is a diagnostic; a tree that cannot be walked is still deleted.
            }
            catch (UnauthorizedAccessException)
            {
            }
            return total;
        }

        /// <summary>
        /// Discard a persistent build directory whose CMake cache was configured
        /// from a different source tree, or by a different pair of build tools, than
        /// this export is about to drive.
        /// </summary>
        /// <remarks>
        /// <para>A CMake cache records the source directory it was configured from,
        /// and cmake refuses outright when a later configure names another one — the
        /// error talks about the binary directory and CMakeCache.txt, names neither
        /// dn2cpp nor the export, and its remedy (delete exactly one directory) is
        /// not in it. Since the build directory is deliberately kept across exports,
        /// every way the toolchain's path can move is a way to reach that error:
        /// re-pointing <c>dotnet/export/dn2cpp_toolchain_path</c>, an editor whose
        /// <c>GodotSharp/Dn2Cpp</c> landed somewhere else than last time, a project
        /// directory copied off another machine.</para>
        /// <para>The test is the cache's OWN declaration rather than the toolchain's
        /// identity folded into the slot, and that is a deliberate choice in both
        /// directions. The slot exists so the runtime and the vendored third-party
        /// sources compile once per export target; keying it on anything that tracks
        /// the toolchain's CONTENT — the bundle manifest's <c>content_hash</c>, say —
        /// would throw that away every time dn2cpp is rebuilt, which is the whole
        /// reason the directory persists. Keying it on the toolchain's PATH would
        /// work, but it answers a narrower question: the cache's declaration also
        /// catches a build tree that was moved rather than a toolchain, and it is
        /// what cmake itself is going to compare against, so there is no second
        /// notion of "same tree" to keep in agreement.</para>
        /// </remarks>
        private void ResetStaleBuildCache(string buildDir, string slot)
        {
            string cacheFile = Path.Combine(buildDir, "CMakeCache.txt");
            if (!File.Exists(cacheFile))
                return;

            // The build dir persists across exports, so the tool that wrote it is
            // not necessarily the tool about to run — a flipped editor setting is
            // enough. Discard it when it is not.
            var pinned = new List<(string Name, string Expected, string What)>
            {
                ("CMAKE_HOME_DIRECTORY", _toolchain.RuntimeDir, "runtime sources"),
                ("CMAKE_COMMAND", _cmakeExe, "cmake"),
                ("CMAKE_MAKE_PROGRAM", _ninjaExe, "build program"),
            };

            // cmake never re-detects a compiler it has cached, so a tree configured
            // by another toolset would take this import's INCLUDE and LIB. Asked
            // only when the import ran: elsewhere the cached compiler is one cmake
            // chose for itself (/usr/bin/c++ where the probe found clang++), and an
            // unconditional test would recompile the whole runtime every export.
            if (_msvc is not null)
                pinned.Add(("CMAKE_CXX_COMPILER", _msvc.ClExe, "C++ compiler"));

            var cached = new string?[pinned.Count];
            foreach (string line in File.ReadLines(cacheFile))
            {
                // NAME:TYPE=VALUE, matched on the NAME alone: an entry's type is
                // whatever wrote it — the generator's FILEPATH, or the type a -D
                // spelled — and none of that decides which tool the cache names.
                int colon = line.IndexOf(':');
                int equals = colon < 0 ? -1 : line.IndexOf('=', colon);
                if (equals < 0)
                    continue;

                string name = line.Substring(0, colon);
                for (int i = 0; i < pinned.Count; i++)
                {
                    if (cached[i] is null && name == pinned[i].Name)
                        cached[i] = line.Substring(equals + 1).Trim();
                }
            }

            for (int i = 0; i < pinned.Count; i++)
            {
                // A cache missing one of these is one cmake never finished writing
                // (an interrupted or failed first configure). It is not "current",
                // and the configure that follows would fail on it, so it goes too.
                if (cached[i] is { } value && SamePath(value, pinned[i].Expected))
                    continue;

                GD.Print($"dn2cpp: stale build cache reset ({slot}): its CMakeCache.txt names " +
                    $"'{cached[i] ?? "<nothing>"}' as the {pinned[i].What}, but this export uses " +
                    $"'{pinned[i].Expected}' — recreating {buildDir}");
                RecreateDirectory(buildDir);

                return;
            }
        }

        /// <summary>
        /// Whether two paths name one file or directory, as far as a CMake cache
        /// value and a path this process built can be compared.
        /// </summary>
        /// <remarks>
        /// Three normalizations, and on Windows all three are load-bearing at once:
        /// cmake writes cache paths with forward slashes on every platform while
        /// <see cref="Dn2CppToolchain.RuntimeDir"/> arrives with the platform's own
        /// separator (the same fold <see cref="CMakePath"/> applies going the other
        /// way), a trailing separator is not a difference, and NTFS is
        /// case-insensitive — so a comparison that is exact on any of the three
        /// reports a stale cache on every export and recompiles the runtime each
        /// time, which is the opposite failure and a silent one.
        /// </remarks>
        private static bool SamePath(string a, string b)
        {
            static string Normalize(string path) => CMakePath(path).TrimEnd('/');

            return string.Equals(Normalize(a), Normalize(b),
                OS.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        public void Dispose()
        {
            _log.Dispose();
        }
    }
}
