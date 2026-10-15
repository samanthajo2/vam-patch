using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;

namespace VamScanCache
{
    // Instrumenting profiler: patches every non-trivial method (including coroutine
    // MoveNext bodies) of Assembly-CSharp types whose name starts with one of the
    // Plugin.DeepProfileTypes prefixes, and tracks inclusive and exclusive (self) time
    // on the main thread. Exclusive time includes Unity engine calls made directly
    // from that method, which is what points at the actual hotspots.
    internal static class DeepProfiler
    {
        class Stat
        {
            public string Name;
            public long Calls, Self, Total;
            public int Depth;
        }

        struct Frame
        {
            public Stat Stat;
            public long Start;
            public long Child;
        }

        static readonly Dictionary<MethodBase, Stat> stats = new Dictionary<MethodBase, Stat>();
        static readonly Frame[] stack = new Frame[4096];
        static int sp;
        static int mainThreadId;
        static bool recording;

        public static void Install(Harmony harmony, string prefixes)
        {
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var pre = new HarmonyMethod(AccessTools.Method(typeof(DeepProfiler), nameof(Prefix)));
            var fin = new HarmonyMethod(AccessTools.Method(typeof(DeepProfiler), nameof(Finalizer)));
            var wanted = new List<string>();
            foreach (var p in prefixes.Split(','))
            {
                if (p.Trim().Length > 0)
                {
                    wanted.Add(p.Trim());
                }
            }
            var sw = Stopwatch.StartNew();
            int patched = 0, failed = 0;
            foreach (var type in AccessTools.TypeByName("SuperController").Assembly.GetTypes())
            {
                string full = type.FullName ?? type.Name;
                bool match = false;
                foreach (var p in wanted)
                {
                    if (full.StartsWith(p, StringComparison.Ordinal))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match || type.IsInterface || type.IsGenericTypeDefinition || type.ContainsGenericParameters)
                {
                    continue;
                }
                foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!ShouldPatch(m))
                    {
                        continue;
                    }
                    try
                    {
                        string name = Pretty(type) + "." + m.Name;
                        stats[m] = new Stat { Name = name };
                        harmony.Patch(m, prefix: pre, finalizer: fin);
                        patched++;
                    }
                    catch
                    {
                        stats.Remove(m);
                        failed++;
                    }
                }
            }
            Plugin.Log.LogInfo("DeepProfiler: patched " + patched + " methods (" + failed + " failed) in " + sw.ElapsedMilliseconds + " ms");
        }

        static string Pretty(Type t)
        {
            // Show coroutine bodies as Owner.<LoadCo>c__Iterator0
            return t.DeclaringType != null ? Pretty(t.DeclaringType) + "/" + t.Name : t.FullName;
        }

        static bool ShouldPatch(MethodInfo m)
        {
            if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters)
            {
                return false;
            }
            if (m.ReturnType == typeof(IEnumerator) || m.ReturnType.IsByRef)
            {
                return false; // the body runs later in MoveNext, which is patched itself
            }
            string n = m.Name;
            if (n == "Update" || n == "LateUpdate" || n == "FixedUpdate" || n == "OnGUI" || n == "OnRenderObject"
                || n.StartsWith("get_") || n.StartsWith("set_") || n.StartsWith("add_") || n.StartsWith("remove_"))
            {
                return false;
            }
            MethodBody body;
            try
            {
                body = m.GetMethodBody();
            }
            catch
            {
                return false;
            }
            // Skip tiny methods: they are cheap and patching them only adds overhead.
            return body != null && body.GetILAsByteArray().Length >= 48;
        }

        public static void Start()
        {
            foreach (var s in stats.Values)
            {
                s.Calls = s.Self = s.Total = 0;
            }
            sp = 0;
            recording = true;
        }

        public static void Stop() => recording = false;

        static void Prefix(MethodBase __originalMethod)
        {
            if (!recording || Thread.CurrentThread.ManagedThreadId != mainThreadId || sp >= stack.Length)
            {
                return;
            }
            stats.TryGetValue(__originalMethod, out var s);
            stack[sp].Stat = s;
            stack[sp].Start = Stopwatch.GetTimestamp();
            stack[sp].Child = 0;
            sp++;
            if (s != null)
            {
                s.Depth++;
            }
        }

        static void Finalizer(MethodBase __originalMethod)
        {
            if (!recording || Thread.CurrentThread.ManagedThreadId != mainThreadId || sp == 0)
            {
                return;
            }
            stats.TryGetValue(__originalMethod, out var s);
            // Unwind to this method's frame (recovers if an inner frame was missed).
            int i = sp - 1;
            while (i > 0 && stack[i].Stat != s)
            {
                i--;
            }
            if (stack[i].Stat != s)
            {
                return;
            }
            long now = Stopwatch.GetTimestamp();
            long elapsed = now - stack[i].Start;
            sp = i;
            if (s != null)
            {
                s.Calls++;
                s.Self += elapsed - stack[i].Child;
                if (--s.Depth == 0)
                {
                    s.Total += elapsed;
                }
            }
            if (sp > 0)
            {
                stack[sp - 1].Child += elapsed;
            }
        }

        public static string Report(int top)
        {
            var list = new List<Stat>();
            foreach (var s in stats.Values)
            {
                if (s.Calls > 0)
                {
                    list.Add(s);
                }
            }
            var sb = new StringBuilder();
            sb.AppendLine("Deep profile, main thread, top " + top + " by SELF time (includes Unity engine calls made from the method):");
            list.Sort((a, b) => b.Self.CompareTo(a.Self));
            for (int i = 0; i < list.Count && i < top; i++)
            {
                var s = list[i];
                sb.AppendLine(string.Format("  self {0,7:F2} s  total {1,7:F2} s  {2,9} calls  {3}",
                    Stats.Ms(s.Self) / 1000, Stats.Ms(s.Total) / 1000, s.Calls, s.Name));
            }
            sb.AppendLine();
            sb.AppendLine("Deep profile, top " + top + " by TOTAL (inclusive) time:");
            list.Sort((a, b) => b.Total.CompareTo(a.Total));
            for (int i = 0; i < list.Count && i < top; i++)
            {
                var s = list[i];
                sb.AppendLine(string.Format("  total {0,7:F2} s  self {1,7:F2} s  {2,9} calls  {3}",
                    Stats.Ms(s.Total) / 1000, Stats.Ms(s.Self) / 1000, s.Calls, s.Name));
            }
            return sb.ToString();
        }
    }
}
