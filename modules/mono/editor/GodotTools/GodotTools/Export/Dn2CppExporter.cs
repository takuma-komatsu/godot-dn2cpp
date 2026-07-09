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

        /// <summary>How much of the tool output to quote back in an error message.</summary>
        private const int LogTailLines = 30;

        private readonly Dn2CppToolchain _toolchain;
        private readonly string _cmakeExe;
        private readonly string _logPath;
        private readonly StreamWriter _log;
        private readonly Queue<string> _logTail = new Queue<string>();

        private Dn2CppExporter(Dn2CppToolchain toolchain, string cmakeExe)
        {
            _toolchain = toolchain;
            _cmakeExe = cmakeExe;

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
            if (godotPlatform != OS.Platforms.MacOS)
            {
                throw new NotSupportedException(
                    $"The dn2cpp export backend supports macOS only for now, not '{godotPlatform}'. " +
                    "Switch 'dotnet/export_backend' to 'Host Runtime' or 'NativeAOT' for this preset.");
            }

            // The bundle compiles the game with the host's clang++, so the export
            // machine's architecture is the only one it can target.
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

            var missingTools = new List<string>();
            string? cmakeExe = OS.PathWhich("cmake");
            if (cmakeExe is null)
                missingTools.Add($"cmake ({RequiredCMakeMajor}.{RequiredCMakeMinor} or newer)");
            if (OS.PathWhich("ninja") is null)
                missingTools.Add("ninja");
            if (OS.PathWhich("clang++") is null)
                missingTools.Add("clang++ (Xcode Command Line Tools)");

            if (missingTools.Count > 0)
            {
                throw new NotSupportedException(
                    "The dn2cpp export backend needs a C++ toolchain that is not on PATH: " +
                    $"{string.Join(", ", missingTools)}.\n" +
                    "Install it with 'xcode-select --install' and 'brew install cmake ninja', then restart the " +
                    "editor. An editor launched from Finder inherits a minimal PATH, so Homebrew tools can be " +
                    "invisible to it even when a terminal finds them.");
            }

            VerifyCMakeVersion(cmakeExe!);

            if (!Dn2CppToolchain.TryResolve(out Dn2CppToolchain? toolchain, out string toolchainError))
                throw new NotSupportedException(toolchainError);

            return new Dn2CppExporter(toolchain, cmakeExe!);
        }

        /// <summary>
        /// Transpiles and compiles the published game assembly into a drop-in
        /// library, and returns a directory holding exactly that library — the
        /// caller packages its contents into the project data directory.
        /// </summary>
        /// <remarks>
        /// The directory deliberately holds nothing else. The engine only reaches
        /// <c>try_load_native_aot_library</c> when it finds no hostfxr and no
        /// coreclr next to the game, so shipping the publish directory's runtime
        /// alongside would route the exported game straight back to the .NET host.
        /// </remarks>
        public string BuildDropIn(string publishOutputDir, string assemblyName, string buildConfig,
            string runtimeIdentifier, string arch)
        {
            // Create refuses any target set other than the host's own, but it sees
            // one publish config and the caller loops over every architecture of
            // every one of them. A foreign architecture reaching here would stage a
            // host-compiled library into another architecture's data directory,
            // where the engine would load it on no machine at all.
            string hostArch = GetHostArchitecture();
            if (arch != hostArch)
            {
                throw new InvalidOperationException(
                    $"The dn2cpp export backend compiles for '{hostArch}', but the export is packaging '{arch}'.");
            }

            string workDir = Path.Combine(MonoDataDir, "dn2cpp");
            string slot = $"{buildConfig}-{runtimeIdentifier}";
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
            RunTool(_cmakeExe, new List<string>
            {
                "-S", _toolchain.RuntimeDir,
                "-B", buildDir,
                "-G", "Ninja",
                "-DDN2CPP_DOTNET_MODULE=ON",
                $"-DDN2CPP_APP_DIR={genDir}",
                $"-DDN2CPP_APP_NAME={assemblyName}",
            }, "configuring the native build");
            RunTool(_cmakeExe, new List<string> { "--build", buildDir }, "compiling the drop-in library");

            // CMake names a SHARED target's output lib<name>.dylib; the engine opens
            // <name>.dylib, the name a NativeAOT publish produces.
            string builtLibrary = Path.Combine(buildDir, $"lib{assemblyName}.dylib");
            if (!File.Exists(builtLibrary))
            {
                throw new InvalidOperationException(
                    $"The dn2cpp native build produced no '{builtLibrary}'.\nLog: {_logPath}");
            }

            RecreateDirectory(stageDir);
            string stagedLibrary = Path.Combine(stageDir, $"{assemblyName}.dylib");
            File.Copy(builtLibrary, stagedLibrary, overwrite: true);

            LogLine($"staged {stagedLibrary}");
            GD.Print($"dn2cpp: staged {stagedLibrary}");

            return stageDir;
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

                dependencies.Add(candidate);
            }

            dependencies.Sort(StringComparer.Ordinal);
            return dependencies;
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

        private static void VerifyCMakeVersion(string cmakeExe)
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
