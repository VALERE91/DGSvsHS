using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace DGSvsHS.EditorTools
{
    /// <summary>
    /// One-click switcher between the three Unity build flavors used by the DGSvsHS paper:
    /// <list type="bullet">
    ///   <item><b>DGS</b> — Unity DOTS dedicated server. Define: <c>WITH_DGS</c>. Stripping: Low.
    ///         Compiles the full DOTS sim (Server/Dots/*), NGO transport, all gameplay.</item>
    ///   <item><b>HS</b> — Client build connecting to the Bevy/Avian or Arch/BepuPhysics server
    ///         via QUIC. Define: <c>WITH_HS</c>. Stripping: Low. Excludes the Unity DOTS server.</item>
    ///   <item><b>BareBone</b> — Minimal Unity listener for the green-computing baseline.
    ///         Define: <c>WITH_BAREBONE</c>. Stripping: High to give the cleanest possible
    ///         RAM/RSS reading for the apples-to-apples comparison against Bevy/Arch idle.</item>
    /// </list>
    ///
    /// <para>Switches apply to BOTH <see cref="NamedBuildTarget.Standalone"/> and
    /// <see cref="NamedBuildTarget.Server"/> so subsequent Build / Build Profile invocations
    /// pick up the right defines regardless of which platform is selected. The currently
    /// active mode shows a check-mark in the menu.</para>
    /// </summary>
    public static class BuildModeSwitcher
    {
        private const string MenuRoot = "DGSvsHS/Build Mode/";
        private const string MenuDGS      = MenuRoot + "DGS — Unity DOTS Server";
        private const string MenuHS       = MenuRoot + "HS — Client to Bevy/Arch (QUIC)";
        private const string MenuBareBone = MenuRoot + "BareBone — Minimal Listener Baseline";
        private const string MenuShow     = MenuRoot + "Show Current";

        private const string DefineDGS      = "WITH_DGS";
        private const string DefineHS       = "WITH_HS";
        private const string DefineBareBone = "WITH_BAREBONE";

        private static readonly string[] ModeDefines = { DefineDGS, DefineHS, DefineBareBone };

        // ---------- Menu items ----------

        [MenuItem(MenuDGS, priority = 0)]
        public static void SetModeDGS() => Apply("DGS", DefineDGS, ManagedStrippingLevel.Low);

        [MenuItem(MenuHS, priority = 1)]
        public static void SetModeHS() => Apply("HS", DefineHS, ManagedStrippingLevel.Low);

        [MenuItem(MenuBareBone, priority = 2)]
        public static void SetModeBareBone() => Apply("BareBone", DefineBareBone, ManagedStrippingLevel.Low);

        [MenuItem(MenuShow, priority = 20)]
        public static void ShowCurrent()
        {
            var serverDefines = GetDefines(NamedBuildTarget.Server);
            var standaloneDefines = GetDefines(NamedBuildTarget.Standalone);
            var serverStrip = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Server);
            var standaloneStrip = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.Standalone);
            string activeMode = DetectActiveMode(serverDefines);
            Debug.Log(
                $"[BuildMode] Active: {activeMode}\n" +
                $"  Server     defines: {string.Join(";", serverDefines)} | stripping: {serverStrip}\n" +
                $"  Standalone defines: {string.Join(";", standaloneDefines)} | stripping: {standaloneStrip}");
        }

        // ---------- Checkmark validation ----------

        [MenuItem(MenuDGS, validate = true)]
        public static bool ValidateDGS() { Menu.SetChecked(MenuDGS, IsActive(DefineDGS)); return true; }

        [MenuItem(MenuHS, validate = true)]
        public static bool ValidateHS() { Menu.SetChecked(MenuHS, IsActive(DefineHS)); return true; }

        [MenuItem(MenuBareBone, validate = true)]
        public static bool ValidateBareBone() { Menu.SetChecked(MenuBareBone, IsActive(DefineBareBone)); return true; }

        // ---------- Implementation ----------

        private static void Apply(string label, string positiveDefine, ManagedStrippingLevel strip)
        {
            ApplyTo(NamedBuildTarget.Standalone, positiveDefine, strip);
            ApplyTo(NamedBuildTarget.Server, positiveDefine, strip);
            AssetDatabase.SaveAssets();
            Debug.Log($"[BuildMode] → {label} | define={positiveDefine} | stripping={strip} (Standalone + Server)");
        }

        private static void ApplyTo(NamedBuildTarget target, string keepDefine, ManagedStrippingLevel strip)
        {
            // Read current, drop any of our mode defines, re-add the one we want. Preserves
            // any other defines the project might have (UNITY_ANALYTICS, third-party flags, …).
            var current = new HashSet<string>(GetDefines(target));
            foreach (var m in ModeDefines) current.Remove(m);
            current.Add(keepDefine);

            var arr = new string[current.Count];
            current.CopyTo(arr);
            PlayerSettings.SetScriptingDefineSymbols(target, arr);
            PlayerSettings.SetManagedStrippingLevel(target, strip);
        }

        private static string[] GetDefines(NamedBuildTarget target)
        {
            PlayerSettings.GetScriptingDefineSymbols(target, out string[] defs);
            return defs ?? System.Array.Empty<string>();
        }

        private static bool IsActive(string define)
        {
            foreach (var d in GetDefines(NamedBuildTarget.Server))
                if (d == define) return true;
            return false;
        }

        private static string DetectActiveMode(string[] defines)
        {
            bool dgs = false, hs = false, bb = false;
            foreach (var d in defines)
            {
                if (d == DefineDGS) dgs = true;
                else if (d == DefineHS) hs = true;
                else if (d == DefineBareBone) bb = true;
            }
            if (dgs && !hs && !bb) return "DGS";
            if (!dgs && hs && !bb) return "HS";
            if (!dgs && !hs && bb) return "BareBone";
            if (!dgs && !hs && !bb) return "(none — pick a mode)";
            return "AMBIGUOUS (multiple mode defines set — pick one to clean up)";
        }
    }
}
