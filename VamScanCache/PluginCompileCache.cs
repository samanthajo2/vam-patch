using System.Collections.Generic;
using System.IO;
using System.Text;
using DynamicCSharp;
using DynamicCSharp.Compiler;
using HarmonyLib;

namespace VamScanCache
{
    // MVRPluginManager compiles a plugin's C# source every time an atom loads that plugin,
    // so a scene with 76 atoms using the same script compiles it 76 times (and loads 76
    // assemblies that are never unloaded). This reuses the compiled result for identical
    // source + assembly name prefix (which VaM derives from the plugin path and an MD5 of
    // the source).
    //
    // Behavior change: instances of the same plugin then share one assembly, and so share
    // static fields. Off by default for that reason.
    static class PluginCompileCache
    {
        public static string Prefix = "";
        public static readonly Dictionary<string, object> Results = new Dictionary<string, object>();
        public static int Hits, Misses;

        public static string Key(string kind, IEnumerable<string> parts)
        {
            var sb = new StringBuilder(Prefix).Append('\0').Append(kind);
            foreach (var p in parts)
            {
                sb.Append('\0').Append(p);
            }
            return sb.ToString();
        }

        public static IEnumerable<string> FilesWithContents(string[] files)
        {
            foreach (var f in files)
            {
                yield return f;
                yield return File.Exists(f) ? File.ReadAllText(f) : "";
            }
        }

        public static string Report() => "Plugin compile cache: " + Hits + " hits, " + Misses + " compiles";
    }

    [HarmonyPatch(typeof(ScriptCompiler), nameof(ScriptCompiler.SetSuggestedAssemblyNamePrefix))]
    static class TrackPrefix
    {
        static bool Prepare() => Plugin.PluginCompileCacheEnabled.Value;
        static void Postfix(string assemblyNamePrefix) => PluginCompileCache.Prefix = assemblyNamePrefix ?? "";
    }

    [HarmonyPatch(typeof(ScriptDomain), nameof(ScriptDomain.CompileAndLoadScriptSource))]
    static class CacheCompileSource
    {
        static bool Prepare() => Plugin.PluginCompileCacheEnabled.Value;

        static bool Prefix(string source, ref ScriptType __result, out string __state)
        {
            __state = PluginCompileCache.Key("source", new[] { source });
            if (PluginCompileCache.Results.TryGetValue(__state, out var hit))
            {
                PluginCompileCache.Hits++;
                __result = (ScriptType)hit;
                __state = null;
                return false;
            }
            return true;
        }

        static void Postfix(ScriptType __result, string __state)
        {
            if (__state != null && __result != null)
            {
                PluginCompileCache.Misses++;
                PluginCompileCache.Results[__state] = __result;
            }
        }
    }

    [HarmonyPatch(typeof(ScriptDomain), nameof(ScriptDomain.CompileAndLoadScriptSources))]
    static class CacheCompileSources
    {
        static bool Prepare() => Plugin.PluginCompileCacheEnabled.Value;

        static bool Prefix(string[] sources, ref ScriptAssembly __result, out string __state)
        {
            __state = PluginCompileCache.Key("sources", sources);
            if (PluginCompileCache.Results.TryGetValue(__state, out var hit))
            {
                PluginCompileCache.Hits++;
                __result = (ScriptAssembly)hit;
                __state = null;
                return false;
            }
            return true;
        }

        static void Postfix(ScriptAssembly __result, string __state)
        {
            if (__state != null && __result != null)
            {
                PluginCompileCache.Misses++;
                PluginCompileCache.Results[__state] = __result;
            }
        }
    }

    [HarmonyPatch(typeof(ScriptDomain), nameof(ScriptDomain.CompileAndLoadScriptFiles))]
    static class CacheCompileFiles
    {
        static bool Prepare() => Plugin.PluginCompileCacheEnabled.Value;

        static bool Prefix(string[] files, ref ScriptAssembly __result, out string __state)
        {
            // Files can be edited while VaM runs, so their contents are part of the key.
            __state = PluginCompileCache.Key("files", PluginCompileCache.FilesWithContents(files));
            if (PluginCompileCache.Results.TryGetValue(__state, out var hit))
            {
                PluginCompileCache.Hits++;
                __result = (ScriptAssembly)hit;
                __state = null;
                return false;
            }
            return true;
        }

        static void Postfix(ScriptAssembly __result, string __state)
        {
            if (__state != null && __result != null)
            {
                PluginCompileCache.Misses++;
                PluginCompileCache.Results[__state] = __result;
            }
        }
    }
}

namespace VamScanCache
{
    [HarmonyLib.HarmonyPatch(typeof(DynamicCSharp.ScriptDomain), nameof(DynamicCSharp.ScriptDomain.CompileAndLoadScriptFile))]
    static class CacheCompileFile
    {
        static bool Prepare() => Plugin.PluginCompileCacheEnabled.Value;

        static bool Prefix(string file, ref DynamicCSharp.ScriptType __result, out string __state)
        {
            __state = PluginCompileCache.Key("file", PluginCompileCache.FilesWithContents(new[] { file }));
            if (PluginCompileCache.Results.TryGetValue(__state, out var hit))
            {
                PluginCompileCache.Hits++;
                __result = (DynamicCSharp.ScriptType)hit;
                __state = null;
                return false;
            }
            return true;
        }

        static void Postfix(DynamicCSharp.ScriptType __result, string __state)
        {
            if (__state != null && __result != null)
            {
                PluginCompileCache.Misses++;
                PluginCompileCache.Results[__state] = __result;
            }
        }
    }
}

namespace VamScanCache
{
    // Diagnostic: duration of each actual C# compile, in order, to see whether compiles
    // get slower as more plugin assemblies accumulate.
    [HarmonyLib.HarmonyPatch]
    static class CompileTiming
    {
        public static readonly System.Collections.Generic.List<double> Durations = new System.Collections.Generic.List<double>();

        static bool Prepare() => Plugin.ProfileLoads.Value;

        static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return HarmonyLib.AccessTools.Method(typeof(DynamicCSharp.Compiler.ScriptCompiler), "CompileSources");
            yield return HarmonyLib.AccessTools.Method(typeof(DynamicCSharp.Compiler.ScriptCompiler), "CompileFiles");
        }

        static void Prefix(out long __state) => __state = System.Diagnostics.Stopwatch.GetTimestamp();

        static void Postfix(long __state) => Durations.Add(Stats.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - __state));

        public static string Report()
        {
            var sb = new System.Text.StringBuilder();
            double total = 0;
            foreach (var d in Durations)
            {
                total += d;
            }
            sb.AppendLine(string.Format("C# compiles: {0}, total {1:F1} s. Each, in order (ms):", Durations.Count, total / 1000));
            for (int i = 0; i < Durations.Count; i++)
            {
                sb.Append(i % 20 == 0 ? "  " : " ").Append(Durations[i].ToString("F0"));
                if (i % 20 == 19)
                {
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
            return sb.ToString();
        }
    }
}
