using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace VamScanCache
{
    [BepInPlugin(Guid, "VaM Scan Cache", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "VamScanCache";

        const string DefaultTimeMethods =
            "DAZMorphBank.Init, DAZMorphBank.RefreshPackageMorphs, DAZMorphBank.RefreshRuntimeMorphs, DAZMorphBank.RuntimeImportFromDir, " +
            "DAZMorphBank.RuntimeImportMorph, DAZMorphBank.RebuildAllLookups, DAZMorphBank.InitMorphs, DAZMorphBank.ResetMorphs, " +
            "DAZMorph.LoadMetaFromJSON, DAZCharacterSelector.SyncCustomItems, DAZCharacterSelector.RefreshDynamicClothes, " +
            "DAZCharacterSelector.RefreshDynamicHair, DAZCharacterSelector.RestoreFromJSON, DAZCharacterSelector.LateRestoreFromJSON, " +
            "MVR.FileManagement.FileManager.FindAllFiles, MVR.FileManagement.FileManager.ReadAllText, MVR.FileManagement.FileManager.ReadAllBytes, " +
            "MVR.FileManagement.FileManager.GetFileEntry, MVR.FileManagement.VarPackage.FindFiles, MVR.FileManagement.FileManager.SyncJSONCache, " +
            "SimpleJSON.JSON.Parse";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> CacheEnabled;
        internal static ConfigEntry<bool> FastNames;
        internal static ConfigEntry<bool> BatchAnatomy;
        internal static ConfigEntry<bool> PluginCompileCacheEnabled;
        internal static ConfigEntry<bool> SelfTest;
        internal static ConfigEntry<bool> ProfileLoads;
        internal static ConfigEntry<string> AutoLoadScene;
        internal static ConfigEntry<bool> AutoQuitAfterLoad;
        internal static ConfigEntry<string> TimeMethods;
        internal static ConfigEntry<string> DeepProfileTypes;

        void Awake()
        {
            Log = Logger;
            CacheEnabled = Config.Bind("General", "CacheEnabled", true,
                "Use the .var scan cache. false = original VaM behavior (timings are still logged, for comparison).");
            FastNames = Config.Bind("General", "FastNames", true,
                "Compute file entry names with a substring instead of a Regex (identical results).");
            BatchAnatomy = Config.Bind("General", "BatchAnatomy", true,
                "Run DAZCharacterSelector.SyncAnatomy once after resetting all clothing/hair instead of once per item (identical results).");
            PluginCompileCacheEnabled = Config.Bind("General", "PluginCompileCache", false,
                "Compile each distinct plugin source once and reuse it for every atom that loads it. Behavior change: instances of the same plugin then share static fields.");
            SelfTest = Config.Bind("Debug", "SelfTest", false,
                "After each package refresh, read a few random files from cache-built packages and log whether they match.");
            ProfileLoads = Config.Bind("Debug", "ProfileLoads", true,
                "Record a timeline of each scene load to BepInEx/LoadProfile.txt.");
            AutoLoadScene = Config.Bind("Debug", "AutoLoadScene", "",
                "Scene to load once at startup, e.g. Creator.Package.1:/Saves/scene/Scene.json. Empty = off.");
            AutoQuitAfterLoad = Config.Bind("Debug", "AutoQuitAfterLoad", false,
                "Quit VaM after the AutoLoadScene load finishes (for repeated timing runs).");
            TimeMethods = Config.Bind("Debug", "TimeMethods", DefaultTimeMethods,
                "Comma-separated Type.Method list to time during scene loads (all overloads). Empty = off.");
            DeepProfileTypes = Config.Bind("Debug", "DeepProfileTypes", "",
                "Comma-separated type name prefixes; every non-trivial method in matching types is profiled during scene loads (slow startup). Empty = off.");

            if (CacheEnabled.Value)
            {
                ScanCache.Load(Path.Combine(Paths.CachePath, "varscan.bin"));
            }
            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            if (ProfileLoads.Value && !string.IsNullOrEmpty(TimeMethods.Value))
            {
                MethodTimer.Install(harmony, TimeMethods.Value);
            }
            if (ProfileLoads.Value && !string.IsNullOrEmpty(DeepProfileTypes.Value))
            {
                DeepProfiler.Install(harmony, DeepProfileTypes.Value);
            }
            if (ProfileLoads.Value)
            {
                Application.logMessageReceivedThreaded += LoadProfiler.Log;
            }
            Log.LogInfo("Patched. CacheEnabled=" + CacheEnabled.Value + " FastNames=" + FastNames.Value + " BatchAnatomy=" + BatchAnatomy.Value + " PluginCompileCache=" + PluginCompileCacheEnabled.Value + " ProfileLoads=" + ProfileLoads.Value);
        }

        void Update()
        {
            LoadProfiler.Tick();
            AutoLoad.Tick();
        }
    }
}
