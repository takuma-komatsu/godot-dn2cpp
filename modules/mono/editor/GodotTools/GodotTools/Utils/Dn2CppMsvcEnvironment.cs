using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GodotTools.Utils
{
    /// <summary>
    /// The Visual Studio C++ environment a native build needs, imported from the
    /// install this machine has: <c>vswhere</c> finds it, <c>vcvarsall</c> states
    /// it, and the result is handed to the build's child processes as an overlay.
    /// A complete MSVC install puts nothing on PATH and defines no
    /// <c>INCLUDE</c>/<c>LIB</c> outside a Developer Command Prompt, so without
    /// this an editor started from Explorer cannot export at all.
    /// </summary>
    /// <remarks>
    /// The editor's OWN environment is never touched: a
    /// <c>SetEnvironmentVariable</c> here would leak MSVC's PATH into dotnet,
    /// MSBuild and every game launched with Play, with no way back short of a
    /// restart.
    /// </remarks>
    internal sealed class Dn2CppMsvcEnvironment
    {
        /// <summary>The variables vcvarsall sets that a compile and a link read.</summary>
        private static readonly string[] ImportedVars = { "PATH", "INCLUDE", "LIB", "LIBPATH" };

        private Dn2CppMsvcEnvironment(Dictionary<string, string?> env, string clExe, string origin)
        {
            Env = env;
            ClExe = clExe;
            Origin = origin;
        }

        /// <summary>
        /// Environment overlay for every tool the export runs, in
        /// <c>EmscriptenSdk.Env</c>'s shape (a null VALUE means "remove").
        /// </summary>
        public Dictionary<string, string?> Env { get; }

        /// <summary>
        /// The compiler found on the imported PATH — so, the one cmake's default
        /// search will pick under that overlay.
        /// </summary>
        public string ClExe { get; }

        /// <summary>Human-readable description of where <see cref="Env"/> came from.</summary>
        public string Origin { get; }

        /// <summary>
        /// Whether this process was started from an already-initialized MSVC
        /// environment, in which case importing would replace a working
        /// environment with a possibly different one. cl.exe on PATH is not
        /// enough on its own: without <c>LIB</c> the compile succeeds and the
        /// LINK dies. An empty value counts as unset.
        /// </summary>
        public static bool AlreadyInitialized() =>
            OS.PathWhich("cl") is not null
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INCLUDE"))
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LIB"));

        /// <summary>
        /// Runs vswhere and vcvarsall and captures what they name, or returns
        /// <see langword="null"/> with the reason in <paramref name="failure"/>.
        /// Costs seconds; the caller announces it.
        /// </summary>
        public static Dn2CppMsvcEnvironment? Import(out string? failure)
        {
            try
            {
                return TryImport(out failure);
            }
            catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
            {
                failure = e.Message;

                return null;
            }
        }

        private static Dn2CppMsvcEnvironment? TryImport(out string? failure)
        {
            string? vswhere = FindVsWhere();
            if (vswhere is null)
            {
                failure = "no 'Microsoft Visual Studio/Installer/vswhere.exe' under %ProgramFiles(x86)% "
                    + "or %ProgramFiles%";

                return null;
            }

            // vcvarsall's spelling of the host architecture, which is not the Godot
            // one GetHostArchitecture answers. Feeding an arm64 host the x64
            // toolset would fill an arm64 export slot with x64 binaries.
            string arch;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    arch = "x64";
                    break;
                case Architecture.Arm64:
                    arch = "arm64";
                    break;
                default:
                    failure = $"no MSVC toolset for the host architecture "
                        + $"'{RuntimeInformation.ProcessArchitecture}'";

                    return null;
            }

            // -products '*': a Build Tools install carries the C++ toolset and is
            // invisible to the default product filter.
            string install = Capture(vswhere, out int vswhereExit,
                "-latest", "-products", "*",
                "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                "-property", "installationPath", "-utf8").Trim();

            if (vswhereExit != 0 || install.Length == 0)
            {
                failure = $"'{vswhere}' found no install carrying the C++ toolset "
                    + "(Microsoft.VisualStudio.Component.VC.Tools.x86.x64)";

                return null;
            }

            string vcvarsall = Path.Combine(install, "VC", "Auxiliary", "Build", "vcvarsall.bat");
            if (!File.Exists(vcvarsall))
            {
                failure = $"'{vcvarsall}' does not exist, so the install at '{install}' cannot state its "
                    + "environment";

                return null;
            }

            // Split into arguments rather than passed as one '/c "…"' string: .NET
            // escapes an inner quote as \" and cmd does not read that back. The
            // command starts with a bare word for the same reason — cmd strips the
            // outer quotes of a /c string that begins with one.
            //
            // chcp 65001 with a UTF-8 decoder because `set` writes in the console
            // code page: a mangled non-ASCII PATH entry silently removes that
            // directory's tools from every child.
            string dump = Capture("cmd", out int vcvarsExit,
                "/c", "chcp", "65001", ">nul", "&&", "call", vcvarsall, arch, "&&", "set");

            if (vcvarsExit != 0)
            {
                failure = $"'{vcvarsall} {arch}' failed (exit code {vcvarsExit}): {Tail(dump)}";

                return null;
            }

            var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in dump.Split('\n'))
            {
                // `set` writes CRLF; an unstripped '\r' corrupts the last entry of LIB.
                string line = raw.TrimEnd('\r');
                int equals = line.IndexOf('=');
                if (equals <= 0)
                    continue;

                string name = line.Substring(0, equals);
                foreach (string imported in ImportedVars)
                {
                    if (string.Equals(name, imported, StringComparison.OrdinalIgnoreCase))
                        env[imported] = line.Substring(equals + 1);
                }
            }

            // Assigned, never prepended: vcvarsall ran as a child of this process,
            // so what it printed is already MSVC's directories ahead of ours.
            string importedPath = env.TryGetValue("PATH", out string? value) ? value ?? string.Empty : string.Empty;
            string? clExe = FindOnPath(importedPath, "cl.exe");
            if (clExe is null)
            {
                failure = $"'{vcvarsall} {arch}' put no cl.exe on PATH";

                return null;
            }

            failure = null;

            return new Dn2CppMsvcEnvironment(env, clExe, $"{install} (vcvarsall {arch})");
        }

        /// <summary>
        /// The installer's vswhere, located through the environment's own
        /// <c>ProgramFiles(x86)</c>.
        /// </summary>
        /// <remarks>
        /// Read as an environment variable rather than through
        /// <c>SpecialFolder.ProgramFilesX86</c>, which asks the shell for a path no
        /// caller can redirect — a test could then not model a machine without
        /// Visual Studio. A hardcoded fallback path or a PATH search would give the
        /// same back, so there is neither.
        /// </remarks>
        private static string? FindVsWhere()
        {
            foreach (string variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string? root = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrEmpty(root))
                    continue;

                string candidate = Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// <paramref name="exe"/> on the given PATH value, which is the imported
        /// one rather than this process's — what <c>OS.PathWhich</c> would answer
        /// about is the environment that is deliberately not being changed.
        /// </summary>
        private static string? FindOnPath(string path, string exe)
        {
            char[] invalid = Path.GetInvalidPathChars();
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0 || dir.IndexOfAny(invalid) >= 0)
                    continue;

                string candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Runs a tool and returns its standard output. Standard error is left
        /// attached to the editor's: reading only one of two redirected pipes
        /// deadlocks once the other fills.
        /// </summary>
        private static string Capture(string exe, out int exitCode, params string[] args)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    CreateNoWindow = true,
                },
            };

            foreach (string arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;

            return output;
        }

        /// <summary>The last few lines of a failing tool's output, for the message.</summary>
        private static string Tail(string output)
        {
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
                lines[i] = lines[i].TrimEnd('\r');

            return lines.Length <= 5 ? string.Join(" / ", lines)
                : string.Join(" / ", lines, lines.Length - 5, 5);
        }
    }
}
