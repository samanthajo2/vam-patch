using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;

namespace VamScanCache
{
    // Builds a timeline of one scene load: VaM's own loading status changes, every
    // log message, slow Atom.Restore calls, and main-thread frame hitches. Written to
    // BepInEx/LoadProfile.txt when the load finishes, with a summary in the BepInEx log.
    internal static class LoadProfiler
    {
        class Segment
        {
            public string Status;
            public double Start, End;
        }

        static readonly object sync = new object();
        static readonly Stopwatch clock = new Stopwatch();
        static bool active;
        static string sceneName;
        static readonly List<string> timeline = new List<string>();
        static readonly List<Segment> segments = new List<Segment>();
        static readonly List<KeyValuePair<double, string>> restores = new List<KeyValuePair<double, string>>();
        static readonly List<KeyValuePair<double, string>> hitches = new List<KeyValuePair<double, string>>();
        static string status = "(before first status)";
        static double lastFrame;
        static bool sawLoading;
        static int lazyOpensAtStart;
        static int gcAtStart;
        static readonly AccessTools.FieldRef<SuperController, bool> isLoading =
            AccessTools.FieldRefAccess<SuperController, bool>("_isLoading");

        public static double Now => clock.Elapsed.TotalMilliseconds;

        public static void Start(string scene)
        {
            lock (sync)
            {
                if (active)
                {
                    Add("(another load started; previous load not finished)");
                    return;
                }
                active = true;
                sawLoading = false;
                sceneName = scene;
                timeline.Clear();
                segments.Clear();
                restores.Clear();
                hitches.Clear();
                clock.Reset();
                clock.Start();
                lastFrame = 0;
                status = "(before first status)";
                segments.Add(new Segment { Status = status, Start = 0 });
                MethodTimer.Reset();
                DeepProfiler.Start();
                SlowStringCallbacks.Reset();
                CompileTiming.Durations.Clear();
                gcAtStart = GC.CollectionCount(0);
                lazyOpensAtStart = Stats.LazyZipOpens;
                Add("LOAD START " + scene);
            }
        }

        static void Add(string line)
        {
            timeline.Add(string.Format("{0,9:F3}s  {1}", Now / 1000.0, line));
        }

        public static void Status(string txt)
        {
            lock (sync)
            {
                if (!active || txt == status)
                {
                    return;
                }
                double t = Now;
                segments[segments.Count - 1].End = t;
                status = txt;
                segments.Add(new Segment { Status = txt, Start = t });
                Add("STATUS " + txt);
            }
        }

        public static void Log(string condition, string stackTrace, LogType type)
        {
            lock (sync)
            {
                if (!active || condition == null)
                {
                    return;
                }
                string first = condition;
                int nl = first.IndexOf('\n');
                if (nl >= 0)
                {
                    first = first.Substring(0, nl);
                }
                if (first.Length > 300)
                {
                    first = first.Substring(0, 300) + "...";
                }
                Add("LOG[" + type + "] " + first);
            }
        }

        public static void Restored(Atom atom, double ms)
        {
            lock (sync)
            {
                if (!active)
                {
                    return;
                }
                string name = atom != null ? atom.uid + " (" + atom.type + ")" : "?";
                restores.Add(new KeyValuePair<double, string>(ms, name));
                if (ms >= 100)
                {
                    Add(string.Format("RESTORE {0:F0} ms  {1}", ms, name));
                }
            }
        }

        // Called every frame from the plugin's Update().
        public static void Tick()
        {
            lock (sync)
            {
                if (!active)
                {
                    return;
                }
                double t = Now;
                double dt = t - lastFrame;
                lastFrame = t;
                if (dt >= 250)
                {
                    hitches.Add(new KeyValuePair<double, string>(dt, status));
                    Add(string.Format("HITCH {0:F0} ms frame during: {1}", dt, status));
                }
                var sc = SuperController.singleton;
                if (sc == null)
                {
                    return;
                }
                bool loading = isLoading(sc);
                if (loading)
                {
                    sawLoading = true;
                }
                else if (sawLoading)
                {
                    Finish();
                }
            }
        }

        // Diagnostic: how many collections happened during the load, and what one full
        // collection costs with the current heap (Unity 2018 Mono GC is non-generational,
        // so every collection scans the whole heap).
        static string GcReport(int collections)
        {
            long heap = GC.GetTotalMemory(false);
            var sw = Stopwatch.StartNew();
            GC.Collect();
            double ms = sw.Elapsed.TotalMilliseconds;
            return string.Format("GC: {0} collections during load; one full GC.Collect() now takes {1:F0} ms with {2:F0} MB managed heap (=> roughly {3:F1} s of load spent in GC)",
                collections, ms, heap / 1048576.0, collections * ms / 1000);
        }

        static void Finish()
        {
            double total = Now;
            segments[segments.Count - 1].End = total;
            Add("LOAD END");
            DeepProfiler.Stop();
            active = false;

            // Merge segments by status text so repeated statuses (e.g. waits) add up.
            var byStatus = new Dictionary<string, double>();
            foreach (var s in segments)
            {
                string key = s.Status;
                byStatus.TryGetValue(key, out var d);
                byStatus[key] = d + (s.End - s.Start);
            }
            var top = new List<KeyValuePair<string, double>>(byStatus);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            restores.Sort((a, b) => b.Key.CompareTo(a.Key));
            double restoreTotal = 0;
            foreach (var r in restores)
            {
                restoreTotal += r.Key;
            }
            double hitchTotal = 0;
            foreach (var h in hitches)
            {
                hitchTotal += h.Key;
            }

            var sb = new StringBuilder();
            sb.AppendLine(string.Format("Scene load profile: {0}", sceneName));
            sb.AppendLine(string.Format("Total {0:F1} s | Atom.Restore total {1:F1} s over {2} atoms | {3} hitches >=250 ms totaling {4:F1} s",
                total / 1000, restoreTotal / 1000, restores.Count, hitches.Count, hitchTotal / 1000));
            sb.AppendLine();
            sb.AppendLine("Time per loading status (top 30):");
            for (int i = 0; i < top.Count && i < 30; i++)
            {
                sb.AppendLine(string.Format("  {0,8:F1} s  {1}", top[i].Value / 1000, top[i].Key));
            }
            sb.AppendLine();
            sb.AppendLine("Packages whose .var had to be opened during this load: " + (Stats.LazyZipOpens - lazyOpensAtStart));
            sb.AppendLine();
            sb.Append(MethodTimer.Report());
            sb.AppendLine();
            sb.Append(DeepProfiler.Report(60));
            sb.AppendLine();
            sb.Append(SlowStringCallbacks.Report());
            sb.AppendLine(PluginCompileCache.Report());
            sb.Append(CompileTiming.Report());
            sb.AppendLine();
            sb.AppendLine(GcReport(GC.CollectionCount(0) - gcAtStart));
            sb.AppendLine();
            sb.AppendLine("Slowest Atom.Restore (top 20):");
            for (int i = 0; i < restores.Count && i < 20; i++)
            {
                sb.AppendLine(string.Format("  {0,8:F0} ms  {1}", restores[i].Key, restores[i].Value));
            }
            string summary = sb.ToString();

            try
            {
                string path = Path.Combine(Paths.BepInExRootPath, "LoadProfile.txt");
                using (var w = new StreamWriter(path, false, Encoding.UTF8))
                {
                    w.Write(summary);
                    w.WriteLine();
                    w.WriteLine("Timeline:");
                    foreach (var line in timeline)
                    {
                        w.WriteLine(line);
                    }
                }
                Plugin.Log.LogInfo("Scene load profile written to " + path + "\n" + summary);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not write load profile: " + ex.Message + "\n" + summary);
            }
            AutoLoad.OnLoadFinished();
        }
    }

    [HarmonyPatch(typeof(SuperController), "LoadInternal")]
    static class LoadInternalProfile
    {
        static bool Prepare() => Plugin.ProfileLoads.Value;
        static void Prefix(string saveName) => LoadProfiler.Start(saveName);
    }

    [HarmonyPatch(typeof(SuperController), "UpdateLoadingStatus")]
    static class LoadingStatusProfile
    {
        static bool Prepare() => Plugin.ProfileLoads.Value;
        static void Prefix(string txt) => LoadProfiler.Status(txt);
    }

    [HarmonyPatch(typeof(Atom), nameof(Atom.Restore))]
    static class AtomRestoreProfile
    {
        static bool Prepare() => Plugin.ProfileLoads.Value;
        static void Prefix(out double __state) => __state = LoadProfiler.Now;
        static void Postfix(Atom __instance, double __state) => LoadProfiler.Restored(__instance, LoadProfiler.Now - __state);
    }
}
