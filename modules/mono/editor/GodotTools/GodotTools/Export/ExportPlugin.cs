using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GodotTools.Build;
using GodotTools.Internals;
using Directory = GodotTools.Utils.Directory;
using File = GodotTools.Utils.File;
using OS = GodotTools.Utils.OS;
using Path = System.IO.Path;
using System.Globalization;

namespace GodotTools.Export
{
    public partial class ExportPlugin : EditorExportPlugin
    {
        /// <summary>
        /// How the exported game runs its C# code. The values are the indices of
        /// the <c>dotnet/export_backend</c> enum hint, so they are stored in
        /// export presets and must not be reordered.
        /// </summary>
        private enum ExportBackend
        {
            /// <summary>The .NET host runtime loads the published managed assemblies.</summary>
            HostRuntime = 0,

            /// <summary>The publish is ahead-of-time compiled into a drop-in native library.</summary>
            NativeAot = 1,

            /// <summary>The published IL is transpiled to C++ and compiled into a drop-in native library.</summary>
            Dn2Cpp = 2,
        }

        public override string _GetName() => "C#";

        private List<string> _tempFolders = new List<string>();

        private static bool ProjectContainsDotNet()
        {
            return File.Exists(GodotSharpDirs.ProjectSlnPath);
        }

        public override string[] _GetExportFeatures(EditorExportPlatform platform, bool debug)
        {
            if (!ProjectContainsDotNet())
                return Array.Empty<string>();

            return new string[] { "dotnet" };
        }

        public override Godot.Collections.Array<Godot.Collections.Dictionary> _GetExportOptions(EditorExportPlatform platform)
        {
            var exportOptionList = new Godot.Collections.Array<Godot.Collections.Dictionary>();

            if (platform.GetOsName().Equals(OS.Platforms.Android, StringComparison.OrdinalIgnoreCase))
            {
                exportOptionList.Add
                (
                    new Godot.Collections.Dictionary()
                    {
                        {
                            "option", new Godot.Collections.Dictionary()
                            {
                                { "name", "dotnet/android_use_linux_bionic" },
                                { "type", (int)Variant.Type.Bool }
                            }
                        },
                        { "default_value", false }
                    }
                );
            }

            exportOptionList.Add
            (
                new Godot.Collections.Dictionary()
                {
                    {
                        "option", new Godot.Collections.Dictionary()
                        {
                            { "name", "dotnet/include_scripts_content" },
                            { "type", (int)Variant.Type.Bool }
                        }
                    },
                    { "default_value", false }
                }
            );
            exportOptionList.Add
            (
                new Godot.Collections.Dictionary()
                {
                    {
                        "option", new Godot.Collections.Dictionary()
                        {
                            { "name", "dotnet/include_debug_symbols" },
                            { "type", (int)Variant.Type.Bool }
                        }
                    },
                    { "default_value", true }
                }
            );
            exportOptionList.Add
            (
                new Godot.Collections.Dictionary()
                {
                    {
                        "option", new Godot.Collections.Dictionary()
                        {
                            { "name", "dotnet/embed_build_outputs" },
                            { "type", (int)Variant.Type.Bool }
                        }
                    },
                    { "default_value", false }
                }
            );
            exportOptionList.Add
            (
                new Godot.Collections.Dictionary()
                {
                    {
                        "option", new Godot.Collections.Dictionary()
                        {
                            { "name", "dotnet/export_backend" },
                            { "type", (int)Variant.Type.Int },
                            { "hint", (int)PropertyHint.Enum },
                            { "hint_string", "Host Runtime,NativeAOT,dn2cpp" }
                        }
                    },
                    { "default_value", (int)ExportBackend.HostRuntime }
                }
            );
            return exportOptionList;
        }

        private void AddExceptionMessage(EditorExportPlatform platform, Exception exception)
        {
            string? exceptionMessage = exception.Message;
            if (string.IsNullOrEmpty(exceptionMessage))
            {
                exceptionMessage = $"Exception thrown: {exception.GetType().Name}";
            }

            platform.AddMessage(EditorExportPlatform.ExportMessageType.Error, "Export .NET Project", exceptionMessage);

            // We also print exceptions as we receive them to stderr.
            Console.Error.WriteLine(exception);
        }

        // With this method we can override how a file is exported in the PCK
        public override void _ExportFile(string path, string type, string[] features)
        {
            base._ExportFile(path, type, features);

            if (type != Internal.CSharpLanguageType)
                return;

            if (Path.GetExtension(path) != Internal.CSharpLanguageExtension)
                throw new ArgumentException(
                    $"Resource of type {Internal.CSharpLanguageType} has an invalid file extension: {path}",
                    nameof(path));

            if (!ProjectContainsDotNet())
            {
                GetExportPlatform().AddMessage(EditorExportPlatform.ExportMessageType.Error, "Export .NET Project", $"This project contains C# files but no solution file was found at the following path: {GodotSharpDirs.ProjectSlnPath}\n" +
                    "A solution file is required for projects with C# files. Please ensure that the solution file exists in the specified location and try again.");
                throw new InvalidOperationException($"{path} is a C# file but no solution file exists.");
            }

            // TODO: What if the source file is not part of the game's C# project?

            bool includeScriptsContent = (bool)GetOption("dotnet/include_scripts_content");

            if (!includeScriptsContent)
            {
                // We don't want to include the source code on exported games.

                // Sadly, Godot prints errors when adding an empty file (nothing goes wrong, it's just noise).
                // Because of this, we add a file which contains a line break.
                AddFile(path, System.Text.Encoding.UTF8.GetBytes("\n"), remap: false);

                // Tell the Godot exporter that we already took care of the file.
                Skip();
            }
        }

        public override void _ExportBegin(string[] features, bool isDebug, string path, uint flags)
        {
            base._ExportBegin(features, isDebug, path, flags);

            try
            {
                _ExportBeginImpl(features, isDebug, path, flags);
            }
            catch (Exception e)
            {
                AddExceptionMessage(GetExportPlatform(), e);
            }
        }

        private void _ExportBeginImpl(string[] features, bool isDebug, string path, long flags)
        {
            _ = flags; // Unused.

            if (!ProjectContainsDotNet())
                return;

            string osName = GetExportPlatform().GetOsName();

            if (!TryDeterminePlatformFromOSName(osName, out string? platform))
                throw new NotSupportedException("Target platform not supported.");

            if (!new[]
                    {
                        OS.Platforms.Windows, OS.Platforms.LinuxBSD, OS.Platforms.MacOS, OS.Platforms.Android,
                        OS.Platforms.iOS, OS.Platforms.Web,
                    }
                    .Contains(platform))
            {
                throw new NotImplementedException("Target platform not yet implemented.");
            }

            var exportBackend = (ExportBackend)(int)GetOption("dotnet/export_backend");

            // Read before anything is published: every way the Web can fail is a
            // preset checkbox, and finding that out after a transpile and a native
            // build is finding it out the slow way.
            if (platform == OS.Platforms.Web)
            {
                VerifyWebPreset(exportBackend, features);
            }

            bool useAndroidLinuxBionic = (bool)GetOption("dotnet/android_use_linux_bionic");
            PublishConfig publishConfig = new()
            {
                BuildConfig = isDebug ? "ExportDebug" : "ExportRelease",
                IncludeDebugSymbols = (bool)GetOption("dotnet/include_debug_symbols"),
                RidOS = DetermineRuntimeIdentifierOS(platform, useAndroidLinuxBionic),
                Archs = [],
                UseTempDir = platform != OS.Platforms.iOS, // xcode project links directly to files in the publish dir, so use one that sticks around.
                BundleOutputs = true,
            };

            if (features.Contains("x86_64"))
            {
                publishConfig.Archs.Add("x86_64");
            }

            if (features.Contains("x86_32"))
            {
                publishConfig.Archs.Add("x86_32");
            }

            if (features.Contains("arm64"))
            {
                publishConfig.Archs.Add("arm64");
            }

            if (features.Contains("arm32"))
            {
                publishConfig.Archs.Add("arm32");
            }

            if (features.Contains("universal"))
            {
                if (platform == OS.Platforms.MacOS)
                {
                    publishConfig.Archs.Add("x86_64");
                    publishConfig.Archs.Add("arm64");
                }
            }

            if (features.Contains("wasm32"))
            {
                if (platform == OS.Platforms.Web)
                {
                    // The only architecture the Web platform has. It is not a
                    // .NET publish architecture — the publish runs under the
                    // host's runtime identifier, see DetermineRuntimeIdentifierOS —
                    // but it is the name the export and the engine agree on, so it
                    // is what the packaging loop below is keyed on.
                    publishConfig.Archs.Add("wasm32");
                }
            }

            // Fails before the publish runs, so an unusable target or a missing C++
            // toolchain costs no build time. It is handed the architectures that were
            // just resolved, not the features they came from: those are what the loop
            // below publishes and packages.
            using Dn2CppExporter? dn2CppExporter = exportBackend == ExportBackend.Dn2Cpp
                ? Dn2CppExporter.Create(platform, publishConfig.Archs)
                : null;

            // The NativeAOT backend is entirely a publish-time property; the native
            // output it leaves in the publish directory is picked up below.
            List<string>? publishProperties = exportBackend switch
            {
                ExportBackend.NativeAot => new List<string> { "PublishAot=true" },
                // The dn2cpp backend consumes plain IL, so it undoes the AOT
                // publish the SDK's iOS.props forces (these are passed as global
                // -p: properties, which beat the .props) and keeps the publish
                // framework-dependent — no runtime ships next to the game. The
                // -p: pairs also land after --self-contained on the publish
                // command line, so the later definition wins there too.
                //
                // Android wants the same treatment for the second reason alone:
                // the drop-in replaces the runtime wholesale, so a self-contained
                // publish would restore a runtime pack the export then throws
                // away — minutes of download for bytes nothing packages.
                //
                // The Web wants it for that reason and one of its own: it publishes
                // under the HOST's runtime identifier, so a self-contained publish
                // would drop a macOS (or Linux, or Windows) runtime beside the game
                // IL — an apphost for an operating system the browser is not, and a
                // second System.Private.CoreLib.dll that would outrank the bundle's
                // pinned one in the transpiler's --auto-ref search.
                ExportBackend.Dn2Cpp when platform == OS.Platforms.iOS || platform == OS.Platforms.Android
                    || platform == OS.Platforms.Web =>
                    new List<string>
                    {
                        "PublishAot=false",
                        "PublishAotUsingRuntimePack=false",
                        "UseNativeAOTRuntime=false",
                        "SelfContained=false",
                        "UseAppHost=false",
                    },
                _ => null,
            };

            var targets = new List<PublishConfig> { publishConfig };

            if (platform == OS.Platforms.iOS)
            {
                targets.Add(new PublishConfig
                {
                    BuildConfig = publishConfig.BuildConfig,
                    Archs = ["arm64", "x86_64"],
                    BundleOutputs = false,
                    IncludeDebugSymbols = publishConfig.IncludeDebugSymbols,
                    RidOS = OS.DotNetOS.iOSSimulator,
                    UseTempDir = false,
                });
            }

            List<string> outputPaths = new();

            // Not on the Web, whatever the option says. There the drop-in has to
            // land next to index.html as a file the loader can fetch and register
            // before main() runs: AddSharedObject is what puts it there (and into
            // the generated HTML's gdextensionLibs), and nothing ever reads it back
            // out of the pck. A user who ticks 'embed_build_outputs' would otherwise
            // get a game whose C# module is packed where no dlopen can reach it —
            // an export that succeeds and produces a game that cannot start.
            bool embedBuildResults = ((bool)GetOption("dotnet/embed_build_outputs") || platform == OS.Platforms.Android)
                && platform != OS.Platforms.MacOS && platform != OS.Platforms.Web;

            var exportedJars = new HashSet<string>();

            foreach (PublishConfig config in targets)
            {
                string ridOS = config.RidOS;
                string buildConfig = config.BuildConfig;
                bool includeDebugSymbols = config.IncludeDebugSymbols;

                foreach (string arch in config.Archs)
                {
                    string ridArch = DetermineRuntimeIdentifierArch(arch);
                    string runtimeIdentifier = $"{ridOS}-{ridArch}";
                    string projectDataDirName = $"data_{GodotSharpDirs.CSharpProjectName}_{platform}_{arch}";
                    if (platform == OS.Platforms.MacOS)
                    {
                        projectDataDirName = Path.Combine("Contents", "Resources", projectDataDirName);
                    }

                    // Create temporary publish output directory.
                    string publishOutputDir;

                    if (config.UseTempDir)
                    {
                        publishOutputDir = Path.Combine(Path.GetTempPath(), "godot-publish-dotnet",
                            $"{System.Environment.ProcessId}-{buildConfig}-{runtimeIdentifier}");
                        _tempFolders.Add(publishOutputDir);
                    }
                    else
                    {
                        publishOutputDir = Path.Combine(GodotSharpDirs.ProjectBaseOutputPath, "godot-publish-dotnet",
                            $"{buildConfig}-{runtimeIdentifier}");
                    }

                    outputPaths.Add(publishOutputDir);

                    if (!Directory.Exists(publishOutputDir))
                        Directory.CreateDirectory(publishOutputDir);

                    // Execute dotnet publish.
                    if (!BuildManager.PublishProjectBlocking(buildConfig, platform,
                            runtimeIdentifier, publishOutputDir, includeDebugSymbols, publishProperties))
                    {
                        throw new InvalidOperationException("Failed to build project. Check MSBuild panel for details.");
                    }

                    string soExt = ridOS switch
                    {
                        OS.DotNetOS.Win or OS.DotNetOS.Win10 => "dll",
                        OS.DotNetOS.OSX or OS.DotNetOS.iOS or OS.DotNetOS.iOSSimulator => "dylib",
                        _ => "so"
                    };

                    string assemblyPath = Path.Combine(publishOutputDir, $"{GodotSharpDirs.ProjectAssemblyName}.dll");
                    string nativeAotPath = Path.Combine(publishOutputDir,
                        $"{GodotSharpDirs.ProjectAssemblyName}.{soExt}");

                    if (!File.Exists(assemblyPath) && !File.Exists(nativeAotPath))
                    {
                        throw new NotSupportedException(
                            $"Publish succeeded but project assembly not found at '{assemblyPath}' or '{nativeAotPath}'.");
                    }

                    // The dn2cpp backend ships a single drop-in library in place of
                    // the publish directory's managed assemblies, so what gets
                    // packaged is its staging directory rather than the publish.
                    // It runs before the simulator skip below: the simulator
                    // outputs are never bundled, but their libraries feed the
                    // xcframework tail after this loop.
                    string? dn2CppContentsDir = dn2CppExporter?.BuildDropIn(publishOutputDir,
                        GodotSharpDirs.ProjectAssemblyName, buildConfig, runtimeIdentifier, arch);

                    // For ios simulator builds, skip packaging the build outputs.
                    if (!config.BundleOutputs)
                        continue;

                    string exportContentsDir = dn2CppContentsDir ?? publishOutputDir;

                    var manifest = new StringBuilder();

                    // Add to the exported project shared object list or packed resources.
                    RecursePublishContents(exportContentsDir,
                        filterDir: dir =>
                        {
                            if (platform == OS.Platforms.iOS)
                            {
                                // Exclude dsym folders.
                                return !dir.EndsWith(".dsym", StringComparison.OrdinalIgnoreCase);
                            }

                            return true;
                        },
                        filterFile: file =>
                        {
                            if (platform == OS.Platforms.iOS)
                            {
                                // Exclude the dylib artifact, since it's included separately as an xcframework.
                                return Path.GetFileName(file) != $"{GodotSharpDirs.ProjectAssemblyName}.dylib";
                            }

                            return true;
                        },
                        recurseDir: dir =>
                        {
                            if (platform == OS.Platforms.iOS)
                            {
                                // Don't recurse into dsym folders.
                                return !dir.EndsWith(".dsym", StringComparison.OrdinalIgnoreCase);
                            }

                            return true;
                        },
                        addEntry: (path, isFile) =>
                        {
                            // We get called back for both directories and files, but we only package files for now.
                            if (isFile)
                            {
                                if (embedBuildResults)
                                {
                                    if (platform == OS.Platforms.Android)
                                    {
                                        string fileName = Path.GetFileName(path);

                                        if (IsSharedObject(fileName))
                                        {
                                            if (fileName.EndsWith(".so") && !fileName.StartsWith("lib"))
                                            {
                                                // Add 'lib' prefix required for all native libraries in Android.
                                                string newPath = string.Concat(path.AsSpan(0, path.Length - fileName.Length), "lib", fileName);
                                                Godot.DirAccess.RenameAbsolute(path, newPath);
                                                path = newPath;
                                            }

                                            AddSharedObject(path, tags: new string[] { arch },
                                                Path.Join(projectDataDirName,
                                                    Path.GetRelativePath(exportContentsDir,
                                                        Path.GetDirectoryName(path)!)));

                                            return;
                                        }

                                        bool IsSharedObject(string fileName)
                                        {
                                            if (fileName.EndsWith(".jar"))
                                            {
                                                // Don't export the same jar twice. Otherwise we will have conflicts.
                                                // This can happen when exporting for multiple architectures. Dotnet
                                                // stores the jars in .godot/mono/temp/bin/Export[Debug|Release] per
                                                // target architecture. Jars are cpu agnostic so only 1 is needed.
                                                var jarName = Path.GetFileName(fileName);
                                                return exportedJars.Add(jarName);
                                            }

                                            if (fileName.EndsWith(".so") || fileName.EndsWith(".a") || fileName.EndsWith(".dex"))
                                            {
                                                return true;
                                            }

                                            return false;
                                        }
                                    }

                                    string filePath = SanitizeSlashes(Path.GetRelativePath(exportContentsDir, path));
                                    byte[] fileData = File.ReadAllBytes(path);
                                    string hash = Convert.ToBase64String(SHA512.HashData(fileData));

                                    manifest.Append(CultureInfo.InvariantCulture, $"{filePath}\t{hash}\n");

                                    AddFile($"res://.godot/mono/publish/{arch}/{filePath}", fileData, false);
                                }
                                else
                                {
                                    if (platform == OS.Platforms.iOS && path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                                    {
                                        AddAppleEmbeddedPlatformBundleFile(path);
                                    }
                                    else
                                    {
                                        AddSharedObject(path, tags: null,
                                            Path.Join(projectDataDirName,
                                                Path.GetRelativePath(exportContentsDir,
                                                    Path.GetDirectoryName(path)!)));
                                    }
                                }
                            }
                        });

                    if (embedBuildResults)
                    {
                        byte[] fileData = Encoding.Default.GetBytes(manifest.ToString());
                        AddFile($"res://.godot/mono/publish/{arch}/.dotnet-publish-manifest", fileData, false);
                    }
                }
            }

            if (platform == OS.Platforms.iOS)
            {
                if (outputPaths.Count > 2)
                {
                    // lipo the simulator binaries together

                    string outputPath = Path.Combine(outputPaths[1], $"{GodotSharpDirs.ProjectAssemblyName}.dylib");
                    string[] files = outputPaths
                        .Skip(1)
                        .Select(path => Path.Combine(path, $"{GodotSharpDirs.ProjectAssemblyName}.dylib"))
                        .ToArray();

                    if (!Internal.LipOCreateFile(outputPath, files))
                    {
                        throw new InvalidOperationException($"Failed to 'lipo' simulator binaries.");
                    }

                    outputPaths.RemoveRange(2, outputPaths.Count - 2);
                }

                string xcFrameworkPath = Path.Combine(GodotSharpDirs.ProjectBaseOutputPath, publishConfig.BuildConfig, $"{GodotSharpDirs.ProjectAssemblyName}_aot.xcframework");
                if (!BuildManager.GenerateXCFrameworkBlocking(outputPaths, xcFrameworkPath))
                {
                    throw new InvalidOperationException("Failed to generate xcframework.");
                }

                AddAppleEmbeddedPlatformEmbeddedFramework(xcFrameworkPath);
            }
        }

        private static void RecursePublishContents(string path, Func<string, bool> filterDir,
            Func<string, bool> filterFile, Func<string, bool> recurseDir,
            Action<string, bool> addEntry)
        {
            foreach (string file in Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (filterFile(file))
                {
                    addEntry(file, true);
                }
            }

            foreach (string dir in Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (filterDir(dir))
                {
                    addEntry(dir, false);
                    if (recurseDir(dir))
                    {
                        RecursePublishContents(dir, filterDir, filterFile, recurseDir, addEntry);
                    }
                }
            }
        }

        private string SanitizeSlashes(string path)
        {
            if (Path.DirectorySeparatorChar == '\\')
                return path.Replace('\\', '/');
            return path;
        }

        private string DetermineRuntimeIdentifierOS(string platform, bool useAndroidLinuxBionic)
        {
            if (platform == OS.Platforms.Android && useAndroidLinuxBionic)
            {
                return OS.DotNetOS.LinuxBionic;
            }

            if (platform == OS.Platforms.Web)
            {
                // Not 'browser'. The Web publishes the game IL under the HOST's
                // runtime identifier, and that is deliberate.
                //
                // dn2cpp — the only backend the Web accepts — consumes IL and
                // nothing else. It takes its framework closure from the toolchain
                // bundle's pinned ref/, and Emscripten compiles the C++ it emits,
                // so the publish RID is a private, publish-only key: it selects an
                // apphost and a runtime pack, both of which this backend switches
                // off. 'browser-wasm' would demand the wasm-tools workload, resolve
                // a Mono-flavoured CoreLib and move MSBuild's defaults — that is,
                // it would change the IL we transpile, which is the one input that
                // must not vary with the target.
                //
                // Nothing downstream reads the RID back. The names the engine has
                // to agree with are built from 'arch', which stays 'wasm32':
                // data_{proj}_{platform}_{arch} and res://.godot/mono/publish/{arch}
                // here, _get_platform_name() and Engine::get_architecture_name() —
                // 'web' and 'wasm32' — there. Nor do the game's own platform defines
                // move: the publish is handed GodotTargetPlatform=web explicitly, so
                // GODOT_WEB is defined by the platform and never inferred from a RID.
                return DetermineHostRuntimeIdentifierOS();
            }

            return OS.DotNetOSPlatformMap[platform];
        }

        private string DetermineRuntimeIdentifierArch(string arch)
        {
            return arch switch
            {
                "x86" => "x86",
                "x86_32" => "x86",
                "x64" => "x64",
                "x86_64" => "x64",
                "armeabi-v7a" => "arm",
                "arm64-v8a" => "arm64",
                "arm32" => "arm",
                "arm64" => "arm64",
                // The other half of the host RID; see DetermineRuntimeIdentifierOS.
                // 'wasm32' survives untranslated everywhere the engine can see it.
                "wasm32" => DetermineHostRuntimeIdentifierArch(),
                _ => throw new ArgumentOutOfRangeException(nameof(arch), arch, "Unexpected architecture")
            };
        }

        /// <summary>
        /// The RID operating system of the machine running the editor. Only the Web
        /// publishes under it — see <see cref="DetermineRuntimeIdentifierOS"/> for why.
        /// </summary>
        private static string DetermineHostRuntimeIdentifierOS()
        {
            if (OS.IsWindows)
                return OS.DotNetOS.Win;
            if (OS.IsMacOS)
                return OS.DotNetOS.OSX;
            if (OS.IsLinuxBSD)
                return OS.DotNetOS.Linux;

            throw new NotSupportedException(
                "The Web export publishes the game IL under the host's runtime identifier, and this host is not a " +
                "supported .NET publish host.");
        }

        /// <summary>
        /// The RID architecture of the machine running the editor. The companion of
        /// <see cref="DetermineHostRuntimeIdentifierOS"/>.
        /// </summary>
        private static string DetermineHostRuntimeIdentifierArch()
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "x86",
                Architecture.X64 => "x64",
                Architecture.Arm => "arm",
                Architecture.Arm64 => "arm64",
                var other => throw new NotSupportedException(
                    "The Web export publishes the game IL under the host's runtime identifier, and the host " +
                    $"architecture '{other}' is not a supported .NET publish architecture."),
            };
        }

        /// <summary>
        /// Refuses the Web preset combinations that cannot produce a game that runs.
        /// Every one of them is a checkbox the user can flip, so each says which.
        /// </summary>
        private static void VerifyWebPreset(ExportBackend exportBackend, string[] features)
        {
            // The Web has no .NET runtime: no hostfxr, no coreclr. GDMono can only
            // reach the game's C# through try_load_native_aot_library, which dlopens
            // an ahead-of-time compiled drop-in — and dn2cpp is what produces one.
            // The other two backends would leave managed assemblies (or a native
            // library built for an operating system the browser is not) in the
            // publish directory, and the packaging below would hand every one of
            // them to the Web exporter, which stages them flat next to index.html
            // and asks the loader to open each as a WebAssembly side module.
            if (exportBackend != ExportBackend.Dn2Cpp)
            {
                throw new NotSupportedException(
                    "A C# project can only be exported to Web with the dn2cpp export backend: the Web has no .NET " +
                    "runtime to load published assemblies with. Set 'dotnet/export_backend' to 'dn2cpp' in the " +
                    "export preset.");
            }

            // The drop-in is a WebAssembly side module, and only a dlink engine
            // build can dlopen one at all.
            if (!features.Contains("web_extensions"))
            {
                throw new NotSupportedException(
                    "A C# Web export needs 'Extensions Support' enabled in the export preset: the compiled game " +
                    "is loaded as a WebAssembly side module, which only the extensions ('dlink') export template " +
                    "can open.");
            }

            // The drop-in is compiled without -pthread, and Emscripten refuses to
            // load a non-pthread side module into a pthread main module — the game
            // would be fetched, and then fail in the loader before main() runs.
            if (!features.Contains("nothreads"))
            {
                throw new NotSupportedException(
                    "A C# Web export needs 'Thread Support' disabled in the export preset: the compiled game is " +
                    "single-threaded, and Emscripten will not load a non-pthread side module into a threaded " +
                    "main module.");
            }
        }

        public override void _ExportEnd()
        {
            base._ExportEnd();

            string aotTempDir = Path.Combine(Path.GetTempPath(), $"godot-aot-{System.Environment.ProcessId}");

            if (Directory.Exists(aotTempDir))
                Directory.Delete(aotTempDir, recursive: true);

            foreach (string folder in _tempFolders)
            {
                Directory.Delete(folder, recursive: true);
            }
            _tempFolders.Clear();
        }

        /// <summary>
        /// Tries to determine the platform from the export preset's platform OS name.
        /// </summary>
        /// <param name="osName">Name of the export operating system.</param>
        /// <param name="platform">Platform name for the recognized supported platform.</param>
        /// <returns>
        /// <see langword="true"/> when the platform OS name is recognized as a supported platform,
        /// <see langword="false"/> otherwise.
        /// </returns>
        private static bool TryDeterminePlatformFromOSName(string osName, [NotNullWhen(true)] out string? platform)
        {
            if (OS.PlatformFeatureMap.TryGetValue(osName, out platform))
            {
                return true;
            }

            platform = null;
            return false;
        }

        private struct PublishConfig
        {
            public bool UseTempDir;
            public bool BundleOutputs;
            public string RidOS;
            public HashSet<string> Archs;
            public string BuildConfig;
            public bool IncludeDebugSymbols;
        }
    }
}
