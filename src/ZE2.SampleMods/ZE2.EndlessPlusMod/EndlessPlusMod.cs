using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ZE2.ModLoader;

namespace ZE2.EndlessPlusMod
{
    public sealed class EndlessPlusMod : IZe2Mod
    {
        public string Name => "ZE2 Unlimited+";
        public string Version => "1.2.3";

        private const int UnlimitedWaveChunk = 75;
        private const float UnlimitedPlusStartingDifficulty = 1.45f;
        private const float SpawnRateMultiplier = 2.15f;
        private const float OnScreenZombieMultiplier = 1.80f;
        private const float ZombieSpeedMultiplier = 1.20f;
        private const float KillsToWinMultiplier = 1.70f;
        private const int MinimumSpawnBonus = 4;
        private const int MinimumOnScreenBonus = 40;
        private const int MinimumKillBonus = 45;
        private const int MaxSpawnPerSecondCap = 72;
        private const int MaxOnScreenCap = 1500;
        private const int MaxKillsToWinCap = 240000;

        private static readonly Harmony HarmonyInstance = new Harmony("ze2.mod.unlimitedplus");
        private static ManualLogSource log;
        private static bool installed;
        private static bool unlimitedPlusSelected;
        private static bool setupHookLogged;
        private static bool speedHookLogged;
        private static readonly ConditionalWeakTable<object, WaveBaselineStats> WaveBaselines = new ConditionalWeakTable<object, WaveBaselineStats>();
        private static readonly Dictionary<string, FieldInfo> FieldLookupCache = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private static readonly object FieldLookupCacheLock = new object();
        private static Type globalType;
        private static FieldInfo globalUnlimitedModeField;
        private static FieldInfo globalWavesCompletedField;
        private static FieldInfo globalDifficultyModeField;
        private static FieldInfo globalDifficultyLevelField;
        private static FieldInfo globalZombieHealthField;

        private sealed class WaveBaselineStats
        {
            public int BaseZps;
            public int BaseMaxOnScreen;
        }

        public void OnLoad()
        {
            if (installed)
                return;

            installed = true;
            log ??= Logger.CreateLogSource("ZE2 Unlimited+");
            InstallPatches();
            LogInfo("Mod loaded.");
        }

        private static void InstallPatches()
        {
            var waveSelectType = AccessTools.TypeByName("ZombieEstate2.XboxWaveSelect");
            var gameManagerType = AccessTools.TypeByName("ZombieEstate2.GameManager");
            var waveGeneratorType = AccessTools.TypeByName("ZombieEstate2.WaveGenerator");
            var zombieType = AccessTools.TypeByName("ZombieEstate2.Zombie");
            ResolveGlobalFields();

            PatchMethod(waveSelectType, "Setup",
                postfix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(XboxWaveSelectSetupPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(waveSelectType, "Thirty",
                prefix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(XboxWaveSelectThirtyPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(waveSelectType, "Casual",
                prefix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(ResetModeSelectionPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(waveSelectType, "Hard",
                prefix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(ResetModeSelectionPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(waveSelectType, "Unlimited",
                prefix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(ResetModeSelectionPrefix), BindingFlags.Static | BindingFlags.NonPublic)));

            PatchMethod(gameManagerType, "GotoMainMenu",
                prefix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(ClearUnlimitedPlusSelectionPrefix), BindingFlags.Static | BindingFlags.NonPublic)));

            PatchMethod(waveGeneratorType, "UpdateNormalZombieStuff",
                postfix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(UpdateNormalZombieStuffPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(waveGeneratorType, "GetKillWave",
                postfix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(GetKillWavePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(zombieType, "InitSpeed",
                prefix: new HarmonyMethod(typeof(EndlessPlusMod).GetMethod(nameof(ZombieInitSpeedPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void PatchMethod(Type type, string name, HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            if (type == null)
            {
                LogWarn($"Patch skipped: missing type for method '{name}'.");
                return;
            }

            var method = AccessTools.Method(type, name);
            if (method == null)
            {
                LogWarn($"Patch skipped: missing method '{type.FullName}.{name}'.");
                return;
            }

            HarmonyInstance.Patch(method, prefix, postfix);
            LogInfo($"Patched {type.FullName}.{name}.");
        }

        private static void ResolveGlobalFields()
        {
            if (globalType != null)
                return;

            globalType = AccessTools.TypeByName("ZombieEstate2.Global");
            if (globalType == null)
                return;

            globalUnlimitedModeField = AccessTools.Field(globalType, "UnlimitedMode");
            globalWavesCompletedField = AccessTools.Field(globalType, "WavesCompleted");
            globalDifficultyModeField = AccessTools.Field(globalType, "DifficultyModeMod");
            globalDifficultyLevelField = AccessTools.Field(globalType, "DIFFICULTY_LEVEL");
            globalZombieHealthField = AccessTools.Field(globalType, "ZombieHealthMod");
        }

        private static void XboxWaveSelectSetupPostfix(object __instance)
        {
            if (__instance == null)
                return;

            try
            {
                var menuType = AccessTools.TypeByName("ZombieEstate2.Menu");
                var menuItemType = AccessTools.TypeByName("ZombieEstate2.MenuItem");
                var selectedDelegateType = AccessTools.TypeByName("ZombieEstate2.MenuItem+SelectedDelegate");
                if (menuType == null || menuItemType == null || selectedDelegateType == null)
                    return;

                var itemsField = AccessTools.Field(menuType, "Items");
                var textField = AccessTools.Field(menuItemType, "Text");
                var addToMenu = AccessTools.Method(menuType, "AddToMenu", new[] { typeof(string), selectedDelegateType, typeof(string) });
                var thirtyMethod = AccessTools.Method(__instance.GetType(), "Thirty");
                if (itemsField == null || textField == null || addToMenu == null || thirtyMethod == null)
                    return;

                var items = itemsField.GetValue(__instance) as IList;
                if (items != null && items.Cast<object>().Any(i =>
                {
                    var text = textField.GetValue(i) as string;
                    return string.Equals(text, "Unlimited+", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(text, "Endless+", StringComparison.OrdinalIgnoreCase);
                }))
                    return;

                var del = Delegate.CreateDelegate(selectedDelegateType, __instance, thirtyMethod, false);
                if (del == null)
                    return;

                addToMenu.Invoke(__instance, new object[]
                {
                    "Unlimited+",
                    del,
                    "Horde mode: faster zombies, higher spawn pressure, and bigger crowds with no max wave."
                });

                if (!setupHookLogged)
                {
                    setupHookLogged = true;
                    LogInfo("Added Unlimited+ to Mode Select menu.");
                }
            }
            catch (Exception ex)
            {
                LogWarn($"Failed adding menu item: {ex.Message}");
            }
        }

        private static bool XboxWaveSelectThirtyPrefix(object __instance)
        {
            unlimitedPlusSelected = true;

            SetGlobalFieldValue(globalUnlimitedModeField, true);
            SetGlobalFieldValue(globalZombieHealthField, 1f);
            SetGlobalFieldValue(globalDifficultyModeField, UnlimitedPlusStartingDifficulty);
            SetGlobalFieldValue(globalDifficultyLevelField, 4);

            var countField = AccessTools.Field(__instance?.GetType(), "count");
            countField?.SetValue(__instance, UnlimitedWaveChunk);

            AccessTools.Method(__instance?.GetType(), "Start")?.Invoke(__instance, null);
            LogInfo("Selected Unlimited+ mode.");
            return false;
        }

        private static bool ResetModeSelectionPrefix()
        {
            unlimitedPlusSelected = false;
            return true;
        }

        private static void ClearUnlimitedPlusSelectionPrefix()
        {
            unlimitedPlusSelected = false;
        }

        private static void UpdateNormalZombieStuffPostfix(object wave)
        {
            if (!IsUnlimitedPlusActive() || wave == null)
                return;

            var zps = GetIntFieldCached(wave, "ZombiesPerSecond");
            var maxOnScreen = GetIntFieldCached(wave, "MaxNumberOfZombiesOnScreen");
            if (zps <= 0 || maxOnScreen <= 0)
                return;

            // Use per-wave baselines so values do not compound every time UpdateNormalZombieStuff runs.
            var baseline = WaveBaselines.GetValue(wave, _ => new WaveBaselineStats
            {
                BaseZps = Math.Max(1, zps),
                BaseMaxOnScreen = Math.Max(1, maxOnScreen)
            });

            var wavesCompleted = Math.Max(0, GetGlobalIntFast(globalWavesCompletedField));
            var ramp = 1f + Math.Min(0.45f, wavesCompleted * 0.03f);

            var boostedZps = Math.Max(
                baseline.BaseZps + MinimumSpawnBonus,
                (int)Math.Ceiling(baseline.BaseZps * SpawnRateMultiplier * ramp));

            var boostedMax = Math.Max(
                baseline.BaseMaxOnScreen + MinimumOnScreenBonus,
                (int)Math.Ceiling(baseline.BaseMaxOnScreen * OnScreenZombieMultiplier * ramp));

            SetIntFieldCached(wave, "ZombiesPerSecond", Math.Min(MaxSpawnPerSecondCap, boostedZps));
            SetIntFieldCached(wave, "MaxNumberOfZombiesOnScreen", Math.Min(MaxOnScreenCap, boostedMax));
        }

        private static void GetKillWavePostfix(object __result)
        {
            if (!IsUnlimitedPlusActive() || __result == null)
                return;

            var kills = GetIntFieldCached(__result, "KillsToWin");
            if (kills <= 0)
                return;

            var boostedKills = Math.Max(kills + MinimumKillBonus, (int)Math.Ceiling(kills * KillsToWinMultiplier));
            SetIntFieldCached(__result, "KillsToWin", Math.Min(MaxKillsToWinCap, boostedKills));
        }

        private static void ZombieInitSpeedPrefix(ref float __0)
        {
            if (!IsUnlimitedPlusActive() || __0 <= 0f)
                return;

            var boosted = __0 * ZombieSpeedMultiplier;
            var safeCap = __0 + 0.45f;
            __0 = Math.Max(0.05f, Math.Min(boosted, safeCap));

            if (!speedHookLogged)
            {
                speedHookLogged = true;
                LogInfo($"Zombie speed multiplier active: x{ZombieSpeedMultiplier:0.00}");
            }
        }

        private static bool IsUnlimitedPlusActive()
            => unlimitedPlusSelected && GetGlobalBoolFast(globalUnlimitedModeField);

        private static bool GetGlobalBoolFast(FieldInfo field)
        {
            var value = GetGlobalFieldValue(field);
            return value is bool b && b;
        }

        private static int GetGlobalIntFast(FieldInfo field)
        {
            var value = GetGlobalFieldValue(field);
            if (value == null)
                return 0;

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static object GetGlobalFieldValue(FieldInfo field)
        {
            if (field == null)
                return null;
            return field?.GetValue(null);
        }

        private static void SetGlobalFieldValue(FieldInfo field, object value)
        {
            if (field == null)
                return;
            field?.SetValue(null, value);
        }

        private static int GetIntFieldCached(object instance, string fieldName)
        {
            var field = FindFieldCached(instance, fieldName);
            if (field == null)
                return 0;

            try
            {
                return Convert.ToInt32(field.GetValue(instance));
            }
            catch
            {
                return 0;
            }
        }

        private static void SetIntFieldCached(object instance, string fieldName, int value)
        {
            var field = FindFieldCached(instance, fieldName);
            field?.SetValue(instance, value);
        }

        private static FieldInfo FindFieldCached(object instance, string fieldName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(fieldName))
                return null;

            var type = instance.GetType();
            var cacheKey = type.FullName + "|" + fieldName;

            lock (FieldLookupCacheLock)
            {
                if (FieldLookupCache.TryGetValue(cacheKey, out var cached))
                    return cached;
            }

            var cursorType = type;
            while (cursorType != null)
            {
                var field = cursorType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    lock (FieldLookupCacheLock)
                    {
                        if (!FieldLookupCache.ContainsKey(cacheKey))
                            FieldLookupCache[cacheKey] = field;
                    }
                    return field;
                }
                cursorType = cursorType.BaseType;
            }

            return null;
        }

        private static void LogInfo(string message)
        {
            log?.LogInfo(message);
            Console.WriteLine("[ZE2 Unlimited+] " + message);
        }

        private static void LogWarn(string message)
        {
            log?.LogWarning(message);
            Console.WriteLine("[ZE2 Unlimited+] " + message);
        }
    }
}
