using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using ZE2.ModLoader;

namespace ZE2.ProgressionRevivalMod
{
    public sealed class ProgressionRevivalMod : IZe2Mod
    {
        public string Name => "ZE2 Progression Revival";
        public string Version => "1.0.0";

        private const int BaseXpPerLevel = 100;
        private const int MaxLevelsPerKill = 20;

        private static readonly Harmony HarmonyInstance = new Harmony("ze2.mod.progressionrevival");
        private static ManualLogSource log;
        private static bool installed;

        private static ConditionalWeakTable<object, ProgressionState> progressionByStats = new ConditionalWeakTable<object, ProgressionState>();
        private static ConditionalWeakTable<object, TalentMenuState> talentMenusByStore = new ConditionalWeakTable<object, TalentMenuState>();

        private static readonly Dictionary<string, object> buttonValueCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private static Type playerStatsType;
        private static Type playerType;
        private static Type gameManagerType;
        private static Type xboxStoreType;
        private static Type buttonPressType;
        private static Type vector2Type;
        private static Type rectangleType;
        private static Type colorType;
        private static Type spriteBatchType;
        private static Type texture2DType;
        private static Type spriteFontType;

        private static FieldInfo playerStatsParentField;
        private static FieldInfo playerStatsField;
        private static FieldInfo storePlayerField;
        private static FieldInfo storeStatsButtonField;
        private static FieldInfo storeTopLeftField;
        private static FieldInfo storeStateField;
        private static FieldInfo globalPlayerListField;
        private static FieldInfo globalPixelField;
        private static FieldInfo globalStoreFontSmallField;
        private static FieldInfo globalFontField;

        private static MethodInfo playerLevelUpMethod;
        private static MethodInfo playerGetIndexMethod;
        private static MethodInfo playerStatsGetTalentPointsMethod;
        private static MethodInfo playerStatsAddTalentPointsMethod;
        private static MethodInfo playerStatsGetTalentsMethod;
        private static MethodInfo inputButtonPressedMethod;
        private static MethodInfo zeButtonSetTextMethod;
        private static MethodInfo talentApplyMethod;
        private static MethodInfo soundPlayMethod;
        private static MethodInfo shadowDrawStringMethod;
        private static MethodInfo spriteBatchDrawRectMethod;
        private static MethodInfo vector2MeasureStringMethod;
        private static MethodInfo playerFireUpdatePropertiesMethod;

        private static ConstructorInfo vector2Ctor;
        private static ConstructorInfo rectangleCtor;
        private static ConstructorInfo colorCtor;

        private static FieldInfo talentNameField;
        private static FieldInfo talentDescriptionField;
        private static FieldInfo talentCurrentLevelField;
        private static FieldInfo talentTotalLevelsField;

        private static FieldInfo vector2XField;
        private static FieldInfo vector2YField;

        private static PropertyInfo localOwnershipProperty;
        private static FieldInfo playerMinionCountField;
        private static FieldInfo playerTalentSpecPropsField;
        private static PropertyInfo specialPropertiesMinionCountProperty;

        private sealed class ProgressionState
        {
            public int Level;
            public int XpIntoLevel;
            public int TotalXp;
        }

        private sealed class TalentMenuState
        {
            public bool Open;
            public int Index;
            public object Player;
            public readonly List<object> Talents = new List<object>();
        }

        public void OnLoad()
        {
            if (installed)
                return;

            installed = true;
            log ??= Logger.CreateLogSource("ZE2 Progression Revival");

            ResolveRefs();
            InstallPatches();
            LogInfo("Loaded progression + talent revival mod.");
        }

        private static void ResolveRefs()
        {
            playerStatsType = AccessTools.TypeByName("ZombieEstate2.PlayerStats");
            playerType = AccessTools.TypeByName("ZombieEstate2.Player");
            gameManagerType = AccessTools.TypeByName("ZombieEstate2.GameManager");
            xboxStoreType = AccessTools.TypeByName("ZombieEstate2.StoreScreen.XboxStore.XboxStore");
            buttonPressType = AccessTools.TypeByName("ZombieEstate2.ButtonPress");
            vector2Type = AccessTools.TypeByName("Microsoft.Xna.Framework.Vector2");
            rectangleType = AccessTools.TypeByName("Microsoft.Xna.Framework.Rectangle");
            colorType = AccessTools.TypeByName("Microsoft.Xna.Framework.Color");
            spriteBatchType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteBatch");
            texture2DType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.Texture2D");
            spriteFontType = AccessTools.TypeByName("Microsoft.Xna.Framework.Graphics.SpriteFont");

            playerStatsParentField = AccessTools.Field(playerStatsType, "parent");
            playerStatsField = AccessTools.Field(playerType, "Stats");
            storePlayerField = AccessTools.Field(xboxStoreType, "mPlayer");
            storeStatsButtonField = AccessTools.Field(xboxStoreType, "mStoreStats");
            storeTopLeftField = AccessTools.Field(xboxStoreType, "mTopLeft");
            storeStateField = AccessTools.Field(xboxStoreType, "mState");

            var globalType = AccessTools.TypeByName("ZombieEstate2.Global");
            globalPlayerListField = AccessTools.Field(globalType, "PlayerList");
            globalPixelField = AccessTools.Field(globalType, "Pixel");
            globalStoreFontSmallField = AccessTools.Field(globalType, "StoreFontSmall");
            globalFontField = AccessTools.Field(globalType, "Font");

            playerLevelUpMethod = AccessTools.Method(playerType, "LevelUp");
            playerGetIndexMethod = AccessTools.Method(playerType, "get_Index");
            playerFireUpdatePropertiesMethod = AccessTools.Method(playerType, "FireUpdateProperties");
            localOwnershipProperty = AccessTools.Property(playerType, "IAmOwnedByLocalPlayer");
            playerMinionCountField = AccessTools.Field(playerType, "MinionCount");
            playerTalentSpecPropsField = AccessTools.Field(playerType, "TalentSpecProps");

            var specialPropertiesType = AccessTools.TypeByName("ZombieEstate2.SpecialProperties");
            specialPropertiesMinionCountProperty = AccessTools.Property(specialPropertiesType, "MinionCount");

            playerStatsGetTalentPointsMethod = AccessTools.Method(playerStatsType, "GetTalentPoints");
            playerStatsAddTalentPointsMethod = AccessTools.Method(playerStatsType, "AddTalentPoints", new[] { typeof(int) });
            playerStatsGetTalentsMethod = AccessTools.Method(playerStatsType, "GetTalents");

            var inputManagerType = AccessTools.TypeByName("ZombieEstate2.InputManager");
            inputButtonPressedMethod = AccessTools.Method(inputManagerType, "ButtonPressed", new[] { buttonPressType, typeof(int), typeof(bool) });

            var talentManagerType = AccessTools.TypeByName("ZombieEstate2.TalentManager");
            var talentType = AccessTools.TypeByName("ZombieEstate2.Talent");
            talentApplyMethod = AccessTools.Method(talentManagerType, "ApplyTalent", new[] { talentType, playerType });

            var bulletCreatorType = AccessTools.TypeByName("ZombieEstate2.BulletCreator");
            PatchMethod(bulletCreatorType, "BulletsFired",
                transpiler: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(BulletsFiredTranspiler), BindingFlags.Static | BindingFlags.NonPublic)));

            var soundEngineType = AccessTools.TypeByName("ZombieEstate2.SoundEngine");
            soundPlayMethod = AccessTools.Method(soundEngineType, "PlaySound", new[] { typeof(string), typeof(float) });

            var shadowType = AccessTools.TypeByName("ZombieEstate2.Shadow");
            shadowDrawStringMethod = AccessTools.Method(
                shadowType,
                "DrawString",
                new[] { typeof(string), spriteFontType, vector2Type, typeof(int), colorType, spriteBatchType });

            spriteBatchDrawRectMethod = AccessTools.Method(spriteBatchType, "Draw", new[] { texture2DType, rectangleType, colorType });

            vector2Ctor = AccessTools.Constructor(vector2Type, new[] { typeof(float), typeof(float) });
            rectangleCtor = AccessTools.Constructor(rectangleType, new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
            colorCtor = AccessTools.Constructor(colorType, new[] { typeof(byte), typeof(byte), typeof(byte), typeof(byte) });
            vector2MeasureStringMethod = AccessTools.Method(spriteFontType, "MeasureString", new[] { typeof(string) });

            talentNameField = AccessTools.Field(talentType, "Name");
            talentDescriptionField = AccessTools.Field(talentType, "Description");
            talentCurrentLevelField = AccessTools.Field(talentType, "CurrentLevel");
            talentTotalLevelsField = AccessTools.Field(talentType, "TotalLevels");

            vector2XField = AccessTools.Field(vector2Type, "X");
            vector2YField = AccessTools.Field(vector2Type, "Y");
        }

        private static void InstallPatches()
        {
            PatchMethod(playerStatsType, "KilledZombie",
                postfix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(KilledZombiePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(playerType, "InitPlayer",
                postfix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(PlayerInitPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(gameManagerType, "DrawHUDStuff",
                postfix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(DrawHudPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(gameManagerType, "GotoMainMenu",
                prefix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(ResetStatePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(gameManagerType, "GotoCharSelect",
                prefix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(CloseTalentMenusPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(xboxStoreType, "SetupButtons",
                postfix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(XboxStoreSetupButtonsPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(xboxStoreType, "<SetupButtons>b__52_1",
                prefix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(XboxStoreTalentButtonPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(xboxStoreType, "Update",
                prefix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(XboxStoreUpdatePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            PatchMethod(xboxStoreType, "Draw",
                postfix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(XboxStoreDrawPostfix), BindingFlags.Static | BindingFlags.NonPublic)));

            if (xboxStoreType != null)
            {
                foreach (var closeMethod in xboxStoreType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => string.Equals(m.Name, "Close", StringComparison.Ordinal)))
                {
                    HarmonyInstance.Patch(
                        closeMethod,
                        prefix: new HarmonyMethod(typeof(ProgressionRevivalMod).GetMethod(nameof(XboxStoreClosePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
            }
        }

        private static void PatchMethod(Type type, string name, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null)
        {
            if (type == null)
            {
                LogWarn($"Missing type for patch '{name}'.");
                return;
            }

            var method = AccessTools.Method(type, name);
            if (method == null)
            {
                LogWarn($"Missing method '{type.FullName}.{name}' for patch.");
                return;
            }

            HarmonyInstance.Patch(method, prefix, postfix, transpiler);
            LogInfo($"Patched {type.FullName}.{name}.");
        }

        private static void PlayerInitPostfix(object __instance)
        {
            try
            {
                var stats = playerStatsField?.GetValue(__instance);
                if (stats == null)
                    return;

                progressionByStats.Remove(stats);
                progressionByStats.Add(stats, new ProgressionState());
            }
            catch (Exception ex)
            {
                LogWarn($"Player.InitPlayer progression reset failed: {ex.Message}");
            }
        }

        private static void KilledZombiePostfix(object __instance)
        {
            if (__instance == null)
                return;

            try
            {
                var stats = __instance;
                var player = playerStatsParentField?.GetValue(stats);
                if (player == null)
                    return;

                var state = progressionByStats.GetOrCreateValue(stats);
                state.TotalXp += 1;
                state.XpIntoLevel += 1;

                var safety = 0;
                while (safety < MaxLevelsPerKill)
                {
                    var requiredXp = GetXpRequiredForNextLevel(state.Level);
                    if (state.XpIntoLevel < requiredXp)
                        break;

                    state.XpIntoLevel -= requiredXp;
                    state.Level += 1;
                    playerLevelUpMethod?.Invoke(player, null);
                    safety++;
                }
            }
            catch (Exception ex)
            {
                LogWarn($"KilledZombie progression hook failed: {ex.Message}");
            }
        }

        private static int GetXpRequiredForNextLevel(int currentLevel)
        {
            var next = Math.Max(1, currentLevel + 1);
            return BaseXpPerLevel * next;
        }

        private static void ResetStatePrefix()
        {
            progressionByStats = new ConditionalWeakTable<object, ProgressionState>();
            talentMenusByStore = new ConditionalWeakTable<object, TalentMenuState>();
        }

        private static void CloseTalentMenusPrefix()
        {
            talentMenusByStore = new ConditionalWeakTable<object, TalentMenuState>();
        }

        private static void XboxStoreClosePrefix(object __instance)
        {
            if (__instance == null)
                return;

            if (!talentMenusByStore.TryGetValue(__instance, out var menu))
                return;

            menu.Open = false;
        }

        private static void XboxStoreSetupButtonsPostfix(object __instance)
        {
            try
            {
                var button = storeStatsButtonField?.GetValue(__instance);
                if (button == null)
                    return;

                zeButtonSetTextMethod ??= AccessTools.Method(button.GetType(), "SetText", new[] { typeof(string) });
                zeButtonSetTextMethod?.Invoke(button, new object[] { "Talents" });
            }
            catch (Exception ex)
            {
                LogWarn($"Failed renaming shop button to talents: {ex.Message}");
            }
        }

        private static bool XboxStoreTalentButtonPrefix(object __instance)
        {
            try
            {
                var menu = talentMenusByStore.GetOrCreateValue(__instance);
                var player = storePlayerField?.GetValue(__instance);
                if (player == null)
                    return false;

                menu.Player = player;
                menu.Open = true;
                RefreshTalentMenu(menu);
                if (menu.Talents.Count == 0)
                {
                    menu.Open = false;
                    PlaySound("ze2_death", 0.9f);
                    return false;
                }

                menu.Index = Math.Max(0, Math.Min(menu.Index, menu.Talents.Count - 1));
                PlaySound("ze2_menunav", 0.45f);
            }
            catch (Exception ex)
            {
                LogWarn($"Talent menu open failed: {ex.Message}");
            }

            // Suppress stock gun stats dialog; this button is now the talent selector.
            return false;
        }

        private static bool XboxStoreUpdatePrefix(object __instance)
        {
            try
            {
                if (!talentMenusByStore.TryGetValue(__instance, out var menu) || !menu.Open)
                    return true;

                HandleTalentMenuUpdate(menu);
                return false;
            }
            catch (Exception ex)
            {
                LogWarn($"Talent menu update hook failed: {ex.Message}");
                return true;
            }
        }

        private static void HandleTalentMenuUpdate(TalentMenuState menu)
        {
            var player = menu.Player;
            if (player == null)
            {
                menu.Open = false;
                return;
            }

            RefreshTalentMenu(menu);
            if (menu.Talents.Count == 0)
            {
                menu.Open = false;
                return;
            }

            var index = GetPlayerIndex(player);
            if (Pressed("Negative", index) || Pressed("OpenStore", index) || Pressed("Inventory", index))
            {
                menu.Open = false;
                PlaySound("ze2_menuselect", 0.25f);
                return;
            }

            if (Pressed("MoveNorth", index) || Pressed("XboxMoveNorth", index))
            {
                menu.Index = (menu.Index - 1 + menu.Talents.Count) % menu.Talents.Count;
                PlaySound("ze2_menunav", 0.25f);
            }
            else if (Pressed("MoveSouth", index) || Pressed("XboxMoveSouth", index))
            {
                menu.Index = (menu.Index + 1) % menu.Talents.Count;
                PlaySound("ze2_menunav", 0.25f);
            }

            if (Pressed("Affirmative", index))
            {
                TryPurchaseTalent(menu);
            }
        }

        private static void RefreshTalentMenu(TalentMenuState menu)
        {
            menu.Talents.Clear();

            var player = menu.Player;
            var stats = playerStatsField?.GetValue(player);
            if (stats == null)
                return;

            if (!(playerStatsGetTalentsMethod?.Invoke(stats, null) is IEnumerable talents))
                return;

            foreach (var talent in talents)
            {
                if (talent != null)
                    menu.Talents.Add(talent);
            }

            if (menu.Index >= menu.Talents.Count)
                menu.Index = Math.Max(0, menu.Talents.Count - 1);
        }

        private static void TryPurchaseTalent(TalentMenuState menu)
        {
            var player = menu.Player;
            if (player == null || menu.Talents.Count == 0)
                return;

            var stats = playerStatsField?.GetValue(player);
            if (stats == null)
                return;

            var points = ToInt(playerStatsGetTalentPointsMethod?.Invoke(stats, null));
            if (points <= 0)
            {
                PlaySound("ze2_death", 1f);
                return;
            }

            var talent = menu.Talents[Math.Max(0, Math.Min(menu.Index, menu.Talents.Count - 1))];
            var currentLevel = ToInt(talentCurrentLevelField?.GetValue(talent));
            var totalLevels = ToInt(talentTotalLevelsField?.GetValue(talent));

            if (currentLevel >= totalLevels)
            {
                PlaySound("ze2_death", 1f);
                return;
            }

            playerStatsAddTalentPointsMethod?.Invoke(stats, new object[] { -1 });
            talentApplyMethod?.Invoke(null, new[] { talent, player });
            ApplyPostTalentFixups(talent, player);
            PlaySound("ze2_upgrade", 0.65f);
            RefreshTalentMenu(menu);
        }

        private static IEnumerable<CodeInstruction> BulletsFiredTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            var helper = typeof(ProgressionRevivalMod).GetMethod(nameof(GetMinionLimitForComparison), BindingFlags.Static | BindingFlags.NonPublic);
            var patched = false;

            for (var i = 0; i < codes.Count - 1; i++)
            {
                if (!patched &&
                    IsCountGetter(codes[i]) &&
                    codes[i + 1].opcode == OpCodes.Ldc_I4_2)
                {
                    codes[i + 1] = new CodeInstruction(OpCodes.Ldloc_1);
                    codes.Insert(i + 2, new CodeInstruction(OpCodes.Call, helper));
                    patched = true;
                    i++;
                }
            }

            if (patched)
                LogInfo("Patched minion gun cap to respect Minion Master.");
            else
                LogWarn("Could not patch minion gun cap; Minion Master may still be capped at 2.");

            return codes;
        }

        private static bool IsCountGetter(CodeInstruction instruction)
        {
            return instruction != null &&
                   instruction.opcode == OpCodes.Callvirt &&
                   instruction.operand is MethodInfo method &&
                   string.Equals(method.Name, "get_Count", StringComparison.Ordinal);
        }

        private static int GetMinionLimitForComparison(object player)
        {
            var directCount = ToInt(playerMinionCountField?.GetValue(player));
            var talentProps = playerTalentSpecPropsField?.GetValue(player);
            var talentCount = ToInt(specialPropertiesMinionCountProperty?.GetValue(talentProps, null));
            return Math.Max(2, Math.Max(directCount, talentCount + 1));
        }

        private static void ApplyPostTalentFixups(object talent, object player)
        {
            try
            {
                var name = talentNameField?.GetValue(talent) as string;
                if (!string.Equals(name, "Minion Master", StringComparison.OrdinalIgnoreCase))
                    return;

                var desiredExtraMinions = Math.Max(0, ToInt(talentCurrentLevelField?.GetValue(talent)));
                var desiredDirectCount = 1 + desiredExtraMinions;

                var talentProps = playerTalentSpecPropsField?.GetValue(player);
                if (talentProps != null && specialPropertiesMinionCountProperty != null)
                {
                    var currentTalentCount = ToInt(specialPropertiesMinionCountProperty.GetValue(talentProps, null));
                    if (currentTalentCount < desiredExtraMinions)
                        specialPropertiesMinionCountProperty.SetValue(talentProps, desiredExtraMinions, null);
                }

                var currentDirectCount = ToInt(playerMinionCountField?.GetValue(player));
                if (currentDirectCount < desiredDirectCount)
                    playerMinionCountField?.SetValue(player, desiredDirectCount);
                playerFireUpdatePropertiesMethod?.Invoke(player, null);
                LogInfo("Applied Minion Master runtime cap fix.");
            }
            catch (Exception ex)
            {
                LogWarn($"Minion Master fixup failed: {ex.Message}");
            }
        }

        private static void XboxStoreDrawPostfix(object __instance, object __0)
        {
            try
            {
                if (!talentMenusByStore.TryGetValue(__instance, out var menu) || !menu.Open)
                    return;

                DrawTalentOverlay(__instance, menu, __0);
            }
            catch (Exception ex)
            {
                LogWarn($"Talent menu draw hook failed: {ex.Message}");
            }
        }

        private static void DrawTalentOverlay(object store, TalentMenuState menu, object spriteBatch)
        {
            if (spriteBatch == null)
                return;

            var pixel = globalPixelField?.GetValue(null);
            if (pixel == null)
                return;

            var topLeft = storeTopLeftField?.GetValue(store);
            var topLeftX = ToInt(GetVectorFieldValue(topLeft, vector2XField));
            var topLeftY = ToInt(GetVectorFieldValue(topLeft, vector2YField));

            var panelX = topLeftX + 115;
            var panelY = topLeftY + 34;
            var panelW = 540;
            var panelH = 430;

            DrawRect(spriteBatch, pixel, panelX, panelY, panelW, panelH, MakeColor(8, 10, 16, 230));
            DrawRect(spriteBatch, pixel, panelX + 2, panelY + 2, panelW - 4, panelH - 4, MakeColor(28, 32, 44, 220));

            var stats = playerStatsField?.GetValue(menu.Player);
            var points = ToInt(playerStatsGetTalentPointsMethod?.Invoke(stats, null));
            DrawShadowText(spriteBatch, $"Talent Selector  (Points: {points})", panelX + 16, panelY + 12, MakeColor(245, 245, 245, 255));
            DrawShadowText(spriteBatch, "A: Spend Point   B/Store: Back", panelX + 16, panelY + 36, MakeColor(190, 210, 240, 255));

            if (menu.Talents.Count == 0)
            {
                DrawShadowText(spriteBatch, "No talents available for this character.", panelX + 16, panelY + 72, MakeColor(255, 180, 180, 255));
                return;
            }

            var maxVisible = 11;
            var start = Math.Max(0, Math.Min(menu.Index - (maxVisible / 2), Math.Max(0, menu.Talents.Count - maxVisible)));
            var end = Math.Min(menu.Talents.Count, start + maxVisible);
            var drawY = panelY + 74;

            for (var i = start; i < end; i++)
            {
                var talent = menu.Talents[i];
                var name = (talentNameField?.GetValue(talent) as string) ?? $"Talent {i + 1}";
                var lvl = ToInt(talentCurrentLevelField?.GetValue(talent));
                var max = ToInt(talentTotalLevelsField?.GetValue(talent));
                var selected = i == menu.Index;
                var row = $"{(selected ? ">" : " ")} {name} [{lvl}/{max}]";
                DrawShadowText(spriteBatch, row, panelX + 18, drawY, selected ? MakeColor(255, 230, 120, 255) : MakeColor(230, 230, 235, 255));
                drawY += 27;
            }

            var currentTalent = menu.Talents[Math.Max(0, Math.Min(menu.Index, menu.Talents.Count - 1))];
            var desc = GetTalentDescription(currentTalent);
            if (!string.IsNullOrWhiteSpace(desc))
            {
                DrawShadowText(spriteBatch, "Description:", panelX + 16, panelY + panelH - 120, MakeColor(205, 220, 255, 255));
                DrawWrappedText(spriteBatch, desc, panelX + 16, panelY + panelH - 96, panelW - 32, 3, MakeColor(220, 225, 230, 255));
            }
        }

        private static string GetTalentDescription(object talent)
        {
            if (talent == null)
                return string.Empty;

            if (!(talentDescriptionField?.GetValue(talent) is string[] lines) || lines.Length == 0)
                return string.Empty;

            var level = ToInt(talentCurrentLevelField?.GetValue(talent));
            var safeIndex = Math.Max(0, Math.Min(level, lines.Length - 1));
            var candidate = lines[safeIndex];
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

            return lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;
        }

        private static void DrawWrappedText(object spriteBatch, string text, int x, int y, int maxWidth, int maxLines, object color)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var font = GetHudFont();
            if (font == null || vector2MeasureStringMethod == null)
            {
                DrawShadowText(spriteBatch, text, x, y, color);
                return;
            }

            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return;

            var line = string.Empty;
            var drawY = y;
            var lineCount = 0;

            foreach (var word in words)
            {
                var test = string.IsNullOrEmpty(line) ? word : $"{line} {word}";
                var size = vector2MeasureStringMethod.Invoke(font, new object[] { test });
                var width = (float)(GetVectorFieldValue(size, vector2XField) ?? 0f);

                if (width <= maxWidth || string.IsNullOrEmpty(line))
                {
                    line = test;
                    continue;
                }

                DrawShadowText(spriteBatch, line, x, drawY, color);
                lineCount++;
                if (lineCount >= maxLines)
                    return;

                drawY += 22;
                line = word;
            }

            if (lineCount < maxLines && !string.IsNullOrWhiteSpace(line))
                DrawShadowText(spriteBatch, line, x, drawY, color);
        }

        private static void DrawHudPostfix(object __0)
        {
            try
            {
                var spriteBatch = __0;
                if (spriteBatch == null)
                    return;

                var player = GetFirstLocalPlayer();
                if (player == null)
                    return;

                var stats = playerStatsField?.GetValue(player);
                if (stats == null)
                    return;

                var state = progressionByStats.GetOrCreateValue(stats);
                var needed = GetXpRequiredForNextLevel(state.Level);
                var ratio = needed <= 0 ? 0f : Math.Max(0f, Math.Min(1f, state.XpIntoLevel / (float)needed));
                var points = ToInt(playerStatsGetTalentPointsMethod?.Invoke(stats, null));

                var pixel = globalPixelField?.GetValue(null);
                if (pixel == null)
                    return;

                const int x = 24;
                const int y = 24;
                const int barW = 318;
                const int barH = 18;

                DrawRect(spriteBatch, pixel, x, y, barW, barH, MakeColor(12, 12, 18, 200));
                DrawRect(spriteBatch, pixel, x + 2, y + 2, barW - 4, barH - 4, MakeColor(34, 34, 46, 235));
                DrawRect(spriteBatch, pixel, x + 2, y + 2, Math.Max(0, (int)((barW - 4) * ratio)), barH - 4, MakeColor(78, 210, 125, 245));

                DrawShadowText(
                    spriteBatch,
                    $"LVL {state.Level}   XP {state.XpIntoLevel}/{needed}   TP {points}",
                    x,
                    y + 24,
                    MakeColor(245, 245, 245, 255));
            }
            catch (Exception ex)
            {
                LogWarn($"HUD draw hook failed: {ex.Message}");
            }
        }

        private static object GetFirstLocalPlayer()
        {
            if (!(globalPlayerListField?.GetValue(null) is IEnumerable players))
                return null;

            foreach (var p in players)
            {
                if (p == null)
                    continue;

                if (IsLocalPlayer(p))
                    return p;
            }

            return null;
        }

        private static bool IsLocalPlayer(object player)
        {
            if (player == null)
                return false;

            try
            {
                if (localOwnershipProperty == null)
                    localOwnershipProperty = AccessTools.Property(player.GetType(), "IAmOwnedByLocalPlayer");

                if (localOwnershipProperty == null)
                    return false;

                var value = localOwnershipProperty.GetValue(player, null);
                return value is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static int GetPlayerIndex(object player)
        {
            if (player == null)
                return 0;

            try
            {
                return ToInt(playerGetIndexMethod?.Invoke(player, null));
            }
            catch
            {
                return 0;
            }
        }

        private static bool Pressed(string buttonName, int playerIndex)
        {
            if (buttonPressType == null || inputButtonPressedMethod == null)
                return false;

            if (!buttonValueCache.TryGetValue(buttonName, out var enumValue))
            {
                try
                {
                    enumValue = Enum.Parse(buttonPressType, buttonName, true);
                    buttonValueCache[buttonName] = enumValue;
                }
                catch
                {
                    return false;
                }
            }

            try
            {
                var pressed = inputButtonPressedMethod.Invoke(null, new[] { enumValue, (object)playerIndex, false });
                return pressed is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        private static void DrawRect(object spriteBatch, object pixel, int x, int y, int w, int h, object color)
        {
            if (spriteBatch == null || pixel == null || spriteBatchDrawRectMethod == null || rectangleCtor == null || color == null)
                return;
            if (w <= 0 || h <= 0)
                return;

            var rect = rectangleCtor.Invoke(new object[] { x, y, w, h });
            spriteBatchDrawRectMethod.Invoke(spriteBatch, new[] { pixel, rect, color });
        }

        private static void DrawShadowText(object spriteBatch, string text, int x, int y, object color)
        {
            if (spriteBatch == null || string.IsNullOrWhiteSpace(text) || shadowDrawStringMethod == null || vector2Ctor == null)
                return;

            var font = GetHudFont();
            if (font == null)
                return;

            var pos = vector2Ctor.Invoke(new object[] { (float)x, (float)y });
            shadowDrawStringMethod.Invoke(null, new[] { text, font, pos, (object)1, color ?? MakeColor(255, 255, 255, 255), spriteBatch });
        }

        private static object GetHudFont()
        {
            var font = globalStoreFontSmallField?.GetValue(null);
            if (font != null)
                return font;
            return globalFontField?.GetValue(null);
        }

        private static object MakeColor(byte r, byte g, byte b, byte a)
        {
            if (colorCtor == null)
                return null;
            return colorCtor.Invoke(new object[] { r, g, b, a });
        }

        private static object GetVectorFieldValue(object vec, FieldInfo field)
        {
            if (vec == null || field == null)
                return null;

            try
            {
                return field.GetValue(vec);
            }
            catch
            {
                return null;
            }
        }

        private static int ToInt(object value)
        {
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

        private static void PlaySound(string cue, float volume)
        {
            if (soundPlayMethod == null || string.IsNullOrWhiteSpace(cue))
                return;

            try
            {
                soundPlayMethod.Invoke(null, new object[] { cue, volume });
            }
            catch
            {
                // Ignore transient sound issues.
            }
        }

        private static void LogInfo(string message)
        {
            log?.LogInfo(message);
        }

        private static void LogWarn(string message)
        {
            log?.LogWarning(message);
        }
    }
}
