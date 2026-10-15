using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using HarmonyLib;
using MVR.FileManagement;

namespace VamScanCache
{
    // VaM computes every file/directory entry's Name with Regex.Replace(path, ".*/", ""),
    // i.e. "the part after the last slash". That runs once per file in every package at
    // startup. This swaps the call for a substring, with identical results.
    internal static class FastName
    {
        static readonly MethodInfo regexReplace =
            AccessTools.Method(typeof(Regex), nameof(Regex.Replace), new[] { typeof(string), typeof(string), typeof(string) });
        static readonly MethodInfo replacement = AccessTools.Method(typeof(FastName), nameof(Replace));

        public static string Replace(string input, string pattern, string repl)
        {
            // '.' doesn't match '\n', so with a newline the regex can match more than once.
            if (pattern == ".*/" && repl.Length == 0 && input != null && input.IndexOf('\n') < 0)
            {
                int i = input.LastIndexOf('/');
                return i < 0 ? input : input.Substring(i + 1);
            }
            return Regex.Replace(input, pattern, repl);
        }

        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ci in instructions)
            {
                if (ci.Calls(regexReplace))
                {
                    ci.operand = replacement;
                }
                yield return ci;
            }
        }
    }

    [HarmonyPatch]
    static class FastNamePatch
    {
        static bool Prepare() => Plugin.FastNames.Value;

        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Constructor(typeof(VarFileEntry),
                new[] { typeof(VarPackage), typeof(string), typeof(System.DateTime), typeof(long), typeof(bool) });
            yield return AccessTools.Constructor(typeof(VarDirectoryEntry),
                new[] { typeof(VarPackage), typeof(string), typeof(VarDirectoryEntry) });
            yield return AccessTools.Constructor(typeof(FileEntry), new[] { typeof(string) });
            yield return AccessTools.Constructor(typeof(DirectoryEntry), new[] { typeof(string) });
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            FastName.Transpile(instructions);
    }
}
