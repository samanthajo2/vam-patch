using System.Diagnostics;
using UnityEngine;

namespace VamScanCache
{
    // Debug helper: loads Plugin.AutoLoadScene once VaM has been idle for a few seconds,
    // so scene-load timings can be reproduced without clicking through the UI.
    internal static class AutoLoad
    {
        static bool done;
        static readonly Stopwatch idle = new Stopwatch();

        public static void Tick()
        {
            string scene = Plugin.AutoLoadScene.Value;
            if (done || string.IsNullOrEmpty(scene))
            {
                return;
            }
            var sc = SuperController.singleton;
            if (sc == null || sc.isLoading)
            {
                idle.Reset();
                return;
            }
            if (!idle.IsRunning)
            {
                idle.Start();
            }
            if (idle.ElapsedMilliseconds < 5000)
            {
                return;
            }
            done = true;
            Plugin.Log.LogInfo("AutoLoadScene: loading " + scene);
            sc.Load(scene);
        }

        public static void OnLoadFinished()
        {
            if (done && Plugin.AutoQuitAfterLoad.Value)
            {
                Plugin.Log.LogInfo("AutoQuitAfterLoad: quitting");
                Application.Quit();
            }
        }
    }
}
