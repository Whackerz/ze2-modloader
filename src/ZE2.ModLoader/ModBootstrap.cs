using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Serialization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using ZombieEstate2;

namespace ZE2ModLoader
{
    public static class ModBootstrap
    {
        private const int CellSize = 16;
        private const int LogicalAtlasCells = 64;
        private static readonly Regex SafePrefix = new Regex("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
        private static readonly HashSet<string> VanillaLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Estate",
            "School",
            "Mall",
            "Skyscraper",
            "DesertTown",
            "Office",
            "Farm"
        };

        private static readonly object Gate = new object();
        private static readonly List<CharacterEntry> CharacterEntries = new List<CharacterEntry>();
        private static readonly List<GunEntry> GunEntries = new List<GunEntry>();
        private static readonly List<BulletEntry> BulletEntries = new List<BulletEntry>();
        private static readonly Dictionary<string, int> MapSectorCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> MapFolderPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MapAssetEntry> MapAssets = new Dictionary<string, MapAssetEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Texture2D> RuntimeTextureCache = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<ContentManager> RuntimeContentManagers = new List<ContentManager>();
        private static readonly Dictionary<Player, int> PlayerExperience = new Dictionary<Player, int>();

        private const int ExperiencePerZombie = 10;
        private const int ExperiencePerTalentPoint = 100;

        private static string GameRoot = AppDomain.CurrentDomain.BaseDirectory;
        private static string ModsRoot;
        private static readonly List<string> ModRoots = new List<string>();
        private static string LogPath;
        private static bool Initialized;
        private static bool Discovered;
        private static GraphicsDevice RuntimeGraphicsDevice;
        private static Texture2D VanillaMasterEnvTex;
        private static Texture2D VanillaCurrentLevelLightTex;
        private static Texture2D VanillaMasterLightTex;
        private static Texture2D BlankCustomShadowTexture;
        private static bool VanillaLevelTexturesCaptured;

        public static void Initialize(object graphicsDevice)
        {
            lock (Gate)
            {
                if (Initialized)
                {
                    return;
                }

                SetupPaths();
                RuntimeGraphicsDevice = graphicsDevice as GraphicsDevice;
                Log("Initializing ZE2ModLoader.");
                DiscoverMods();
                MergeModSprites(RuntimeGraphicsDevice);
                Initialized = true;
                Log("ZE2ModLoader initialization complete.");
            }
        }

        public static void LoadCustomCharacters(object characters)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                List<CharacterSettings> settings = characters as List<CharacterSettings>;
                if (settings == null)
                {
                    Log("Character load skipped: unexpected character collection object.");
                    return;
                }

                HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CharacterSettings existing in settings)
                {
                    if (!string.IsNullOrEmpty(existing.name))
                    {
                        existingNames.Add(existing.name);
                    }
                }

                int loaded = 0;
                foreach (CharacterEntry entry in CharacterEntries)
                {
                    try
                    {
                        CharacterSettings custom = LoadCharacter(entry.CharacterPath);
                        if (string.IsNullOrWhiteSpace(custom.name))
                        {
                            Log("Skipped character with blank name: " + entry.CharacterPath);
                            continue;
                        }

                        if (existingNames.Contains(custom.name))
                        {
                            Log("Skipped duplicate character name: " + custom.name);
                            continue;
                        }

                        if (entry.AssignedTexCoord.HasValue)
                        {
                            custom.texCoord = entry.AssignedTexCoord.Value;
                        }

                        if (custom.Properties != null)
                        {
                            custom.Properties.HealRecievedMod = 0f;
                        }

                        custom.PointsToUnlock = 0;
                        settings.Add(custom);
                        existingNames.Add(custom.name);
                        loaded++;
                        Log(string.Format("Loaded character {0} from {1}.", custom.name, entry.ModId));
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to load character " + entry.CharacterPath + ": " + ex.Message);
                    }
                }

                Log("Custom character load complete. Loaded " + loaded + " character(s).");
            }
        }

        public static void LoadCustomGuns(object guns, object uidGuns, object uidGunsRev)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                List<GunStats> stats = guns as List<GunStats>;
                Dictionary<short, string> uidForward = uidGuns as Dictionary<short, string>;
                Dictionary<string, short> uidReverse = uidGunsRev as Dictionary<string, short>;
                if (stats == null)
                {
                    Log("Gun load skipped: unexpected gun collection object.");
                    return;
                }

                HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (GunStats existing in stats)
                {
                    if (existing != null && !string.IsNullOrEmpty(existing.GunName))
                    {
                        existingNames.Add(existing.GunName);
                    }
                }

                short nextUid = GetNextGunUid(uidForward);
                int loaded = 0;
                foreach (GunEntry entry in GunEntries)
                {
                    try
                    {
                        GunStats custom = LoadGun(entry.GunPath);
                        if (!ValidateGun(custom, entry.GunPath))
                        {
                            continue;
                        }

                        if (existingNames.Contains(custom.GunName))
                        {
                            Log("Skipped duplicate gun name: " + custom.GunName);
                            continue;
                        }

                        if (entry.AssignedTexCoord.HasValue)
                        {
                            custom.GunXCoord = entry.AssignedTexCoord.Value.X;
                            custom.GunYCoord = entry.AssignedTexCoord.Value.Y;
                        }

                        stats.Add(custom);
                        existingNames.Add(custom.GunName);
                        AddGunUid(custom.GunName, uidForward, uidReverse, ref nextUid);
                        loaded++;
                        Log(string.Format("Loaded gun {0} from {1}.", custom.GunName, entry.ModId));
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to load gun " + entry.GunPath + ": " + ex.Message);
                    }
                }

                if (loaded > 0)
                {
                    stats.Sort();
                }

                Log("Custom gun load complete. Loaded " + loaded + " gun(s).");
            }
        }

        public static void LoadCustomBullets()
        {
            try
            {
                Type bulletCreator = typeof(BulletCreator);
                object bullets = GetStaticField(bulletCreator, "bulletStats");
                object uidBullets = GetStaticField(bulletCreator, "UID_Bullets");
                object uidBulletsRev = GetStaticField(bulletCreator, "UID_BulletsRev");
                LoadCustomBullets(bullets, uidBullets, uidBulletsRev);
            }
            catch (Exception ex)
            {
                Log("Custom bullet load failed before dictionary access: " + ex.Message);
            }
        }

        public static void LoadCustomBullets(object bullets, object uidBullets, object uidBulletsRev)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                Dictionary<string, BulletStats> stats = bullets as Dictionary<string, BulletStats>;
                Dictionary<int, string> uidForward = uidBullets as Dictionary<int, string>;
                Dictionary<string, int> uidReverse = uidBulletsRev as Dictionary<string, int>;
                if (stats == null)
                {
                    Log("Bullet load skipped: unexpected bullet collection object.");
                    return;
                }

                HashSet<string> existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string existing in stats.Keys)
                {
                    if (!string.IsNullOrEmpty(existing))
                    {
                        existingNames.Add(existing);
                    }
                }

                int nextUid = GetNextBulletUid(uidForward);
                int loaded = 0;
                foreach (BulletEntry entry in BulletEntries)
                {
                    try
                    {
                        BulletStats custom = LoadBullet(entry.BulletPath);
                        if (!ValidateBullet(custom, entry.BulletPath))
                        {
                            continue;
                        }

                        string bulletName = string.IsNullOrWhiteSpace(custom.Name)
                            ? Path.GetFileNameWithoutExtension(entry.BulletPath)
                            : custom.Name.Trim();
                        if (existingNames.Contains(bulletName))
                        {
                            Log("Skipped duplicate bullet name: " + bulletName);
                            continue;
                        }

                        custom.Name = bulletName;
                        ApplyBulletSprite(entry, custom);
                        stats[bulletName] = custom;
                        existingNames.Add(bulletName);
                        AddBulletUid(bulletName, uidForward, uidReverse, ref nextUid);
                        loaded++;
                        Log(string.Format("Loaded bullet {0} from {1}.", bulletName, entry.ModId));
                    }
                    catch (Exception ex)
                    {
                        Log("Failed to load bullet " + entry.BulletPath + ": " + ex.Message);
                    }
                }

                Log("Custom bullet load complete. Loaded " + loaded + " bullet(s).");
            }
        }

        public static void AwardTalentExperience(object attackerObject, object zombieObject, bool fromNet)
        {
            if (fromNet)
            {
                return;
            }

            Zombie zombie = zombieObject as Zombie;
            if (zombie == null || zombie.DontCountAsKill)
            {
                return;
            }

            Shootable attacker = attackerObject as Shootable;
            if (attacker == null)
            {
                return;
            }

            Minion minion = attacker as Minion;
            Player player = minion != null ? minion.parent as Player : attacker as Player;
            if (player == null || player.Stats == null || !player.IAmOwnedByLocalPlayer)
            {
                return;
            }

            try
            {
                player.Stats.KilledZombie(zombie);
            }
            catch
            {
            }

            lock (Gate)
            {
                int experience;
                PlayerExperience.TryGetValue(player, out experience);
                experience += ExperiencePerZombie;
                while (experience >= ExperiencePerTalentPoint)
                {
                    experience -= ExperiencePerTalentPoint;
                    try
                    {
                        player.LevelUp();
                    }
                    catch
                    {
                        player.Stats.AddTalentPoints(1);
                    }

                    Log("Awarded 1 Talent Point to player " + player.Index + " from zombie XP.");
                }

                PlayerExperience[player] = experience;
            }
        }

        public static void EnsurePlayerTalents(object playerObject)
        {
            Player player = playerObject as Player;
            if (player == null || player.Stats == null)
            {
                return;
            }

            CharacterStats stats = player.Stats.MyStats;
            if (stats.Talents == null || stats.Talents.Count == 0)
            {
                TalentManager.AddGenericTalents();
                stats.Talents = CloneTalents(TalentManager.GetTalents(player.Stats.CharSettings));
                player.Stats.MyStats = stats;
                Log("Initialized talent list for player " + player.Index + ".");
            }
        }

        public static void ApplyTalent(object talentObject, object playerObject)
        {
            Talent talent = talentObject as Talent;
            Player player = playerObject as Player;
            if (talent == null || player == null || player.Stats == null)
            {
                return;
            }

            int maxLevel = GetTalentMaxLevel(talent);
            if (talent.CurrentLevel >= maxLevel)
            {
                Log("Talent apply skipped because it is already maxed: " + talent.Name);
                return;
            }

            float oldMaxHealth = player.SpecialProperties.MaxHealth;
            float oldHealth = player.Health;
            talent.CurrentLevel++;
            int levelIndex = Math.Max(0, talent.CurrentLevel - 1);

            if (!string.IsNullOrEmpty(talent.AbilityClassName))
            {
                Type abilityType = Type.GetType(talent.AbilityClassName);
                if (abilityType != null)
                {
                    player.Ability = (Ability)Activator.CreateInstance(abilityType, new object[] { player, levelIndex });
                }
                return;
            }

            string attribute = talent.Attribute ?? string.Empty;
            float modifier = GetTalentModifier(talent, levelIndex);

            if (attribute == "Health")
            {
                player.TalentSpecProps.MaxHealth += modifier;
            }
            else if (attribute == "Speed")
            {
                float baseSpeed = Math.Max(1f, player.SpecialProperties.Speed - player.TalentSpecProps.Speed);
                player.TalentSpecProps.Speed += baseSpeed * Math.Max(0f, modifier - 1f);
            }
            else if (attribute == "Reload Speed")
            {
                player.ReloadSpeedMod = modifier;
                player.TalentSpecProps.ReloadTimeMod += Math.Max(0f, 1f - modifier) * 100f;
            }
            else if (attribute == "Money Boost")
            {
                player.ZombieKillMoneyMod = modifier;
                player.TalentSpecProps.MoneyBonus += Math.Max(0f, modifier - 1f) * 100f;
            }
            else if (attribute == "Assault Storage")
            {
                player.Stats.AddMaxAmmo(AmmoType.ASSAULT, (int)modifier);
            }
            else if (attribute == "Heavy Storage")
            {
                player.Stats.AddMaxAmmo(AmmoType.HEAVY, (int)modifier);
            }
            else if (attribute == "Explosive Storage")
            {
                player.Stats.AddMaxAmmo(AmmoType.EXPLOSIVE, (int)modifier);
            }
            else if (attribute == "Shells Storage")
            {
                player.Stats.AddMaxAmmo(AmmoType.SHELLS, (int)modifier);
            }
            else if (attribute == "MinionCount")
            {
                player.MinionCount++;
                player.TalentSpecProps.MinionCount++;
            }

            player.FireUpdateProperties();
            if (player.SpecialProperties.MaxHealth > oldMaxHealth)
            {
                player.Health = Math.Min(player.SpecialProperties.MaxHealth, oldHealth + player.SpecialProperties.MaxHealth - oldMaxHealth);
            }

            Log(string.Format("Applied talent {0} level {1} to player {2}.", talent.Name, talent.CurrentLevel, player.Index));
        }

        public static void OpenXboxTalentStore(object store)
        {
            if (store == null)
            {
                return;
            }

            Player player = GetInstanceField(store, "mPlayer") as Player;
            object screen = GetInstanceField(store, "mScreen");
            EnsurePlayerTalents(player);
            SetXboxStoreState(store, "Stats_Store");
            SetInstanceField(store, "mSelectedItem", null);
            SetInstanceField(store, "mHighlightedItem", null);
            SetInstanceField(store, "mFirstFrame", false);
            PopulateTalentXboxScreen(screen, player);
            SetButtonText(GetInstanceField(store, "mPurchaseButton"), "Buy Talent");
            SetButtonText(GetInstanceField(store, "mBackButton"), "Store");
            InvokeInstanceMethod(screen, "RefireEvent", new object[] { true });
            PlayMenuSound();
            Log("Opened Xbox talent store.");
        }

        public static bool UpdateXboxTalentStore(object store)
        {
            if (!IsXboxTalentStore(store) || IsAnyXboxStoreDialogActive(store))
            {
                return false;
            }

            InvokeInstanceMethod(GetInstanceField(store, "mScreen"), "Update", null);
            InvokeInstanceMethod(GetInstanceField(store, "mPurchaseButton"), "Update", null);
            InvokeInstanceMethod(GetInstanceField(store, "mBackButton"), "Update", null);
            SetInstanceField(store, "mFirstFrame", false);
            return true;
        }

        public static bool DrawXboxTalentStore(object store, object spriteBatchObject)
        {
            SpriteBatch spriteBatch = spriteBatchObject as SpriteBatch;
            if (!IsXboxTalentStore(store) || spriteBatch == null || IsAnyXboxStoreDialogActive(store))
            {
                return false;
            }

            InvokeInstanceMethod(store, "DrawStore", new object[] { spriteBatch });
            InvokeInstanceMethod(store, "DrawCurrencies", new object[] { spriteBatch });
            DrawTalentPointCount(store, spriteBatch);
            InvokeInstanceMethod(GetInstanceField(store, "mBackButton"), "Draw", new object[] { spriteBatch });
            InvokeInstanceMethod(GetInstanceField(store, "mPurchaseButton"), "Draw", new object[] { spriteBatch });
            return true;
        }

        public static bool TryPurchaseXboxTalent(object store)
        {
            if (!IsXboxTalentStore(store) || IsAnyXboxStoreDialogActive(store))
            {
                return false;
            }

            Player player = GetInstanceField(store, "mPlayer") as Player;
            object highlighted = GetInstanceField(store, "mHighlightedItem");
            Talent talent = GetXboxItemTag(highlighted) as Talent;
            if (player == null || talent == null)
            {
                return true;
            }

            object firstFrame = GetInstanceField(store, "mFirstFrame");
            if (firstFrame is bool && (bool)firstFrame)
            {
                return true;
            }

            if (talent.CurrentLevel >= GetTalentMaxLevel(talent))
            {
                ShowXboxStoreDialog(store, "This talent is fully upgraded!");
                return true;
            }

            if (player.Stats.GetTalentPoints() <= 0)
            {
                ShowXboxStoreDialog(store, "You need at least 1 Talent Point!");
                return true;
            }

            player.Stats.AddTalentPoints(-1);
            ApplyTalent(talent, player);
            PopulateTalentXboxScreen(GetInstanceField(store, "mScreen"), player);
            InvokeInstanceMethod(GetInstanceField(store, "mScreen"), "RefireEvent", new object[] { false });
            try
            {
                SoundEngine.PlaySound("ze2_money", 0.6f);
            }
            catch
            {
            }

            return true;
        }

        public static bool TryHandleXboxTalentHighlight(object store, object item)
        {
            if (!IsXboxTalentStore(store))
            {
                return false;
            }

            if (item == null)
            {
                SetInstanceField(store, "mSelectedItem", null);
                SetInstanceField(store, "mHighlightedItem", null);
                return true;
            }

            Talent talent = GetXboxItemTag(item) as Talent;
            if (talent == null)
            {
                return false;
            }

            object selected = CreateXboxSelectedTalent(store, talent);
            if (selected == null)
            {
                return false;
            }

            SetInstanceField(store, "mSelectedItem", selected);
            SetInstanceField(store, "mHighlightedItem", item);
            object maxInflate = GetInstanceField(store, "MAX_INFLATE");
            if (maxInflate is float)
            {
                SetInstanceField(store, "mInflateTime", maxInflate);
            }

            return true;
        }

        public static bool TryCloseXboxTalentStore(object store)
        {
            if (!IsXboxTalentStore(store))
            {
                return false;
            }

            SetXboxStoreState(store, "Store");
            SetInstanceField(store, "mSelectedItem", null);
            SetInstanceField(store, "mHighlightedItem", null);
            SetButtonText(GetInstanceField(store, "mPurchaseButton"), "Purchase");
            SetButtonText(GetInstanceField(store, "mBackButton"), "Back");
            object screen = GetInstanceField(store, "mScreen");
            InvokeInstanceMethod(screen, "PopulateGuns", new object[] { GunStatsLoader.GunStatsList });
            InvokeInstanceMethod(screen, "UpdateGunListOwnAfford", null);
            InvokeInstanceMethod(screen, "RefireEvent", new object[] { false });
            PlayMenuSound();
            Log("Closed Xbox talent store.");
            return true;
        }

        public static int GetSectorCount(string levelName)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                if (string.Equals(levelName, "Mall", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(levelName, "Skyscraper", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(levelName, "Office", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(levelName, "Farm", StringComparison.OrdinalIgnoreCase))
                {
                    return 3;
                }

                if (VanillaLevelNames.Contains(levelName))
                {
                    return 2;
                }

                int manifestCount;
                if (MapSectorCounts.TryGetValue(levelName, out manifestCount))
                {
                    return manifestCount;
                }

                int detected = DetectSectorCount(levelName);
                return detected > 0 ? detected : 2;
            }
        }

        public static string[] GetLevelMenuFiles(string directory, string searchPattern)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                List<string> files = new List<string>();
                HashSet<string> knownPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string realDirectory = ResolveGameRelativeDirectory(directory);

                if (Directory.Exists(realDirectory))
                {
                    foreach (string file in Directory.GetFiles(realDirectory, searchPattern))
                    {
                        files.Add(file);
                        string prefix = GetPrefixFromZeroFile(file);
                        if (!string.IsNullOrEmpty(prefix))
                        {
                            knownPrefixes.Add(prefix);
                        }
                    }
                }

                List<string> modPrefixes = new List<string>(MapSectorCounts.Keys);
                modPrefixes.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string prefix in modPrefixes)
                {
                    if (knownPrefixes.Add(prefix))
                    {
                        string mapFolder;
                        files.Add(MapFolderPaths.TryGetValue(prefix, out mapFolder)
                            ? Path.Combine(mapFolder, prefix + "0.xml")
                            : Path.Combine(ModsRoot, prefix + "0.xml"));
                    }
                }

                Log("Level menu file query returned " + files.Count + " file(s).");
                return files.ToArray();
            }
        }

        public static bool ShouldLoadLevelMenuFiles(string directory)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                bool shouldLoad = Directory.Exists(ResolveGameRelativeDirectory(directory)) || MapSectorCounts.Count > 0;
                Log("Level menu directory gate for " + directory + ": " + shouldLoad.ToString());
                return shouldLoad;
            }
        }

        public static string[] GetCustomMapPrefixes()
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                List<string> prefixes = new List<string>(MapSectorCounts.Keys);
                prefixes.Sort(StringComparer.OrdinalIgnoreCase);
                return prefixes.ToArray();
            }
        }

        public static void AddCustomMapsToXboxLevelSelect(object menu)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                Menu gameMenu = menu as Menu;
                if (gameMenu == null)
                {
                    Log("Xbox level menu injection skipped: unexpected menu object.");
                    return;
                }

                int added = 0;
                foreach (string prefix in GetSortedCustomMapPrefixes())
                {
                    if (HasMenuItem(gameMenu, prefix))
                    {
                        continue;
                    }

                    ModLevelMenuAction action = new ModLevelMenuAction(menu, prefix);
                    gameMenu.AddToMenu(prefix, new MenuItem.SelectedDelegate(action.StartXbox), "Modded level loaded from the Mods folder.");
                    AddStaticStringListItem(menu.GetType(), "LevelNames", prefix);
                    added++;
                }

                Log("Xbox level menu injection added " + added + " custom map(s).");
            }
        }

        public static void AddCustomMapsToLevelSelect(object levelSelect)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                object arrow = GetInstanceField(levelSelect, "LevelSel");
                if (arrow == null)
                {
                    Log("LevelSelect injection skipped: LevelSel was unavailable.");
                    return;
                }

                IList values = GetInstanceField(arrow, "Values") as IList;
                if (values == null)
                {
                    Log("LevelSelect injection skipped: Values list was unavailable.");
                    return;
                }

                int added = 0;
                foreach (string prefix in GetSortedCustomMapPrefixes())
                {
                    if (!values.Contains(prefix))
                    {
                        values.Add(prefix);
                        added++;
                    }
                }

                Log("LevelSelect injection added " + added + " custom map(s).");
            }
        }

        public static void StartCustomMapFromLevelSelect(object levelSelect, string selectedLevel)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                if (!MapFolderPaths.ContainsKey(selectedLevel))
                {
                    return;
                }

                MethodInfo itemSelected = GetMethod(levelSelect.GetType(), "ItemSelected");
                if (itemSelected == null)
                {
                    Log("LevelSelect custom start skipped: ItemSelected was unavailable for " + selectedLevel + ".");
                    return;
                }

                itemSelected.Invoke(levelSelect, new object[] { selectedLevel });
                Log("Started custom map from LevelSelect: " + selectedLevel + ".");
            }
        }

        public static bool HasCustomMap(string levelName)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                return MapFolderPaths.ContainsKey(levelName);
            }
        }

        public static void ApplyLevelAssets(string levelName)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                CaptureVanillaLevelTextures();

                if (string.IsNullOrWhiteSpace(levelName))
                {
                    RestoreVanillaLevelSheet();
                    return;
                }

                bool customMap = MapFolderPaths.ContainsKey(levelName);
                MapAssetEntry assets;
                MapAssets.TryGetValue(levelName, out assets);

                RuntimeTextureAsset externalShadow = ResolveExternalShadowTexture(levelName);

                if (assets == null && externalShadow == null)
                {
                    RestoreVanillaLevelSheet();
                    if (customMap)
                    {
                        Global.CurrentLevelLightTex = GetBlankCustomShadowTexture();
                        Global.MainOnTop = false;
                        Global.DarkMod = 1f;
                        LogAndTerminal("No external shadow texture found; using blank custom shadow texture.");
                    }

                    return;
                }

                bool applied = false;
                if (assets != null && assets.Tilesheet != null)
                {
                    Texture2D tilesheet = LoadRuntimeTexture(assets.Tilesheet, true);
                    if (tilesheet != null)
                    {
                        Global.MasterEnvTex = tilesheet;
                        applied = true;
                    }
                    else
                    {
                        RestoreVanillaLevelSheet();
                    }
                }
                else
                {
                    RestoreVanillaLevelSheet();
                }

                if (externalShadow != null)
                {
                    Texture2D shadowTexture = LoadRuntimeTexture(externalShadow, false);
                    if (shadowTexture != null)
                    {
                        Global.CurrentLevelLightTex = shadowTexture;
                        applied = true;
                        LogAndTerminal("Loaded external shadow texture: " + externalShadow.Path);
                    }
                    else if (customMap)
                    {
                        Global.CurrentLevelLightTex = GetBlankCustomShadowTexture();
                        LogAndTerminal("No external shadow texture found; using blank custom shadow texture.");
                    }
                }
                else if (assets != null && assets.LightTexture != null)
                {
                    Texture2D lightTexture = LoadRuntimeTexture(assets.LightTexture, false);
                    if (lightTexture != null)
                    {
                        Global.CurrentLevelLightTex = lightTexture;
                        applied = true;
                    }
                }
                else if (customMap)
                {
                    Global.CurrentLevelLightTex = GetBlankCustomShadowTexture();
                    LogAndTerminal("No external shadow texture found; using blank custom shadow texture.");
                }
                else if (VanillaCurrentLevelLightTex != null)
                {
                    Global.CurrentLevelLightTex = VanillaCurrentLevelLightTex;
                }

                if (assets != null && assets.HasMainOnTop)
                {
                    Global.MainOnTop = assets.MainOnTop;
                }
                else if (customMap)
                {
                    Global.MainOnTop = false;
                }

                if (assets != null && assets.HasDarkMod)
                {
                    Global.DarkMod = assets.DarkMod;
                }
                else if (customMap)
                {
                    Global.DarkMod = 1f;
                }

                if (applied || (assets != null && (assets.HasMainOnTop || assets.HasDarkMod)))
                {
                    Log("Applied custom level assets for " + levelName + ".");
                }
            }
        }

        public static string ResolveLevelBasePath(string levelName, int sectorIndex, string fallbackBasePath)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                string mapFolder;
                if (!MapFolderPaths.TryGetValue(levelName, out mapFolder))
                {
                    return fallbackBasePath;
                }

                string resolved = Path.Combine(mapFolder, levelName + sectorIndex.ToString());
                if (sectorIndex == 0)
                {
                    Log("Resolved map " + levelName + " to " + mapFolder + ".");
                }

                return resolved;
            }
        }

        public static string ResolveLevelPath(string fallbackPath)
        {
            lock (Gate)
            {
                SetupPaths();
                if (!Discovered)
                {
                    DiscoverMods();
                }

                string fileName = Path.GetFileName(fallbackPath);
                if (string.IsNullOrEmpty(fileName))
                {
                    return fallbackPath;
                }

                string extension = Path.GetExtension(fileName);
                string baseName = string.IsNullOrEmpty(extension)
                    ? fileName
                    : fileName.Substring(0, fileName.Length - extension.Length);
                if (baseName.Length < 2)
                {
                    return fallbackPath;
                }

                char sectorChar = baseName[baseName.Length - 1];
                if (sectorChar < '0' || sectorChar > '2')
                {
                    return fallbackPath;
                }

                string levelName = baseName.Substring(0, baseName.Length - 1);
                string mapFolder;
                if (!MapFolderPaths.TryGetValue(levelName, out mapFolder))
                {
                    return fallbackPath;
                }

                string resolved = Path.Combine(mapFolder, baseName) + extension;
                if (sectorChar == '0')
                {
                    Log("Resolved map path " + fallbackPath + " to " + resolved + ".");
                }

                return resolved;
            }
        }

        private static void SetupPaths()
        {
            if (ModsRoot != null)
            {
                return;
            }

            string appRoot = AppDomain.CurrentDomain.BaseDirectory;
            string loaderLocation = typeof(ModBootstrap).Assembly.Location;
            string loaderRoot = string.IsNullOrEmpty(loaderLocation)
                ? appRoot
                : (Path.GetDirectoryName(loaderLocation) ?? appRoot);

            GameRoot = appRoot;
            ModsRoot = Path.Combine(GameRoot, "Mods");
            LogPath = Path.Combine(ModsRoot, "ZE2ModLoader.log");
            Directory.CreateDirectory(ModsRoot);

            AddModRoot(ModsRoot);
            AddModRoot(Path.Combine(GameRoot, "BepInEx", "plugins", "Mods"));
            AddModRoot(Path.Combine(loaderRoot, "Mods"));
        }

        private static void AddModRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath = Path.GetFullPath(path);
            foreach (string existing in ModRoots)
            {
                if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            ModRoots.Add(fullPath);
        }

        private static void DiscoverMods()
        {
            if (Discovered)
            {
                return;
            }

            CharacterEntries.Clear();
            GunEntries.Clear();
            BulletEntries.Clear();
            MapSectorCounts.Clear();
            MapFolderPaths.Clear();
            MapAssets.Clear();

            Dictionary<string, int> configuredOrder = LoadConfiguredModOrder();
            List<ModDiscovery> discovered = new List<ModDiscovery>();

            foreach (string root in ModRoots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string modDir in Directory.GetDirectories(root))
                {
                    string manifestPath = Path.Combine(modDir, "mod.xml");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    try
                    {
                        Ze2ModManifest manifest = LoadManifest(manifestPath);
                        string modId = string.IsNullOrWhiteSpace(manifest.Id) ? Path.GetFileName(modDir) : manifest.Id.Trim();
                        int order;
                        if (!configuredOrder.TryGetValue(modId, out order))
                        {
                            order = 10000 + discovered.Count;
                        }
                        discovered.Add(new ModDiscovery
                        {
                            Directory = modDir,
                            Manifest = manifest,
                            ModId = modId,
                            Order = order
                        });
                    }
                    catch (Exception ex)
                    {
                        Log("Skipped malformed mod manifest " + manifestPath + ": " + ex.Message);
                    }
                }
            }

            discovered.Sort(delegate(ModDiscovery left, ModDiscovery right)
            {
                int order = left.Order.CompareTo(right.Order);
                if (order != 0)
                {
                    return order;
                }

                return string.Compare(left.ModId, right.ModId, StringComparison.OrdinalIgnoreCase);
            });

            foreach (ModDiscovery entry in discovered)
            {
                try
                {
                    Ze2ModManifest manifest = entry.Manifest;
                    string modId = entry.ModId;
                        if (!manifest.Enabled)
                        {
                            Log("Skipped disabled mod: " + modId);
                            continue;
                        }

                        DiscoverCharacters(entry.Directory, modId, manifest);
                        DiscoverGuns(entry.Directory, modId, manifest);
                        DiscoverBullets(entry.Directory, modId, manifest);
                        DiscoverMaps(entry.Directory, modId, manifest);
                        Log("Discovered mod: " + modId);
                }
                catch (Exception ex)
                {
                    Log("Skipped mod " + entry.ModId + ": " + ex.Message);
                }
            }

            Discovered = true;
        }

        private static Dictionary<string, int> LoadConfiguredModOrder()
        {
            Dictionary<string, int> order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string configPath = Path.Combine(GameRoot, "BepInEx", "config", "ZE2.ModManager.xml");
                if (!File.Exists(configPath))
                {
                    return order;
                }

                XDocument doc = XDocument.Load(configPath);
                if (doc.Root == null)
                {
                    return order;
                }

                foreach (XElement mod in doc.Root.Elements("Mod"))
                {
                    string id = (string)mod.Attribute("Id");
                    int loadOrder;
                    if (!string.IsNullOrWhiteSpace(id) &&
                        int.TryParse((string)mod.Attribute("LoadOrder"), out loadOrder))
                    {
                        order[id.Trim()] = loadOrder;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Failed to read mod manager load order: " + ex.Message);
            }

            return order;
        }

        private static void DiscoverCharacters(string modDir, string modId, Ze2ModManifest manifest)
        {
            if (manifest.Characters == null)
            {
                return;
            }

            foreach (CharacterManifest character in manifest.Characters)
            {
                if (character == null || string.IsNullOrWhiteSpace(character.File))
                {
                    Log("Skipped character entry with no File in " + modId);
                    continue;
                }

                string characterPath = ResolveModPath(modDir, character.File);
                if (characterPath == null || !File.Exists(characterPath))
                {
                    Log("Skipped missing character file in " + modId + ": " + character.File);
                    continue;
                }

                string spritePath = null;
                if (!string.IsNullOrWhiteSpace(character.Sprite))
                {
                    spritePath = ResolveModPath(modDir, character.Sprite);
                    if (spritePath == null || !File.Exists(spritePath))
                    {
                        Log("Skipped missing sprite for " + character.File + " in " + modId + ": " + character.Sprite);
                        spritePath = null;
                    }
                }

                CharacterEntries.Add(new CharacterEntry
                {
                    ModId = modId,
                    CharacterPath = characterPath,
                    SpritePath = spritePath
                });
            }
        }

        private static void DiscoverGuns(string modDir, string modId, Ze2ModManifest manifest)
        {
            if (manifest.Guns == null)
            {
                return;
            }

            foreach (GunManifest gun in manifest.Guns)
            {
                if (gun == null || string.IsNullOrWhiteSpace(gun.File))
                {
                    Log("Skipped gun entry with no File in " + modId);
                    continue;
                }

                string gunPath = ResolveModPath(modDir, gun.File);
                if (gunPath == null || !File.Exists(gunPath))
                {
                    Log("Skipped missing gun file in " + modId + ": " + gun.File);
                    continue;
                }

                string spritePath = null;
                if (!string.IsNullOrWhiteSpace(gun.Sprite))
                {
                    spritePath = ResolveModPath(modDir, gun.Sprite);
                    if (spritePath == null || !File.Exists(spritePath))
                    {
                        Log("Skipped missing gun sprite for " + gun.File + " in " + modId + ": " + gun.Sprite);
                        spritePath = null;
                    }
                }

                GunEntries.Add(new GunEntry
                {
                    ModId = modId,
                    GunPath = gunPath,
                    SpritePath = spritePath
                });
            }
        }

        private static void DiscoverBullets(string modDir, string modId, Ze2ModManifest manifest)
        {
            if (manifest.Bullets == null)
            {
                return;
            }

            foreach (BulletManifest bullet in manifest.Bullets)
            {
                if (bullet == null || string.IsNullOrWhiteSpace(bullet.File))
                {
                    Log("Skipped bullet entry with no File in " + modId);
                    continue;
                }

                string bulletPath = ResolveModPath(modDir, bullet.File);
                if (bulletPath == null || !File.Exists(bulletPath))
                {
                    Log("Skipped missing bullet file in " + modId + ": " + bullet.File);
                    continue;
                }

                string spritePath = null;
                if (!string.IsNullOrWhiteSpace(bullet.Sprite))
                {
                    spritePath = ResolveModPath(modDir, bullet.Sprite);
                    if (spritePath == null || !File.Exists(spritePath))
                    {
                        Log("Skipped missing bullet sprite for " + bullet.File + " in " + modId + ": " + bullet.Sprite);
                        spritePath = null;
                    }
                }

                BulletEntries.Add(new BulletEntry
                {
                    ModId = modId,
                    BulletPath = bulletPath,
                    SpritePath = spritePath,
                    SpriteMode = bullet.SpriteMode
                });
            }
        }

        private static void DiscoverMaps(string modDir, string modId, Ze2ModManifest manifest)
        {
            if (manifest.Maps == null)
            {
                return;
            }

            foreach (MapManifest map in manifest.Maps)
            {
                if (map == null)
                {
                    continue;
                }

                string prefix = (map.Prefix ?? string.Empty).Trim();
                if (!IsSafePrefix(prefix))
                {
                    Log("Skipped map with unsafe prefix in " + modId + ": " + prefix);
                    continue;
                }

                if (VanillaLevelNames.Contains(prefix))
                {
                    Log("Skipped map using vanilla prefix in " + modId + ": " + prefix);
                    continue;
                }

                if (MapSectorCounts.ContainsKey(prefix))
                {
                    Log("Skipped duplicate map prefix " + prefix + " in " + modId);
                    continue;
                }

                int sectorCount = ClampSectorCount(map.SectorCount);
                string folder = string.IsNullOrWhiteSpace(map.Folder) ? "Maps/" + prefix : map.Folder;
                string mapPath = ResolveModPath(modDir, folder);
                if (mapPath == null || !Directory.Exists(mapPath))
                {
                    Log("Skipped missing map folder for " + prefix + " in " + modId);
                    continue;
                }

                if (!ValidateMapFiles(mapPath, prefix, sectorCount))
                {
                    Log("Skipped map with missing required files: " + prefix);
                    continue;
                }

                MapSectorCounts[prefix] = sectorCount;
                MapFolderPaths[prefix] = mapPath;

                MapAssetEntry assets = DiscoverMapAssets(modDir, modId, prefix, map);
                if (assets != null)
                {
                    MapAssets[prefix] = assets;
                }
            }
        }

        private static bool ValidateMapFiles(string mapPath, string prefix, int sectorCount)
        {
            for (int i = 0; i < sectorCount; i++)
            {
                if (!File.Exists(Path.Combine(mapPath, prefix + i + ".xml")) ||
                    !File.Exists(Path.Combine(mapPath, prefix + i + "_Ground.xml")) ||
                    !File.Exists(Path.Combine(mapPath, prefix + i + "_Ground.bin")) ||
                    !File.Exists(Path.Combine(mapPath, prefix + i + "_Walls.xml")) ||
                    !File.Exists(Path.Combine(mapPath, prefix + i + "_Walls.bin")))
                {
                    return false;
                }
            }

            return File.Exists(Path.Combine(mapPath, prefix + "_Path.txt"));
        }

        private static MapAssetEntry DiscoverMapAssets(string modDir, string modId, string prefix, MapManifest map)
        {
            MapAssetEntry assets = new MapAssetEntry
            {
                ModId = modId,
                Prefix = prefix
            };

            string tilesheet = FirstNonBlank(map.Tilesheet, map.Texture, map.AssetSheet);
            if (!string.IsNullOrWhiteSpace(tilesheet))
            {
                assets.Tilesheet = ResolveOptionalTextureAsset(modDir, tilesheet, "tilesheet", prefix, modId);
            }

            if (!string.IsNullOrWhiteSpace(map.LightTexture))
            {
                assets.LightTexture = ResolveOptionalTextureAsset(modDir, map.LightTexture, "light texture", prefix, modId);
            }

            float darkMod;
            if (!string.IsNullOrWhiteSpace(map.DarkMod))
            {
                if (float.TryParse(map.DarkMod, NumberStyles.Float, CultureInfo.InvariantCulture, out darkMod))
                {
                    assets.HasDarkMod = true;
                    assets.DarkMod = darkMod;
                }
                else
                {
                    Log("Skipped invalid DarkMod for map " + prefix + " in " + modId + ": " + map.DarkMod);
                }
            }

            bool mainOnTop;
            if (!string.IsNullOrWhiteSpace(map.MainOnTop))
            {
                if (bool.TryParse(map.MainOnTop, out mainOnTop))
                {
                    assets.HasMainOnTop = true;
                    assets.MainOnTop = mainOnTop;
                }
                else
                {
                    Log("Skipped invalid MainOnTop for map " + prefix + " in " + modId + ": " + map.MainOnTop);
                }
            }

            if (assets.Tilesheet == null &&
                assets.LightTexture == null &&
                !assets.HasDarkMod &&
                !assets.HasMainOnTop)
            {
                return null;
            }

            return assets;
        }

        private static RuntimeTextureAsset ResolveOptionalTextureAsset(string modDir, string relativePath, string assetKind, string prefix, string modId)
        {
            string assetPath = ResolveModPath(modDir, relativePath);
            if (assetPath == null)
            {
                Log("Skipped unsafe " + assetKind + " path for map " + prefix + " in " + modId + ": " + relativePath);
                return null;
            }

            if (!File.Exists(assetPath))
            {
                Log("Skipped missing " + assetKind + " for map " + prefix + " in " + modId + ": " + relativePath);
                return null;
            }

            string extension = Path.GetExtension(assetPath);
            bool isPng = string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase);
            bool isXnb = string.Equals(extension, ".xnb", StringComparison.OrdinalIgnoreCase);
            if (!isPng && !isXnb)
            {
                Log("Skipped unsupported " + assetKind + " for map " + prefix + " in " + modId + ": " + relativePath + ". Use .xnb or .png.");
                return null;
            }

            RuntimeTextureAsset asset = new RuntimeTextureAsset
            {
                Path = assetPath,
                IsXnb = isXnb
            };

            if (isXnb)
            {
                asset.ContentRoot = Path.GetFullPath(modDir);
                asset.AssetName = NormalizeContentAssetName(relativePath);
            }

            return asset;
        }

        private static bool ValidateGun(GunStats gun, string path)
        {
            if (gun == null)
            {
                Log("Skipped null gun: " + path);
                return false;
            }

            if (string.IsNullOrWhiteSpace(gun.GunName))
            {
                Log("Skipped gun with blank GunName: " + path);
                return false;
            }

            if (gun.GunProperties == null || gun.GunProperties.Count < 4)
            {
                Log("Skipped gun with fewer than four GunProperties levels: " + gun.GunName);
                return false;
            }

            if (gun.SpecialProperties == null || gun.SpecialProperties.Count < 4)
            {
                Log("Skipped gun with fewer than four SpecialProperties levels: " + gun.GunName);
                return false;
            }

            return true;
        }

        private static bool ValidateBullet(BulletStats bullet, string path)
        {
            if (bullet == null)
            {
                Log("Skipped null bullet: " + path);
                return false;
            }

            if (string.IsNullOrWhiteSpace(bullet.Name))
            {
                bullet.Name = Path.GetFileNameWithoutExtension(path);
            }

            if (string.IsNullOrWhiteSpace(bullet.Name))
            {
                Log("Skipped bullet with blank Name: " + path);
                return false;
            }

            if (bullet.VertTexCoord == null)
            {
                bullet.VertTexCoord = new ZEPoint(63, 63);
            }

            if (bullet.HorizTexCoord == null)
            {
                bullet.HorizTexCoord = new ZEPoint(63, 63);
            }

            if (bullet.BehaviorsStrings == null || bullet.BehaviorsStrings.Length == 0)
            {
                bullet.BehaviorsStrings = new[] { "Straight, 1.0, 0.0, 0.0, 1.0" };
            }

            return true;
        }

        private static short GetNextGunUid(Dictionary<short, string> uidForward)
        {
            short next = 1;
            if (uidForward == null)
            {
                return next;
            }

            foreach (short key in uidForward.Keys)
            {
                if (key >= next)
                {
                    next = (short)(key + 1);
                }
            }

            return next;
        }

        private static void AddGunUid(string gunName, Dictionary<short, string> uidForward, Dictionary<string, short> uidReverse, ref short nextUid)
        {
            if (uidForward == null || uidReverse == null || uidReverse.ContainsKey(gunName))
            {
                return;
            }

            while (uidForward.ContainsKey(nextUid))
            {
                nextUid++;
            }

            uidForward[nextUid] = gunName;
            uidReverse[gunName] = nextUid;
            nextUid++;
        }

        private static int GetNextBulletUid(Dictionary<int, string> uidForward)
        {
            int next = 1;
            if (uidForward == null)
            {
                return next;
            }

            foreach (int key in uidForward.Keys)
            {
                if (key >= next)
                {
                    next = key + 1;
                }
            }

            return next;
        }

        private static void AddBulletUid(string bulletName, Dictionary<int, string> uidForward, Dictionary<string, int> uidReverse, ref int nextUid)
        {
            if (uidForward == null || uidReverse == null || uidReverse.ContainsKey(bulletName))
            {
                return;
            }

            while (uidForward.ContainsKey(nextUid))
            {
                nextUid++;
            }

            uidForward[nextUid] = bulletName;
            uidReverse[bulletName] = nextUid;
            nextUid++;
        }

        private static void ApplyBulletSprite(BulletEntry entry, BulletStats bullet)
        {
            if (!entry.AssignedTexCoord.HasValue)
            {
                return;
            }

            Point start = entry.AssignedTexCoord.Value;
            string mode = string.IsNullOrWhiteSpace(entry.SpriteMode)
                ? (entry.AssignedCellCount >= 2 ? "Pair" : "Horizontal")
                : entry.SpriteMode.Trim();
            if (string.Equals(mode, "Both", StringComparison.OrdinalIgnoreCase))
            {
                bullet.HorizTexCoord = new ZEPoint(start.X, start.Y);
                bullet.VertTexCoord = new ZEPoint(start.X, start.Y);
            }
            else if (string.Equals(mode, "Vertical", StringComparison.OrdinalIgnoreCase))
            {
                bullet.HorizTexCoord = new ZEPoint(63, 63);
                bullet.VertTexCoord = new ZEPoint(start.X, start.Y);
            }
            else if (string.Equals(mode, "Pair", StringComparison.OrdinalIgnoreCase))
            {
                bullet.HorizTexCoord = new ZEPoint(start.X, start.Y);
                bullet.VertTexCoord = new ZEPoint(start.X + 1, start.Y);
            }
            else
            {
                bullet.HorizTexCoord = new ZEPoint(start.X, start.Y);
                bullet.VertTexCoord = new ZEPoint(63, 63);
            }
        }

        private static string GetPrefixFromZeroFile(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrEmpty(name) || !name.EndsWith("0", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name.Substring(0, name.Length - 1);
        }

        private static void CaptureVanillaLevelTextures()
        {
            if (VanillaLevelTexturesCaptured)
            {
                return;
            }

            VanillaMasterEnvTex = Global.MasterEnvTex;
            VanillaCurrentLevelLightTex = Global.CurrentLevelLightTex;
            VanillaMasterLightTex = Global.MasterLightTex;
            VanillaLevelTexturesCaptured = true;
        }

        private static void RestoreVanillaLevelSheet()
        {
            if (VanillaMasterEnvTex != null)
            {
                Global.MasterEnvTex = VanillaMasterEnvTex;
            }

            if (VanillaMasterLightTex != null)
            {
                Global.MasterLightTex = VanillaMasterLightTex;
            }
        }

        private static RuntimeTextureAsset ResolveExternalShadowTexture(string levelName)
        {
            if (string.IsNullOrWhiteSpace(levelName))
            {
                return null;
            }

            string fileName = levelName + "_Shadow.png";
            List<string> candidates = new List<string>
            {
                Path.Combine(GameRoot, "Levels", fileName),
                Path.Combine(GameRoot, "Data", "Levels", fileName)
            };

            string mapFolder;
            if (MapFolderPaths.TryGetValue(levelName, out mapFolder))
            {
                candidates.Add(Path.Combine(mapFolder, fileName));
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return new RuntimeTextureAsset
                    {
                        Path = Path.GetFullPath(candidate),
                        IsXnb = false
                    };
                }
            }

            return null;
        }

        private static Texture2D GetBlankCustomShadowTexture()
        {
            if (BlankCustomShadowTexture != null)
            {
                return BlankCustomShadowTexture;
            }

            GraphicsDevice graphicsDevice = RuntimeGraphicsDevice ?? Global.GraphicsDevice;
            if (graphicsDevice == null)
            {
                return VanillaCurrentLevelLightTex;
            }

            BlankCustomShadowTexture = new Texture2D(graphicsDevice, 512, 512, false, SurfaceFormat.Color);
            Color[] pixels = new Color[512 * 512];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.Transparent;
            }
            BlankCustomShadowTexture.SetData(pixels);
            return BlankCustomShadowTexture;
        }

        private static void LogAndTerminal(string message)
        {
            Log(message);
            try
            {
                Terminal.WriteMessage(message);
            }
            catch
            {
            }
        }

        private static Texture2D LoadRuntimeTexture(RuntimeTextureAsset asset, bool isTilesheet)
        {
            string cacheKey = asset.IsXnb
                ? "xnb|" + asset.ContentRoot + "|" + asset.AssetName
                : "png|" + asset.Path;

            Texture2D cached;
            if (RuntimeTextureCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            GraphicsDevice graphicsDevice = RuntimeGraphicsDevice ?? Global.GraphicsDevice;
            if (graphicsDevice == null)
            {
                Log("Could not load runtime texture because GraphicsDevice was unavailable: " + asset.Path);
                return null;
            }

            try
            {
                Texture2D texture;
                if (asset.IsXnb)
                {
                    if (Global.Content == null || Global.Content.ServiceProvider == null)
                    {
                        Log("Could not load XNB texture because Global.Content was unavailable: " + asset.Path);
                        return null;
                    }

                    ContentManager contentManager = new ContentManager(Global.Content.ServiceProvider, asset.ContentRoot);
                    RuntimeContentManagers.Add(contentManager);
                    texture = contentManager.Load<Texture2D>(asset.AssetName);
                }
                else
                {
                    using (FileStream stream = File.OpenRead(asset.Path))
                    {
                        texture = Texture2D.FromStream(graphicsDevice, stream);
                    }
                }

                if (isTilesheet && (texture.Width != 512 || texture.Height != 512))
                {
                    Log("Loaded map tilesheet with nonstandard size " + texture.Width + "x" + texture.Height + ". Maps are authored against 512x512 sheets with 16x16 tiles: " + asset.Path);
                }

                RuntimeTextureCache[cacheKey] = texture;
                Log("Loaded runtime " + (asset.IsXnb ? "XNB" : "PNG") + " texture: " + asset.Path);
                return texture;
            }
            catch (Exception ex)
            {
                Log("Failed to load runtime texture " + asset.Path + ": " + ex.Message);
                return null;
            }
        }

        private static List<string> GetSortedCustomMapPrefixes()
        {
            List<string> prefixes = new List<string>(MapSectorCounts.Keys);
            prefixes.Sort(StringComparer.OrdinalIgnoreCase);
            return prefixes;
        }

        private static bool HasMenuItem(Menu menu, string text)
        {
            IList items = GetInstanceField(menu, "Items") as IList;
            if (items == null)
            {
                return false;
            }

            foreach (object item in items)
            {
                MenuItem menuItem = item as MenuItem;
                if (menuItem != null && string.Equals(menuItem.Text, text, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<Talent> CloneTalents(List<Talent> source)
        {
            List<Talent> clones = new List<Talent>();
            if (source == null)
            {
                return clones;
            }

            foreach (Talent talent in source)
            {
                if (talent != null)
                {
                    clones.Add(CloneTalent(talent));
                }
            }

            return clones;
        }

        private static Talent CloneTalent(Talent source)
        {
            Talent clone = new Talent();
            clone.Name = source.Name;
            clone.Attribute = source.Attribute;
            clone.AbilityClassName = source.AbilityClassName;
            clone.CurrentLevel = source.CurrentLevel;
            clone.TotalLevels = source.TotalLevels;
            clone.TexCoord = source.TexCoord;
            clone.Description = source.Description == null ? null : (string[])source.Description.Clone();
            clone.PointsReq = source.PointsReq == null ? null : (int[])source.PointsReq.Clone();
            clone.Modifiers = source.Modifiers == null ? null : (float[])source.Modifiers.Clone();
            return clone;
        }

        private static int GetTalentMaxLevel(Talent talent)
        {
            if (talent == null)
            {
                return 0;
            }

            if (talent.TotalLevels > 0)
            {
                return talent.TotalLevels;
            }

            if (talent.Modifiers != null && talent.Modifiers.Length > 0)
            {
                return talent.Modifiers.Length;
            }

            if (talent.Description != null && talent.Description.Length > 0)
            {
                return talent.Description.Length;
            }

            return 3;
        }

        private static float GetTalentModifier(Talent talent, int levelIndex)
        {
            if (talent == null || talent.Modifiers == null || talent.Modifiers.Length == 0)
            {
                return 0f;
            }

            int index = Math.Max(0, Math.Min(levelIndex, talent.Modifiers.Length - 1));
            return talent.Modifiers[index];
        }

        private static void PopulateTalentXboxScreen(object screen, Player player)
        {
            if (screen == null || player == null || player.Stats == null)
            {
                return;
            }

            EnsurePlayerTalents(player);
            List<Talent> talents = player.Stats.GetTalents();
            if (talents == null)
            {
                talents = new List<Talent>();
            }

            Type screenType = screen.GetType();
            FieldInfo widthField = GetField(screenType, "mWidth");
            FieldInfo heightField = GetField(screenType, "mHeight");
            FieldInfo itemsField = GetField(screenType, "mItems");
            if (widthField == null || heightField == null || itemsField == null)
            {
                Log("Talent store screen population skipped: XboxItemScreen fields were unavailable.");
                return;
            }

            int width = (int)widthField.GetValue(screen);
            int height = (int)heightField.GetValue(screen);
            Type itemType = typeof(Player).Assembly.GetType("ZombieEstate2.StoreScreen.XboxStore.XboxItem");
            if (itemType == null)
            {
                Log("Talent store screen population skipped: XboxItem type was unavailable.");
                return;
            }

            ConstructorInfo itemCtor = itemType.GetConstructor(new[] { typeof(int), typeof(int), typeof(object), typeof(bool), typeof(bool) });
            if (itemCtor == null)
            {
                Log("Talent store screen population skipped: XboxItem constructor was unavailable.");
                return;
            }

            Array items = Array.CreateInstance(itemType, width, height);
            int count = Math.Min(talents.Count, width * height);
            for (int i = 0; i < count; i++)
            {
                Talent talent = talents[i];
                bool canBuy = talent != null && talent.CurrentLevel < GetTalentMaxLevel(talent) && player.Stats.GetTalentPoints() > 0;
                object item = itemCtor.Invoke(new object[] { talent.TexCoord.X, talent.TexCoord.Y, talent, false, canBuy });
                SetInstanceField(item, "ID", talent.Name ?? "Talent");
                items.SetValue(item, i % width, i / width);
            }

            itemsField.SetValue(screen, items);
            SetInstanceField(screen, "mCurrentX", 0);
            SetInstanceField(screen, "mCurrentY", 0);
            SetInstanceField(screen, "mWindowX", 0);
            SetInstanceField(screen, "mWindowY", 0);
            SetInstanceField(screen, "mFirstLoop", false);
        }

        private static object GetXboxItemTag(object item)
        {
            if (item == null)
            {
                return null;
            }

            PropertyInfo property = item.GetType().GetProperty("Tag", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(item, null);
        }

        private static object CreateXboxSelectedTalent(object store, Talent talent)
        {
            Type selectedType = store.GetType().GetNestedType("SelectedItem", BindingFlags.NonPublic);
            if (selectedType == null)
            {
                Log("Talent highlight skipped: XboxStore.SelectedItem type was unavailable.");
                return null;
            }

            object selected = Activator.CreateInstance(selectedType, true);
            Player player = GetInstanceField(store, "mPlayer") as Player;
            Vector2 topLeft = GetVector2Field(store, "mTopLeft");
            Vector2 nameCenter = GetVector2Field(store, "mLocNameCenter");
            int maxLevel = GetTalentMaxLevel(talent);
            int descriptionIndex = Math.Max(0, Math.Min(talent.CurrentLevel, maxLevel - 1));
            string description = talent.Description != null && talent.Description.Length > descriptionIndex
                ? talent.Description[descriptionIndex]
                : "Spend a Talent Point to improve this character.";

            if (talent.CurrentLevel >= maxLevel)
            {
                description = "Fully upgraded. " + description;
            }

            object scrollBox = CreateScrollBox(
                description + string.Format(" Level {0}/{1}.", talent.CurrentLevel, maxLevel),
                new Rectangle((int)topLeft.X + 192, (int)topLeft.Y + 76, 304, 135),
                player);

            SetInstanceField(selected, "Src", Global.GetTexRectange(talent.TexCoord.X, talent.TexCoord.Y));
            SetInstanceField(selected, "AmmoSrc", Global.GetTexRectange(63, 63));
            SetInstanceField(selected, "Name", talent.Name ?? "Talent");
            SetInstanceField(selected, "Cost", 1);
            SetInstanceField(selected, "CostString", talent.CurrentLevel >= maxLevel ? "Max Level" : "Cost: 1 TP");
            SetInstanceField(selected, "Desc", scrollBox);
            SetInstanceField(selected, "Stats", null);
            SetInstanceField(selected, "AmmoType", AmmoType.INFINITE);
            SetInstanceField(selected, "AMMOITEM", null);
            SetInstanceField(selected, "Level", talent.CurrentLevel);
            SetInstanceField(selected, "NameLocation", CenterText(Global.EquationFontSmall, nameCenter, talent.Name ?? "Talent"));
            SetInstanceField(selected, "CostLocation", new Vector2(topLeft.X + 302f, topLeft.Y + 38f));
            return selected;
        }

        private static object CreateScrollBox(string text, Rectangle rectangle, Player player)
        {
            Type scrollType = typeof(Player).Assembly.GetType("ZombieEstate2.ScrollBox");
            if (scrollType == null)
            {
                return null;
            }

            ConstructorInfo ctor = scrollType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(Rectangle), typeof(SpriteFont), typeof(Player), typeof(Color) },
                null);
            return ctor == null ? null : ctor.Invoke(new object[] { text, rectangle, Global.StoreFont, player, Color.LightBlue });
        }

        private static void DrawTalentPointCount(object store, SpriteBatch spriteBatch)
        {
            Player player = GetInstanceField(store, "mPlayer") as Player;
            if (player == null || player.Stats == null)
            {
                return;
            }

            Vector2 topLeft = GetVector2Field(store, "mTopLeft");
            Vector2 position = new Vector2(topLeft.X + 330f, topLeft.Y + 200f);
            Shadow.DrawString("TP: " + player.Stats.GetTalentPoints(), Global.EquationFontSmall, position, 1, Color.Pink, spriteBatch);
        }

        private static Vector2 CenterText(SpriteFont font, Vector2 center, string text)
        {
            Vector2 size = font.MeasureString(text);
            return new Vector2(center.X - size.X / 2f, center.Y - size.Y / 2f);
        }

        private static void ShowXboxStoreDialog(object store, string message)
        {
            Player player = GetInstanceField(store, "mPlayer") as Player;
            if (player == null)
            {
                return;
            }

            Type dialogType = typeof(Player).Assembly.GetType("ZombieEstate2.StoreScreen.XboxStore.XboxStoreDialog");
            if (dialogType == null)
            {
                return;
            }

            Type enumType = dialogType.GetNestedType("XboxDialogType", BindingFlags.Public | BindingFlags.NonPublic);
            if (enumType == null)
            {
                return;
            }

            object ok = Enum.Parse(enumType, "Ok");
            ConstructorInfo ctor = dialogType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(Vector2), typeof(Player), enumType },
                null);
            if (ctor == null)
            {
                return;
            }

            object dialog = ctor.Invoke(new object[] { message, GetVector2Field(store, "mTopLeft"), player, ok });
            SetInstanceField(store, "mNotEnoughMoney", dialog);
            try
            {
                SoundEngine.PlaySound("ze2_death", 1f);
            }
            catch
            {
            }
        }

        private static bool IsAnyXboxStoreDialogActive(object store)
        {
            string[] dialogFields = { "mNotEnoughMoney", "mAreYouSureSell", "mAreYouSureBuy", "mAreYouSureUpgrade", "mStatsDialog" };
            foreach (string fieldName in dialogFields)
            {
                object dialog = GetInstanceField(store, fieldName);
                if (dialog == null)
                {
                    continue;
                }

                object active = GetInstanceField(dialog, "Active");
                if (active is bool && (bool)active)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsXboxTalentStore(object store)
        {
            object state = GetInstanceField(store, "mState");
            return state != null && string.Equals(state.ToString(), "Stats_Store", StringComparison.Ordinal);
        }

        private static void SetXboxStoreState(object store, string stateName)
        {
            if (store == null)
            {
                return;
            }

            FieldInfo stateField = GetField(store.GetType(), "mState");
            if (stateField == null)
            {
                return;
            }

            object state = Enum.Parse(stateField.FieldType, stateName);
            stateField.SetValue(store, state);
        }

        private static void SetButtonText(object button, string text)
        {
            InvokeInstanceMethod(button, "SetText", new object[] { text });
        }

        private static void PlayMenuSound()
        {
            try
            {
                SoundEngine.PlaySound("ze2_menunav", 0.45f);
            }
            catch
            {
            }
        }

        private static Vector2 GetVector2Field(object target, string fieldName)
        {
            object value = GetInstanceField(target, fieldName);
            return value is Vector2 ? (Vector2)value : Vector2.Zero;
        }

        private static void SetInstanceField(object target, string fieldName, object value)
        {
            if (target == null)
            {
                return;
            }

            FieldInfo field = GetField(target.GetType(), fieldName);
            if (field != null)
            {
                field.SetValue(target, value);
            }
        }

        private static object InvokeInstanceMethod(object target, string methodName, object[] args)
        {
            if (target == null)
            {
                return null;
            }

            MethodInfo method = GetMethod(target.GetType(), methodName, args);
            return method == null ? null : method.Invoke(target, args);
        }

        private static object GetInstanceField(object target, string fieldName)
        {
            if (target == null)
            {
                return null;
            }

            FieldInfo field = GetField(target.GetType(), fieldName);
            return field == null ? null : field.GetValue(target);
        }

        private static object GetStaticField(Type type, string fieldName)
        {
            FieldInfo field = GetField(type, fieldName);
            return field == null ? null : field.GetValue(null);
        }

        private static FieldInfo GetField(Type type, string fieldName)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    return field;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static MethodInfo GetMethod(Type type, string methodName)
        {
            while (type != null)
            {
                MethodInfo method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                {
                    return method;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static MethodInfo GetMethod(Type type, string methodName, int parameterCount)
        {
            while (type != null)
            {
                MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (MethodInfo method in methods)
                {
                    if (method.Name == methodName && method.GetParameters().Length == parameterCount)
                    {
                        return method;
                    }
                }

                type = type.BaseType;
            }

            return null;
        }

        private static MethodInfo GetMethod(Type type, string methodName, object[] args)
        {
            int parameterCount = args == null ? 0 : args.Length;
            while (type != null)
            {
                MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                foreach (MethodInfo method in methods)
                {
                    if (method.Name != methodName)
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != parameterCount)
                    {
                        continue;
                    }

                    bool matches = true;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        object arg = args[i];
                        if (arg != null && !parameters[i].ParameterType.IsInstanceOfType(arg))
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        return method;
                    }
                }

                type = type.BaseType;
            }

            return null;
        }

        private static void AddStaticStringListItem(Type type, string fieldName, string value)
        {
            FieldInfo field = GetField(type, fieldName);
            IList list = field == null ? null : field.GetValue(null) as IList;
            if (list != null && !list.Contains(value))
            {
                list.Add(value);
            }
        }

        private static void MergeModSprites(GraphicsDevice graphicsDevice)
        {
            if (graphicsDevice == null)
            {
                Log("Sprite merge skipped: GraphicsDevice was unavailable.");
                return;
            }

            bool hasSprites =
                CharacterEntries.Exists(e => !string.IsNullOrEmpty(e.SpritePath)) ||
                GunEntries.Exists(e => !string.IsNullOrEmpty(e.SpritePath)) ||
                BulletEntries.Exists(e => !string.IsNullOrEmpty(e.SpritePath));
            if (!hasSprites)
            {
                return;
            }

            Texture2D original = Global.MasterTexture;
            if (original == null)
            {
                Log("Sprite merge skipped: Global.MasterTexture was unavailable.");
                return;
            }

            int cellsPerRow = Math.Min(LogicalAtlasCells, original.Width / CellSize);
            if (cellsPerRow <= 0)
            {
                Log("Sprite merge skipped: invalid master texture width.");
                return;
            }

            int cellRows = Math.Min(LogicalAtlasCells, original.Height / CellSize);
            Color[] merged = new Color[original.Width * original.Height];
            original.GetData(merged);

            HashSet<int> claimedAtlasCells = new HashSet<int>();
            int mergedCount = 0;
            foreach (CharacterEntry entry in CharacterEntries)
            {
                if (MergeCharacterSprite(graphicsDevice, entry, merged, original.Width, cellsPerRow, cellRows, claimedAtlasCells))
                {
                    mergedCount++;
                }
            }

            foreach (GunEntry entry in GunEntries)
            {
                if (MergeGunSprite(graphicsDevice, entry, merged, original.Width, cellsPerRow, cellRows, claimedAtlasCells))
                {
                    mergedCount++;
                }
            }

            foreach (BulletEntry entry in BulletEntries)
            {
                if (MergeBulletSprite(graphicsDevice, entry, merged, original.Width, cellsPerRow, cellRows, claimedAtlasCells))
                {
                    mergedCount++;
                }
            }

            if (mergedCount == 0)
            {
                return;
            }

            Texture2D mergedTexture = new Texture2D(graphicsDevice, original.Width, original.Height, false, SurfaceFormat.Color);
            mergedTexture.SetData(merged);
            Global.MasterTexture = mergedTexture;
            Log("Merged " + mergedCount + " custom sprite asset(s) into existing Global.MasterTexture atlas cells.");
        }

        private static bool MergeCharacterSprite(
            GraphicsDevice graphicsDevice,
            CharacterEntry entry,
            Color[] atlasData,
            int atlasWidth,
            int cellsPerRow,
            int cellRows,
            HashSet<int> claimedAtlasCells)
        {
            if (string.IsNullOrEmpty(entry.SpritePath))
            {
                return false;
            }

            try
            {
                using (FileStream stream = File.OpenRead(entry.SpritePath))
                using (Texture2D sprite = Texture2D.FromStream(graphicsDevice, stream))
                {
                    bool isSingleCell = sprite.Width == CellSize && sprite.Height == CellSize;
                    bool isFacingStrip = sprite.Width == CellSize * 4 && sprite.Height == CellSize;
                    if (!isSingleCell && !isFacingStrip)
                    {
                        Log("Skipped character sprite that is not 16x16 or 64x16: " + entry.SpritePath);
                        return false;
                    }

                    Point? atlasCell = FindFreeSpriteRun(atlasData, atlasWidth, cellsPerRow, cellRows, 4, claimedAtlasCells);
                    if (!atlasCell.HasValue)
                    {
                        Log("Skipped character sprite because no four-cell blank atlas run was available: " + entry.SpritePath);
                        return false;
                    }

                    Color[] spriteData = new Color[sprite.Width * sprite.Height];
                    sprite.GetData(spriteData);
                    for (int facing = 0; facing < 4; facing++)
                    {
                        int sourceCellX = isFacingStrip ? facing : 0;
                        CopySpriteCellToAtlas(spriteData, sprite.Width, atlasData, atlasWidth, sourceCellX, atlasCell.Value.X + facing, atlasCell.Value.Y);
                        claimedAtlasCells.Add(atlasCell.Value.Y * cellsPerRow + atlasCell.Value.X + facing);
                    }

                    entry.AssignedTexCoord = atlasCell.Value;
                    Log(string.Format("Assigned character sprite {0} to atlas cells {1}-{2},{3}.", entry.SpritePath, atlasCell.Value.X, atlasCell.Value.X + 3, atlasCell.Value.Y));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("Failed to merge character sprite " + entry.SpritePath + ": " + ex.Message);
                return false;
            }
        }

        private static bool MergeGunSprite(
            GraphicsDevice graphicsDevice,
            GunEntry entry,
            Color[] atlasData,
            int atlasWidth,
            int cellsPerRow,
            int cellRows,
            HashSet<int> claimedAtlasCells)
        {
            if (string.IsNullOrEmpty(entry.SpritePath))
            {
                return false;
            }

            try
            {
                using (FileStream stream = File.OpenRead(entry.SpritePath))
                using (Texture2D sprite = Texture2D.FromStream(graphicsDevice, stream))
                {
                    int sourceCells = sprite.Width / CellSize;
                    bool valid = sprite.Height == CellSize && (sourceCells == 1 || sourceCells == 2 || sourceCells == 8) && sprite.Width == sourceCells * CellSize;
                    if (!valid)
                    {
                        Log("Skipped gun sprite that is not 16x16, 32x16, or 128x16: " + entry.SpritePath);
                        return false;
                    }

                    Point? atlasCell = FindFreeSpriteRun(atlasData, atlasWidth, cellsPerRow, cellRows, 8, claimedAtlasCells);
                    if (!atlasCell.HasValue)
                    {
                        Log("Skipped gun sprite because no eight-cell blank atlas run was available: " + entry.SpritePath);
                        return false;
                    }

                    Color[] spriteData = new Color[sprite.Width * sprite.Height];
                    sprite.GetData(spriteData);
                    for (int cell = 0; cell < 8; cell++)
                    {
                        int sourceCellX = sourceCells == 8 ? cell : (sourceCells == 2 ? cell % 2 : 0);
                        CopySpriteCellToAtlas(spriteData, sprite.Width, atlasData, atlasWidth, sourceCellX, atlasCell.Value.X + cell, atlasCell.Value.Y);
                        claimedAtlasCells.Add(atlasCell.Value.Y * cellsPerRow + atlasCell.Value.X + cell);
                    }

                    entry.AssignedTexCoord = atlasCell.Value;
                    Log(string.Format("Assigned gun sprite {0} to atlas cells {1}-{2},{3}.", entry.SpritePath, atlasCell.Value.X, atlasCell.Value.X + 7, atlasCell.Value.Y));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("Failed to merge gun sprite " + entry.SpritePath + ": " + ex.Message);
                return false;
            }
        }

        private static bool MergeBulletSprite(
            GraphicsDevice graphicsDevice,
            BulletEntry entry,
            Color[] atlasData,
            int atlasWidth,
            int cellsPerRow,
            int cellRows,
            HashSet<int> claimedAtlasCells)
        {
            if (string.IsNullOrEmpty(entry.SpritePath))
            {
                return false;
            }

            try
            {
                using (FileStream stream = File.OpenRead(entry.SpritePath))
                using (Texture2D sprite = Texture2D.FromStream(graphicsDevice, stream))
                {
                    int sourceCells = sprite.Width / CellSize;
                    bool valid = sprite.Height == CellSize && (sourceCells == 1 || sourceCells == 2) && sprite.Width == sourceCells * CellSize;
                    if (!valid)
                    {
                        Log("Skipped bullet sprite that is not 16x16 or 32x16: " + entry.SpritePath);
                        return false;
                    }

                    bool pairMode = sourceCells == 2 && (string.IsNullOrWhiteSpace(entry.SpriteMode) || string.Equals(entry.SpriteMode.Trim(), "Pair", StringComparison.OrdinalIgnoreCase));
                    int runLength = pairMode ? 2 : 1;
                    Point? atlasCell = FindFreeSpriteRun(atlasData, atlasWidth, cellsPerRow, cellRows, runLength, claimedAtlasCells);
                    if (!atlasCell.HasValue)
                    {
                        Log("Skipped bullet sprite because no blank atlas cell was available: " + entry.SpritePath);
                        return false;
                    }

                    Color[] spriteData = new Color[sprite.Width * sprite.Height];
                    sprite.GetData(spriteData);
                    for (int cell = 0; cell < runLength; cell++)
                    {
                        CopySpriteCellToAtlas(spriteData, sprite.Width, atlasData, atlasWidth, cell, atlasCell.Value.X + cell, atlasCell.Value.Y);
                        claimedAtlasCells.Add(atlasCell.Value.Y * cellsPerRow + atlasCell.Value.X + cell);
                    }

                    entry.AssignedTexCoord = atlasCell.Value;
                    entry.AssignedCellCount = runLength;
                    Log(string.Format("Assigned bullet sprite {0} to atlas cell(s) {1}-{2},{3}.", entry.SpritePath, atlasCell.Value.X, atlasCell.Value.X + runLength - 1, atlasCell.Value.Y));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log("Failed to merge bullet sprite " + entry.SpritePath + ": " + ex.Message);
                return false;
            }
        }

        private static Point? FindFreeSpriteRun(Color[] textureData, int textureWidth, int cellsPerRow, int cellRows, int runLength, HashSet<int> claimedAtlasCells)
        {
            for (int y = 0; y < cellRows; y++)
            {
                for (int x = 0; x <= cellsPerRow - runLength; x++)
                {
                    bool available = true;
                    for (int offset = 0; offset < runLength; offset++)
                    {
                        int cellIndex = y * cellsPerRow + x + offset;
                        if (claimedAtlasCells.Contains(cellIndex) ||
                            IsReservedAtlasCell(x + offset, y, cellsPerRow, cellRows) ||
                            !IsBlankAtlasCell(textureData, textureWidth, x + offset, y))
                        {
                            available = false;
                            break;
                        }
                    }

                    if (available)
                    {
                        return new Point(x, y);
                    }
                }
            }

            return null;
        }

        private static bool IsReservedAtlasCell(int cellX, int cellY, int cellsPerRow, int cellRows)
        {
            return cellX == cellsPerRow - 1 && cellY == cellRows - 1;
        }

        private static bool IsBlankAtlasCell(Color[] textureData, int textureWidth, int cellX, int cellY)
        {
            int startX = cellX * CellSize;
            int startY = cellY * CellSize;
            for (int y = 0; y < CellSize; y++)
            {
                int row = (startY + y) * textureWidth + startX;
                for (int x = 0; x < CellSize; x++)
                {
                    if (textureData[row + x].A != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void CopySpriteCellToAtlas(Color[] spriteData, int spriteWidth, Color[] atlasData, int atlasWidth, int sourceCellX, int atlasCellX, int atlasCellY)
        {
            int sourceX = sourceCellX * CellSize;
            int destinationX = atlasCellX * CellSize;
            int destinationY = atlasCellY * CellSize;
            for (int y = 0; y < CellSize; y++)
            {
                Array.Copy(
                    spriteData,
                    y * spriteWidth + sourceX,
                    atlasData,
                    (destinationY + y) * atlasWidth + destinationX,
                    CellSize);
            }
        }

        private static Ze2ModManifest LoadManifest(string path)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(Ze2ModManifest));
            using (FileStream stream = File.OpenRead(path))
            {
                return (Ze2ModManifest)serializer.Deserialize(stream);
            }
        }

        private static CharacterSettings LoadCharacter(string path)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(CharacterSettings));
            using (FileStream stream = File.OpenRead(path))
            {
                return (CharacterSettings)serializer.Deserialize(stream);
            }
        }

        private static GunStats LoadGun(string path)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(GunStats));
            using (FileStream stream = File.OpenRead(path))
            {
                return (GunStats)serializer.Deserialize(stream);
            }
        }

        private static BulletStats LoadBullet(string path)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(BulletStats));
            using (FileStream stream = File.OpenRead(path))
            {
                return (BulletStats)serializer.Deserialize(stream);
            }
        }

        private static string ResolveModPath(string modDir, string relative)
        {
            string root = Path.GetFullPath(modDir);
            string combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return combined;
        }

        private static string FirstNonBlank(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string NormalizeContentAssetName(string relativePath)
        {
            string assetName = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (assetName.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase))
            {
                assetName = assetName.Substring(0, assetName.Length - 4);
            }

            return assetName.Replace(Path.DirectorySeparatorChar, '\\');
        }

        private static string ResolveGameRelativeDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || Path.IsPathRooted(directory))
            {
                return directory;
            }

            string currentRelative = Path.GetFullPath(directory);
            if (Directory.Exists(currentRelative))
            {
                return currentRelative;
            }

            return Path.Combine(GameRoot, directory);
        }

        private static bool IsSafePrefix(string prefix)
        {
            return !string.IsNullOrWhiteSpace(prefix) && SafePrefix.IsMatch(prefix);
        }

        private static int ClampSectorCount(int count)
        {
            if (count < 1)
            {
                return 2;
            }

            if (count > 3)
            {
                return 3;
            }

            return count;
        }

        private static int DetectSectorCount(string levelName)
        {
            if (!IsSafePrefix(levelName))
            {
                return 0;
            }

            string[] roots =
            {
                Path.Combine(GameRoot, "Levels"),
                Path.Combine(GameRoot, "Data", "Levels")
            };

            int count = 0;
            for (int i = 0; i < 3; i++)
            {
                bool exists = false;
                foreach (string root in roots)
                {
                    if (File.Exists(Path.Combine(root, levelName + i + ".xml")))
                    {
                        exists = true;
                        break;
                    }
                }

                if (exists)
                {
                    count = i + 1;
                }
            }

            return count;
        }

        private static void Log(string message)
        {
            try
            {
                SetupPaths();
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }

        private sealed class CharacterEntry
        {
            public string ModId;
            public string CharacterPath;
            public string SpritePath;
            public Point? AssignedTexCoord;
        }

        private sealed class GunEntry
        {
            public string ModId;
            public string GunPath;
            public string SpritePath;
            public Point? AssignedTexCoord;
        }

        private sealed class BulletEntry
        {
            public string ModId;
            public string BulletPath;
            public string SpritePath;
            public string SpriteMode;
            public Point? AssignedTexCoord;
            public int AssignedCellCount;
        }

        private sealed class MapAssetEntry
        {
            public string ModId;
            public string Prefix;
            public RuntimeTextureAsset Tilesheet;
            public RuntimeTextureAsset LightTexture;
            public bool HasMainOnTop;
            public bool MainOnTop;
            public bool HasDarkMod;
            public float DarkMod;
        }

        private sealed class ModDiscovery
        {
            public string Directory;
            public Ze2ModManifest Manifest;
            public string ModId;
            public int Order;
        }

        private sealed class RuntimeTextureAsset
        {
            public string Path;
            public bool IsXnb;
            public string ContentRoot;
            public string AssetName;
        }

        private sealed class ModLevelMenuAction
        {
            private readonly object Menu;
            private readonly string Prefix;

            public ModLevelMenuAction(object menu, string prefix)
            {
                Menu = menu;
                Prefix = prefix;
            }

            public void StartXbox()
            {
                FieldInfo nameField = GetField(Menu.GetType(), "name");
                MethodInfo startMethod = GetMethod(Menu.GetType(), "Start");
                if (nameField == null || startMethod == null)
                {
                    Log("Xbox custom map start skipped: Start/name unavailable for " + Prefix + ".");
                    return;
                }

                nameField.SetValue(Menu, Prefix);
                startMethod.Invoke(Menu, null);
                Log("Started custom map from XboxLevelSelect: " + Prefix + ".");
            }
        }
    }

    [XmlRoot("Ze2Mod")]
    public sealed class Ze2ModManifest
    {
        public string Id;
        public string Name;
        public bool Enabled = true;

        [XmlArrayItem("Character")]
        public CharacterManifest[] Characters;

        [XmlArrayItem("Gun")]
        public GunManifest[] Guns;

        [XmlArrayItem("Bullet")]
        public BulletManifest[] Bullets;

        [XmlArrayItem("Map")]
        public MapManifest[] Maps;
    }

    public sealed class CharacterManifest
    {
        [XmlAttribute]
        public string File;

        [XmlAttribute]
        public string Sprite;
    }

    public sealed class GunManifest
    {
        [XmlAttribute]
        public string File;

        [XmlAttribute]
        public string Sprite;
    }

    public sealed class BulletManifest
    {
        [XmlAttribute]
        public string File;

        [XmlAttribute]
        public string Sprite;

        [XmlAttribute]
        public string SpriteMode;
    }

    public sealed class MapManifest
    {
        [XmlAttribute]
        public string Prefix;

        [XmlAttribute]
        public string Folder;

        [XmlAttribute]
        public int SectorCount;

        [XmlAttribute]
        public string Tilesheet;

        [XmlAttribute]
        public string Texture;

        [XmlAttribute]
        public string AssetSheet;

        [XmlAttribute]
        public string LightTexture;

        [XmlAttribute]
        public string DarkMod;

        [XmlAttribute]
        public string MainOnTop;
    }
}
