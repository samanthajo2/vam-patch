using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;

namespace VamScanCache
{
    // DAZCharacterSelector.ResetClothing/ResetHair call SetActiveClothingItem/SetActiveHairItem
    // for every installed clothing/hair item, and each of those calls SyncAnatomy(), which
    // itself loops over every clothing and hair item: O(items^2), ~43 s with ~15k items.
    // SyncAnatomy only derives anatomy flags from the items' current state, so running it
    // once after the loop gives the same result.
    static class AnatomyBatch
    {
        public static int Depth;
        public static readonly HashSet<DAZCharacterSelector> Pending = new HashSet<DAZCharacterSelector>();
        static readonly System.Action<DAZCharacterSelector> syncAnatomy =
            AccessTools.MethodDelegate<System.Action<DAZCharacterSelector>>(AccessTools.Method(typeof(DAZCharacterSelector), "SyncAnatomy"));

        public static void Begin() => Depth++;

        public static void End()
        {
            if (--Depth > 0 || Pending.Count == 0)
            {
                return;
            }
            var list = new List<DAZCharacterSelector>(Pending);
            Pending.Clear();
            foreach (var cs in list)
            {
                if (cs != null)
                {
                    syncAnatomy(cs);
                }
            }
        }
    }

    [HarmonyPatch(typeof(DAZCharacterSelector), "ResetClothing")]
    static class ResetClothingBatch
    {
        static bool Prepare() => Plugin.BatchAnatomy.Value;
        static void Prefix() => AnatomyBatch.Begin();
        static void Finalizer() => AnatomyBatch.End();
    }

    [HarmonyPatch(typeof(DAZCharacterSelector), "ResetHair")]
    static class ResetHairBatch
    {
        static bool Prepare() => Plugin.BatchAnatomy.Value;
        static void Prefix() => AnatomyBatch.Begin();
        static void Finalizer() => AnatomyBatch.End();
    }

    [HarmonyPatch(typeof(DAZCharacterSelector), "SyncAnatomy")]
    static class SyncAnatomyBatch
    {
        static bool Prepare() => Plugin.BatchAnatomy.Value;

        static bool Prefix(DAZCharacterSelector __instance)
        {
            if (AnatomyBatch.Depth > 0)
            {
                AnatomyBatch.Pending.Add(__instance);
                return false;
            }
            return true;
        }
    }

    // Diagnostic: which callbacks make JSONStorableString value changes slow during loads.
    [HarmonyPatch(typeof(JSONStorableString), "InternalSetVal")]
    static class SlowStringCallbacks
    {
        class Entry
        {
            public long Calls, Ticks;
        }

        static readonly Dictionary<string, Entry> slow = new Dictionary<string, Entry>();

        static bool Prepare() => Plugin.ProfileLoads.Value;

        static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        static void Postfix(JSONStorableString __instance, long __state)
        {
            long d = Stopwatch.GetTimestamp() - __state;
            if (Stats.Ms(d) < 5)
            {
                return;
            }
            string key = Describe(__instance);
            if (!slow.TryGetValue(key, out var e))
            {
                slow[key] = e = new Entry();
            }
            e.Calls++;
            e.Ticks += d;
        }

        static string Describe(JSONStorableString js)
        {
            string cb = js.setCallbackFunction != null
                ? js.setCallbackFunction.Method.DeclaringType + "." + js.setCallbackFunction.Method.Name
                : js.setJSONCallbackFunction != null
                    ? js.setJSONCallbackFunction.Method.DeclaringType + "." + js.setJSONCallbackFunction.Method.Name
                    : "(no callback)";
            string owner = js.storable != null ? js.storable.GetType().Name : "?";
            return cb + "  [param '" + js.name + "' on " + owner + "]";
        }

        public static void Reset() => slow.Clear();

        public static string Report()
        {
            var list = new List<KeyValuePair<string, Entry>>(slow);
            list.Sort((a, b) => b.Value.Ticks.CompareTo(a.Value.Ticks));
            var sb = new StringBuilder();
            sb.AppendLine("JSONStorableString changes taking >= 5 ms, by callback:");
            for (int i = 0; i < list.Count && i < 25; i++)
            {
                sb.AppendLine(string.Format("  {0,8:F2} s  {1,6} calls  {2}", Stats.Ms(list[i].Value.Ticks) / 1000, list[i].Value.Calls, list[i].Key));
            }
            return sb.ToString();
        }
    }
}
