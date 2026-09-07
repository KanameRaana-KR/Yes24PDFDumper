using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using YES24Plugin;

// StartupHook MUST be in the global namespace with exactly this signature.
internal class StartupHook
{
    internal static string DumpRoot;
    internal static Harmony HarmonyInstance;
    private static string HookDir;
    private static bool _hooked = false;
    private static readonly object _hookLock = new();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetEnvironmentVariableW(string lpName, string lpValue);

    public static void Initialize()
    {
        // 1. Immediately scrub DOTNET_STARTUP_HOOKS and other injection variables from Win32 environment
        // so UnDrmClientNet's native ScrubDotnetInjectionVars() never detects anything!
        try
        {
            string[] scrubList = new[]
            {
                "DOTNET_STARTUP_HOOKS",
                "DOTNET_ADDITIONAL_DEPS",
                "CORECLR_PROFILER",
                "CORECLR_ENABLE_PROFILING",
                "CORECLR_PROFILER_PATH_32",
                "CORECLR_PROFILER_PATH_64",
                "CORECLR_PROFILER_PATH",
                "DOTNET_EnableDiagnostics"
            };
            foreach (var v in scrubList)
            {
                SetEnvironmentVariableW(v, null);
                Environment.SetEnvironmentVariable(v, null);
            }
        }
        catch { }

        try
        {
            HookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);

            // Sideload our own deps (LibCore.dll) from the hook DLL's directory,
            // since app deps.json doesn't know about us.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    var reqName = new AssemblyName(e.Name).Name;
                    var name = reqName + ".dll";
                    var cand = Path.Combine(HookDir, name);
                    if (File.Exists(cand)) return Assembly.LoadFrom(cand);
                    if (reqName.Equals("0Harmony", StringComparison.OrdinalIgnoreCase))
                    {
                        var libCore = Path.Combine(HookDir, "LibCore.dll");
                        if (File.Exists(libCore)) return Assembly.LoadFrom(libCore);
                    }
                }
                catch { }
                return null;
            };

            DumpRoot = Environment.GetEnvironmentVariable("YES24_DUMP_PATH");
            if (string.IsNullOrWhiteSpace(DumpRoot))
                DumpRoot = Path.Combine(Path.GetTempPath(), "yes24_dump");
            Directory.CreateDirectory(DumpRoot);

            Log($"[YES24Plugin] Startup hook alive. Dump dir: {DumpRoot}");
            Log($"[YES24Plugin] PID={Pid}");

            // Watch for YES24eBook.dll to load — that's when we know the viewer is coming up.
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) => TryHook(e.LoadedAssembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                TryHook(asm);
        }
        catch (Exception ex) { Log("[YES24Plugin] Initialize failed: " + ex); }
    }

    private static void TryHook(Assembly asm)
    {
        if (asm == null) return;
        string name = asm.GetName().Name ?? "";
        if (!name.Equals("YES24eBook", StringComparison.OrdinalIgnoreCase)) return;

        lock (_hookLock)
        {
            if (_hooked) return;
            _hooked = true;
        }

        try
        {
            // Preload UnDrmClientNet from the install dir (adjacent x64\ folder).
            string[] candidates =
            {
                @"C:\Program Files\YES24eBook\x64\UnDrmClientNet.dll",
                Path.Combine(Path.GetDirectoryName(asm.Location) ?? "", "x64", "UnDrmClientNet.dll"),
                Path.Combine(Path.GetDirectoryName(asm.Location) ?? "", "UnDrmClientNet.dll"),
            };
            Assembly undrm = null;
            foreach (var c in candidates)
            {
                if (!File.Exists(c)) continue;
                try { undrm = Assembly.LoadFrom(c); Log("[YES24Plugin] Preloaded " + c); break; }
                catch (Exception ex) { Log("[YES24Plugin]   load fail " + c + ": " + ex.Message); }
            }
            if (undrm == null)
            {
                Log("[YES24Plugin] Could not locate UnDrmClientNet.dll — aborting.");
                return;
            }

            HarmonyInstance = new Harmony("nyx.yes24.plugin");

            // Install anti-tamper neutralizer on UnDrmAntiPassAssembly
            PdfPatches.HookSecurity(HarmonyInstance, undrm);

            // Hook the pure-C# wrapper in YES24eBook.dll
            PdfPatches.HookPdfViewModel(HarmonyInstance, asm);
            Log("[YES24Plugin] Ready. Open a book in the viewer to trigger extraction.");
        }
        catch (Exception ex) { Log("[YES24Plugin] Hook install failed: " + ex); }
    }

    internal static int Pid => System.Diagnostics.Process.GetCurrentProcess().Id;

    internal static void Log(string msg)
    {
        try
        {
            Console.WriteLine(msg);
            string dir = DumpRoot ?? Path.GetTempPath();
            string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "dumper.log"), line);
        }
        catch { }
    }
}
