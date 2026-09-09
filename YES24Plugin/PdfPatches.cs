using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace YES24Plugin;

/// <summary>
/// Robust hooks and scavenger for YES24 eBook:
/// 1. Anti-Tamper Bypass (UnDrmAntiPassAssembly / RuntimeDetector)
/// 2. Managed Hooks on ezPDFBookLib (EZPDF_OpenDRM) and EPUBViewModel
/// 3. Multi-strategy live scavenger:
///    - Invokes UnDrmClient byte[] decryptor (dynamic matching AB / GetMemFileContent)
///    - Invokes UnDrmClient Stream decryptor (dynamic matching C / GetStreamContent)
///    - Reads PDFViewModel._pStream directly
/// </summary>
internal static class PdfPatches
{
    private static readonly object _writeLock = new();
    private static int _counter = 0;
    private static readonly HashSet<string> _dumpedFiles = new(StringComparer.OrdinalIgnoreCase);

    // Captured live PDFViewModel instance
    internal static object LivePdfViewModel;
    internal static Type PdfViewModelType;

    public static void HookSecurity(Harmony h, Assembly undrm)
    {
        try
        {
            var antiType = undrm.GetType("UnDrmSecurityCoreNet.UnDrmAntiPassAssembly");
            if (antiType != null)
            {
                var mDetect = antiType.GetMethod("DetectFakeAssembly", BindingFlags.Public | BindingFlags.Static);
                if (mDetect != null)
                {
                    var prefix = new HarmonyMethod(typeof(PdfPatches).GetMethod(nameof(Prefix_DetectFakeAssembly), BindingFlags.Static | BindingFlags.NonPublic));
                    h.Patch(mDetect, prefix: prefix);
                    StartupHook.Log("[Hook]   patched UnDrmAntiPassAssembly.DetectFakeAssembly");
                }
            }

            var detectorType = undrm.GetType("UnDrmSecurityCoreNet.UnDrmRuntimeAntiPassAssemblyDetector");
            if (detectorType != null)
            {
                var mCheck = detectorType.GetMethod("CheckForFakeAssembly", BindingFlags.Public | BindingFlags.Static);
                if (mCheck != null)
                {
                    var prefix = new HarmonyMethod(typeof(PdfPatches).GetMethod(nameof(Prefix_CheckForFakeAssembly), BindingFlags.Static | BindingFlags.NonPublic));
                    h.Patch(mCheck, prefix: prefix);
                    StartupHook.Log("[Hook]   patched UnDrmRuntimeAntiPassAssemblyDetector.CheckForFakeAssembly");
                }
            }
        }
        catch (Exception ex)
        {
            StartupHook.Log("[Hook]   Security neutralizer failed: " + ex.Message);
        }
    }

    private static bool Prefix_DetectFakeAssembly(ref bool __result)
    {
        __result = false; // No fake assembly detected!
        return false;     // Skip original check!
    }

    private static bool Prefix_CheckForFakeAssembly()
    {
        return false;     // Skip original check!
    }

    public static void HookPdfViewModel(Harmony h, Assembly yes24)
    {
        // 1. Cache PDFViewModel type for visual tree scanner
        PdfViewModelType = yes24.GetTypes()
            .FirstOrDefault(x => x.FullName == "Yes24eBook.ViewModels.Viewer.PDFViewModel");
        if (PdfViewModelType != null)
        {
            StartupHook.Log("[Hook] Located PDFViewModel type.");
        }

        // 2. Hook ezPDFBookLib::EZPDF_OpenDRM
        try
        {
            var ezType = yes24.GetTypes()
                .FirstOrDefault(x => x.FullName == "Yes24eBook.Viewer.Pdf.ezPDFBookLib");
            if (ezType != null)
            {
                var mOpen = ezType.GetMethod("EZPDF_OpenDRM", BindingFlags.Public | BindingFlags.Static);
                if (mOpen != null)
                {
                    var post = new HarmonyMethod(typeof(PdfPatches).GetMethod(nameof(Post_EZPDF_OpenDRM), BindingFlags.Static | BindingFlags.NonPublic));
                    h.Patch(mOpen, postfix: post);
                    StartupHook.Log("[Hook]   patched ezPDFBookLib.EZPDF_OpenDRM");
                }
            }
        }
        catch (Exception ex)
        {
            StartupHook.Log("[Hook]   EZPDF_OpenDRM patch failed: " + ex.Message);
        }

        // 3. Hook EPUBViewModel for EPUB books
        try
        {
            var epubType = yes24.GetTypes()
                .FirstOrDefault(x => x.FullName == "Yes24eBook.ViewModels.Viewer.EPUBViewModel");
            if (epubType != null)
            {
                var mMem = epubType.GetMethod("getMemFileContent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (mMem != null)
                {
                    var post = new HarmonyMethod(typeof(PdfPatches).GetMethod(nameof(Post_GetMemFileContent), BindingFlags.Static | BindingFlags.NonPublic));
                    h.Patch(mMem, postfix: post);
                    StartupHook.Log("[Hook]   patched EPUBViewModel.getMemFileContent");
                }

                var mStream = epubType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "getStreamContent" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(string));
                if (mStream != null)
                {
                    var post = new HarmonyMethod(typeof(PdfPatches).GetMethod(nameof(Post_GetStreamContent), BindingFlags.Static | BindingFlags.NonPublic));
                    h.Patch(mStream, postfix: post);
                    StartupHook.Log("[Hook]   patched EPUBViewModel.getStreamContent");
                }
            }
        }
        catch (Exception ex)
        {
            StartupHook.Log("[Hook]   EPUBViewModel patch failed: " + ex.Message);
        }

        // 4. Kick off background scavenger
        Task.Run(ActiveScavenge);
    }

    private static void Post_EZPDF_OpenDRM(string __1)
    {
        try
        {
            string path = __1;
            StartupHook.Log($"[Hook] EZPDF_OpenDRM opened: {path}");
            if (!string.IsNullOrEmpty(path))
            {
                Task.Run(() =>
                {
                    Thread.Sleep(500); // Give viewer a moment to settle stream
                    TryExtractFile(path);
                });
            }
        }
        catch (Exception ex)
        {
            StartupHook.Log("[Hook] Post_EZPDF_OpenDRM error: " + ex.Message);
        }
    }

    private static bool TryExtractFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        if (_dumpedFiles.Contains(path)) return true;

        byte[] bytes = InvokeGetMemFileContent(path);
        if (bytes != null && bytes.Length > 0)
        {
            SaveDump(path, bytes);
            _dumpedFiles.Add(path);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Walk WPF PresentationSource -> visual tree -> DataContext looking
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
        string contentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Yes24eBook", ".content");

        int lastLogSec = 0;

        for (int spin = 0; spin < 600; spin++) // up to 10 min
        {
            Thread.Sleep(1000);
            if (LivePdfViewModel == null)
            {
                LivePdfViewModel = FindLivePdfViewModel();
                if (LivePdfViewModel != null)
                    StartupHook.Log("[Scavenge] Located PDFViewModel via visual tree.");
            }
            if (LivePdfViewModel == null) continue;
            if (!Directory.Exists(contentRoot)) continue;

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
                    if (_dumpedFiles.Contains(f)) continue;

                    string outFile = Path.Combine(StartupHook.DumpRoot, Path.GetFileName(f));
                    if (File.Exists(outFile) && new FileInfo(outFile).Length > 1024)
                    {
                        _dumpedFiles.Add(f);
                        continue;
                    }

                    try
                    {
                        byte[] bytes = InvokeGetMemFileContent(f);
                        if (bytes == null || bytes.Length == 0)
                        {
                            if (spin - lastLogSec > 5)
                            {
                                StartupHook.Log($"[Scavenge] {Path.GetFileName(f)} -> waiting for decryption (null/empty)");
                                lastLogSec = spin;
                            }
                            continue;
                        }

                        SaveDump(f, bytes);
                        _dumpedFiles.Add(f);
                    }
                    catch (Exception ex)
                    {
                        StartupHook.Log($"[Scavenge] {Path.GetFileName(f)} -> ERROR: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
    }

    private static byte[] InvokeGetMemFileContent(string path)
    {
        var vm = LivePdfViewModel ?? FindLivePdfViewModel();
        if (vm == null || PdfViewModelType == null) return null;

        // 1. First get drmClient instance
        object drmClient = null;
        var propInfo = PdfViewModelType.GetProperty("drmClient",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            ?? PdfViewModelType.GetProperty("get_drmClient",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

        if (propInfo != null)
            drmClient = propInfo.GetValue(vm);

        if (drmClient == null)
        {
            var getter = PdfViewModelType.GetMethod("get_drmClient",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            if (getter != null)
                drmClient = getter.Invoke(vm, null);
        }

        if (drmClient == null)
        {
            var f = PdfViewModelType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(x => x.FieldType.FullName != null && x.FieldType.FullName.Contains("UnDrmClient"));
            if (f != null)
                drmClient = f.GetValue(vm);
        }

        // Strategy A: Call method on drmClient returning byte[] taking (string)
        // Matches obfuscated "AB" in latest version, "GetMemFileContent" in older versions
        if (drmClient != null)
        {
            var miBytes = drmClient.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => m.ReturnType == typeof(byte[]) &&
                                     m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType == typeof(string));
            if (miBytes != null)
            {
                try
                {
                    var res = (byte[])miBytes.Invoke(drmClient, new object[] { path });
                    if (res != null && res.Length > 0)
                    {
                        StartupHook.Log($"[Scavenge] Obtained {res.Length} bytes via {miBytes.DeclaringType?.Name}.{miBytes.Name}('{Path.GetFileName(path)}')");
                        return res;
                    }
                }
                catch (Exception ex)
                {
                    StartupHook.Log($"[Scavenge] {miBytes.Name} error: {ex.Message}");
                }
            }

            // Strategy B: Call method on drmClient returning Stream taking (string)
            // Matches obfuscated "C" in latest version, "GetStreamContent" in older versions
            var miStream = drmClient.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m => typeof(Stream).IsAssignableFrom(m.ReturnType) &&
                                     m.GetParameters().Length == 1 &&
                                     m.GetParameters()[0].ParameterType == typeof(string));
            if (miStream != null)
            {
                try
                {
                    using var s = miStream.Invoke(drmClient, new object[] { path }) as Stream;
                    if (s != null)
                    {
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        var res = ms.ToArray();
                        if (res != null && res.Length > 0)
                        {
                            StartupHook.Log($"[Scavenge] Obtained {res.Length} bytes via {miStream.DeclaringType?.Name}.{miStream.Name} stream");
                            return res;
                        }
                    }
                }
                catch (Exception ex)
                {
                    StartupHook.Log($"[Scavenge] {miStream.Name} error: {ex.Message}");
                }
            }
        }
        else
        {
            StartupHook.Log("[Scavenge] drmClient not reachable on PDFViewModel.");
        }

        // Strategy C: Read _pStream or any Stream field on PDFViewModel directly
        var streamFields = PdfViewModelType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            .Where(f => typeof(Stream).IsAssignableFrom(f.FieldType));

        foreach (var sf in streamFields)
        {
            try
            {
                var s = sf.GetValue(vm) as Stream;
                if (s != null && s.CanRead)
                {
                    long origPos = 0;
                    if (s.CanSeek)
                    {
                        origPos = s.Position;
                        s.Position = 0;
                    }
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    if (s.CanSeek)
                    {
                        s.Position = origPos;
                    }
                    var res = ms.ToArray();
                    if (res != null && res.Length > 0)
                    {
                        StartupHook.Log($"[Scavenge] Obtained {res.Length} bytes directly via {sf.Name} stream");
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                StartupHook.Log($"[Scavenge] Stream field {sf.Name} read error: {ex.Message}");
            }
        }

        return null;
    }

    // Postfix for getStreamContent
    private static void Post_GetStreamContent(string __0, Stream __result)
    {
        try
        {
            if (__result == null) return;
            if (!__result.CanSeek)
            {
                StartupHook.Log($"[Dump] Stream('{__0}') — non-seekable, skipping.");
                return;
            }
            long pos = __result.Position;
            __result.Position = 0;
            using var ms = new MemoryStream();
            __result.CopyTo(ms);
            __result.Position = pos;
            var bytes = ms.ToArray();
            SaveDump(__0, bytes);
        }
        catch (Exception ex) { StartupHook.Log("[Dump] Stream postfix: " + ex.Message); }
    }

    // Postfix for getMemFileContent
    private static void Post_GetMemFileContent(string __0, byte[] __result)
    {
        try
        {
            if (__result != null && __result.Length > 0)
                SaveDump(__0, __result);
        }
        catch (Exception ex)
        {
            StartupHook.Log("[Dump] Post_GetMemFileContent error: " + ex);
        }
    }

    private static void SaveDump(string origPath, byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return;

        string path = origPath ?? "(unknown)";
        int size = bytes.Length;
        string head = HexHead(bytes, 8);
        StartupHook.Log($"[Dump] Decrypted payload: '{Path.GetFileName(path)}' -> {size} bytes, head={head}");

        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) ext = SniffExtension(bytes);
        string origName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(origName)) origName = "content";
        origName = Sanitize(origName);

        int idx = Interlocked.Increment(ref _counter);
        string outFile = Path.Combine(StartupHook.DumpRoot, $"{origName}{ext}");

        lock (_writeLock)
        {
            if (File.Exists(outFile))
            {
                long existing = new FileInfo(outFile).Length;
                if (existing == bytes.Length) return; // duplicate, skip
                if (existing > bytes.Length)
                    outFile = Path.Combine(StartupHook.DumpRoot, $"{origName}_{idx:D3}{ext}");
            }
            File.WriteAllBytes(outFile, bytes);
        }

        StartupHook.Log($"[Dump]   Saved -> {outFile}");
        if (bytes.Length >= 4 &&
            bytes[0] == 0x25 && bytes[1] == 0x50 &&
            bytes[2] == 0x44 && bytes[3] == 0x46)
        {
            StartupHook.Log("[Dump]   [SUCCESS] Valid PDF signature (%PDF) - PDF extracted cleanly!");
        }
        else if (bytes.Length >= 4 &&
            bytes[0] == 0x50 && bytes[1] == 0x4B)
        {
            StartupHook.Log("[Dump]   [SUCCESS] Valid ZIP/EPUB signature (PK..) - EPUB extracted cleanly!");
        }
    }

    private static string SniffExtension(byte[] b)
    {
        if (b.Length < 4) return ".bin";
        if (b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) return ".pdf";
        if (b[0] == 0x50 && b[1] == 0x4B) return ".zip";
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
