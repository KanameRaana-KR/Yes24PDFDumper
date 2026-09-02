using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using YES24Dumper;

// StartupHook MUST be in the global namespace with exactly this signature.
internal class StartupHook
{
    internal static string DumpRoot;
    internal static Harmony HarmonyInstance;
    private static string HookDir;
    private static bool _hooked = false;
    private static readonly object _hookLock = new();

    public static void Initialize()
    {
        try
        {
            HookDir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);

            // Sideload our own deps (0Harmony.dll) from the hook DLL's directory,
            // since app deps.json doesn't know about us.
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                try
                {
                    var name = new AssemblyName(e.Name).Name + ".dll";
                    var cand = Path.Combine(HookDir, name);
                    if (File.Exists(cand)) return Assembly.LoadFrom(cand);
                }
                catch { }
                return null;
            };

            DumpRoot = Environment.GetEnvironmentVariable("YES24_DUMP_PATH");
            if (string.IsNullOrWhiteSpace(DumpRoot))
                DumpRoot = Path.Combine(Path.GetTempPath(), "yes24_dump");
            Directory.CreateDirectory(DumpRoot);

            Log($"[YES24Dumper] Startup hook alive.  Dump dir: {DumpRoot}");
            Log($"[YES24Dumper] PID={Pid}");

            // Watch for YES24eBook.dll to load — that's when we know the viewer is coming up.
            AppDomain.CurrentDomain.AssemblyLoad += (s, e) => TryHook(e.LoadedAssembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                TryHook(asm);
        }
        catch (Exception ex) { Log("[YES24Dumper] Initialize failed: " + ex); }
    }

    private static void TryHook(Assembly asm)
    {
        if (asm == null) return;
        string name = asm.GetName().Name ?? "";
        // We only need YES24eBook.dll to be loaded so we know x64/UnDrmClientNet.dll
        // is discoverable next to it.
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
                try { undrm = Assembly.LoadFrom(c); Log("[YES24Dumper] Preloaded " + c); break; }
                catch (Exception ex) { Log("[YES24Dumper]   load fail " + c + ": " + ex.Message); }
            }
            if (undrm == null)
            {
                Log("[YES24Dumper] Could not locate UnDrmClientNet.dll — aborting.");
                return;
            }

            HarmonyInstance = new Harmony("nyx.yes24.dumper");
            // C++/CLI methods in UnDrmClientNet cause Harmony InvalidProgramException.
            // Hook the pure-C# wrapper in YES24eBook.dll instead — same byte[], no post-processing.
            PdfPatches.HookPdfViewModel(HarmonyInstance, asm);
            Log("[YES24Dumper] Ready. Open a book in the viewer to trigger extraction.");
        }
        catch (Exception ex) { Log("[YES24Dumper] Hook install failed: " + ex); }
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
