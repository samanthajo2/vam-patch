using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace VamScanCache
{
    // Times every overload of the methods named in Plugin.TimeMethods ("Type.Method,...").
    // Inclusive time; recursive calls are counted once. Reported with each scene load profile.
    internal static class MethodTimer
    {
        class Stat
        {
            public string Name;
            public long Calls;
            public long Ticks;
            public long MaxTicks;
            public int Depth;
            public long Start;
        }

        static readonly Dictionary<MethodBase, Stat> stats = new Dictionary<MethodBase, Stat>();
        static readonly object sync = new object();

        public static void Install(Harmony harmony, string spec)
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(MethodTimer), nameof(Prefix)));
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(MethodTimer), nameof(Postfix)));
            foreach (var raw in spec.Split(','))
            {
                string item = raw.Trim();
                int dot = item.LastIndexOf('.');
                if (dot <= 0)
                {
                    continue;
                }
                string typeName = item.Substring(0, dot);
                string methodName = item.Substring(dot + 1);
                var type = AccessTools.TypeByName(typeName);
                if (type == null)
                {
                    Plugin.Log.LogWarning("TimeMethods: no type " + typeName);
                    continue;
                }
                int n = 0;
                foreach (var m in AccessTools.GetDeclaredMethods(type))
                {
                    // Coroutines only build an enumerator when called; timing them says nothing.
                    if (m.Name != methodName || m.IsAbstract || m.IsGenericMethodDefinition || m.ReturnType == typeof(IEnumerator))
                    {
                        continue;
                    }
                    try
                    {
                        lock (sync)
                        {
                            stats[m] = new Stat { Name = item + (n > 0 ? "#" + n : "") };
                        }
                        harmony.Patch(m, prefix, postfix);
                        n++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("TimeMethods: could not patch " + item + ": " + ex.Message);
                    }
                }
                if (n == 0)
                {
                    Plugin.Log.LogWarning("TimeMethods: no method " + item);
                }
            }
        }

        static void Prefix(MethodBase __originalMethod)
        {
            lock (sync)
            {
                if (stats.TryGetValue(__originalMethod, out var s) && s.Depth++ == 0)
                {
                    s.Start = Stopwatch.GetTimestamp();
                }
            }
        }

        static void Postfix(MethodBase __originalMethod)
        {
            long now = Stopwatch.GetTimestamp();
            lock (sync)
            {
                if (stats.TryGetValue(__originalMethod, out var s) && s.Depth > 0 && --s.Depth == 0)
                {
                    long d = now - s.Start;
                    s.Calls++;
                    s.Ticks += d;
                    if (d > s.MaxTicks)
                    {
                        s.MaxTicks = d;
                    }
                }
            }
        }

        public static void Reset()
        {
            lock (sync)
            {
                foreach (var s in stats.Values)
                {
                    s.Calls = s.Ticks = s.MaxTicks = 0;
                }
            }
        }

        public static string Report()
        {
            var list = new List<Stat>();
            lock (sync)
            {
                foreach (var s in stats.Values)
                {
                    if (s.Calls > 0)
                    {
                        list.Add(s);
                    }
                }
            }
            list.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
            var sb = new StringBuilder();
            sb.AppendLine("Timed methods (inclusive; nested methods overlap):");
            foreach (var s in list)
            {
                sb.AppendLine(string.Format("  {0,9:F1} s  {1,8} calls  max {2,8:F0} ms  {3}",
                    Stats.Ms(s.Ticks) / 1000, s.Calls, Stats.Ms(s.MaxTicks), s.Name));
            }
            return sb.ToString();
        }
    }
}
