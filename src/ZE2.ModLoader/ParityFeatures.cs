using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ZombieEstate2;

namespace ZE2.ModLoader
{
    internal static class ParityFeatures
    {
        private static ManualLogSource LogSource;
        private static Harmony ParityHarmony;
        private static Type BootstrapType;

        public static void Install(ManualLogSource logSource)
        {
            LogSource = logSource;
            ResolveBootstrap();

            ParityHarmony = new Harmony("ze2.modloader.parity");
            Patch(typeof(Level), ".ctor", new Type[] { typeof(string) }, postfix: "LevelCtorPostfix");
            Patch(typeof(Level), "ThreadLoad", Type.EmptyTypes, prefix: "LevelThreadLoadPrefix");
            Patch(typeof(Player), "InitPlayer", null, postfix: "PlayerInitPostfix");
            Patch(AccessTools.TypeByName("ZombieEstate2.MainMenu"), "Setup", Type.EmptyTypes, postfix: "MainMenuSetupPostfix");
            Patch(AccessTools.TypeByName("ZombieEstate2.TalentManager"), "ApplyTalent", null, prefix: "TalentManagerApplyTalentPrefix");
            Patch(typeof(Zombie), "Kill", null, prefix: "ZombieKillPrefix", postfix: "ZombieKillPostfix");

            Type xboxStore = AccessTools.TypeByName("ZombieEstate2.StoreScreen.XboxStore.XboxStore");
            Patch(xboxStore, "SetupButtons", Type.EmptyTypes, postfix: "XboxStoreSetupButtonsPostfix");
            Patch(xboxStore, "Update", Type.EmptyTypes, prefix: "XboxStoreUpdatePrefix");
            Patch(xboxStore, "Draw", null, prefix: "XboxStoreDrawPrefix");
            Patch(xboxStore, "PurchasePressed", null, prefix: "XboxStorePurchasePressedPrefix");
            Patch(xboxStore, "ItemHighlighted", null, prefix: "XboxStoreItemHighlightedPrefix");
            Patch(xboxStore, "Close", new Type[] { typeof(object), typeof(EventArgs) }, prefix: "XboxStoreClosePrefix");

            LogSource.LogInfo("ZE2 ModLoader parity features installed.");
        }

        private static void Patch(Type type, string methodName, Type[] args, string prefix = null, string postfix = null)
        {
            if (type == null)
            {
                LogSource.LogWarning("Patch skipped because target type was unavailable for " + methodName + ".");
                return;
            }

            MethodBase target;
            if (methodName == ".ctor")
            {
                target = AccessTools.Constructor(type, args ?? Type.EmptyTypes);
            }
            else
            {
                target = args == null
                    ? AccessTools.Method(type, methodName)
                    : AccessTools.Method(type, methodName, args);
            }

            if (target == null)
            {
                LogSource.LogWarning("Patch skipped because target method was unavailable: " + type.FullName + "." + methodName);
                return;
            }

            HarmonyMethod pre = prefix == null ? null : new HarmonyMethod(typeof(ParityFeatures).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic));
            HarmonyMethod post = postfix == null ? null : new HarmonyMethod(typeof(ParityFeatures).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic));
            ParityHarmony.Patch(target, pre, post);
            LogSource.LogInfo("Patched " + type.FullName + "." + methodName + ".");
        }

        private static void ResolveBootstrap()
        {
            BootstrapType = typeof(ZE2ModLoader.ModBootstrap);

            if (BootstrapType == null)
            {
                LogSource.LogWarning("ZE2ModLoader.ModBootstrap was not found. Parity features will be inert.");
            }
        }

        private static object InvokeBootstrap(string methodName, params object[] args)
        {
            if (BootstrapType == null)
            {
                ResolveBootstrap();
            }

            MethodInfo method = BootstrapType == null ? null : AccessTools.Method(BootstrapType, methodName);
            if (method == null)
            {
                LogSource.LogWarning("Missing ZE2ModLoader.ModBootstrap." + methodName + ".");
                return null;
            }

            return method.Invoke(null, args);
        }

        private static bool InvokeBootstrapBool(string methodName, params object[] args)
        {
            object value = InvokeBootstrap(methodName, args);
            return value is bool && (bool)value;
        }

        private static string InvokeBootstrapString(string methodName, params object[] args)
        {
            object value = InvokeBootstrap(methodName, args);
            return value as string;
        }

        private static int InvokeBootstrapInt(string methodName, int fallback, params object[] args)
        {
            object value = InvokeBootstrap(methodName, args);
            return value is int ? (int)value : fallback;
        }

        private static string GetLevelName(Level level)
        {
            FieldInfo field = AccessTools.Field(typeof(Level), "FileName");
            return field == null ? null : field.GetValue(level) as string;
        }

        private static void LevelCtorPostfix(Level __instance)
        {
            string levelName = GetLevelName(__instance);
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                InvokeBootstrap("ApplyLevelAssets", levelName);
            }
        }

        private static bool LevelThreadLoadPrefix(Level __instance)
        {
            string levelName = GetLevelName(__instance);
            if (string.IsNullOrWhiteSpace(levelName) || !InvokeBootstrapBool("HasCustomMap", levelName))
            {
                return true;
            }

            try
            {
                RunCustomLevelThreadLoad(__instance, levelName);
                return false;
            }
            catch (Exception ex)
            {
                LogSource.LogError("Custom level ThreadLoad failed for " + levelName + ": " + ex);
                return true;
            }
        }

        private static void RunCustomLevelThreadLoad(Level level, string levelName)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Stopwatch stopwatch = Stopwatch.StartNew();
            Terminal.WriteMessage("------------Initializing custom mod level...");

            FieldInfo sectorsField = AccessTools.Field(typeof(Level), "Sectors");
            FieldInfo gameField = AccessTools.Field(typeof(Level), "game");
            FieldInfo mainSectorField = AccessTools.Field(typeof(Level), "mainSectorIndex");
            Game game = gameField.GetValue(level) as Game;
            int mainSectorIndex = mainSectorField == null ? 0 : (int)mainSectorField.GetValue(level);
            int sectorCount = Math.Max(1, Math.Min(3, InvokeBootstrapInt("GetSectorCount", 2, levelName)));

            List<Sector> sectors = new List<Sector>();
            for (int i = 0; i < sectorCount; i++)
            {
                sectors.Add(new Sector(game, i, levelName));
            }

            sectorsField.SetValue(level, sectors);
            Global.Level = sectors[Math.Max(0, Math.Min(mainSectorIndex, sectors.Count - 1))];
            Level.TilesDoneLoading = 0;
            Terminal.WriteMessage("------------Initialized custom mod level in " + stopwatch.Elapsed.TotalSeconds);

            stopwatch.Reset();
            stopwatch.Start();
            Terminal.WriteMessage("------------Loading custom mod level...");
            level.doneLoading = false;

            for (int i = 0; i < sectors.Count; i++)
            {
                if (i == mainSectorIndex || GameManager.PLANONEDITING)
                {
                    string sectorPath = InvokeBootstrapString("ResolveLevelPath", "Data//Levels//" + levelName + i + ".xml")
                        ?? "Data//Levels//" + levelName + i + ".xml";
                    LoadSector(sectors[i], sectorPath);
                }

                string basePath = InvokeBootstrapString("ResolveLevelPath", "Data//Levels//" + levelName + i)
                    ?? "Data//Levels//" + levelName + i;
                sectors[i].LoadSectorVertices(basePath, !GameManager.PLANONEDITING);

                if (i == mainSectorIndex)
                {
                    sectors[i].BuildAdjacentLists();
                }

                if (!GameManager.PLANONEDITING && i != mainSectorIndex)
                {
                    sectors[i].CLEARTILES();
                }
            }

            if (!GameManager.PLANONEDITING)
            {
                for (int i = 0; i < sectors.Count; i++)
                {
                    if (i != mainSectorIndex)
                    {
                        sectors[i].CLEARTILES();
                    }
                }
            }

            Level.ShopKeepLocation = new Vector3(15.5f, 0f, 10f);
            for (int x = 0; x < 32; x++)
            {
                for (int y = 0; y < 32; y++)
                {
                    Tile tile = Global.Level.GetTile(x, y);
                    IList properties = tile.TileProperties;
                    if (properties.Contains(TilePropertyType.ShopKeep))
                    {
                        Level.ShopKeepLocation = new Vector3((float)x + 0.5f, 0f, (float)y + 0.8f);
                    }
                    if (properties.Contains(TilePropertyType.PlayerOneSpawn))
                    {
                        level.PlayerSpawns[0] = new Vector3((float)x + 0.5f, 0f, (float)y + 0.5f);
                    }
                    if (properties.Contains(TilePropertyType.PlayerTwoSpawn))
                    {
                        level.PlayerSpawns[1] = new Vector3((float)x + 0.5f, 0f, (float)y + 0.5f);
                    }
                    if (properties.Contains(TilePropertyType.PlayerThreeSpawn))
                    {
                        level.PlayerSpawns[2] = new Vector3((float)x + 0.5f, 0f, (float)y + 0.5f);
                    }
                    if (properties.Contains(TilePropertyType.PlayerFourSpawn))
                    {
                        level.PlayerSpawns[3] = new Vector3((float)x + 0.5f, 0f, (float)y + 0.5f);
                    }
                    if (properties.Contains(TilePropertyType.DemonFire))
                    {
                        level.FireTiles.Add(tile);
                    }
                    if (properties.Contains(TilePropertyType.BossArea))
                    {
                        level.BossArea.Add(tile);
                    }
                }
            }

            Global.WaveMaster = new WaveMaster(Global.Level, Global.WAVE_GEN_SEED);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(4000);
            level.doneLoading = true;
            Terminal.WriteMessage("------------Loaded custom mod level in " + stopwatch.Elapsed.TotalSeconds);
            Terminal.WriteMessage("*****Loading of custom mod level complete!*****");
        }

        private static void PlayerInitPostfix(Player __instance)
        {
            InvokeBootstrap("EnsurePlayerTalents", __instance);
        }

        private static void MainMenuSetupPostfix(object __instance)
        {
            MethodInfo addToMenu = AccessTools.Method(typeof(Menu), "AddToMenu", new Type[] { typeof(string), typeof(MenuItem.SelectedDelegate), typeof(string) });
            if (addToMenu == null)
            {
                return;
            }

            MenuItem.SelectedDelegate open = delegate
            {
                ScreenFader.Fade(delegate
                {
                    MenuManager.PushMenu(new ModManagerMenu());
                });
            };
            addToMenu.Invoke(__instance, new object[]
            {
                "Mods",
                open,
                "View loaded mods, enable or disable XML mods, adjust load order, and edit mod-declared options. Restart the game after changing enabled mods or load order."
            });
        }

        private static void LoadSector(Sector sector, string path)
        {
            Type sectorSaver = AccessTools.TypeByName("ZombieEstate2.SectorSaver");
            MethodInfo loadSector = sectorSaver == null ? null : AccessTools.Method(sectorSaver, "LoadSector");
            if (loadSector == null)
            {
                throw new MissingMethodException("ZombieEstate2.SectorSaver", "LoadSector");
            }

            loadSector.Invoke(null, new object[] { sector, path });
        }

        private static bool TalentManagerApplyTalentPrefix(object talent, Player parent)
        {
            InvokeBootstrap("ApplyTalent", talent, parent);
            return false;
        }

        private static void ZombieKillPrefix(Zombie __instance, out bool __state)
        {
            FieldInfo killedField = AccessTools.Field(typeof(Zombie), "mKilled");
            __state = killedField != null && (bool)killedField.GetValue(__instance);
        }

        private static void ZombieKillPostfix(Zombie __instance, Shootable attacker, bool fromNet, bool __state)
        {
            if (!__state)
            {
                InvokeBootstrap("AwardTalentExperience", attacker, __instance, fromNet);
            }
        }

        private static void XboxStoreSetupButtonsPostfix(object __instance)
        {
            object button = AccessTools.Field(__instance.GetType(), "mStoreStats").GetValue(__instance);
            if (button == null)
            {
                return;
            }

            MethodInfo setText = AccessTools.Method(button.GetType(), "SetText", new Type[] { typeof(string) });
            if (setText != null)
            {
                setText.Invoke(button, new object[] { "Talents" });
            }

            FieldInfo pressedField = AccessTools.Field(button.GetType(), "OnPressed");
            if (pressedField != null)
            {
                EventHandler handler = delegate { InvokeBootstrap("OpenXboxTalentStore", __instance); };
                pressedField.SetValue(button, handler);
            }
        }

        private static bool XboxStoreUpdatePrefix(object __instance)
        {
            return !InvokeBootstrapBool("UpdateXboxTalentStore", __instance);
        }

        private static bool XboxStoreDrawPrefix(object __instance, object spriteBatch)
        {
            return !InvokeBootstrapBool("DrawXboxTalentStore", __instance, spriteBatch);
        }

        private static bool XboxStorePurchasePressedPrefix(object __instance)
        {
            return !InvokeBootstrapBool("TryPurchaseXboxTalent", __instance);
        }

        private static bool XboxStoreItemHighlightedPrefix(object __instance, object item)
        {
            return !InvokeBootstrapBool("TryHandleXboxTalentHighlight", __instance, item);
        }

        private static bool XboxStoreClosePrefix(object __instance)
        {
            return !InvokeBootstrapBool("TryCloseXboxTalentStore", __instance);
        }
    }
}
