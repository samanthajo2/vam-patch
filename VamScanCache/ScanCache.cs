using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VamScanCache
{
    // Everything VarPackage.Scan() learns from opening a .var, keyed on the
    // .var's path and validated by its size and modified time.
    internal class CachedPackage
    {
        public long Size;
        public long MTimeUtcTicks;
        public string[] Names;
        public long[] Times;
        public long[] Sizes;
        public byte[] Meta; // raw meta.json bytes, null if the package has none
        public string Json; // VaM's own per-package .vmi/.vam JSON cache (Cache/PackageJSON), null if not captured
    }

    internal static class ScanCache
    {
        const uint Magic = 0x31435356; // "VSC1"
        const int FormatVersion = 1;

        static string cachePath;
        static readonly Dictionary<string, CachedPackage> packages =
            new Dictionary<string, CachedPackage>(StringComparer.OrdinalIgnoreCase);
        static readonly object sync = new object();
        static bool dirty;

        public static bool Loaded { get; private set; }

        public static void Load(string path)
        {
            cachePath = path;
            Loaded = true;
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo("No scan cache yet at " + path + " (it will be built this launch)");
                return;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                using (var r = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (r.ReadUInt32() != Magic || r.ReadInt32() != FormatVersion)
                    {
                        Plugin.Log.LogWarning("Scan cache has an unknown format; rebuilding");
                        return;
                    }
                    int count = r.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        string key = r.ReadString();
                        var p = new CachedPackage();
                        p.Size = r.ReadInt64();
                        p.MTimeUtcTicks = r.ReadInt64();
                        int n = r.ReadInt32();
                        p.Names = new string[n];
                        p.Times = new long[n];
                        p.Sizes = new long[n];
                        for (int j = 0; j < n; j++)
                        {
                            p.Names[j] = r.ReadString();
                            p.Times[j] = r.ReadInt64();
                            p.Sizes[j] = r.ReadInt64();
                        }
                        int metaLen = r.ReadInt32();
                        p.Meta = metaLen >= 0 ? r.ReadBytes(metaLen) : null;
                        p.Json = r.ReadBoolean() ? r.ReadString() : null;
                        packages[key] = p;
                    }
                }
                Plugin.Log.LogInfo("Loaded scan cache: " + packages.Count + " packages in " + sw.ElapsedMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                packages.Clear();
                Plugin.Log.LogWarning("Could not read scan cache, rebuilding: " + ex.Message);
            }
        }

        public static CachedPackage TryGet(string key, long size, long mtimeUtcTicks)
        {
            lock (sync)
            {
                if (packages.TryGetValue(key, out var p) && p.Size == size && p.MTimeUtcTicks == mtimeUtcTicks)
                {
                    return p;
                }
                return null;
            }
        }

        public static void Put(string key, CachedPackage p)
        {
            lock (sync)
            {
                packages[key] = p;
                dirty = true;
            }
        }

        public static void SaveIfDirty()
        {
            if (!Loaded)
            {
                return;
            }
            lock (sync)
            {
                if (!dirty)
                {
                    return;
                }
                var sw = Stopwatch.StartNew();
                var gone = new List<string>();
                foreach (var key in packages.Keys)
                {
                    if (!File.Exists(key))
                    {
                        gone.Add(key);
                    }
                }
                foreach (var key in gone)
                {
                    packages.Remove(key);
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                    string tmp = cachePath + ".tmp";
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    using (var w = new BinaryWriter(fs, Encoding.UTF8))
                    {
                        w.Write(Magic);
                        w.Write(FormatVersion);
                        w.Write(packages.Count);
                        foreach (var kv in packages)
                        {
                            var p = kv.Value;
                            w.Write(kv.Key);
                            w.Write(p.Size);
                            w.Write(p.MTimeUtcTicks);
                            w.Write(p.Names.Length);
                            for (int j = 0; j < p.Names.Length; j++)
                            {
                                w.Write(p.Names[j]);
                                w.Write(p.Times[j]);
                                w.Write(p.Sizes[j]);
                            }
                            if (p.Meta == null)
                            {
                                w.Write(-1);
                            }
                            else
                            {
                                w.Write(p.Meta.Length);
                                w.Write(p.Meta);
                            }
                            w.Write(p.Json != null);
                            if (p.Json != null)
                            {
                                w.Write(p.Json);
                            }
                        }
                    }
                    if (File.Exists(cachePath))
                    {
                        File.Delete(cachePath);
                    }
                    File.Move(tmp, cachePath);
                    dirty = false;
                    Plugin.Log.LogInfo("Saved scan cache: " + packages.Count + " packages in " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("Could not save scan cache: " + ex.Message);
                }
            }
        }
    }
}
