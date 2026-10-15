using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using ICSharpCode.SharpZipLib.Zip;
using MVR.FileManagement;
using SimpleJSON;

namespace VamScanCache
{
    // Accessors for VarPackage's protected members (no source, so no friend access).
    internal static class VP
    {
        public static readonly AccessTools.FieldRef<VarPackage, Dictionary<string, VarDirectoryEntry>> DirectoryEntryLookup =
            AccessTools.FieldRefAccess<VarPackage, Dictionary<string, VarDirectoryEntry>>("_DirectoryEntryLookup");
        public static readonly AccessTools.FieldRef<VarPackage, VarFileEntry> MetaEntry =
            AccessTools.FieldRefAccess<VarPackage, VarFileEntry>("metaEntry");
        public static readonly AccessTools.FieldRef<VarPackage, JSONClass> JsonCache =
            AccessTools.FieldRefAccess<VarPackage, JSONClass>("jsonCache");

        public static readonly Action<VarPackage, List<VarFileEntry>> SetFileEntries = Setter<List<VarFileEntry>>("FileEntries");
        public static readonly Action<VarPackage, List<VarDirectoryEntry>> SetDirectoryEntries = Setter<List<VarDirectoryEntry>>("DirectoryEntries");
        public static readonly Action<VarPackage, VarDirectoryEntry> SetRootDirectory = Setter<VarDirectoryEntry>("RootDirectory");
        public static readonly Action<VarPackage, bool> SetIsSimulated = Setter<bool>("IsSimulated");
        public static readonly Action<VarPackage, DateTime> SetLastWriteTime = Setter<DateTime>("LastWriteTime");
        public static readonly Action<VarPackage, long> SetSize = Setter<long>("Size");
        public static readonly Action<VarPackage, ZipFile> SetZipFile = Setter<ZipFile>("ZipFile");
        public static readonly Action<VarPackage, bool> SetInvalid = Setter<bool>("invalid");

        public static readonly Action<VarPackage> SyncEnabled =
            AccessTools.MethodDelegate<Action<VarPackage>>(AccessTools.Method(typeof(VarPackage), "SyncEnabled"));
        public static readonly Action<VarPackage, VarFileEntry> CreateDirectoryEntries =
            AccessTools.MethodDelegate<Action<VarPackage, VarFileEntry>>(AccessTools.Method(typeof(VarPackage), "CreateDirectoryEntries"));

        static Action<VarPackage, T> Setter<T>(string name) =>
            AccessTools.MethodDelegate<Action<VarPackage, T>>(AccessTools.PropertySetter(typeof(VarPackage), name));

        static readonly MethodInfo streamSetter = AccessTools.PropertySetter(typeof(FileEntryStream), "Stream");
        public static void SetStream(FileEntryStream s, Stream value) => streamSetter.Invoke(s, new object[] { value });
    }

    internal sealed class RefEq<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T a, T b) => ReferenceEquals(a, b);
        public int GetHashCode(T o) => RuntimeHelpers.GetHashCode(o);
    }

    // Accumulated timings for one FileManager.Refresh(), so before/after can be compared
    // with CacheEnabled=false vs true.
    internal static class Stats
    {
        public static long ScanTicks, ZipOpenTicks, JsonCacheTicks, MetaTicks;
        public static int Hits, Misses, Uncached, ZipOpens, LazyZipOpens;

        public static void Reset()
        {
            ScanTicks = ZipOpenTicks = JsonCacheTicks = MetaTicks = 0;
            Hits = Misses = Uncached = ZipOpens = 0;
        }

        public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    internal static class Lazy
    {
        // Packages built from the cache: their ZipFile is opened on first real read.
        public static readonly HashSet<VarPackage> Packages = new HashSet<VarPackage>(new RefEq<VarPackage>());
        // meta.json bytes served from the cache instead of decompressing from the .var.
        public static readonly Dictionary<VarFileEntry, byte[]> Meta = new Dictionary<VarFileEntry, byte[]>(new RefEq<VarFileEntry>());
        public static readonly object Sync = new object();

        public static void EnsureZipOpen(VarPackage vp)
        {
            if (vp.ZipFile != null)
            {
                return;
            }
            lock (Sync)
            {
                if (vp.ZipFile != null || !Packages.Contains(vp))
                {
                    return;
                }
                // Same open as VarPackage.Scan().
                FileStream file = File.Open(vp.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var zip = new ZipFile(file);
                zip.IsStreamOwner = true;
                VP.SetZipFile(vp, zip);
                Stats.LazyZipOpens++;
            }
        }
    }

    [HarmonyPatch(typeof(FileManager), nameof(FileManager.Refresh))]
    static class RefreshPatch
    {
        static void Prefix(out long __state)
        {
            Stats.Reset();
            __state = Stopwatch.GetTimestamp();
        }

        static void Postfix(long __state)
        {
            double total = Stats.Ms(Stopwatch.GetTimestamp() - __state);
            Plugin.Log.LogInfo(string.Format(
                "Refresh {0:F0} ms | Scan {1:F0} ms (cache hits {2}, misses {3}, uncached {4}) | ZipFile opens {5}: {6:F0} ms | SyncJSONCache {7:F0} ms | LoadMetaData {8:F0} ms | lazy zip opens so far {9}",
                total, Stats.Ms(Stats.ScanTicks), Stats.Hits, Stats.Misses, Stats.Uncached,
                Stats.ZipOpens, Stats.Ms(Stats.ZipOpenTicks), Stats.Ms(Stats.JsonCacheTicks), Stats.Ms(Stats.MetaTicks),
                Stats.LazyZipOpens));
            if (Plugin.ProfileLoads.Value)
            {
                HeapReport();
            }
            ScanCache.SaveIfDirty();
            if (Plugin.SelfTest.Value)
            {
                SelfTest();
            }
        }

        // Diagnostic: how big VaM's in-memory package file index is, and the managed heap after it.
        static void HeapReport()
        {
            long files = 0, dirs = 0, chars = 0;
            foreach (var vp in FileManager.GetPackages())
            {
                if (vp.FileEntries == null)
                {
                    continue;
                }
                files += vp.FileEntries.Count;
                dirs += vp.DirectoryEntries != null ? vp.DirectoryEntries.Count : 0;
                foreach (var fe in vp.FileEntries)
                {
                    // 9 public path strings, plus 5 flag/hide path strings of about Path's length.
                    chars += fe.Uid.Length + fe.UidLowerInvariant.Length + fe.Path.Length + fe.SlashPath.Length + fe.FullPath.Length
                        + fe.FullSlashPath.Length + fe.Name.Length + fe.InternalPath.Length + fe.InternalSlashPath.Length + 5L * fe.Path.Length;
                }
            }
            long before = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            GC.Collect();
            Plugin.Log.LogInfo(string.Format("Package index: {0} file entries, {1} directory entries (~{2:F0} MB in their path strings). Managed heap {3:F0} MB; one full GC takes {4} ms",
                files, dirs, (chars * 2 + files * 14 * 20) / 1048576.0, before / 1048576.0, sw.ElapsedMilliseconds));
        }

        // Reads real files out of cache-built packages to check the lazy ZipFile open,
        // and meta.json served from the cache.
        static void SelfTest()
        {
            VarPackage[] pkgs;
            lock (Lazy.Sync)
            {
                pkgs = new VarPackage[Lazy.Packages.Count];
                Lazy.Packages.CopyTo(pkgs);
            }
            int tested = 0, ok = 0;
            var rng = new System.Random(1);
            for (int n = 0; n < 200 && tested < 25 && pkgs.Length > 0; n++)
            {
                var vp = pkgs[rng.Next(pkgs.Length)];
                if (vp.FileEntries == null || vp.FileEntries.Count == 0)
                {
                    continue;
                }
                var fe = vp.FileEntries[rng.Next(vp.FileEntries.Count)];
                tested++;
                try
                {
                    long len = 0;
                    using (var s = fe.OpenStream())
                    {
                        var buf = new byte[65536];
                        int r;
                        while ((r = s.Stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            len += r;
                        }
                    }
                    if (len == fe.Size)
                    {
                        ok++;
                    }
                    else
                    {
                        Plugin.Log.LogWarning("SelfTest size mismatch " + fe.Uid + ": read " + len + ", expected " + fe.Size);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("SelfTest failed reading " + fe.Uid + ": " + ex.Message);
                }
            }
            Plugin.Log.LogInfo("SelfTest: " + ok + "/" + tested + " files read correctly from cache-built packages (lazy zip opens " + Stats.LazyZipOpens + ")");
        }
    }

    [HarmonyPatch(typeof(VarPackage), "Scan")]
    static class ScanPatch
    {
        // __state: timestamp at entry, negative if the cache was used (original skipped).
        static bool Prefix(VarPackage __instance, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            if (!Plugin.CacheEnabled.Value || !File.Exists(__instance.Path))
            {
                return true;
            }
            var fi = new FileInfo(__instance.Path);
            var cached = ScanCache.TryGet(__instance.Path, fi.Length, fi.LastWriteTimeUtc.Ticks);
            if (cached == null)
            {
                return true;
            }
            BuildFromCache(__instance, fi, cached);
            Stats.Hits++;
            __state = -__state;
            return false;
        }

        static void Postfix(VarPackage __instance, long __state)
        {
            bool fromCache = __state < 0;
            long start = fromCache ? -__state : __state;
            if (!fromCache && Plugin.CacheEnabled.Value)
            {
                Record(__instance);
            }
            Stats.ScanTicks += Stopwatch.GetTimestamp() - start;
        }

        // Mirrors the zip branch of VarPackage.Scan(), minus opening the zip.
        static void BuildFromCache(VarPackage vp, FileInfo fi, CachedPackage c)
        {
            var files = new List<VarFileEntry>(c.Names.Length);
            var dirs = new List<VarDirectoryEntry>();
            var lookup = new Dictionary<string, VarDirectoryEntry>();
            VP.SetFileEntries(vp, files);
            VP.SetDirectoryEntries(vp, dirs);
            VP.DirectoryEntryLookup(vp) = lookup;
            lookup.Add(string.Empty, vp.RootDirectory); // original does this too, before RootDirectory is (re)created
            VP.SyncEnabled(vp);
            VP.SetIsSimulated(vp, false);
            VP.SetLastWriteTime(vp, fi.LastWriteTime);
            VP.SetSize(vp, fi.Length);
            var root = new VarDirectoryEntry(vp, string.Empty);
            VP.SetRootDirectory(vp, root);
            dirs.Add(root);
            VarFileEntry meta = null;
            for (int i = 0; i < c.Names.Length; i++)
            {
                var fe = new VarFileEntry(vp, c.Names[i], new DateTime(c.Times[i]), c.Sizes[i]);
                files.Add(fe);
                VP.CreateDirectoryEntries(vp, fe);
                if (c.Names[i] == "meta.json")
                {
                    meta = fe;
                }
            }
            VP.MetaEntry(vp) = meta;
            VP.SetZipFile(vp, null);
            lock (Lazy.Sync)
            {
                Lazy.Packages.Add(vp);
                if (meta != null && c.Meta != null)
                {
                    Lazy.Meta[meta] = c.Meta;
                }
            }
            if (meta == null)
            {
                VP.SetInvalid(vp, true);
            }
            else if (CacheManager.CachingEnabled && c.Json != null)
            {
                // Same result as SyncJSONCache() hitting its own cache, without reading
                // two small files per package.
                VP.JsonCache(vp) = JSON.Parse(c.Json).AsObject;
            }
            else
            {
                vp.SyncJSONCache();
            }
        }

        // After the original Scan(): remember what it found. Only successful zip scans are cached;
        // broken packages just take the original path again (and log their error again).
        static void Record(VarPackage vp)
        {
            if (vp.IsSimulated || vp.ZipFile == null)
            {
                Stats.Uncached++;
                return;
            }
            var fi = new FileInfo(vp.Path);
            var files = vp.FileEntries;
            var c = new CachedPackage
            {
                Size = fi.Length,
                MTimeUtcTicks = fi.LastWriteTimeUtc.Ticks,
                Names = new string[files.Count],
                Times = new long[files.Count],
                Sizes = new long[files.Count],
            };
            for (int i = 0; i < files.Count; i++)
            {
                c.Names[i] = files[i].InternalSlashPath;
                c.Times[i] = files[i].LastWriteTime.Ticks;
                c.Sizes[i] = files[i].Size;
            }
            try
            {
                var ze = vp.ZipFile.GetEntry("meta.json");
                if (ze != null)
                {
                    using (var s = vp.ZipFile.GetInputStream(ze))
                    {
                        c.Meta = ReadAll(s);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not cache meta.json of " + vp.Path + ": " + ex.Message);
                c.Meta = null;
            }
            var json = VP.JsonCache(vp);
            c.Json = json != null ? json.ToString(string.Empty) : null;
            ScanCache.Put(vp.Path, c);
            Stats.Misses++;
        }

        static byte[] ReadAll(Stream s)
        {
            var ms = new MemoryStream();
            var buf = new byte[32768];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
    }

    // The one place VaM reads from a package's ZipFile. Opens the zip on demand for
    // cache-built packages, and serves meta.json from the cache.
    [HarmonyPatch(typeof(VarFileEntryStream), MethodType.Constructor, new[] { typeof(VarFileEntry) })]
    static class VarFileEntryStreamPatch
    {
        static bool Prefix(VarFileEntryStream __instance, VarFileEntry entry)
        {
            if (entry == null || entry.Simulated)
            {
                return true;
            }
            byte[] meta;
            lock (Lazy.Sync)
            {
                Lazy.Meta.TryGetValue(entry, out meta);
            }
            if (meta != null)
            {
                VP.SetStream(__instance, new MemoryStream(meta, false));
                return false; // base FileEntryStream ctor is empty, nothing skipped
            }
            Lazy.EnsureZipOpen(entry.Package);
            return true;
        }
    }

    [HarmonyPatch(typeof(VarPackage), nameof(VarPackage.Dispose))]
    static class DisposePatch
    {
        static void Postfix(VarPackage __instance)
        {
            lock (Lazy.Sync)
            {
                Lazy.Packages.Remove(__instance);
                var metaEntry = VP.MetaEntry(__instance);
                if (metaEntry != null)
                {
                    Lazy.Meta.Remove(metaEntry);
                }
            }
        }
    }

    [HarmonyPatch(typeof(VarPackage), nameof(VarPackage.SyncJSONCache))]
    static class SyncJSONCacheTiming
    {
        static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();
        static void Postfix(long __state) => Stats.JsonCacheTicks += Stopwatch.GetTimestamp() - __state;
    }

    [HarmonyPatch(typeof(VarPackage), nameof(VarPackage.LoadMetaData))]
    static class LoadMetaDataTiming
    {
        static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();
        static void Postfix(long __state) => Stats.MetaTicks += Stopwatch.GetTimestamp() - __state;
    }

    // Time SharpZipLib's open + central directory parse, the per-.var disk read.
    [HarmonyPatch]
    static class ZipFileOpenTiming
    {
        static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Constructor(typeof(ZipFile), new[] { typeof(FileStream) });

        static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        static void Postfix(long __state)
        {
            Interlocked.Increment(ref Stats.ZipOpens);
            Stats.ZipOpenTicks += Stopwatch.GetTimestamp() - __state;
        }
    }
}
