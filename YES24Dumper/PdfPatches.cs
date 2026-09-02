using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace YES24Dumper;

/// <summary>
/// Single, surgical hook on the one method IL-verified to return the fully
/// decrypted book bytes:
///     UnDrmClientNet.UnDrmClient::GetMemFileContent(string) -> byte[]
///
/// Verified via IDA IL disasm (UnDrmClientNet.dll @ 0x53d0):
///   * calls native undrm_helper_get_content_wide()
///   * Marshal.Copy(outBuf, byte[], 0, len)
///   * bdb_free(outBuf), returns the clear-text array
///
/// Anti-hook detector (UnDrmSecurityCoreNet.UnDrmAntiPassAssembly.
/// DetectSuspiciousTypes) only scans loaded assemblies for the string
/// literals "FakeAssembly" / "AssemblyInfoLoader" / "AssemblyInfoResult",
/// plus Assembly subclasses outside System.Reflection.*. HarmonyLib and
/// YES24Dumper match none of these, so we're invisible.
/// </summary>
internal static class PdfPatches
{
    private static readonly object _writeLock = new();
    private static int _counter = 0;

    // Captured live PDFViewModel instance — set by ctor postfix.
    internal static object LivePdfViewModel;
    internal static Type PdfViewModelType;

    /// <summary>
    /// C++/CLI methods on UnDrmClientNet.UnDrmClient throw InvalidProgramException
    /// when Harmony rewrites them (mixed-mode IL). Hook the pure-C# wrapper in
    /// YES24eBook.dll instead — same byte[], zero post-processing (verified via
    /// PDFViewModel::getMemFileContent IL disasm).
    /// </summary>
    public static void HookPdfViewModel(Harmony h, Assembly yes24)
    {
        var t = yes24.GetTypes()
            .FirstOrDefault(x => x.FullName == "Yes24eBook.ViewModels.Viewer.PDFViewModel");
        if (t == null)
        {
            StartupHook.Log("[Hook] PDFViewModel type not found in YES24eBook.dll.");
            return;
        }

        var targets = new[]
        {
            new { name = "getMemFileContent", post = nameof(Post_GetMemFileContent),
                  ret = typeof(byte[]),  args = new[] { typeof(string) } },
            new { name = "getStreamContent", post = nameof(Post_GetStreamContent),
                  ret = (Type)null,      args = new[] { typeof(string) } },
        };

        int hooked = 0;
        foreach (var spec in targets)
        {
            var mi = t.GetMethod(spec.name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, spec.args, null);
            if (mi == null)
            {
                StartupHook.Log($"[Hook]   {spec.name}({string.Join(",", spec.args.Select(x => x.Name))}) not found");
                continue;
            }
            try
            {
                var pf = new HarmonyMethod(typeof(PdfPatches).GetMethod(spec.post,
                    BindingFlags.Static | BindingFlags.NonPublic));
                h.Patch(mi, postfix: pf);
                StartupHook.Log($"[Hook]   patched PDFViewModel.{spec.name} -> {mi.ReturnType.Name}");
                hooked++;
            }
            catch (Exception ex)
            {
                StartupHook.Log($"[Hook]   FAIL {spec.name}: {ex.Message}");
            }
        }
        StartupHook.Log($"[Hook] {hooked} method(s) patched.");

        // ctor also has an obfuscator stub — can't hook it. Instead we find the
        // live PDFViewModel instance by scanning the WPF DataContext tree.
        PdfViewModelType = t;

        // Kick off a background scavenger.
        System.Threading.Tasks.Task.Run(ActiveScavenge);
    }

    /// <summary>
    /// Walk every WPF PresentationSource → visual tree → DataContext looking
    /// for an instance of Yes24eBook.ViewModels.Viewer.PDFViewModel.
    /// </summary>
    private static object FindLivePdfViewModel()
    {
        if (PdfViewModelType == null) return null;
        try
        {
            var psType = Type.GetType("System.Windows.PresentationSource, PresentationCore");
            var srcs = psType?.GetProperty("CurrentSources", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
            if (srcs == null) return null;

            object found = null;
            foreach (var src in srcs)
            {
                if (src == null) continue;
                var root = src.GetType().GetProperty("RootVisual")?.GetValue(src);
                if (root == null) continue;

                var dispatcher = root.GetType().GetProperty("Dispatcher")?.GetValue(root);
                Action work = () => { found = SearchVisual(root); };
                if (dispatcher != null)
                {
                    var invoke = dispatcher.GetType().GetMethod("Invoke", new[] { typeof(Action) });
                    try { invoke?.Invoke(dispatcher, new object[] { work }); } catch { }
                }
                else work();
                if (found != null) return found;
            }
        }
        catch (Exception ex) { StartupHook.Log("[Scavenge] tree scan: " + ex.Message); }
        return null;
    }

    private static object SearchVisual(object node, int depth = 0)
    {
        if (node == null || depth > 40) return null;
        try
        {
            // DataContext check
            var dc = node.GetType().GetProperty("DataContext")?.GetValue(node);
            if (dc != null && PdfViewModelType.IsInstanceOfType(dc)) return dc;

            var helper = Type.GetType("System.Windows.Media.VisualTreeHelper, PresentationCore");
            if (helper == null) return null;
            int n = (int)helper.GetMethod("GetChildrenCount").Invoke(null, new object[] { node });
            var getChild = helper.GetMethod("GetChild");
            for (int i = 0; i < n; i++)
            {
                var child = getChild.Invoke(null, new object[] { node, i });
                var hit = SearchVisual(child, depth + 1);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    private static void ActiveScavenge()
    {
        // Wait for a viewer instance and for the app to have written the .content folder.
        string contentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Yes24eBook", ".content");

        for (int spin = 0; spin < 600; spin++)          // up to 10 min
        {
            System.Threading.Thread.Sleep(1000);
            if (LivePdfViewModel == null)
            {
                LivePdfViewModel = FindLivePdfViewModel();
                if (LivePdfViewModel != null)
                    StartupHook.Log("[Scavenge] Located PDFViewModel via visual tree.");
            }
            if (LivePdfViewModel == null) continue;
            if (!Directory.Exists(contentRoot)) continue;

            // Find every book folder that has a .PDF next to a rights.xml (the DRM'd payload).
            string[] books;
            try
            {
                books = Directory.GetDirectories(contentRoot)
                    .Where(d => File.Exists(Path.Combine(d, "rights.xml")))
                    .ToArray();
            }
            catch { continue; }

            foreach (var bookDir in books)
            {
                foreach (var f in Directory.GetFiles(bookDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext != ".pdf" && ext != ".epub") continue;

                    string outFile = Path.Combine(StartupHook.DumpRoot, Path.GetFileName(f));
                    if (File.Exists(outFile) && new FileInfo(outFile).Length > 1024) continue; // already done

                    try
                    {
                        byte[] bytes = InvokeGetMemFileContent(f);
                        if (bytes == null || bytes.Length == 0)
                        {
                            StartupHook.Log($"[Scavenge] {f} -> null/empty");
                            continue;
                        }
                        // Reuse the same writer.
                        Post_GetMemFileContent(f, bytes);
                    }
                    catch (Exception ex)
                    {
                        StartupHook.Log($"[Scavenge] {f} -> ERROR: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
    }

    private static byte[] InvokeGetMemFileContent(string path)
    {
        var vm = LivePdfViewModel;
        if (vm == null || PdfViewModelType == null) return null;

        // First get the drmClient property.
        var propInfo = PdfViewModelType.GetProperty("drmClient",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            ?? PdfViewModelType.GetProperty("get_drmClient",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        object drmClient;
        if (propInfo != null)
            drmClient = propInfo.GetValue(vm);
        else
        {
            var getter = PdfViewModelType.GetMethod("get_drmClient",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (getter == null)
            {
                // Try any field of type UnDrmClient
                var f = PdfViewModelType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(x => x.FieldType.FullName == "UnDrmClientNet.UnDrmClient");
                if (f == null) { StartupHook.Log("[Scavenge] drmClient not reachable."); return null; }
                drmClient = f.GetValue(vm);
            }
            else drmClient = getter.Invoke(vm, null);
        }
        if (drmClient == null) { StartupHook.Log("[Scavenge] drmClient == null (book not yet opened?)"); return null; }

        var mi = drmClient.GetType().GetMethod("GetMemFileContent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new[] { typeof(string) }, null);
        if (mi == null) { StartupHook.Log("[Scavenge] UnDrmClient.GetMemFileContent(string) missing"); return null; }

        return (byte[])mi.Invoke(drmClient, new object[] { path });
    }

    // Kept for backward-compat name — same as HookPdfViewModel now.
    public static void HookUnDrm(Harmony h, Assembly _) { /* obsolete */ }
    public static void Apply(Harmony h, Assembly yes24) => HookPdfViewModel(h, yes24);

    // Postfix for getStreamContent: reads the returned UnDrmStream fully into a byte[]
    // and rewinds so the viewer isn't disturbed.
    private static void Post_GetStreamContent(string __0, System.IO.Stream __result)
    {
        try
        {
            if (__result == null) return;
            if (!__result.CanSeek)
            {
                StartupHook.Log($"[Dump] Stream(\"{__0}\") — non-seekable, skipping.");
                return;
            }
            long pos = __result.Position;
            __result.Position = 0;
            using var ms = new System.IO.MemoryStream();
            __result.CopyTo(ms);
            __result.Position = pos;
            var bytes = ms.ToArray();
            // Reuse the byte[] writer.
            Post_GetMemFileContent(__0, bytes);
        }
        catch (Exception ex) { StartupHook.Log("[Dump] Stream postfix: " + ex.Message); }
    }

    // path = __0, result = __result. Post-return: bytes are the clear-text content.
    private static void Post_GetMemFileContent(string __0, byte[] __result)
    {
        try
        {
            string path = __0 ?? "(null)";
            int size = __result?.Length ?? 0;
            string head = __result != null ? HexHead(__result, 8) : "(null)";
            StartupHook.Log($"[Dump] GetMemFileContent(\"{path}\") -> byte[{size}]  head={head}");

            if (__result == null || __result.Length == 0) return;

            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) ext = SniffExtension(__result);
            string origName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(origName)) origName = "content";
            origName = Sanitize(origName);

            int idx = Interlocked.Increment(ref _counter);
            string outFile = Path.Combine(StartupHook.DumpRoot, $"{origName}{ext}");

            // If we're called multiple times for the same file (viewer might read again),
            // don't overwrite a bigger correct copy with a truncated one; and if identical,
            // skip. Otherwise disambiguate.
            lock (_writeLock)
            {
                if (File.Exists(outFile))
                {
                    long existing = new FileInfo(outFile).Length;
                    if (existing == __result.Length) return;      // duplicate, skip
                    if (existing > __result.Length)                // keep the bigger one
                        outFile = Path.Combine(StartupHook.DumpRoot,
                            $"{origName}_{idx:D3}{ext}");
                }
                File.WriteAllBytes(outFile, __result);
            }

            StartupHook.Log($"[Dump]   saved -> {outFile}");
            if (__result.Length >= 4 &&
                __result[0] == 0x25 && __result[1] == 0x50 &&
                __result[2] == 0x44 && __result[3] == 0x46)
            {
                StartupHook.Log("[Dump]   ✓ Valid PDF signature — full book extracted.");
            }
        }
        catch (Exception ex)
        {
            StartupHook.Log("[Dump] postfix error: " + ex);
        }
    }

    private static string SniffExtension(byte[] b)
    {
        if (b.Length < 4) return ".bin";
        if (b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return ".pdf";
        if (b[0] == 0x50 && b[1] == 0x4B) return ".zip";  // epub/zip
        if (b.Length >= 5 && b[0] == 0x3C && b[1] == 0x3F && b[2] == 0x78 && b[3] == 0x6D) return ".xml";
        if (b[0] == 0x3C) return ".html";
        return ".bin";
    }

    private static string HexHead(byte[] b, int n)
    {
        int len = Math.Min(n, b.Length);
        var sb = new StringBuilder(len * 3);
        for (int i = 0; i < len; i++) sb.Append(b[i].ToString("x2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        if (s.Length > 80) s = s.Substring(0, 80);
        return s;
    }
}
