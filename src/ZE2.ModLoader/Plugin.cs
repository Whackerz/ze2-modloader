using BepInEx;
using BepInEx.Logging;
using BepInEx.NET.Common;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Serialization;
using HarmonyLib;

namespace ZE2.ModLoader
{
    [BepInPlugin("ze2.modloader", "ZE2 ModLoader", "2.4.1")]
    public sealed class Plugin : BasePlugin
    {
        private ManualLogSource log;
        private string gameRoot;
        private string modsRoot;
        private string legacyModsRoot;
        private string characterBackupRoot;
        private string managedFilesStatePath;
        private string managedBackupRoot;
        private string generatedBridgeAssetsRoot;
        private HashSet<string> characterSlots;
        private HashSet<string> previousManagedFileTargets;
        private readonly HashSet<string> currentManagedFileTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Harmony harmony;
        private bool restoredCharacterSlotsThisRun;
        private readonly List<string> pendingInjectedCharacterFiles = new List<string>();
        private readonly List<string> pendingGunNames = new List<string>();
        private readonly List<string> pendingBulletNames = new List<string>();
        private readonly List<string> pendingSpriteBoundGunNames = new List<string>();
        private readonly List<string> pendingSpriteBoundBulletNames = new List<string>();
        private readonly List<LegacySpriteBinding> pendingCharacterSpriteBindings = new List<LegacySpriteBinding>();
        private readonly List<LegacySpriteBinding> pendingGunSpriteBindings = new List<LegacySpriteBinding>();
        private readonly List<LegacySpriteBinding> pendingBulletSpriteBindings = new List<LegacySpriteBinding>();
        private static readonly HashSet<string> BaseMapPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Estate",
            "School",
            "Mall",
            "Skyscraper",
            "DesertTown",
            "Office",
            "Farm"
        };

        private sealed class LegacySpriteBinding
        {
            public string DataPath;
            public string SpritePath;
            public string SpriteMode;
        }

        private sealed class MapBridgeEntry
        {
            public string Prefix;
            public int SectorCount;
        }

        public override void Load()
        {
            log = Log;
            gameRoot = Path.GetFullPath(AppContext.BaseDirectory);
            modsRoot = Path.Combine(gameRoot, "BepInEx", "plugins", "Mods");
            legacyModsRoot = Path.Combine(gameRoot, "Mods");
            characterBackupRoot = Path.Combine(modsRoot, "_CharacterBackups");
            managedFilesStatePath = Path.Combine(gameRoot, "BepInEx", "plugins", "ZE2.ModLoader.managedfiles.txt");
            managedBackupRoot = Path.Combine(modsRoot, "_ManagedFileBackups");
            generatedBridgeAssetsRoot = Path.Combine(modsRoot, "_GeneratedBridgeAssets");
            Directory.CreateDirectory(modsRoot);
            Directory.CreateDirectory(characterBackupRoot);
            Directory.CreateDirectory(managedBackupRoot);
            ResetGeneratedBridgeAssets();
            harmony = new Harmony("ze2.modloader.expansion");
            previousManagedFileTargets = LoadManagedFileState();

            characterSlots = LoadCharacterSlots();
            log.LogInfo($"ZE2 ModLoader v2.4.1 ready. XML roots: '{modsRoot}', '{legacyModsRoot}'.");
            log.LogInfo($"Character slot mode enabled ({characterSlots.Count} slots).");
            RestoreOriginalCharacterSlots();
            restoredCharacterSlotsThisRun = true;
            ApplyXmlModPacks();
            PruneStaleManagedFiles();
            SaveManagedFileState();
            CharacterExpansionPatches.Initialize(
                log,
                gameRoot,
                pendingInjectedCharacterFiles,
                pendingGunNames,
                pendingBulletNames,
                pendingSpriteBoundGunNames,
                pendingSpriteBoundBulletNames);
            CharacterExpansionPatches.Install(harmony);
            ParityFeatures.Install(log);
            LoadDllMods();
        }

        private HashSet<string> LoadCharacterSlots()
        {
            var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var charsDir = Path.Combine(gameRoot, "Data", "Characters");
            if (!Directory.Exists(charsDir))
                return slots;

            foreach (var path in Directory.GetFiles(charsDir))
            {
                var ext = Path.GetExtension(path);
                if (ext.Equals(".chr", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".char", StringComparison.OrdinalIgnoreCase))
                {
                    slots.Add(Path.GetFileName(path));
                }
            }

            return slots;
        }

        private void ApplyXmlModPacks()
        {
            var manifests = EnumerateManifestPaths().ToArray();
            if (manifests.Length == 0)
            {
                log.LogInfo("No XML mod packs found.");
                return;
            }

            log.LogInfo($"Discovered {manifests.Length} XML manifest(s).");
            foreach (var manifest in manifests)
            {
                try
                {
                    ApplySingleXmlModPack(manifest);
                }
                catch (Exception ex)
                {
                    log.LogError($"Failed processing '{manifest}': {ex.Message}");
                }
            }
        }

        private IEnumerable<string> EnumerateManifestPaths()
        {
            var roots = new[]
            {
                modsRoot,
                legacyModsRoot
            };

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
            {
                foreach (var manifest in Directory.GetFiles(root, "mod.xml", SearchOption.AllDirectories))
                {
                    var packDir = Path.GetDirectoryName(manifest) ?? string.Empty;
                    if (packDir.EndsWith("ZE2_SpriteBridge", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (packDir.EndsWith("ZE2_MapBridge", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (packDir.EndsWith("_CharacterBackups", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!seen.Add(Path.GetFullPath(manifest)))
                        continue;
                    yield return manifest;
                }
            }
        }

        private void ApplySingleXmlModPack(string manifestPath)
        {
            var packDir = Path.GetDirectoryName(manifestPath) ?? modsRoot;
            var doc = LoadManifestXmlWithRecovery(manifestPath);
            var root = doc.Root;
            if (root == null)
            {
                log.LogWarning($"Skipping invalid manifest root: {manifestPath}");
                return;
            }

            if (string.Equals(root.Name.LocalName, "Ze2XmlMod", StringComparison.OrdinalIgnoreCase))
            {
                ApplyZe2XmlManifest(packDir, root);
                return;
            }

            if (string.Equals(root.Name.LocalName, "Ze2Mod", StringComparison.OrdinalIgnoreCase))
            {
                ApplyLegacyZe2Manifest(packDir, root);
                return;
            }

            log.LogWarning($"Skipping unsupported manifest root '{root.Name.LocalName}': {manifestPath}");
        }

        private XDocument LoadManifestXmlWithRecovery(string manifestPath)
        {
            try
            {
                return XDocument.Load(manifestPath);
            }
            catch (Exception firstEx)
            {
                var raw = File.ReadAllText(manifestPath);
                if (!TryExtractSingleRootXml(raw, out var recoveredXml))
                    throw;

                try
                {
                    var recovered = XDocument.Parse(recoveredXml);
                    log.LogWarning($"Recovered malformed manifest '{manifestPath}' by trimming trailing non-XML content ({firstEx.Message}).");
                    return recovered;
                }
                catch
                {
                    throw;
                }
            }
        }

        private static bool TryExtractSingleRootXml(string raw, out string xml)
        {
            xml = string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var content = raw;
            var start = content.IndexOf('<');
            if (start < 0)
                return false;
            content = content.Substring(start);

            while (true)
            {
                var decl = Regex.Match(content, @"^\s*<\?xml\b[^>]*\?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!decl.Success)
                    break;
                content = content.Substring(decl.Length);
            }

            var rootOpen = Regex.Match(content, @"<\s*([A-Za-z_][\w\.\-:]*)\b[^>]*>", RegexOptions.Singleline);
            if (!rootOpen.Success)
                return false;

            var rootName = rootOpen.Groups[1].Value;
            var closeTag = $"</{rootName}>";
            var closeIndex = content.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
            if (closeIndex < 0)
                return false;

            var end = closeIndex + closeTag.Length;
            xml = content.Substring(0, end);
            return true;
        }

        private void ApplyZe2XmlManifest(string packDir, XElement root)
        {
            var modName = GetAttributeValue(root, "name");
            if (string.IsNullOrWhiteSpace(modName))
                modName = Path.GetFileName(packDir);
            var enabled = ParseBool(GetAttributeValue(root, "enabled"), true);
            if (!enabled)
            {
                log.LogInfo($"XML mod disabled: {modName}");
                return;
            }

            var applied = 0;
            var filesNode = root.Element("Files");
            if (filesNode != null)
            {
                foreach (var fileNode in filesNode.Elements("File"))
                {
                    var source = ((string)fileNode.Attribute("source")) ?? string.Empty;
                    var target = ((string)fileNode.Attribute("target")) ?? string.Empty;
                    applied += ApplyRawFile(modName, packDir, source, target, allowCharacterTarget: false);
                }
            }

            applied += ApplyTypedFiles(modName, packDir, root, "Guns", "Data/Guns", ".gun");
            applied += ApplyTypedFiles(modName, packDir, root, "Bullets", "Data/Bullets", ".bul");
            applied += ApplyImplicitTypedFiles(modName, packDir, "Data/Guns", ".gun", pendingGunNames);
            applied += ApplyImplicitTypedFiles(modName, packDir, "Data/Bullets", ".bul", pendingBulletNames);
            applied += ApplyCharacterReplacements(modName, packDir, root);
            applied += ApplyMaps(modName, packDir, root);

            log.LogInfo($"XML mod '{modName}' applied {applied} file(s).");
        }

        private void ApplyLegacyZe2Manifest(string packDir, XElement root)
        {
            var modName = GetElementValue(root, "Name");
            if (string.IsNullOrWhiteSpace(modName))
                modName = GetAttributeValue(root, "name");
            if (string.IsNullOrWhiteSpace(modName))
                modName = Path.GetFileName(packDir);

            var enabledText = GetElementValue(root, "Enabled");
            if (string.IsNullOrWhiteSpace(enabledText))
                enabledText = GetAttributeValue(root, "enabled");
            var enabled = ParseBool(enabledText, true);
            if (!enabled)
            {
                log.LogInfo($"Legacy XML mod disabled: {modName}");
                return;
            }

            var applied = 0;
            applied += ApplyLegacyTypedFiles(modName, packDir, root, "Guns", "Gun", "Data/Guns", ".gun");
            applied += ApplyLegacyTypedFiles(modName, packDir, root, "Bullets", "Bullet", "Data/Bullets", ".bul");
            applied += ApplyImplicitTypedFiles(modName, packDir, "Data/Guns", ".gun", pendingGunNames);
            applied += ApplyImplicitTypedFiles(modName, packDir, "Data/Bullets", ".bul", pendingBulletNames);
            applied += ApplyLegacyCharacters(modName, packDir, root);
            applied += ApplyMaps(modName, packDir, root);

            log.LogInfo($"Legacy XML mod '{modName}' applied {applied} file(s).");
        }

        private int ApplyTypedFiles(string modName, string packDir, XElement root, string sectionName, string targetDir, string ext)
        {
            var section = GetChildElement(root, sectionName);
            if (section == null)
                return 0;

            var applied = 0;
            foreach (var addNode in EnumerateSectionEntries(section, "Add"))
            {
                var sourceRel = GetAttributeValue(addNode, "source");
                var name = GetAttributeValue(addNode, "name");
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileName(sourceRel);
                var spriteRel = GetAttributeValue(addNode, "sprite");
                var spriteMode = GetAttributeValue(addNode, "spriteMode");
                if (string.IsNullOrWhiteSpace(sourceRel) || string.IsNullOrWhiteSpace(name))
                    continue;

                if (!string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning($"[{modName}] {sectionName} skipped (extension mismatch): {name}");
                    continue;
                }

                if (string.Equals(sectionName, "Guns", StringComparison.OrdinalIgnoreCase))
                {
                    var hasSprite = !string.IsNullOrWhiteSpace(spriteRel);
                    if (hasSprite)
                    {
                        var bound = TryRegisterLegacySpriteBinding(pendingGunSpriteBindings, packDir, sourceRel, spriteRel, spriteMode);
                        if (bound)
                        {
                            pendingSpriteBoundGunNames.Add(Path.GetFileNameWithoutExtension(name));
                        }
                    }

                    var targetRel = Path.Combine(targetDir, name).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: false);
                    AddPendingName(pendingGunNames, Path.GetFileNameWithoutExtension(name));
                }
                else if (string.Equals(sectionName, "Bullets", StringComparison.OrdinalIgnoreCase))
                {
                    var hasSprite = !string.IsNullOrWhiteSpace(spriteRel);
                    if (hasSprite)
                    {
                        var bound = TryRegisterLegacySpriteBinding(pendingBulletSpriteBindings, packDir, sourceRel, spriteRel, spriteMode);
                        if (bound)
                        {
                            pendingSpriteBoundBulletNames.Add(Path.GetFileNameWithoutExtension(name));
                        }
                    }

                    var targetRel = Path.Combine(targetDir, name).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: false);
                    AddPendingName(pendingBulletNames, Path.GetFileNameWithoutExtension(name));
                }
                else
                {
                    var targetRel = Path.Combine(targetDir, name).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: false);
                }
            }

            return applied;
        }

        private int ApplyCharacterReplacements(string modName, string packDir, XElement root)
        {
            var section = GetChildElement(root, "Characters");
            if (section == null)
                return 0;

            var applied = 0;
            foreach (var addNode in EnumerateSectionEntries(section, "Add", "Character"))
            {
                var sourceRel = GetAttributeValue(addNode, "source", "file");
                var slot = GetAttributeValue(addNode, "slot");
                var spriteRel = GetAttributeValue(addNode, "sprite");
                if (string.IsNullOrWhiteSpace(sourceRel))
                    continue;

                if (string.IsNullOrWhiteSpace(slot))
                {
                    // Expansion mode: stage as additional runtime character without replacing base slots.
                    var addSourcePath = Path.GetFullPath(Path.Combine(packDir, sourceRel));
                    if (!addSourcePath.StartsWith(packDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(addSourcePath))
                    {
                        log.LogWarning($"[{modName}] Character add skipped: source not found ({sourceRel}).");
                        continue;
                    }
                    pendingInjectedCharacterFiles.Add(addSourcePath);
                    log.LogInfo($"[{modName}] Staged runtime character add: {Path.GetFileName(addSourcePath)}");
                    TryRegisterLegacySpriteBinding(pendingCharacterSpriteBindings, packDir, sourceRel, spriteRel, string.Empty);
                    continue;
                }

                var slotFile = NormalizeSlotFile(slot);
                if (!characterSlots.Contains(slotFile))
                {
                    log.LogWarning($"[{modName}] Character slot not found in base game: {slotFile}");
                    continue;
                }

                if (!restoredCharacterSlotsThisRun)
                {
                    RestoreOriginalCharacterSlots();
                    restoredCharacterSlotsThisRun = true;
                }

                var sourceExt = Path.GetExtension(sourceRel);
                if (!(sourceExt.Equals(".chr", StringComparison.OrdinalIgnoreCase) || sourceExt.Equals(".char", StringComparison.OrdinalIgnoreCase)))
                {
                    log.LogWarning($"[{modName}] Character source skipped (extension mismatch): {sourceRel}");
                    continue;
                }

                var targetRel = Path.Combine("Data/Characters", slotFile).Replace('\\', '/');
                BackupOriginalCharacterSlot(slotFile);
                applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: true);
            }

            return applied;
        }

        private int ApplyMaps(string modName, string packDir, XElement root)
        {
            log.LogInfo($"[{modName}] Map staging skipped; maps are loaded directly from Mods by the integrated loader.");
            return 0;

            var maps = GetChildElement(root, "Maps");
            if (maps == null)
                return 0;

            var applied = 0;
            foreach (var addNode in EnumerateSectionEntries(maps, "Add"))
            {
                var source = GetAttributeValue(addNode, "source");
                var target = GetAttributeValue(addNode, "target");
                if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(target))
                {
                    applied += ApplyRawFile(modName, packDir, source, target, allowCharacterTarget: false);
                    continue;
                }

                var sourceDirRel = GetAttributeValue(addNode, "sourceDir");
                var targetDirRel = GetAttributeValue(addNode, "targetDir");
                if (string.IsNullOrWhiteSpace(targetDirRel))
                    targetDirRel = "Data/Levels";
                if (string.IsNullOrWhiteSpace(sourceDirRel))
                    continue;

                var sourceDir = Path.GetFullPath(Path.Combine(packDir, sourceDirRel));
                if (!sourceDir.StartsWith(packDir, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(sourceDir))
                    continue;

                foreach (var sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
                {
                    var relative = GetRelativePathPortable(sourceDir, sourceFile).Replace('\\', '/');
                    var sourceRelFromPack = GetRelativePathPortable(packDir, sourceFile).Replace('\\', '/');
                    var targetRel = Path.Combine(targetDirRel, relative).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRelFromPack, targetRel, allowCharacterTarget: false);
                }
            }

            foreach (var mapNode in EnumerateSectionEntries(maps, "Map"))
            {
                var prefix = GetAttributeValue(mapNode, "Prefix", "prefix");
                if (string.IsNullOrWhiteSpace(prefix))
                    continue;

                var folder = GetAttributeValue(mapNode, "Folder", "folder");
                if (string.IsNullOrWhiteSpace(folder))
                    folder = $"Maps/{prefix}";

                var sourceDir = Path.GetFullPath(Path.Combine(packDir, folder.Replace('/', Path.DirectorySeparatorChar)));
                if (!sourceDir.StartsWith(packDir, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(sourceDir))
                {
                    log.LogWarning($"[{modName}] Map folder not found for prefix '{prefix}': {folder}");
                    continue;
                }

                foreach (var sourceFile in Directory.GetFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(sourceFile);
                    if (string.IsNullOrWhiteSpace(fileName))
                        continue;
                    if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sourceRelFromPack = GetRelativePathPortable(packDir, sourceFile).Replace('\\', '/');
                    var targetRel = Path.Combine("Data/Levels", fileName).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRelFromPack, targetRel, allowCharacterTarget: false);
                }
            }

            return applied;
        }

        private int ApplyLegacyTypedFiles(string modName, string packDir, XElement root, string sectionName, string entryName, string targetDir, string ext)
        {
            var section = GetChildElement(root, sectionName);
            if (section == null)
                return 0;

            var applied = 0;
            foreach (var node in EnumerateSectionEntries(section, entryName, "Add"))
            {
                var sourceRel = GetAttributeValue(node, "file", "source");
                var name = GetAttributeValue(node, "name");
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileName(sourceRel);
                var spriteRel = GetAttributeValue(node, "sprite");
                var spriteMode = GetAttributeValue(node, "spriteMode");
                if (string.IsNullOrWhiteSpace(sourceRel) || string.IsNullOrWhiteSpace(name))
                    continue;

                if (!Path.HasExtension(name))
                    name = name + ext;
                if (!string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning($"[{modName}] {sectionName} skipped (extension mismatch): {name}");
                    continue;
                }

                if (string.Equals(sectionName, "Guns", StringComparison.OrdinalIgnoreCase))
                {
                    var hasSprite = !string.IsNullOrWhiteSpace(spriteRel);
                    if (hasSprite)
                    {
                        var bound = TryRegisterLegacySpriteBinding(pendingGunSpriteBindings, packDir, sourceRel, spriteRel, spriteMode);
                        if (bound)
                        {
                            pendingSpriteBoundGunNames.Add(Path.GetFileNameWithoutExtension(name));
                        }
                    }

                    var targetRel = Path.Combine(targetDir, name).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: false);
                    AddPendingName(pendingGunNames, Path.GetFileNameWithoutExtension(name));
                }
                else if (string.Equals(sectionName, "Bullets", StringComparison.OrdinalIgnoreCase))
                {
                    var hasSprite = !string.IsNullOrWhiteSpace(spriteRel);
                    if (hasSprite)
                    {
                        var bound = TryRegisterLegacySpriteBinding(pendingBulletSpriteBindings, packDir, sourceRel, spriteRel, spriteMode);
                        if (bound)
                        {
                            pendingSpriteBoundBulletNames.Add(Path.GetFileNameWithoutExtension(name));
                        }
                    }

                    var targetRel = Path.Combine(targetDir, name).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: false);
                    AddPendingName(pendingBulletNames, Path.GetFileNameWithoutExtension(name));
                }
                else
                {
                    var targetRel = Path.Combine(targetDir, name).Replace('\\', '/');
                    applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: false);
                }
            }

            return applied;
        }

        private int ApplyLegacyCharacters(string modName, string packDir, XElement root)
        {
            var section = GetChildElement(root, "Characters");
            if (section == null)
                return 0;

            var applied = 0;
            foreach (var node in EnumerateSectionEntries(section, "Character", "Add"))
            {
                var sourceRel = GetAttributeValue(node, "file", "source");
                var slot = GetAttributeValue(node, "slot");
                var spriteRel = GetAttributeValue(node, "sprite");
                if (string.IsNullOrWhiteSpace(sourceRel))
                    continue;

                if (string.IsNullOrWhiteSpace(slot))
                {
                    var addSourcePath = Path.GetFullPath(Path.Combine(packDir, sourceRel));
                    if (!addSourcePath.StartsWith(packDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(addSourcePath))
                    {
                        log.LogWarning($"[{modName}] Character add skipped: source not found ({sourceRel}).");
                        continue;
                    }

                    pendingInjectedCharacterFiles.Add(addSourcePath);
                    log.LogInfo($"[{modName}] Staged runtime character add: {Path.GetFileName(addSourcePath)}");
                    TryRegisterLegacySpriteBinding(pendingCharacterSpriteBindings, packDir, sourceRel, spriteRel, string.Empty);
                    continue;
                }

                var slotFile = NormalizeSlotFile(slot);
                if (!characterSlots.Contains(slotFile))
                {
                    log.LogWarning($"[{modName}] Character slot not found in base game: {slotFile}");
                    continue;
                }

                if (!restoredCharacterSlotsThisRun)
                {
                    RestoreOriginalCharacterSlots();
                    restoredCharacterSlotsThisRun = true;
                }

                var sourceExt = Path.GetExtension(sourceRel);
                if (!(sourceExt.Equals(".chr", StringComparison.OrdinalIgnoreCase) || sourceExt.Equals(".char", StringComparison.OrdinalIgnoreCase)))
                {
                    log.LogWarning($"[{modName}] Character source skipped (extension mismatch): {sourceRel}");
                    continue;
                }

                var targetRel = Path.Combine("Data/Characters", slotFile).Replace('\\', '/');
                BackupOriginalCharacterSlot(slotFile);
                applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: true);
            }

            return applied;
        }

        private int ApplyRawFile(string modName, string packDir, string sourceRel, string targetRel, bool allowCharacterTarget)
        {
            log.LogInfo($"[{modName}] Raw file staging skipped for '{targetRel}'; content remains in the mod folder.");
            return 0;

            if (string.IsNullOrWhiteSpace(sourceRel) || string.IsNullOrWhiteSpace(targetRel))
                return 0;
            if (targetRel.Contains(".."))
                return 0;

            var normalizedTarget = targetRel.Replace('\\', '/');
            var isCharacterTarget = normalizedTarget.StartsWith("Data/Characters/", StringComparison.OrdinalIgnoreCase);
            if (isCharacterTarget && !allowCharacterTarget)
            {
                log.LogWarning($"[{modName}] Character writes require <Characters> slot replacement. Skipped: {normalizedTarget}");
                return 0;
            }

            var sourcePath = Path.GetFullPath(Path.Combine(packDir, sourceRel));
            if (!sourcePath.StartsWith(packDir, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (!File.Exists(sourcePath))
            {
                log.LogWarning($"[{modName}] Missing source file: {sourceRel}");
                return 0;
            }

            var targetPath = Path.GetFullPath(Path.Combine(gameRoot, normalizedTarget.Replace('/', Path.DirectorySeparatorChar)));
            if (!targetPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                return 0;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? gameRoot);
            var existedBefore = File.Exists(targetPath);
            if (existedBefore)
                BackupManagedOriginal(targetPath);
            File.Copy(sourcePath, targetPath, true);
            TrackManagedTargetPath(targetPath);
            StageRuntimeRegistryFromTargetPath(normalizedTarget);
            log.LogInfo($"[{modName}] Applied: {sourceRel} -> {normalizedTarget}");
            return 1;
        }

        private int ApplyImplicitTypedFiles(string modName, string packDir, string targetDir, string ext, List<string> pendingNames)
        {
            if (string.IsNullOrWhiteSpace(packDir) || !Directory.Exists(packDir))
                return 0;

            var applied = 0;
            var discovered = 0;
            foreach (var sourcePath in Directory.GetFiles(packDir, "*" + ext, SearchOption.AllDirectories))
            {
                if (!sourcePath.StartsWith(packDir, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fileName = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                var stem = Path.GetFileNameWithoutExtension(fileName);
                if (!AddPendingName(pendingNames, stem))
                    continue;

                discovered++;
                var sourceRel = GetRelativePathPortable(packDir, sourcePath).Replace('\\', '/');
                var targetRel = Path.Combine(targetDir, fileName).Replace('\\', '/');
                applied += ApplyRawFile(modName, packDir, sourceRel, targetRel, allowCharacterTarget: false);
            }

            if (discovered > 0)
                log.LogInfo($"[{modName}] Auto-discovered {discovered} unlisted '{ext}' file(s) for runtime/shop loading.");

            return applied;
        }

        private void StageRuntimeRegistryFromTargetPath(string normalizedTarget)
        {
            if (string.IsNullOrWhiteSpace(normalizedTarget))
                return;

            var target = normalizedTarget.Replace('\\', '/');
            if (target.StartsWith("Data/Guns/", StringComparison.OrdinalIgnoreCase) &&
                target.EndsWith(".gun", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(target);
                if (AddPendingName(pendingGunNames, stem))
                    log.LogInfo($"Staged runtime gun registry entry: {stem}");
                return;
            }

            if (target.StartsWith("Data/Bullets/", StringComparison.OrdinalIgnoreCase) &&
                target.EndsWith(".bul", StringComparison.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(target);
                if (AddPendingName(pendingBulletNames, stem))
                    log.LogInfo($"Staged runtime bullet registry entry: {stem}");
            }
        }

        private static bool AddPendingName(List<string> target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name))
                return false;

            var trimmed = name.Trim();
            if (trimmed.Length == 0)
                return false;

            if (target.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)))
                return false;

            target.Add(trimmed);
            return true;
        }

        private bool TryRegisterLegacySpriteBinding(List<LegacySpriteBinding> targetList, string packDir, string dataSourceRel, string spriteRel, string spriteMode)
        {
            if (targetList == null || string.IsNullOrWhiteSpace(dataSourceRel) || string.IsNullOrWhiteSpace(spriteRel))
                return false;

            var dataPath = ResolveExistingPath(packDir, dataSourceRel);
            var spritePath = ResolveExistingPath(packDir, spriteRel, ".png", ".jpg", ".jpeg", ".bmp");
            if (ReferenceEquals(targetList, pendingCharacterSpriteBindings))
                spritePath = PrepareCharacterSpriteForBridge(spritePath);
            if (ReferenceEquals(targetList, pendingGunSpriteBindings))
                spritePath = PrepareGunSpriteForBridge(spritePath);
            if (string.IsNullOrWhiteSpace(dataPath) || string.IsNullOrWhiteSpace(spritePath))
            {
                log.LogWarning($"Legacy sprite binding skipped (missing file): data='{dataSourceRel}', sprite='{spriteRel}'.");
                return false;
            }
            if (!dataPath.StartsWith(packDir, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!spritePath.StartsWith(packDir, StringComparison.OrdinalIgnoreCase) &&
                !spritePath.StartsWith(generatedBridgeAssetsRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            targetList.Add(new LegacySpriteBinding
            {
                DataPath = dataPath,
                SpritePath = spritePath,
                SpriteMode = spriteMode ?? string.Empty
            });
            return true;
        }

        private void BuildLegacySpriteBridgeMod()
        {
            try
            {
                var hasSprites = pendingCharacterSpriteBindings.Count > 0 ||
                                 pendingGunSpriteBindings.Count > 0 ||
                                 pendingBulletSpriteBindings.Count > 0;
                if (!hasSprites)
                {
                    log.LogInfo("No sprite bindings staged; legacy sprite bridge remains empty.");
                    return;
                }

                EnsureLegacySpriteLoaderDllExists();

                var roots = GetBridgeRoots();
                var rootBridge = roots[0];
                var legacyPluginBridge = roots[1];
                var built = 0;
                if (BuildLegacySpriteBridgeModAt(rootBridge))
                    built++;
                if (BuildLegacySpriteBridgeModAt(legacyPluginBridge))
                    built++;

                if (built > 0)
                {
                    log.LogInfo($"Built legacy sprite bridge mod at {built}/2 target(s) with " +
                        $"{pendingCharacterSpriteBindings.Count} character sprite(s), " +
                        $"{pendingGunSpriteBindings.Count} gun sprite(s), " +
                        $"{pendingBulletSpriteBindings.Count} bullet sprite(s).");
                }
                else
                {
                    log.LogWarning("Legacy sprite bridge rebuild failed for all targets; previous bridge contents were preserved.");
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed building legacy sprite bridge mod: {ex.Message}");
            }
        }

        private bool BuildLegacySpriteBridgeModAt(string bridgeRoot)
        {
            var stagingRoot = bridgeRoot + "__staging";
            var backupRoot = bridgeRoot + "__backup";
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, true);
                Directory.CreateDirectory(stagingRoot);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed preparing staging sprite bridge folder '{stagingRoot}': {ex.Message}");
                return false;
            }

            var charsDir = Path.Combine(stagingRoot, "Characters");
            var gunsDir = Path.Combine(stagingRoot, "Guns");
            var bulletsDir = Path.Combine(stagingRoot, "Bullets");
            var spritesDir = Path.Combine(stagingRoot, "Sprites");
            Directory.CreateDirectory(charsDir);
            Directory.CreateDirectory(gunsDir);
            Directory.CreateDirectory(bulletsDir);
            Directory.CreateDirectory(spritesDir);

            var charsNode = new XElement("Characters");
            var gunsNode = new XElement("Guns");
            var bulletsNode = new XElement("Bullets");
            var spriteNameUniqueness = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var binding in pendingCharacterSpriteBindings)
                {
                    if (!TryCopyBindingFiles(binding, charsDir, spritesDir, spriteNameUniqueness, out var dataRel, out var spriteRelOut))
                        continue;
                    charsNode.Add(new XElement("Character",
                        new XAttribute("File", dataRel),
                        new XAttribute("Sprite", spriteRelOut)));
                }

                foreach (var binding in pendingGunSpriteBindings)
                {
                    if (!TryCopyBindingFiles(binding, gunsDir, spritesDir, spriteNameUniqueness, out var dataRel, out var spriteRelOut))
                        continue;
                    gunsNode.Add(new XElement("Gun",
                        new XAttribute("File", dataRel),
                        new XAttribute("Sprite", spriteRelOut)));
                }

                foreach (var binding in pendingBulletSpriteBindings)
                {
                    if (!TryCopyBindingFiles(binding, bulletsDir, spritesDir, spriteNameUniqueness, out var dataRel, out var spriteRelOut))
                        continue;
                    var elem = new XElement("Bullet",
                        new XAttribute("File", dataRel),
                        new XAttribute("Sprite", spriteRelOut));
                if (!string.IsNullOrWhiteSpace(binding.SpriteMode))
                    elem.Add(new XAttribute("SpriteMode", binding.SpriteMode));
                bulletsNode.Add(elem);
            }

            var manifest = new XElement("Ze2Mod",
                new XElement("Id", "ze2_sprite_bridge"),
                new XElement("Name", "ZE2 Sprite Bridge"),
                new XElement("Enabled", "true"),
                charsNode.HasElements ? charsNode : null,
                gunsNode.HasElements ? gunsNode : null,
                bulletsNode.HasElements ? bulletsNode : null
            );
            new XDocument(manifest).Save(Path.Combine(stagingRoot, "mod.xml"));

            try
            {
                if (Directory.Exists(backupRoot))
                    Directory.Delete(backupRoot, true);

                if (Directory.Exists(bridgeRoot))
                    Directory.Move(bridgeRoot, backupRoot);

                Directory.Move(stagingRoot, bridgeRoot);

                if (Directory.Exists(backupRoot))
                {
                    try
                    {
                        Directory.Delete(backupRoot, true);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning($"Failed removing sprite bridge backup '{backupRoot}': {ex.Message}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed swapping staged sprite bridge into '{bridgeRoot}': {ex.Message}");
                try
                {
                    if (Directory.Exists(stagingRoot))
                        Directory.Delete(stagingRoot, true);
                }
                catch
                {
                }

                try
                {
                    if (!Directory.Exists(bridgeRoot) && Directory.Exists(backupRoot))
                        Directory.Move(backupRoot, bridgeRoot);
                }
                catch
                {
                }

                return false;
            }
        }

        private static bool TryCopyBindingFiles(
            LegacySpriteBinding binding,
            string targetDataDir,
            string targetSpriteDir,
            HashSet<string> spriteNameUniqueness,
            out string dataRelPath,
            out string spriteRelPath)
        {
            dataRelPath = string.Empty;
            spriteRelPath = string.Empty;
            if (binding == null || !File.Exists(binding.DataPath) || !File.Exists(binding.SpritePath))
                return false;

            try
            {
                Directory.CreateDirectory(targetDataDir);
                Directory.CreateDirectory(targetSpriteDir);

                var dataName = Path.GetFileName(binding.DataPath);
                if (string.IsNullOrWhiteSpace(dataName))
                    return false;
                var dataOut = Path.Combine(targetDataDir, dataName);
                File.Copy(binding.DataPath, dataOut, true);
                dataRelPath = Path.Combine(Path.GetFileName(targetDataDir), dataName).Replace('\\', '/');

                var spriteName = Path.GetFileName(binding.SpritePath);
                if (string.IsNullOrWhiteSpace(spriteName))
                    return false;
                var baseName = Path.GetFileNameWithoutExtension(spriteName);
                var ext = Path.GetExtension(spriteName);
                var unique = spriteName;
                var i = 2;
                while (!spriteNameUniqueness.Add(unique))
                {
                    unique = $"{baseName}_{i}{ext}";
                    i++;
                }

                var spriteOut = Path.Combine(targetSpriteDir, unique);
                File.Copy(binding.SpritePath, spriteOut, true);
                spriteRelPath = Path.Combine("Sprites", unique).Replace('\\', '/');
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureLegacySpriteLoaderDllExists()
        {
            // No-op after source merge: ModBootstrap is compiled into ZE2.ModLoader.dll.
        }

        private void LoadDllMods()
        {
            var dlls = Directory.GetFiles(modsRoot, "*.dll", SearchOption.TopDirectoryOnly);
            if (dlls.Length == 0)
                return;

            foreach (var dll in dlls)
            {
                try
                {
                    var asm = System.Reflection.Assembly.LoadFrom(dll);
                    foreach (var t in asm.GetTypes().Where(t => typeof(IZe2Mod).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface))
                    {
                        var mod = (IZe2Mod)Activator.CreateInstance(t);
                        mod.OnLoad();
                        log.LogInfo($"Loaded mod: {mod.Name} v{mod.Version}");
                    }
                }
                catch (Exception ex)
                {
                    log.LogError($"DLL mod load failed ({Path.GetFileName(dll)}): {ex.Message}");
                }
            }
        }

        private void BackupOriginalCharacterSlot(string slotFile)
        {
            try
            {
                var source = Path.Combine(gameRoot, "Data", "Characters", slotFile);
                if (!File.Exists(source))
                    return;

                var backup = Path.Combine(characterBackupRoot, slotFile);
                if (File.Exists(backup))
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(backup) ?? characterBackupRoot);
                File.Copy(source, backup, false);
                log.LogInfo($"Backed up base character slot: {slotFile}");
            }
            catch (Exception ex)
            {
                log.LogWarning($"Backup failed for character slot {slotFile}: {ex.Message}");
            }
        }

        private void RestoreOriginalCharacterSlots()
        {
            try
            {
                if (!Directory.Exists(characterBackupRoot))
                    return;

                var restored = 0;
                foreach (var backup in Directory.GetFiles(characterBackupRoot, "*.chr", SearchOption.TopDirectoryOnly)
                    .Concat(Directory.GetFiles(characterBackupRoot, "*.char", SearchOption.TopDirectoryOnly)))
                {
                    var slotFile = Path.GetFileName(backup);
                    var target = Path.Combine(gameRoot, "Data", "Characters", slotFile);
                    if (!File.Exists(target))
                        continue;
                    File.Copy(backup, target, true);
                    restored++;
                }

                if (restored > 0)
                    log.LogInfo($"Restored {restored} base character slot(s) before applying mods.");
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed restoring base character slots: {ex.Message}");
            }
        }

        private static string NormalizeSlotFile(string slot)
        {
            var trimmed = slot.Trim();
            if (trimmed.EndsWith(".chr", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(trimmed);
            return Path.GetFileName(trimmed + ".chr");
        }

        private void TrackManagedTargetPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
                return;

            var normalized = Path.GetFullPath(targetPath);
            if (!normalized.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                return;
            if (!IsManagedCandidatePath(normalized))
                return;

            currentManagedFileTargets.Add(normalized);
        }

        private bool IsManagedCandidatePath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;
            if (!fullPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            var dataRoot = Path.Combine(gameRoot, "Data") + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            if (fullPath.StartsWith(Path.Combine(gameRoot, "Data", "Characters") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        private string GetManagedBackupPath(string targetPath)
        {
            var relative = GetRelativePathPortable(gameRoot, targetPath);
            return Path.Combine(managedBackupRoot, relative);
        }

        private void BackupManagedOriginal(string targetPath)
        {
            try
            {
                if (!IsManagedCandidatePath(targetPath))
                    return;
                if (!File.Exists(targetPath))
                    return;

                var backupPath = GetManagedBackupPath(targetPath);
                if (File.Exists(backupPath))
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(backupPath) ?? managedBackupRoot);
                File.Copy(targetPath, backupPath, false);
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed backing up managed target '{targetPath}': {ex.Message}");
            }
        }

        private HashSet<string> LoadManagedFileState()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(managedFilesStatePath))
                    return set;

                foreach (var line in File.ReadAllLines(managedFilesStatePath))
                {
                    var trimmed = line?.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;
                    var full = Path.GetFullPath(trimmed);
                    if (!full.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    set.Add(full);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to load managed file state: {ex.Message}");
            }

            return set;
        }

        private void SaveManagedFileState()
        {
            try
            {
                var dir = Path.GetDirectoryName(managedFilesStatePath) ?? gameRoot;
                Directory.CreateDirectory(dir);
                File.WriteAllLines(managedFilesStatePath, currentManagedFileTargets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed to save managed file state: {ex.Message}");
            }
        }

        private void PruneStaleManagedFiles()
        {
            try
            {
                if (previousManagedFileTargets == null || previousManagedFileTargets.Count == 0)
                    return;

                var removed = 0;
                var restored = 0;
                foreach (var prior in previousManagedFileTargets)
                {
                    if (currentManagedFileTargets.Contains(prior))
                        continue;
                    if (!prior.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!IsManagedCandidatePath(prior))
                        continue;

                    var backupPath = GetManagedBackupPath(prior);
                    if (File.Exists(backupPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(prior) ?? gameRoot);
                        File.Copy(backupPath, prior, true);
                        restored++;
                    }
                    else if (File.Exists(prior))
                    {
                        File.Delete(prior);
                        removed++;
                    }

                    var parent = Path.GetDirectoryName(prior);
                    if (!string.IsNullOrWhiteSpace(parent) &&
                        Directory.Exists(parent) &&
                        !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent, false);
                    }
                }

                if (removed > 0 || restored > 0)
                    log.LogInfo($"Pruned stale managed files: removed {removed}, restored {restored}.");
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed pruning stale managed files: {ex.Message}");
            }
        }

        private string[] GetBridgeRoots()
        {
            return new[]
            {
                Path.Combine(gameRoot, "Mods", "ZE2_SpriteBridge")
            };
        }

        private void PurgeLegacySpriteBridgeMods()
        {
            var removed = 0;
            foreach (var root in GetBridgeRoots())
            {
                try
                {
                    if (!Directory.Exists(root))
                        continue;
                    Directory.Delete(root, true);
                    removed++;
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Failed to purge bridge folder '{root}': {ex.Message}");
                }
            }

            if (removed > 0)
                log.LogInfo($"Purged {removed} legacy sprite bridge folder(s).");
        }

        private string[] GetMapBridgeRoots()
        {
            return new[]
            {
                Path.Combine(gameRoot, "Mods", "ZE2_MapBridge")
            };
        }

        private void PurgeLegacyMapBridgeMods()
        {
            var removed = 0;
            foreach (var root in GetMapBridgeRoots())
            {
                try
                {
                    if (!Directory.Exists(root))
                        continue;
                    Directory.Delete(root, true);
                    removed++;
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Failed to purge map bridge folder '{root}': {ex.Message}");
                }
            }

            if (removed > 0)
                log.LogInfo($"Purged {removed} legacy map bridge folder(s).");
        }

        private void BuildLegacyMapBridgeMod()
        {
            try
            {
                PurgeLegacyMapBridgeMods();

                var entries = DiscoverCustomMapBridgeEntries();
                if (entries.Count == 0)
                {
                    log.LogInfo("No custom map files detected; legacy map bridge remains empty.");
                    return;
                }

                var levelsDir = Path.Combine(gameRoot, "Data", "Levels");
                foreach (var root in GetMapBridgeRoots())
                    BuildLegacyMapBridgeModAt(root, levelsDir, entries);

                log.LogInfo($"Built legacy map bridge with {entries.Count} custom map prefix(es).");
            }
            catch (Exception ex)
            {
                log.LogWarning($"Failed building legacy map bridge mod: {ex.Message}");
            }
        }

        private List<MapBridgeEntry> DiscoverCustomMapBridgeEntries()
        {
            var entries = new List<MapBridgeEntry>();
            var levelsDir = Path.Combine(gameRoot, "Data", "Levels");
            if (!Directory.Exists(levelsDir))
                return entries;

            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pathFile in Directory.GetFiles(levelsDir, "*_Path.txt", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(pathFile);
                if (string.IsNullOrWhiteSpace(name) || !name.EndsWith("_Path.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                var prefix = name.Substring(0, name.Length - "_Path.txt".Length);
                if (string.IsNullOrWhiteSpace(prefix) || BaseMapPrefixes.Contains(prefix))
                    continue;
                prefixes.Add(prefix);
            }

            foreach (var xmlPath in Directory.GetFiles(levelsDir, "*.xml", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileNameWithoutExtension(xmlPath);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;
                if (fileName.EndsWith("_Ground", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith("_Walls", StringComparison.OrdinalIgnoreCase))
                    continue;

                var i = fileName.Length - 1;
                while (i >= 0 && char.IsDigit(fileName[i]))
                    i--;
                if (i < 0 || i == fileName.Length - 1)
                    continue;

                var prefix = fileName.Substring(0, i + 1);
                if (string.IsNullOrWhiteSpace(prefix) || prefix.IndexOf('_') >= 0 || BaseMapPrefixes.Contains(prefix))
                    continue;

                prefixes.Add(prefix);
            }

            foreach (var prefix in prefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var sectorCount = DetectCustomMapSectorCount(levelsDir, prefix);
                if (sectorCount <= 0)
                {
                    log.LogWarning($"Skipped custom map bridge prefix '{prefix}' (no sector files found).");
                    continue;
                }

                if (!ValidateCustomMapFiles(levelsDir, prefix, sectorCount))
                {
                    log.LogWarning($"Skipped custom map bridge prefix '{prefix}' (missing required map files).");
                    continue;
                }

                entries.Add(new MapBridgeEntry { Prefix = prefix, SectorCount = sectorCount });
            }

            return entries;
        }

        private static int DetectCustomMapSectorCount(string levelsDir, string prefix)
        {
            var count = 0;
            while (File.Exists(Path.Combine(levelsDir, $"{prefix}{count}.xml")))
                count++;
            return count;
        }

        private static bool ValidateCustomMapFiles(string levelsDir, string prefix, int sectorCount)
        {
            if (string.IsNullOrWhiteSpace(levelsDir) || string.IsNullOrWhiteSpace(prefix) || sectorCount <= 0)
                return false;

            if (!File.Exists(Path.Combine(levelsDir, $"{prefix}_Path.txt")))
                return false;

            for (var i = 0; i < sectorCount; i++)
            {
                if (!File.Exists(Path.Combine(levelsDir, $"{prefix}{i}.xml")))
                    return false;
                if (!File.Exists(Path.Combine(levelsDir, $"{prefix}{i}_Ground.xml")))
                    return false;
                if (!File.Exists(Path.Combine(levelsDir, $"{prefix}{i}_Ground.bin")))
                    return false;
                if (!File.Exists(Path.Combine(levelsDir, $"{prefix}{i}_Walls.xml")))
                    return false;
                if (!File.Exists(Path.Combine(levelsDir, $"{prefix}{i}_Walls.bin")))
                    return false;
            }

            return true;
        }

        private void BuildLegacyMapBridgeModAt(string bridgeRoot, string levelsDir, IReadOnlyList<MapBridgeEntry> entries)
        {
            if (Directory.Exists(bridgeRoot))
                Directory.Delete(bridgeRoot, true);
            Directory.CreateDirectory(bridgeRoot);

            var mapsRoot = Path.Combine(bridgeRoot, "Maps");
            Directory.CreateDirectory(mapsRoot);

            var mapsNode = new XElement("Maps");
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Prefix) || entry.SectorCount <= 0)
                    continue;

                var targetFolderRel = Path.Combine("Maps", entry.Prefix).Replace('\\', '/');
                var targetFolder = Path.Combine(bridgeRoot, targetFolderRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(targetFolder);

                File.Copy(Path.Combine(levelsDir, $"{entry.Prefix}_Path.txt"), Path.Combine(targetFolder, $"{entry.Prefix}_Path.txt"), true);
                for (var i = 0; i < entry.SectorCount; i++)
                {
                    File.Copy(Path.Combine(levelsDir, $"{entry.Prefix}{i}.xml"), Path.Combine(targetFolder, $"{entry.Prefix}{i}.xml"), true);
                    File.Copy(Path.Combine(levelsDir, $"{entry.Prefix}{i}_Ground.xml"), Path.Combine(targetFolder, $"{entry.Prefix}{i}_Ground.xml"), true);
                    File.Copy(Path.Combine(levelsDir, $"{entry.Prefix}{i}_Ground.bin"), Path.Combine(targetFolder, $"{entry.Prefix}{i}_Ground.bin"), true);
                    File.Copy(Path.Combine(levelsDir, $"{entry.Prefix}{i}_Walls.xml"), Path.Combine(targetFolder, $"{entry.Prefix}{i}_Walls.xml"), true);
                    File.Copy(Path.Combine(levelsDir, $"{entry.Prefix}{i}_Walls.bin"), Path.Combine(targetFolder, $"{entry.Prefix}{i}_Walls.bin"), true);
                }

                mapsNode.Add(new XElement("Map",
                    new XAttribute("Prefix", entry.Prefix),
                    new XAttribute("Folder", targetFolderRel),
                    new XAttribute("SectorCount", entry.SectorCount)));
            }

            var manifest = new XElement("Ze2Mod",
                new XElement("Id", "ze2_map_bridge"),
                new XElement("Name", "ZE2 Map Bridge"),
                new XElement("Enabled", "true"),
                mapsNode.HasElements ? mapsNode : null
            );
            new XDocument(manifest).Save(Path.Combine(bridgeRoot, "mod.xml"));
        }

        private void ResetGeneratedBridgeAssets()
        {
            try
            {
                var parent = Path.Combine(modsRoot, "_GeneratedBridgeAssets");
                Directory.CreateDirectory(parent);
                var session = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                generatedBridgeAssetsRoot = Path.Combine(parent, session);
                Directory.CreateDirectory(generatedBridgeAssetsRoot);
            }
            catch (Exception ex)
            {
                generatedBridgeAssetsRoot = Path.Combine(gameRoot, "Mods", "_GeneratedBridgeAssets_Fallback");
                try
                {
                    Directory.CreateDirectory(generatedBridgeAssetsRoot);
                }
                catch
                {
                }
                log.LogWarning($"Failed to initialize generated bridge assets folder, using fallback '{generatedBridgeAssetsRoot}': {ex.Message}");
            }
        }

        private const int SpriteCellSize = 16;
        private const int CharacterDirectionCount = 4;
        private const int GunFrameCount = 8;

        private string PrepareCharacterSpriteForBridge(string sourceSpritePath)
        {
            if (string.IsNullOrWhiteSpace(sourceSpritePath) || !File.Exists(sourceSpritePath))
                return sourceSpritePath;

            try
            {
                using var bmp = new Bitmap(sourceSpritePath);
                if (IsPreferredCharacterStripDimensions(bmp.Width, bmp.Height))
                    return sourceSpritePath;
            }
            catch (Exception ex)
            {
                log.LogWarning($"Character sprite probe failed for '{Path.GetFileName(sourceSpritePath)}': {ex.Message}");
            }

            var companionStrip = TryUseCompanionCharacterStrip(sourceSpritePath);
            if (!string.IsNullOrWhiteSpace(companionStrip))
                return companionStrip;

            return RewriteCharacterSpriteForBridge(sourceSpritePath);
        }

        private string PrepareGunSpriteForBridge(string sourceSpritePath)
        {
            if (string.IsNullOrWhiteSpace(sourceSpritePath) || !File.Exists(sourceSpritePath))
                return sourceSpritePath;

            try
            {
                using var bmp = new Bitmap(sourceSpritePath);
                if (IsPreferredGunStripDimensions(bmp.Width, bmp.Height))
                    return sourceSpritePath;
            }
            catch (Exception ex)
            {
                log.LogWarning($"Gun sprite probe failed for '{Path.GetFileName(sourceSpritePath)}': {ex.Message}");
            }

            var companionStrip = TryUseCompanionGunStrip(sourceSpritePath);
            if (!string.IsNullOrWhiteSpace(companionStrip))
                return companionStrip;

            return RewriteGunSpriteForBridge(sourceSpritePath);
        }

        private string TryUseCompanionCharacterStrip(string sourceSpritePath)
        {
            if (string.IsNullOrWhiteSpace(sourceSpritePath) || !File.Exists(sourceSpritePath))
                return string.Empty;

            var directory = Path.GetDirectoryName(sourceSpritePath);
            var baseName = Path.GetFileNameWithoutExtension(sourceSpritePath);
            var ext = Path.GetExtension(sourceSpritePath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
                return string.Empty;

            var candidates = new[]
            {
                Path.Combine(directory, baseName + "CharacterStrip" + ext),
                Path.Combine(directory, baseName + "_CharacterStrip" + ext),
                Path.Combine(directory, baseName + "Strip" + ext),
                Path.Combine(directory, baseName + "_Strip" + ext),
                Path.Combine(directory, baseName + "-Strip" + ext),
                Path.Combine(directory, baseName + ".strip" + ext)
            };

            foreach (var candidate in candidates
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var strip = new Bitmap(candidate);
                    if (!IsLegacyCharacterStripDimensions(strip.Width, strip.Height))
                        continue;

                    if (IsPreferredCharacterStripDimensions(strip.Width, strip.Height))
                    {
                        log.LogInfo($"Using companion character strip '{Path.GetFileName(candidate)}' for '{Path.GetFileName(sourceSpritePath)}'.");
                        return candidate;
                    }

                    var rewritten = RewriteCharacterSpriteForBridge(candidate);
                    if (!string.IsNullOrWhiteSpace(rewritten))
                    {
                        log.LogInfo($"Using companion character strip '{Path.GetFileName(candidate)}' (normalized) for '{Path.GetFileName(sourceSpritePath)}'.");
                        return rewritten;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Failed probing companion character strip '{Path.GetFileName(candidate)}': {ex.Message}");
                }
            }

            return string.Empty;
        }

        private string TryUseCompanionGunStrip(string sourceSpritePath)
        {
            if (string.IsNullOrWhiteSpace(sourceSpritePath) || !File.Exists(sourceSpritePath))
                return string.Empty;

            var directory = Path.GetDirectoryName(sourceSpritePath);
            var baseName = Path.GetFileNameWithoutExtension(sourceSpritePath);
            var ext = Path.GetExtension(sourceSpritePath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
                return string.Empty;

            var candidates = new[]
            {
                Path.Combine(directory, baseName + "GunStrip" + ext),
                Path.Combine(directory, baseName + "_GunStrip" + ext),
                Path.Combine(directory, baseName + "Strip" + ext),
                Path.Combine(directory, baseName + "_Strip" + ext),
                Path.Combine(directory, baseName + "-Strip" + ext),
                Path.Combine(directory, baseName + ".strip" + ext)
            };

            foreach (var candidate in candidates
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var strip = new Bitmap(candidate);
                    if (!IsLegacyGunStripDimensions(strip.Width, strip.Height))
                        continue;

                    if (IsPreferredGunStripDimensions(strip.Width, strip.Height))
                    {
                        log.LogInfo($"Using companion gun strip '{Path.GetFileName(candidate)}' for '{Path.GetFileName(sourceSpritePath)}'.");
                        return candidate;
                    }

                    var rewritten = RewriteGunSpriteForBridge(candidate);
                    if (!string.IsNullOrWhiteSpace(rewritten))
                    {
                        log.LogInfo($"Using companion gun strip '{Path.GetFileName(candidate)}' (normalized) for '{Path.GetFileName(sourceSpritePath)}'.");
                        return rewritten;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning($"Failed probing companion strip '{Path.GetFileName(candidate)}': {ex.Message}");
                }
            }

            return string.Empty;
        }

        private static bool IsLegacyCharacterStripDimensions(int width, int height)
            => height == SpriteCellSize && (width == SpriteCellSize || width == SpriteCellSize * CharacterDirectionCount);

        private static bool IsPreferredCharacterStripDimensions(int width, int height)
            => width == SpriteCellSize * CharacterDirectionCount && height == SpriteCellSize;

        private static bool IsLegacyGunStripDimensions(int width, int height)
            => height == SpriteCellSize && (width == SpriteCellSize || width == SpriteCellSize * 2 || width == SpriteCellSize * GunFrameCount);

        private static bool IsPreferredGunStripDimensions(int width, int height)
            => width == SpriteCellSize * GunFrameCount && height == SpriteCellSize;

        private static void LeftAlignGunStripCells(Bitmap strip)
        {
            if (strip == null || strip.Height != SpriteCellSize || strip.Width < SpriteCellSize)
                return;

            var cellCount = strip.Width / SpriteCellSize;
            if (cellCount <= 0)
                return;

            for (var cell = 0; cell < cellCount; cell++)
            {
                var cellStartX = cell * SpriteCellSize;
                var minOpaqueX = SpriteCellSize;

                for (var y = 0; y < SpriteCellSize; y++)
                {
                    for (var x = 0; x < SpriteCellSize; x++)
                    {
                        if (strip.GetPixel(cellStartX + x, y).A <= 0)
                            continue;
                        if (x < minOpaqueX)
                            minOpaqueX = x;
                    }
                }

                if (minOpaqueX <= 0 || minOpaqueX >= SpriteCellSize)
                    continue;

                var temp = new Color[SpriteCellSize, SpriteCellSize];
                for (var y = 0; y < SpriteCellSize; y++)
                {
                    for (var x = 0; x < SpriteCellSize; x++)
                    {
                        var sx = x + minOpaqueX;
                        temp[x, y] = sx < SpriteCellSize
                            ? strip.GetPixel(cellStartX + sx, y)
                            : Color.Transparent;
                    }
                }

                for (var y = 0; y < SpriteCellSize; y++)
                {
                    for (var x = 0; x < SpriteCellSize; x++)
                    {
                        strip.SetPixel(cellStartX + x, y, temp[x, y]);
                    }
                }
            }
        }

        private string RewriteCharacterSpriteForBridge(string sourceSpritePath)
        {
            if (string.IsNullOrWhiteSpace(sourceSpritePath) || !File.Exists(sourceSpritePath))
                return sourceSpritePath;

            try
            {
                Directory.CreateDirectory(generatedBridgeAssetsRoot);

                var data = File.ReadAllBytes(sourceSpritePath);
                using var ms = new MemoryStream(data);
                using var src = new Bitmap(ms);

                if (IsPreferredCharacterStripDimensions(src.Width, src.Height))
                    return sourceSpritePath;

                var sourceFrames = GetHorizontalSourceFrames(src, CharacterDirectionCount);
                if (sourceFrames.Count == 0)
                    return sourceSpritePath;

                using var normalized = new Bitmap(SpriteCellSize * CharacterDirectionCount, SpriteCellSize, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(normalized))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.SmoothingMode = SmoothingMode.None;

                    for (var frame = 0; frame < CharacterDirectionCount; frame++)
                    {
                        var sourceIndex = SelectCharacterSourceFrameIndex(sourceFrames.Count, frame);
                        var srcRect = sourceFrames[sourceIndex];
                        var dstRect = new Rectangle(frame * SpriteCellSize, 0, SpriteCellSize, SpriteCellSize);
                        g.DrawImage(src, dstRect, srcRect, GraphicsUnit.Pixel);
                    }
                }

                var baseName = Path.GetFileNameWithoutExtension(sourceSpritePath);
                var outputName = $"{baseName}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.png";
                var outputPath = Path.Combine(generatedBridgeAssetsRoot, outputName);
                normalized.Save(outputPath, ImageFormat.Png);
                log.LogInfo($"Normalized character sprite '{Path.GetFileName(sourceSpritePath)}' to 64x16 directional strip.");
                return outputPath;
            }
            catch (Exception ex)
            {
                log.LogWarning($"Character sprite rewrite failed for '{Path.GetFileName(sourceSpritePath)}': {ex.Message}");
                return sourceSpritePath;
            }
        }

        private string RewriteGunSpriteForBridge(string sourceSpritePath)
        {
            if (string.IsNullOrWhiteSpace(sourceSpritePath) || !File.Exists(sourceSpritePath))
                return sourceSpritePath;

            try
            {
                Directory.CreateDirectory(generatedBridgeAssetsRoot);

                var data = File.ReadAllBytes(sourceSpritePath);
                using var ms = new MemoryStream(data);
                using var src = new Bitmap(ms);

                if (IsPreferredGunStripDimensions(src.Width, src.Height))
                    return sourceSpritePath;

                var sourceFrames = GetHorizontalSourceFrames(src, GunFrameCount);
                if (sourceFrames.Count == 0)
                    return sourceSpritePath;

                using var normalized = new Bitmap(SpriteCellSize * GunFrameCount, SpriteCellSize, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(normalized))
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.SmoothingMode = SmoothingMode.None;

                    for (var frame = 0; frame < GunFrameCount; frame++)
                    {
                        var sourceIndex = SelectGunSourceFrameIndex(sourceFrames.Count, frame);
                        var srcRect = sourceFrames[sourceIndex];
                        var dstRect = new Rectangle(frame * SpriteCellSize, 0, SpriteCellSize, SpriteCellSize);
                        g.DrawImage(src, dstRect, srcRect, GraphicsUnit.Pixel);
                    }
                }

                LeftAlignGunStripCells(normalized);

                var baseName = Path.GetFileNameWithoutExtension(sourceSpritePath);
                var outputName = $"{baseName}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.png";
                var outputPath = Path.Combine(generatedBridgeAssetsRoot, outputName);
                normalized.Save(outputPath, ImageFormat.Png);
                log.LogInfo($"Normalized gun sprite '{Path.GetFileName(sourceSpritePath)}' to 128x16 directional+fire strip.");
                return outputPath;
            }
            catch (Exception ex)
            {
                log.LogWarning($"Gun sprite rewrite failed for '{Path.GetFileName(sourceSpritePath)}': {ex.Message}");
                return sourceSpritePath;
            }
        }

        private static List<Rectangle> GetHorizontalSourceFrames(Bitmap source, int preferredFrameCount)
        {
            var frames = new List<Rectangle>();
            if (source == null || source.Width <= 0 || source.Height <= 0)
                return frames;

            if (source.Height == SpriteCellSize && source.Width % SpriteCellSize == 0)
            {
                var cellCount = Math.Max(1, source.Width / SpriteCellSize);
                for (var i = 0; i < cellCount; i++)
                {
                    frames.Add(new Rectangle(i * SpriteCellSize, 0, SpriteCellSize, SpriteCellSize));
                }

                return frames;
            }

            var inferredCount = 1;
            if (preferredFrameCount > 0 &&
                source.Width % preferredFrameCount == 0 &&
                source.Width / preferredFrameCount > 0)
            {
                inferredCount = preferredFrameCount;
            }
            else if (source.Width % CharacterDirectionCount == 0 && source.Width / CharacterDirectionCount >= 8)
            {
                inferredCount = CharacterDirectionCount;
            }
            else if (source.Width % 2 == 0 && source.Width / 2 >= 8)
            {
                inferredCount = 2;
            }

            var frameWidth = Math.Max(1, source.Width / inferredCount);
            for (var i = 0; i < inferredCount; i++)
            {
                var x = i * frameWidth;
                var width = i == inferredCount - 1 ? source.Width - x : frameWidth;
                if (width <= 0)
                    continue;
                frames.Add(new Rectangle(x, 0, width, source.Height));
            }

            return frames;
        }

        private static int SelectCharacterSourceFrameIndex(int sourceCount, int targetFrame)
        {
            if (sourceCount <= 1)
                return 0;
            if (sourceCount == 2)
                return targetFrame % 2;
            if (sourceCount == 3)
            {
                var map = new[] { 0, 1, 2, 1 };
                return map[targetFrame % map.Length];
            }

            return targetFrame % Math.Min(sourceCount, CharacterDirectionCount);
        }

        private static int SelectGunSourceFrameIndex(int sourceCount, int targetFrame)
        {
            if (sourceCount <= 1)
                return 0;
            if (sourceCount == 2)
                return targetFrame < CharacterDirectionCount ? 0 : 1;
            if (sourceCount == CharacterDirectionCount)
                return targetFrame % CharacterDirectionCount;
            if (sourceCount >= GunFrameCount)
                return targetFrame;
            return targetFrame % sourceCount;
        }

        private static string GetAttributeValue(XElement element, params string[] names)
        {
            if (element == null || names == null || names.Length == 0)
                return string.Empty;
            foreach (var name in names)
            {
                var attr = element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
                if (attr != null && !string.IsNullOrWhiteSpace(attr.Value))
                    return attr.Value.Trim();
            }
            return string.Empty;
        }

        private static string GetElementValue(XElement element, string name)
        {
            var child = GetChildElement(element, name);
            return child?.Value?.Trim() ?? string.Empty;
        }

        private static XElement GetChildElement(XElement parent, string name)
        {
            if (parent == null || string.IsNullOrWhiteSpace(name))
                return null;
            return parent.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<XElement> EnumerateSectionEntries(XElement section, params string[] names)
        {
            if (section == null || names == null || names.Length == 0)
                yield break;

            var seen = new HashSet<XElement>();
            foreach (var name in names)
            {
                foreach (var element in section.Elements().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (seen.Add(element))
                        yield return element;
                }
            }
        }

        private static string ResolveExistingPath(string packDir, string relativePath, params string[] extensionFallbacks)
        {
            if (string.IsNullOrWhiteSpace(packDir) || string.IsNullOrWhiteSpace(relativePath))
                return string.Empty;

            var full = Path.GetFullPath(Path.Combine(packDir, relativePath));
            if (full.StartsWith(packDir, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                return full;

            if (!Path.HasExtension(relativePath) && extensionFallbacks != null)
            {
                foreach (var ext in extensionFallbacks.Where(e => !string.IsNullOrWhiteSpace(e)))
                {
                    var withExt = Path.GetFullPath(Path.Combine(packDir, relativePath + ext));
                    if (withExt.StartsWith(packDir, StringComparison.OrdinalIgnoreCase) && File.Exists(withExt))
                        return withExt;
                }
            }

            return string.Empty;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static string GetRelativePathPortable(string relativeTo, string path)
        {
            var fromUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(relativeTo)));
            var toUri = new Uri(Path.GetFullPath(path));
            var relativeUri = fromUri.MakeRelativeUri(toUri);
            return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                return path + Path.DirectorySeparatorChar;
            return path;
        }
    }

    internal static class CharacterExpansionPatches
    {
        private static ManualLogSource log;
        private static string gameRoot;
        private static List<string> stagedCharacterFiles;
        private static List<string> stagedGunNames;
        private static List<string> stagedBulletNames;
        private static HashSet<string> stagedGunNameSet;
        private static readonly HashSet<string> loggedPassesFilterGunNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool loggedRuntimeGunEnsure;
        private static HashSet<string> stagedSpriteBoundGunNames;
        private static HashSet<string> stagedSpriteBoundBulletNames;
        private static bool installed;
        private static bool legacySpriteInitAttempted;
        private static bool legacySpriteMergeCompleted;
        private static bool updateHookSeen;
        private static bool legacyBridgeResolved;
        private static Assembly legacyBridgeAssembly;
        private static Type legacyBootstrapType;
        private static MethodInfo legacyInitializeMethod;
        private static MethodInfo legacyLoadCustomCharactersMethod;
        private static MethodInfo legacyLoadCustomGunsMethod;
        private static MethodInfo legacyLoadCustomBulletsMethod;
        private static MethodInfo legacyAddCustomMapsToLevelSelectMethod;
        private static MethodInfo legacyAddCustomMapsToXboxLevelSelectMethod;
        private static MethodInfo legacyStartCustomMapFromLevelSelectMethod;
        private static bool legacyCharactersAppliedLogged;
        private static bool legacyGunsAppliedLogged;
        private static bool legacyBulletsAppliedLogged;
        private static readonly HashSet<string> KnownBasePcLevelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "The Estate",
            "Zombie High School",
            "Mall",
            "Skyscraper",
            "Desert Town"
        };
        private static readonly HashSet<string> KnownBaseMapPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Estate",
            "School",
            "Mall",
            "Skyscraper",
            "DesertTown",
            "Office",
            "Farm"
        };

        internal static void Initialize(
            ManualLogSource logger,
            string root,
            List<string> pendingFiles,
            List<string> pendingGuns,
            List<string> pendingBullets,
            List<string> pendingSpriteBoundGuns,
            List<string> pendingSpriteBoundBullets)
        {
            log = logger;
            gameRoot = root;
            stagedCharacterFiles = pendingFiles ?? new List<string>();
            stagedGunNames = pendingGuns ?? new List<string>();
            stagedBulletNames = pendingBullets ?? new List<string>();
            stagedGunNameSet = BuildStagedGunNameSet(stagedGunNames);
            loggedPassesFilterGunNames.Clear();
            stagedSpriteBoundGunNames = new HashSet<string>((pendingSpriteBoundGuns ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            stagedSpriteBoundBulletNames = new HashSet<string>((pendingSpriteBoundBullets ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
        }

        internal static void Install(Harmony harmony)
        {
            if (installed)
                return;

            var patched = 0;

            var keeperType = AccessTools.TypeByName("ZombieEstate2.PlayerStatKeeper");
            var init = keeperType == null ? null : AccessTools.Method(keeperType, "InitCharSettings");
            if (init != null)
            {
                harmony.Patch(init, postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(InitCharSettingsPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                patched++;
            }

            var screenType = AccessTools.TypeByName("ZombieEstate2.StoreScreen.XboxStore.XboxItemScreen");
            if (screenType != null)
            {
                foreach (var m in screenType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!string.Equals(m.Name, "PopulateCharacters", StringComparison.Ordinal))
                        continue;
                    var p = m.GetParameters();
                    if (p.Length != 2)
                        continue;
                    harmony.Patch(
                        m,
                        prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(PopulateCharactersPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                        finalizer: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(PopulateCharactersFinalizer), BindingFlags.Static | BindingFlags.NonPublic))
                    );
                    log?.LogInfo($"Patched PopulateCharacters overload: ({p[0].ParameterType.Name}, {p[1].ParameterType.Name})");
                    patched++;
                }
            }

            var gunLoaderType = AccessTools.TypeByName("ZombieEstate2.GunStatsLoader");
            var loadAllGuns = gunLoaderType == null ? null : AccessTools.Method(gunLoaderType, "LoadAllGuns");
            if (loadAllGuns != null)
            {
                harmony.Patch(
                    loadAllGuns,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LoadAllGunsPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched GunStatsLoader.LoadAllGuns postfix.");
                patched++;
            }

            var loadAllGunsInFolder = gunLoaderType == null ? null : AccessTools.Method(gunLoaderType, "LoadAllGunsInFolder");
            if (loadAllGunsInFolder != null)
            {
                harmony.Patch(
                    loadAllGunsInFolder,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LoadAllGunsPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched GunStatsLoader.LoadAllGunsInFolder postfix.");
                patched++;
            }

            var loadIndividualFiles = gunLoaderType == null ? null : AccessTools.Method(gunLoaderType, "LoadIndividualFiles");
            if (loadIndividualFiles != null)
            {
                harmony.Patch(
                    loadIndividualFiles,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LoadAllGunsPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched GunStatsLoader.LoadIndividualFiles postfix.");
                patched++;
            }

            var bulletCreatorType = AccessTools.TypeByName("ZombieEstate2.BulletCreator");
            var loadBullets = bulletCreatorType == null ? null : AccessTools.Method(bulletCreatorType, "LoadBullets");
            if (loadBullets != null)
            {
                harmony.Patch(
                    loadBullets,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LoadBulletsPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched BulletCreator.LoadBullets postfix.");
                patched++;
            }

            var loadAllBulletsInFolder = bulletCreatorType == null ? null : AccessTools.Method(bulletCreatorType, "LoadAllBulletsInFolder");
            if (loadAllBulletsInFolder != null)
            {
                harmony.Patch(
                    loadAllBulletsInFolder,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LoadBulletsPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched BulletCreator.LoadAllBulletsInFolder postfix.");
                patched++;
            }

            var loadProtoBullets = bulletCreatorType == null ? null : AccessTools.Method(bulletCreatorType, "LoadProtoBullets");
            if (loadProtoBullets != null)
            {
                harmony.Patch(
                    loadProtoBullets,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LoadBulletsPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched BulletCreator.LoadProtoBullets postfix.");
                patched++;
            }

            var gameType = AccessTools.TypeByName("ZombieEstate2.Game1");
            var loadContent = gameType == null ? null : AccessTools.Method(gameType, "LoadContent");
            if (loadContent != null)
            {
                harmony.Patch(
                    loadContent,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(GameLoadContentPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched Game1.LoadContent postfix for legacy sprite merge.");
                patched++;
            }

            var update = gameType == null ? null : AccessTools.Method(gameType, "Update");
            if (update != null)
            {
                harmony.Patch(
                    update,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(GameUpdatePostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched Game1.Update postfix for deferred legacy sprite merge.");
                patched++;
            }

            var gameManagerType = AccessTools.TypeByName("ZombieEstate2.GameManager");
            var gotoCharSelect = gameManagerType == null ? null : AccessTools.Method(gameManagerType, "GotoCharSelect");
            if (gotoCharSelect != null)
            {
                harmony.Patch(
                    gotoCharSelect,
                    prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(GotoCharSelectPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched GameManager.GotoCharSelect prefix for legacy sprite merge.");
                patched++;
            }

            var charSelectType = AccessTools.TypeByName("ZombieEstate2.UI.Xbox.XboxCharacterSelect");
            var specialPropsType = AccessTools.TypeByName("ZombieEstate2.SpecialProperties");
            if (charSelectType != null && specialPropsType != null)
            {
                var updateStats = AccessTools.Method(charSelectType, "UpdateStatsGUI", new[] { specialPropsType });
                if (updateStats != null)
                {
                    harmony.Patch(
                        updateStats,
                        prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(UpdateStatsGuiPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                    );
                    log?.LogInfo("Patched XboxCharacterSelect.UpdateStatsGUI null-guard.");
                    patched++;
                }
            }

            var pcGunStoreType = AccessTools.TypeByName("ZombieEstate2.PCGunStore");
            var gunStatsType = AccessTools.TypeByName("ZombieEstate2.GunStats");
            if (pcGunStoreType != null && gunStatsType != null)
            {
                var addItems = AccessTools.Method(pcGunStoreType, "AddItems");
                if (addItems != null)
                {
                    harmony.Patch(
                        addItems,
                        prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(PCGunStoreAddItemsPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                    );
                    log?.LogInfo("Patched PCGunStore.AddItems prefix for runtime custom gun ensure.");
                    patched++;
                }

                var passesFilter = AccessTools.Method(pcGunStoreType, "PassesFilter", new[] { gunStatsType });
                if (passesFilter != null)
                {
                    harmony.Patch(
                        passesFilter,
                        prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(PCGunStorePassesFilterPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                    );
                    log?.LogInfo("Patched PCGunStore.PassesFilter prefix for custom guns.");
                    patched++;
                }
            }

            var storeType = AccessTools.TypeByName("ZombieEstate2.Store");
            var setupGuns = storeType == null ? null : AccessTools.Method(storeType, "SetUpGuns");
            if (setupGuns != null)
            {
                harmony.Patch(
                    setupGuns,
                    prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(StoreSetUpGunsPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched Store.SetUpGuns prefix for runtime custom gun ensure.");
                patched++;
            }

            var playerType = AccessTools.TypeByName("ZombieEstate2.Player");
            var xboxStoreType = AccessTools.TypeByName("ZombieEstate2.StoreScreen.XboxStore.XboxStore");
            var xboxStoreCtor = xboxStoreType == null || playerType == null ? null : AccessTools.Constructor(xboxStoreType, new[] { playerType });
            if (xboxStoreCtor != null)
            {
                harmony.Patch(
                    xboxStoreCtor,
                    prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(XboxStoreCtorPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched XboxStore constructor prefix for runtime custom gun ensure.");
                patched++;
            }

            var ammoType = AccessTools.TypeByName("ZombieEstate2.AmmoType");
            if (screenType != null && gunStatsType != null && ammoType != null)
            {
                var gunListType = typeof(List<>).MakeGenericType(gunStatsType);

                var populateGunsByType = AccessTools.Method(screenType, "PopulateGuns", new[] { gunListType, ammoType });
                if (populateGunsByType != null)
                {
                    harmony.Patch(
                        populateGunsByType,
                        postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(PopulateGunsByTypePostfix), BindingFlags.Static | BindingFlags.NonPublic))
                    );
                    log?.LogInfo("Patched XboxItemScreen.PopulateGuns(List<GunStats>, AmmoType) postfix for price ordering.");
                    patched++;
                }

                var populateGunsList = AccessTools.Method(screenType, "PopulateGuns", new[] { gunListType });
                if (populateGunsList != null)
                {
                    harmony.Patch(
                        populateGunsList,
                        postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(PopulateGunsListPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                    );
                    log?.LogInfo("Patched XboxItemScreen.PopulateGuns(List<GunStats>) postfix for price ordering.");
                    patched++;
                }
            }

            var pcLevelSelectType = AccessTools.TypeByName("ZombieEstate2.LevelSelect");
            var pcLevelSelectSetup = pcLevelSelectType == null ? null : AccessTools.Method(pcLevelSelectType, "Setup");
            if (pcLevelSelectSetup != null)
            {
                harmony.Patch(
                    pcLevelSelectSetup,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LevelSelectSetupPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched LevelSelect.Setup postfix for custom map injection.");
                patched++;
            }

            var pcLevelSelectGo = pcLevelSelectType == null ? null : AccessTools.Method(pcLevelSelectType, "GoPressed");
            if (pcLevelSelectGo != null)
            {
                harmony.Patch(
                    pcLevelSelectGo,
                    prefix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(LevelSelectGoPressedPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched LevelSelect.GoPressed prefix for custom map start.");
                patched++;
            }

            var xboxLevelSelectType = AccessTools.TypeByName("ZombieEstate2.XboxLevelSelect");
            var xboxLevelSelectSetup = xboxLevelSelectType == null ? null : AccessTools.Method(xboxLevelSelectType, "Setup");
            if (xboxLevelSelectSetup != null)
            {
                harmony.Patch(
                    xboxLevelSelectSetup,
                    postfix: new HarmonyMethod(typeof(CharacterExpansionPatches).GetMethod(nameof(XboxLevelSelectSetupPostfix), BindingFlags.Static | BindingFlags.NonPublic))
                );
                log?.LogInfo("Patched XboxLevelSelect.Setup postfix for custom map injection.");
                patched++;
            }

            installed = true;
            log?.LogInfo($"Character expansion patches installed: {patched}");
        }

        private static void InitCharSettingsPostfix()
        {
            try
            {
                LoadAllGunsPostfix();
                LoadBulletsPostfix();

                var keeperType = AccessTools.TypeByName("ZombieEstate2.PlayerStatKeeper");
                var charType = AccessTools.TypeByName("ZombieEstate2.CharacterSettings");
                if (keeperType == null || charType == null)
                    return;

                var listField = AccessTools.Field(keeperType, "CharacterSettings");
                var runtimeList = listField?.GetValue(null) as System.Collections.IList;
                if (runtimeList == null)
                    return;

                // Let legacy bridge populate custom characters first so their sprite atlas
                // coordinates are attached before our fallback injector runs.
                TryInvokeLegacyLoadCustomCharacters(runtimeList);

                var nameField = AccessTools.Field(charType, "name");
                var startingGunField = AccessTools.Field(charType, "startingGun");
                var propsField = AccessTools.Field(charType, "Properties");
                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in runtimeList)
                {
                    var n = item == null ? null : nameField?.GetValue(item) as string;
                    if (!string.IsNullOrWhiteSpace(n))
                        existing.Add(n);
                }

                var beforeCount = runtimeList.Count;
                var injected = 0;
                var injectedGunNames = new List<string>();
                foreach (var file in stagedCharacterFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(file))
                        continue;
                    var ext = Path.GetExtension(file);
                    if (!(ext.Equals(".chr", StringComparison.OrdinalIgnoreCase) || ext.Equals(".char", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var serializer = new System.Xml.Serialization.XmlSerializer(charType);
                    using var stream = File.OpenRead(file);
                    var character = serializer.Deserialize(stream);
                    var name = nameField?.GetValue(character) as string;
                    if (string.IsNullOrWhiteSpace(name))
                        name = Path.GetFileNameWithoutExtension(file);
                    if (existing.Contains(name))
                        continue;
                    runtimeList.Add(character);
                    existing.Add(name);
                    var startGun = startingGunField?.GetValue(character) as string;
                    if (!string.IsNullOrWhiteSpace(startGun))
                        injectedGunNames.Add(startGun);
                    injected++;
                }

                var fixedMissingProps = 0;
                if (propsField != null)
                {
                    var fallbackProps = FindFirstNonNullProperties(runtimeList, propsField);
                    foreach (var item in runtimeList)
                    {
                        if (item == null)
                            continue;
                        if (propsField.GetValue(item) != null)
                            continue;
                        propsField.SetValue(item, CloneSpecialProperties(fallbackProps, propsField.FieldType));
                        fixedMissingProps++;
                    }
                }

                log?.LogInfo($"Character runtime list count: {beforeCount} -> {runtimeList.Count} (injected {injected}).");
                if (fixedMissingProps > 0)
                    log?.LogInfo($"Patched missing character Properties on {fixedMissingProps} character(s).");

                if (injectedGunNames.Count > 0)
                    ValidateInjectedCharacterStartingGuns(injectedGunNames);
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Runtime character injection failed: {ex.Message}");
            }
        }

        private static void ValidateInjectedCharacterStartingGuns(IEnumerable<string> startingGuns)
        {
            try
            {
                var loaderType = AccessTools.TypeByName("ZombieEstate2.GunStatsLoader");
                if (loaderType == null)
                    return;
                var getStatsMethod = AccessTools.Method(loaderType, "GetStats", new[] { typeof(string) });
                if (getStatsMethod == null)
                    return;

                foreach (var gunName in startingGuns.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var gun = getStatsMethod.Invoke(null, new object[] { gunName });
                    if (gun == null)
                        log?.LogWarning($"Injected character starting gun is missing from runtime registry: '{gunName}'.");
                    else
                        log?.LogInfo($"Verified injected character starting gun in runtime registry: '{gunName}'.");
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Starting gun validation failed: {ex.Message}");
            }
        }

        private static object FindFirstNonNullProperties(System.Collections.IList runtimeList, FieldInfo propsField)
        {
            foreach (var item in runtimeList)
            {
                if (item == null)
                    continue;
                var props = propsField.GetValue(item);
                if (props != null)
                    return props;
            }
            return null;
        }

        private static object CloneSpecialProperties(object source, Type propsType)
        {
            var clone = Activator.CreateInstance(propsType);
            if (source == null)
                return clone;

            foreach (var field in propsType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsInitOnly)
                    continue;
                field.SetValue(clone, field.GetValue(source));
            }

            return clone;
        }

        private static void UpdateStatsGuiPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 1 || __args[0] != null)
                    return;

                var specialPropsType = AccessTools.TypeByName("ZombieEstate2.SpecialProperties");
                if (specialPropsType == null)
                    return;

                __args[0] = Activator.CreateInstance(specialPropsType);
                log?.LogWarning("UpdateStatsGUI received null stats; injected default SpecialProperties.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"UpdateStatsGUI null-guard failed: {ex.Message}");
            }
        }

        private static bool PCGunStorePassesFilterPrefix(object __0, ref bool __result)
        {
            try
            {
                if (__0 == null || stagedGunNameSet == null || stagedGunNameSet.Count == 0)
                    return true;

                var gunType = __0.GetType();
                var gunName = AccessTools.Property(gunType, "GunName")?.GetValue(__0, null) as string;
                if (string.IsNullOrWhiteSpace(gunName) || !stagedGunNameSet.Contains(gunName))
                    return true;

                object costObj = null;
                var costProp = AccessTools.Property(gunType, "Cost");
                if (costProp != null)
                    costObj = costProp.GetValue(__0, null);
                if (costObj == null)
                {
                    var costField = AccessTools.Field(gunType, "Cost");
                    if (costField != null)
                        costObj = costField.GetValue(__0);
                }

                if (costObj != null && int.TryParse(costObj.ToString(), out var cost) && cost > 0)
                {
                    __result = true;
                    if (!string.IsNullOrWhiteSpace(gunName) && loggedPassesFilterGunNames.Add(gunName))
                        log?.LogInfo($"PCGunStore custom filter override applied for '{gunName}' (Cost={cost}).");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"PCGunStore.PassesFilter override failed: {ex.Message}");
            }

            return true;
        }

        private static void PCGunStoreAddItemsPrefix()
        {
            EnsureRuntimeCustomGunRegistry("PCGunStore.AddItems");
        }

        private static void StoreSetUpGunsPrefix()
        {
            EnsureRuntimeCustomGunRegistry("Store.SetUpGuns");
        }

        private static void XboxStoreCtorPrefix()
        {
            EnsureRuntimeCustomGunRegistry("XboxStore..ctor");
        }

        private static void PopulateGunsByTypePostfix(object __instance, object __0, object __1)
        {
            try
            {
                EnsureRuntimeCustomGunRegistry("XboxItemScreen.PopulateGuns(list,type)");
                EnsureMissingCustomGunsVisible(__instance, __0 as System.Collections.IList, __1, requireAmmoMatch: true, "typed view");
                SortVisibleGunItemsByPrice(__instance, "typed view");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"PopulateGuns(list,type) postfix failed: {ex.Message}");
            }
        }

        private static void PopulateGunsListPostfix(object __instance, object __0)
        {
            try
            {
                EnsureRuntimeCustomGunRegistry("XboxItemScreen.PopulateGuns(list)");
                EnsureMissingCustomGunsVisible(__instance, __0 as System.Collections.IList, ammoTypeFilter: null, requireAmmoMatch: false, "full list");
                SortVisibleGunItemsByPrice(__instance, "full list");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"PopulateGuns(list) postfix failed: {ex.Message}");
            }
        }

        private sealed class VisibleGunItem
        {
            public object Cell;
            public int Cost;
            public int OriginalIndex;
            public bool PositiveCost;
        }

        private static void EnsureMissingCustomGunsVisible(object screen, System.Collections.IList sourceList, object ammoTypeFilter, bool requireAmmoMatch, string contextLabel)
        {
            if (screen == null || stagedGunNameSet == null || stagedGunNameSet.Count == 0)
                return;

            var screenType = screen.GetType();
            var itemsField = AccessTools.Field(screenType, "mItems");
            var widthField = AccessTools.Field(screenType, "mWidth");
            var heightField = AccessTools.Field(screenType, "mHeight");
            if (itemsField == null || widthField == null || heightField == null)
                return;

            var itemType = AccessTools.TypeByName("ZombieEstate2.StoreScreen.XboxStore.XboxItem");
            var gunStatsType = AccessTools.TypeByName("ZombieEstate2.GunStats");
            if (itemType == null || gunStatsType == null)
                return;

            var items = itemsField.GetValue(screen) as Array;
            if (items == null || items.Rank != 2)
                return;

            var width = items.GetLength(0);
            var height = items.GetLength(1);
            if (width <= 0 || height <= 0)
                return;

            var tagProp = AccessTools.Property(itemType, "Tag");
            var gunNameProp = AccessTools.Property(gunStatsType, "GunName");
            var costProp = AccessTools.Property(gunStatsType, "Cost");
            var costField = costProp == null ? AccessTools.Field(gunStatsType, "Cost") : null;
            var ammoProp = AccessTools.Property(gunStatsType, "AmmoType");
            var ammoField = ammoProp == null ? AccessTools.Field(gunStatsType, "AmmoType") : null;
            var gunXProp = AccessTools.Property(gunStatsType, "GunXCoord");
            var gunYProp = AccessTools.Property(gunStatsType, "GunYCoord");
            if (tagProp == null || gunNameProp == null || (costProp == null && costField == null) || gunXProp == null || gunYProp == null)
                return;

            var itemCtor = itemType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c =>
                {
                    var p = c.GetParameters();
                    return p.Length == 5 &&
                           p[0].ParameterType == typeof(int) &&
                           p[1].ParameterType == typeof(int) &&
                           p[3].ParameterType == typeof(bool) &&
                           p[4].ParameterType == typeof(bool);
                });
            if (itemCtor == null)
                return;

            var visibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = items.GetValue(x, y);
                    if (cell == null)
                        continue;
                    var tag = tagProp.GetValue(cell, null);
                    if (tag == null || !gunStatsType.IsInstanceOfType(tag))
                        continue;
                    var name = (gunNameProp.GetValue(tag, null) as string)?.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                        visibleNames.Add(name);
                }
            }

            var runtimeList = ResolveRuntimeGunList(sourceList);
            if (runtimeList == null)
                return;

            var stagedDisplayOrder = BuildStagedGunDisplayOrder();
            var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < stagedDisplayOrder.Count; i++)
            {
                var key = stagedDisplayOrder[i];
                if (!string.IsNullOrWhiteSpace(key) && !orderMap.ContainsKey(key))
                    orderMap[key] = i;
            }

            var candidates = new List<object>();
            foreach (var gun in runtimeList)
            {
                if (gun == null || !gunStatsType.IsInstanceOfType(gun))
                    continue;

                var name = (gunNameProp.GetValue(gun, null) as string)?.Trim();
                if (string.IsNullOrWhiteSpace(name) || !stagedGunNameSet.Contains(name) || visibleNames.Contains(name))
                    continue;

                var costObj = costProp != null ? costProp.GetValue(gun, null) : costField.GetValue(gun);
                if (costObj == null || !int.TryParse(costObj.ToString(), out var cost) || cost <= 0)
                    continue;

                if (requireAmmoMatch)
                {
                    var ammoObj = ammoProp != null ? ammoProp.GetValue(gun, null) : ammoField?.GetValue(gun);
                    if (!Equals(ammoObj, ammoTypeFilter))
                        continue;
                }

                candidates.Add(gun);
                visibleNames.Add(name);
            }

            if (candidates.Count == 0)
                return;

            candidates.Sort((a, b) =>
            {
                var aName = (gunNameProp.GetValue(a, null) as string)?.Trim() ?? string.Empty;
                var bName = (gunNameProp.GetValue(b, null) as string)?.Trim() ?? string.Empty;
                var aOrder = orderMap.TryGetValue(aName, out var ai) ? ai : int.MaxValue;
                var bOrder = orderMap.TryGetValue(bName, out var bi) ? bi : int.MaxValue;
                return aOrder.CompareTo(bOrder);
            });

            var requiredSlots = candidates.Count;
            var emptySlots = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (items.GetValue(x, y) == null)
                        emptySlots++;
                }
            }

            if (emptySlots < requiredSlots)
            {
                var extra = requiredSlots - emptySlots;
                var extraRows = (int)Math.Ceiling(extra / (double)Math.Max(width, 1));
                var newHeight = height + Math.Max(1, extraRows);
                var expanded = Array.CreateInstance(itemType, width, newHeight);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                        expanded.SetValue(items.GetValue(x, y), x, y);
                }
                items = expanded;
                height = newHeight;
                itemsField.SetValue(screen, items);
                heightField.SetValue(screen, height);
            }

            var injected = 0;
            foreach (var gun in candidates)
            {
                var placed = false;
                for (var y = 0; y < height && !placed; y++)
                {
                    for (var x = 0; x < width && !placed; x++)
                    {
                        if (items.GetValue(x, y) != null)
                            continue;

                        var gunX = Convert.ToInt32(gunXProp.GetValue(gun, null));
                        var gunY = Convert.ToInt32(gunYProp.GetValue(gun, null)) + 1;
                        var item = itemCtor.Invoke(new[] { (object)gunX, (object)gunY, gun, (object)false, (object)true });
                        items.SetValue(item, x, y);
                        injected++;
                        placed = true;
                    }
                }
            }

            if (injected > 0)
            {
                itemsField.SetValue(screen, items);
                log?.LogInfo($"Injected {injected} missing staged custom gun(s) into Xbox store ({contextLabel}).");
            }
        }

        private static void SortVisibleGunItemsByPrice(object screen, string contextLabel)
        {
            if (screen == null)
                return;

            var screenType = screen.GetType();
            var itemsField = AccessTools.Field(screenType, "mItems");
            if (itemsField == null)
                return;

            var items = itemsField.GetValue(screen) as Array;
            if (items == null || items.Rank != 2)
                return;

            var width = items.GetLength(0);
            var height = items.GetLength(1);
            if (width <= 0 || height <= 0)
                return;

            var itemType = AccessTools.TypeByName("ZombieEstate2.StoreScreen.XboxStore.XboxItem");
            var gunStatsType = AccessTools.TypeByName("ZombieEstate2.GunStats");
            if (itemType == null || gunStatsType == null)
                return;

            var tagProp = AccessTools.Property(itemType, "Tag");
            var costProp = AccessTools.Property(gunStatsType, "Cost");
            var costField = costProp == null ? AccessTools.Field(gunStatsType, "Cost") : null;
            if (tagProp == null || (costProp == null && costField == null))
                return;

            var slots = new List<(int x, int y)>();
            var visibleGuns = new List<VisibleGunItem>();
            var originalIndex = 0;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var cell = items.GetValue(x, y);
                    if (cell == null)
                        continue;

                    var tag = tagProp.GetValue(cell, null);
                    if (tag == null || !gunStatsType.IsInstanceOfType(tag))
                        continue;

                    var costObj = costProp != null ? costProp.GetValue(tag, null) : costField.GetValue(tag);
                    var cost = 0;
                    var parsedCost = costObj != null && int.TryParse(costObj.ToString(), out cost);

                    slots.Add((x, y));
                    visibleGuns.Add(new VisibleGunItem
                    {
                        Cell = cell,
                        Cost = parsedCost ? cost : int.MaxValue,
                        OriginalIndex = originalIndex++,
                        PositiveCost = parsedCost && cost > 0
                    });
                }
            }

            if (visibleGuns.Count <= 1)
                return;

            visibleGuns.Sort((a, b) =>
            {
                if (a.PositiveCost != b.PositiveCost)
                    return a.PositiveCost ? -1 : 1;

                var costCmp = a.Cost.CompareTo(b.Cost);
                if (costCmp != 0)
                    return costCmp;

                return a.OriginalIndex.CompareTo(b.OriginalIndex);
            });

            for (var i = 0; i < slots.Count && i < visibleGuns.Count; i++)
            {
                var slot = slots[i];
                items.SetValue(visibleGuns[i].Cell, slot.x, slot.y);
            }

            itemsField.SetValue(screen, items);
            log?.LogInfo($"Sorted {visibleGuns.Count} visible gun item(s) by price in Xbox store ({contextLabel}).");
        }

        private static List<string> BuildStagedGunDisplayOrder()
        {
            var ordered = new List<string>();
            if (stagedGunNames == null)
                return ordered;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stem in stagedGunNames)
            {
                if (string.IsNullOrWhiteSpace(stem))
                    continue;

                var trimmed = stem.Trim();
                if (trimmed.Length == 0)
                    continue;

                var display = ResolveGunDisplayNameFromData(trimmed);
                if (string.IsNullOrWhiteSpace(display))
                    display = trimmed;

                if (seen.Add(display))
                    ordered.Add(display);
            }

            return ordered;
        }

        private static System.Collections.IList ResolveRuntimeGunList(object fallback)
        {
            var loaderType = AccessTools.TypeByName("ZombieEstate2.GunStatsLoader");
            var listField = loaderType == null ? null : AccessTools.Field(loaderType, "GunStatsList");
            var runtime = listField?.GetValue(null) as System.Collections.IList;
            if (runtime != null)
                return runtime;

            return fallback as System.Collections.IList;
        }

        private static void EnsureRuntimeCustomGunRegistry(string context)
        {
            try
            {
                if (!loggedRuntimeGunEnsure)
                {
                    log?.LogInfo($"Runtime custom gun ensure active (trigger: {context}).");
                    loggedRuntimeGunEnsure = true;
                }

                LoadAllGunsPostfix();
                LoadBulletsPostfix();
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Runtime custom gun ensure failed at {context}: {ex.Message}");
            }
        }

        private static void GameLoadContentPostfix(object __instance)
        {
            try
            {
                if (legacySpriteMergeCompleted)
                    return;
                var graphicsDevice = __instance?.GetType()
                    .GetProperty("GraphicsDevice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(__instance, null);
                TryRunLegacySpriteMerge(graphicsDevice, __instance);
            }
            catch (Exception ex)
            {
                log?.LogWarning($"GameLoadContentPostfix failed: {ex.Message}");
            }
        }

        private static void GameUpdatePostfix()
        {
            if (!updateHookSeen)
            {
                updateHookSeen = true;
                log?.LogInfo("Game1.Update postfix executing.");
            }
            TryRunLegacySpriteMerge(null, null);
        }

        private static bool EnsureLegacyBridgeResolved()
        {
            if (legacyBridgeResolved)
                return legacyBootstrapType != null;

            legacyBridgeResolved = true;
            try
            {
                legacyBridgeAssembly = typeof(ZE2ModLoader.ModBootstrap).Assembly;
                legacyBootstrapType = typeof(ZE2ModLoader.ModBootstrap);
                if (legacyBootstrapType == null)
                {
                    log?.LogWarning("Integrated bridge type not found: ZE2ModLoader.ModBootstrap.");
                    return false;
                }

                legacyInitializeMethod = AccessTools.Method(legacyBootstrapType, "Initialize", new[] { typeof(object) });
                legacyLoadCustomCharactersMethod = AccessTools.Method(legacyBootstrapType, "LoadCustomCharacters", new[] { typeof(object) });
                legacyLoadCustomGunsMethod = AccessTools.Method(legacyBootstrapType, "LoadCustomGuns", new[] { typeof(object), typeof(object), typeof(object) });
                legacyLoadCustomBulletsMethod = AccessTools.Method(legacyBootstrapType, "LoadCustomBullets", new[] { typeof(object), typeof(object), typeof(object) });
                legacyAddCustomMapsToLevelSelectMethod = AccessTools.Method(legacyBootstrapType, "AddCustomMapsToLevelSelect", new[] { typeof(object) });
                legacyAddCustomMapsToXboxLevelSelectMethod = AccessTools.Method(legacyBootstrapType, "AddCustomMapsToXboxLevelSelect", new[] { typeof(object) });
                legacyStartCustomMapFromLevelSelectMethod = AccessTools.Method(legacyBootstrapType, "StartCustomMapFromLevelSelect", new[] { typeof(object), typeof(string) });

                if (legacyInitializeMethod == null)
                    log?.LogWarning("Legacy bridge Initialize(object) method not found.");

                log?.LogInfo("Integrated bridge methods resolved.");
                return legacyInitializeMethod != null;
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Legacy bridge resolution failed: {ex.Message}");
                return false;
            }
        }

        private static void TryInvokeLegacyAddCustomMapsToLevelSelect(object levelSelect)
        {
            try
            {
                if (levelSelect == null || !EnsureLegacyBridgeResolved() || legacyAddCustomMapsToLevelSelectMethod == null)
                    return;
                legacyAddCustomMapsToLevelSelectMethod.Invoke(null, new[] { levelSelect });
                log?.LogInfo("Legacy bridge applied custom maps to PC level select.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Legacy AddCustomMapsToLevelSelect failed: {ex.Message}");
            }
        }

        private static void TryInvokeLegacyAddCustomMapsToXboxLevelSelect(object levelSelect)
        {
            try
            {
                if (levelSelect == null || !EnsureLegacyBridgeResolved() || legacyAddCustomMapsToXboxLevelSelectMethod == null)
                    return;
                legacyAddCustomMapsToXboxLevelSelectMethod.Invoke(null, new[] { levelSelect });
                log?.LogInfo("Legacy bridge applied custom maps to Xbox level select.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Legacy AddCustomMapsToXboxLevelSelect failed: {ex.Message}");
            }
        }

        private static void LevelSelectSetupPostfix(object __instance)
        {
            TryInvokeLegacyAddCustomMapsToLevelSelect(__instance);
            TryInjectCustomMapsIntoPcLevelSelect(__instance);
        }

        private static void XboxLevelSelectSetupPostfix(object __instance)
        {
            TryInvokeLegacyAddCustomMapsToXboxLevelSelect(__instance);
            TryInjectCustomMapsIntoXboxLevelSelect(__instance);
        }

        private static bool LevelSelectGoPressedPrefix(object __instance)
        {
            try
            {
                if (__instance == null)
                    return true;

                var levelSelField = __instance.GetType().GetField("LevelSel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var levelSel = levelSelField?.GetValue(__instance);
                var selected = levelSel?.GetType()
                    .GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(levelSel, null) as string;

                if (string.IsNullOrWhiteSpace(selected) || KnownBasePcLevelNames.Contains(selected))
                    return true;

                var customPrefixes = DiscoverCustomMapPrefixesFromDataLevels();
                var resolvedPrefix = ResolveCustomPrefixFromSelection(selected, customPrefixes);
                if (string.IsNullOrWhiteSpace(resolvedPrefix))
                    resolvedPrefix = selected;

                if (EnsureLegacyBridgeResolved() && legacyStartCustomMapFromLevelSelectMethod != null)
                {
                    legacyStartCustomMapFromLevelSelectMethod.Invoke(null, new object[] { __instance, resolvedPrefix });
                    log?.LogInfo($"Legacy bridge started custom PC map: '{resolvedPrefix}' (selected '{selected}').");
                    return false;
                }

                var itemSelected = AccessTools.Method(__instance.GetType(), "ItemSelected", new[] { typeof(string) });
                if (itemSelected != null)
                {
                    // Fallback path: pass the selected map text through the stock selector flow.
                    itemSelected.Invoke(__instance, new object[] { resolvedPrefix });
                    log?.LogInfo($"Fallback custom PC map start path invoked via ItemSelected('{resolvedPrefix}') (selected '{selected}').");
                    return false;
                }

                log?.LogWarning($"Custom PC map selected but no start path available: '{selected}' (resolved '{resolvedPrefix}').");
                return false;
            }
            catch (Exception ex)
            {
                log?.LogWarning($"LevelSelectGoPressedPrefix failed: {ex.Message}");
                return true;
            }
        }

        private static List<string> DiscoverCustomMapPrefixesFromDataLevels()
        {
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var levelsDir = Path.Combine(gameRoot, "Data", "Levels");
                if (!Directory.Exists(levelsDir))
                    return prefixes.ToList();

                foreach (var pathFile in Directory.GetFiles(levelsDir, "*_Path.txt", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileName(pathFile);
                    if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith("_Path.txt", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var prefix = fileName.Substring(0, fileName.Length - "_Path.txt".Length);
                    if (!string.IsNullOrWhiteSpace(prefix) && !KnownBaseMapPrefixes.Contains(prefix))
                        prefixes.Add(prefix);
                }

                foreach (var xmlFile in Directory.GetFiles(levelsDir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileNameWithoutExtension(xmlFile);
                    if (string.IsNullOrWhiteSpace(name) || name.IndexOf('_') >= 0)
                        continue;

                    var i = name.Length - 1;
                    while (i >= 0 && char.IsDigit(name[i]))
                        i--;
                    if (i < 0 || i == name.Length - 1)
                        continue;

                    var prefix = name.Substring(0, i + 1);
                    if (!string.IsNullOrWhiteSpace(prefix) && !KnownBaseMapPrefixes.Contains(prefix))
                        prefixes.Add(prefix);
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Custom map prefix discovery failed: {ex.Message}");
            }

            return prefixes.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ResolveCustomPrefixFromSelection(string selected, List<string> customPrefixes)
        {
            if (string.IsNullOrWhiteSpace(selected))
                return string.Empty;
            if (customPrefixes == null || customPrefixes.Count == 0)
                return selected;

            foreach (var prefix in customPrefixes)
            {
                if (string.Equals(prefix, selected, StringComparison.OrdinalIgnoreCase))
                    return prefix;
                var display = ToDisplayMapName(prefix);
                if (string.Equals(display, selected, StringComparison.OrdinalIgnoreCase))
                    return prefix;
            }

            return selected;
        }

        private static string ToDisplayMapName(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                return prefix ?? string.Empty;
            var chars = new List<char>(prefix.Length + 8);
            chars.Add(prefix[0]);
            for (var i = 1; i < prefix.Length; i++)
            {
                var c = prefix[i];
                var p = prefix[i - 1];
                if (char.IsUpper(c) && (char.IsLower(p) || char.IsDigit(p)))
                    chars.Add(' ');
                chars.Add(c);
            }
            return new string(chars.ToArray());
        }

        private static void TryInjectCustomMapsIntoPcLevelSelect(object levelSelect)
        {
            try
            {
                if (levelSelect == null)
                    return;
                var prefixes = DiscoverCustomMapPrefixesFromDataLevels();
                if (prefixes.Count == 0)
                    return;

                var levelSelField = levelSelect.GetType().GetField("LevelSel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var levelSel = levelSelField?.GetValue(levelSelect);
                if (levelSel == null)
                    return;

                var valuesField = levelSel.GetType().GetField("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(valuesField?.GetValue(levelSel) is System.Collections.IList values))
                    return;

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in values)
                {
                    if (item is string s && !string.IsNullOrWhiteSpace(s))
                        existing.Add(s);
                }

                var added = 0;
                foreach (var prefix in prefixes)
                {
                    var display = ToDisplayMapName(prefix);
                    if (existing.Contains(display) || existing.Contains(prefix))
                        continue;
                    values.Add(display);
                    existing.Add(display);
                    added++;
                }

                if (added > 0)
                    log?.LogInfo($"Injected {added} custom map option(s) into PC level selector fallback.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"PC level map fallback injection failed: {ex.Message}");
            }
        }

        private static void TryInjectCustomMapsIntoXboxLevelSelect(object levelSelect)
        {
            try
            {
                if (levelSelect == null)
                    return;
                var prefixes = DiscoverCustomMapPrefixesFromDataLevels();
                if (prefixes.Count == 0)
                    return;

                var namesField = levelSelect.GetType().GetField("LevelNames", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (!(namesField?.GetValue(levelSelect) is System.Collections.IList levelNames))
                    return;

                var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in levelNames)
                {
                    if (item is string s && !string.IsNullOrWhiteSpace(s))
                        existing.Add(s);
                }

                var added = 0;
                foreach (var prefix in prefixes)
                {
                    if (existing.Contains(prefix))
                        continue;
                    levelNames.Add(prefix);
                    existing.Add(prefix);
                    added++;
                }

                if (added > 0)
                    log?.LogInfo($"Injected {added} custom map prefix(es) into Xbox level list fallback.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Xbox level map fallback injection failed: {ex.Message}");
            }
        }

        private static void TryInvokeLegacyLoadCustomCharacters(object characters)
        {
            try
            {
                if (characters == null || !EnsureLegacyBridgeResolved() || legacyLoadCustomCharactersMethod == null)
                    return;
                var before = characters is System.Collections.ICollection c ? c.Count : -1;
                legacyLoadCustomCharactersMethod.Invoke(null, new[] { characters });
                var after = characters is System.Collections.ICollection c2 ? c2.Count : -1;
                if (!legacyCharactersAppliedLogged)
                {
                    legacyCharactersAppliedLogged = true;
                    if (before >= 0 && after >= 0)
                        log?.LogInfo($"Legacy bridge applied custom characters (count {before} -> {after}).");
                    else
                        log?.LogInfo("Legacy bridge applied custom characters.");
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Legacy LoadCustomCharacters failed: {ex.Message}");
            }
        }

        private static void TryInvokeLegacyLoadCustomGuns(object guns, object uidGuns, object uidGunsRev)
        {
            try
            {
                if (guns == null || uidGuns == null || uidGunsRev == null || !EnsureLegacyBridgeResolved() || legacyLoadCustomGunsMethod == null)
                    return;
                var before = guns is System.Collections.ICollection c ? c.Count : -1;
                legacyLoadCustomGunsMethod.Invoke(null, new[] { guns, uidGuns, uidGunsRev });
                var after = guns is System.Collections.ICollection c2 ? c2.Count : -1;
                if (!legacyGunsAppliedLogged)
                {
                    legacyGunsAppliedLogged = true;
                    if (before >= 0 && after >= 0)
                        log?.LogInfo($"Legacy bridge applied custom guns (count {before} -> {after}).");
                    else
                        log?.LogInfo("Legacy bridge applied custom guns.");
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Legacy LoadCustomGuns failed: {ex.Message}");
            }
        }

        private static void TryInvokeLegacyLoadCustomBullets(object bullets, object uidBullets, object uidBulletsRev)
        {
            try
            {
                if (bullets == null || uidBullets == null || uidBulletsRev == null || !EnsureLegacyBridgeResolved() || legacyLoadCustomBulletsMethod == null)
                    return;
                var before = bullets is System.Collections.ICollection c ? c.Count : -1;
                legacyLoadCustomBulletsMethod.Invoke(null, new[] { bullets, uidBullets, uidBulletsRev });
                var after = bullets is System.Collections.ICollection c2 ? c2.Count : -1;
                if (!legacyBulletsAppliedLogged)
                {
                    legacyBulletsAppliedLogged = true;
                    if (before >= 0 && after >= 0)
                        log?.LogInfo($"Legacy bridge applied custom bullets (count {before} -> {after}).");
                    else
                        log?.LogInfo("Legacy bridge applied custom bullets.");
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Legacy LoadCustomBullets failed: {ex.Message}");
            }
        }

        private static void GotoCharSelectPrefix(object __instance)
        {
            try
            {
                TryRunLegacySpriteMerge(null, __instance);

                var keeperType = AccessTools.TypeByName("ZombieEstate2.PlayerStatKeeper");
                var listField = keeperType == null ? null : AccessTools.Field(keeperType, "CharacterSettings");
                var characters = listField?.GetValue(null);
                TryInvokeLegacyLoadCustomCharacters(characters);

                var gunLoaderType = AccessTools.TypeByName("ZombieEstate2.GunStatsLoader");
                var guns = gunLoaderType == null ? null : AccessTools.Field(gunLoaderType, "GunStatsList")?.GetValue(null);
                var uidGuns = gunLoaderType == null ? null : AccessTools.Field(gunLoaderType, "UID_Guns")?.GetValue(null);
                var uidGunsRev = gunLoaderType == null ? null : AccessTools.Field(gunLoaderType, "UID_GunsRev")?.GetValue(null);
                TryInvokeLegacyLoadCustomGuns(guns, uidGuns, uidGunsRev);

                var bulletType = AccessTools.TypeByName("ZombieEstate2.BulletCreator");
                var bullets = bulletType == null ? null : AccessTools.Field(bulletType, "bulletStats")?.GetValue(null);
                var uidBullets = bulletType == null ? null : AccessTools.Field(bulletType, "UID_Bullets")?.GetValue(null);
                var uidBulletsRev = bulletType == null ? null : AccessTools.Field(bulletType, "UID_BulletsRev")?.GetValue(null);
                TryInvokeLegacyLoadCustomBullets(bullets, uidBullets, uidBulletsRev);
            }
            catch (Exception ex)
            {
                log?.LogWarning($"GotoCharSelect legacy preload failed: {ex.Message}");
            }
        }

        private static object TryResolveGraphicsDevice(object context)
        {
            // Resolve from the most specific object first (current UI/game instance).
            var fromContext = TryExtractGraphicsDevice(context);
            if (fromContext != null)
                return fromContext;

            var globalType = AccessTools.TypeByName("ZombieEstate2.Global");
            if (globalType != null)
            {
                foreach (var fieldName in new[]
                {
                    "MasterTexture", "MasterEnvTex", "MasterLightTex", "MasterLightTexTransparent", "MenuBG", "WaveHUD", "Pixel"
                })
                {
                    var value = AccessTools.Field(globalType, fieldName)?.GetValue(null);
                    var device = TryExtractGraphicsDevice(value);
                    if (device != null)
                        return device;
                }
            }

            return null;
        }

        private static object TryExtractGraphicsDevice(object candidate)
        {
            if (candidate == null)
                return null;

            var type = candidate.GetType();
            if (string.Equals(type.FullName, "Microsoft.Xna.Framework.Graphics.GraphicsDevice", StringComparison.Ordinal))
                return candidate;

            var direct = type.GetProperty("GraphicsDevice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(candidate, null);
            if (direct != null)
                return direct;

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var fullName = field.FieldType.FullName;
                if (string.IsNullOrWhiteSpace(fullName) || fullName.IndexOf("Texture2D", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                var value = field.GetValue(candidate);
                var device = value?.GetType().GetProperty("GraphicsDevice", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value, null);
                if (device != null)
                    return device;
            }

            return null;
        }

        private static void TryRunLegacySpriteMerge(object graphicsDevice, object context)
        {
            try
            {
                if (legacySpriteMergeCompleted)
                    return;

                if (!EnsureLegacyBridgeResolved())
                    return;

                if (graphicsDevice == null)
                {
                    graphicsDevice = TryResolveGraphicsDevice(context);
                }

                if (graphicsDevice == null)
                {
                    if (!legacySpriteInitAttempted)
                    {
                        log?.LogWarning("Legacy sprite merge deferred: GraphicsDevice unavailable.");
                        legacySpriteInitAttempted = true;
                    }
                    return;
                }

                legacySpriteInitAttempted = true;
                var modsRoot = Path.Combine(gameRoot, "Mods");
                if (!Directory.Exists(modsRoot))
                {
                    log?.LogWarning("Legacy sprite merge skipped: no Mods directory present.");
                    return;
                }

                if (legacyInitializeMethod == null)
                {
                    log?.LogWarning("Legacy sprite merge skipped: Initialize method not found.");
                    return;
                }

                legacyInitializeMethod.Invoke(null, new[] { graphicsDevice });
                legacySpriteMergeCompleted = true;
                log?.LogInfo("Legacy sprite merge initialization invoked.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Legacy sprite merge invocation failed: {ex.Message}");
            }
        }

        private static void LoadAllGunsPostfix()
        {
            try
            {
                TryRunLegacySpriteMerge(null, null);

                if (stagedGunNames == null || stagedGunNames.Count == 0)
                    return;
                log?.LogInfo($"LoadAllGunsPostfix invoked; staged custom guns: {stagedGunNames.Count}.");

                var loaderType = AccessTools.TypeByName("ZombieEstate2.GunStatsLoader");
                if (loaderType == null)
                    return;

                var loadGunMethod = AccessTools.Method(loaderType, "LoadGun", new[] { typeof(string) });
                var listField = AccessTools.Field(loaderType, "GunStatsList");
                var uidField = AccessTools.Field(loaderType, "UID_Guns");
                var uidRevField = AccessTools.Field(loaderType, "UID_GunsRev");
                if (loadGunMethod == null || listField == null)
                    return;

                var gunList = listField.GetValue(null) as System.Collections.IList;
                if (gunList == null)
                    return;
                var uidForwardObj = uidField?.GetValue(null);
                var uidReverseObj = uidRevField?.GetValue(null);

                TryInvokeLegacyLoadCustomGuns(gunList, uidForwardObj, uidReverseObj);

                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var gunNameProp = AccessTools.Property(AccessTools.TypeByName("ZombieEstate2.GunStats"), "GunName");
                var gunStatsType = AccessTools.TypeByName("ZombieEstate2.GunStats");
                var stagedOrderedStems = stagedGunNames
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var g in gunList)
                {
                    var name = gunNameProp?.GetValue(g, null) as string;
                    if (!string.IsNullOrWhiteSpace(name))
                        existingNames.Add(name);
                }

                var canonicalByStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var firstStemForCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var stem in stagedOrderedStems)
                {
                    var canonical = ResolveGunDisplayNameFromData(stem);
                    if (string.IsNullOrWhiteSpace(canonical))
                        canonical = stem;
                    canonicalByStem[stem] = canonical;
                    if (!firstStemForCanonical.ContainsKey(canonical))
                        firstStemForCanonical[canonical] = stem;
                }

                var added = 0;
                foreach (var gunName in stagedOrderedStems)
                {
                    var stem = gunName.Trim();
                    var canonical = canonicalByStem.TryGetValue(stem, out var cn) ? cn : stem;
                    var hasStem = existingNames.Contains(stem);
                    var hasCanonical = existingNames.Contains(canonical);
                    var isAdditionalAlias = hasCanonical &&
                                            !hasStem &&
                                            firstStemForCanonical.TryGetValue(canonical, out var firstStem) &&
                                            !string.Equals(firstStem, stem, StringComparison.OrdinalIgnoreCase);

                    if (hasStem || (hasCanonical && !isAdditionalAlias))
                        continue;

                    var gunStats = loadGunMethod.Invoke(null, new object[] { gunName });
                    if (gunStats == null)
                    {
                        gunStats = LoadGunFromDataFile(gunName, gunStatsType);
                        if (gunStats != null)
                            log?.LogInfo($"Loaded custom gun '{gunName}' via XML fallback.");
                    }
                    if (gunStats == null)
                    {
                        log?.LogWarning($"Gun registry add failed for '{gunName}' (LoadGun returned null).");
                        continue;
                    }

                    if (!gunList.Contains(gunStats))
                        gunList.Add(gunStats);
                    var loadedName = gunNameProp?.GetValue(gunStats, null) as string;
                    if (string.IsNullOrWhiteSpace(loadedName))
                        loadedName = gunName;

                    if (isAdditionalAlias)
                    {
                        try
                        {
                            gunNameProp?.SetValue(gunStats, stem, null);
                            loadedName = stem;
                            log?.LogWarning($"Resolved duplicate custom GunName '{canonical}' using alias '{stem}' so all custom weapons remain visible.");
                        }
                        catch (Exception aliasEx)
                        {
                            log?.LogWarning($"Failed applying alias '{stem}' for duplicate custom GunName '{canonical}': {aliasEx.Message}");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(loadedName) &&
                             existingNames.Contains(loadedName) &&
                             !loadedName.Equals(gunName, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            gunNameProp?.SetValue(gunStats, gunName, null);
                            log?.LogWarning($"Adjusted duplicate custom GunName '{loadedName}' to file-stem name '{gunName}' so both entries stay visible.");
                            loadedName = gunName;
                        }
                        catch (Exception renameEx)
                        {
                            log?.LogWarning($"Failed to adjust duplicate custom GunName '{loadedName}' -> '{gunName}': {renameEx.Message}");
                        }
                    }
                    existingNames.Add(loadedName);
                    added++;
                }

                var forcedVisible = 0;
                foreach (var stem in stagedOrderedStems)
                {
                    if (!TryReadGunCostFromData(stem, out var declaredCost) || declaredCost <= 0)
                        continue;

                    var canonical = canonicalByStem.TryGetValue(stem, out var cn2) ? cn2 : stem;
                    object bestMatch = null;
                    var bestCost = int.MinValue;

                    foreach (var g in gunList)
                    {
                        if (g == null)
                            continue;

                        var n = gunNameProp?.GetValue(g, null) as string;
                        if (string.IsNullOrWhiteSpace(n))
                            continue;

                        var matches = n.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                                      n.Equals(canonical, StringComparison.OrdinalIgnoreCase);
                        if (!matches)
                            continue;

                        if (!TryGetGunCostValue(g, out var c))
                            c = int.MinValue;

                        if (bestMatch != null && c <= bestCost)
                            continue;

                        bestMatch = g;
                        bestCost = c;
                    }

                    if (bestMatch != null && bestCost > 0)
                        continue;

                    var fallback = LoadGunFromDataFile(stem, gunStatsType) ?? loadGunMethod.Invoke(null, new object[] { stem });
                    if (fallback == null)
                    {
                        log?.LogWarning($"Could not promote staged gun '{stem}' for shop visibility (failed to load fallback).");
                        continue;
                    }

                    if (!gunList.Contains(fallback))
                        gunList.Add(fallback);

                    var fallbackName = gunNameProp?.GetValue(fallback, null) as string;
                    if (string.IsNullOrWhiteSpace(fallbackName))
                    {
                        fallbackName = canonical;
                        try
                        {
                            gunNameProp?.SetValue(fallback, fallbackName, null);
                        }
                        catch
                        {
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(fallbackName))
                        existingNames.Add(fallbackName);

                    forcedVisible++;
                    log?.LogInfo($"Promoted staged gun '{stem}' (declared cost {declaredCost}) to runtime registry for shop visibility.");
                }

                EnsureGunUids(uidForwardObj, uidReverseObj, existingNames);
                DeduplicateGunListByName(gunList, uidForwardObj, uidReverseObj);

                if (added > 0)
                    log?.LogInfo($"Injected {added} custom gun(s) into GunStatsLoader registry.");
                if (forcedVisible > 0)
                    log?.LogInfo($"Forced visibility reconciliation added {forcedVisible} staged gun(s) with positive declared cost.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"LoadAllGuns postfix failed: {ex.Message}");
            }
        }

        private static void LoadBulletsPostfix()
        {
            try
            {
                TryRunLegacySpriteMerge(null, null);

                if (stagedBulletNames == null || stagedBulletNames.Count == 0)
                    return;
                log?.LogInfo($"LoadBulletsPostfix invoked; staged custom bullets: {stagedBulletNames.Count}.");

                var creatorType = AccessTools.TypeByName("ZombieEstate2.BulletCreator");
                if (creatorType == null)
                    return;

                var loadBullMethod = AccessTools.Method(creatorType, "LoadBull", new[] { typeof(string) });
                var statsField = AccessTools.Field(creatorType, "bulletStats");
                var uidField = AccessTools.Field(creatorType, "UID_Bullets");
                var uidRevField = AccessTools.Field(creatorType, "UID_BulletsRev");
                if (loadBullMethod == null || statsField == null)
                    return;

                var statsDict = statsField.GetValue(null) as System.Collections.IDictionary;
                if (statsDict == null)
                    return;
                var uidForwardObj = uidField?.GetValue(null);
                var uidReverseObj = uidRevField?.GetValue(null);
                var bulletStatsType = AccessTools.TypeByName("ZombieEstate2.BulletStats");

                TryInvokeLegacyLoadCustomBullets(statsDict, uidForwardObj, uidReverseObj);

                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in statsDict.Keys)
                {
                    if (key is string s && !string.IsNullOrWhiteSpace(s))
                        existingNames.Add(s);
                }

                var added = 0;
                foreach (var bulletName in stagedBulletNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (existingNames.Contains(bulletName))
                        continue;

                    var bulletStats = loadBullMethod.Invoke(null, new object[] { bulletName });
                    if (bulletStats == null)
                    {
                        bulletStats = LoadBulletFromDataFile(bulletName, bulletStatsType);
                        if (bulletStats != null)
                            log?.LogInfo($"Loaded custom bullet '{bulletName}' via XML fallback.");
                    }
                    if (bulletStats == null)
                    {
                        log?.LogWarning($"Bullet registry add failed for '{bulletName}' (LoadBull returned null).");
                        continue;
                    }

                    if (!statsDict.Contains(bulletName))
                        statsDict[bulletName] = bulletStats;
                    existingNames.Add(bulletName);
                    added++;
                }

                EnsureBulletUids(uidForwardObj, uidReverseObj, existingNames);
                DeduplicateBulletStatsByName(statsDict, uidForwardObj, uidReverseObj);

                if (added > 0)
                    log?.LogInfo($"Injected {added} custom bullet(s) into BulletCreator registry.");
            }
            catch (Exception ex)
            {
                log?.LogWarning($"LoadBullets postfix failed: {ex.Message}");
            }
        }

        private static HashSet<string> BuildStagedGunNameSet(IEnumerable<string> names)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (names == null)
                return set;

            foreach (var rawName in names)
            {
                if (string.IsNullOrWhiteSpace(rawName))
                    continue;

                var stem = rawName.Trim();
                if (stem.Length == 0)
                    continue;

                set.Add(stem);

                try
                {
                    if (string.IsNullOrWhiteSpace(gameRoot))
                        continue;

                    var path = Path.Combine(gameRoot, "Data", "Guns", stem + ".gun");
                    if (!File.Exists(path))
                        continue;

                    var doc = XDocument.Load(path);
                    var gunName = doc.Root?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "GunName", StringComparison.OrdinalIgnoreCase))?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(gunName))
                        set.Add(gunName);
                }
                catch
                {
                }
            }

            return set;
        }

        private static string ResolveGunDisplayNameFromData(string stem)
        {
            if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(gameRoot))
                return string.Empty;

            try
            {
                var path = Path.Combine(gameRoot, "Data", "Guns", stem + ".gun");
                if (!File.Exists(path))
                    return string.Empty;

                var doc = XDocument.Load(path);
                var gunName = doc.Root?.Elements()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "GunName", StringComparison.OrdinalIgnoreCase))
                    ?.Value?.Trim();
                return gunName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static object LoadGunFromDataFile(string gunName, Type gunStatsType)
        {
            if (string.IsNullOrWhiteSpace(gunName) || gunStatsType == null || string.IsNullOrWhiteSpace(gameRoot))
                return null;

            var path = Path.Combine(gameRoot, "Data", "Guns", gunName + ".gun");
            if (!File.Exists(path))
                return null;

            try
            {
                var serializer = new XmlSerializer(gunStatsType);
                using var stream = File.OpenRead(path);
                return serializer.Deserialize(stream);
            }
            catch (Exception ex)
            {
                log?.LogWarning($"XML fallback failed for gun '{gunName}': {ex.Message}");
                return null;
            }
        }

        private static object LoadBulletFromDataFile(string bulletName, Type bulletStatsType)
        {
            if (string.IsNullOrWhiteSpace(bulletName) || bulletStatsType == null || string.IsNullOrWhiteSpace(gameRoot))
                return null;

            var path = Path.Combine(gameRoot, "Data", "Bullets", bulletName + ".bul");
            if (!File.Exists(path))
                return null;

            try
            {
                var serializer = new XmlSerializer(bulletStatsType);
                using var stream = File.OpenRead(path);
                return serializer.Deserialize(stream);
            }
            catch (Exception ex)
            {
                log?.LogWarning($"XML fallback failed for bullet '{bulletName}': {ex.Message}");
                return null;
            }
        }

        private static bool TryReadGunCostFromData(string stem, out int cost)
        {
            cost = 0;
            if (string.IsNullOrWhiteSpace(stem) || string.IsNullOrWhiteSpace(gameRoot))
                return false;

            try
            {
                var path = Path.Combine(gameRoot, "Data", "Guns", stem + ".gun");
                if (!File.Exists(path))
                    return false;

                var doc = XDocument.Load(path);
                var raw = doc.Root?.Elements()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "Cost", StringComparison.OrdinalIgnoreCase))
                    ?.Value?.Trim();
                return int.TryParse(raw, out cost);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetGunCostValue(object gunStats, out int cost)
        {
            cost = 0;
            if (gunStats == null)
                return false;

            var gunType = gunStats.GetType();
            object raw = null;
            var costProp = AccessTools.Property(gunType, "Cost");
            if (costProp != null)
                raw = costProp.GetValue(gunStats, null);
            if (raw == null)
            {
                var costField = AccessTools.Field(gunType, "Cost");
                if (costField != null)
                    raw = costField.GetValue(gunStats);
            }

            return raw != null && int.TryParse(raw.ToString(), out cost);
        }

        private static void RemoveSpriteBoundGunsFromRuntime(System.Collections.IList gunList, object uidForwardObj, object uidReverseObj)
        {
            if (gunList == null || stagedSpriteBoundGunNames == null || stagedSpriteBoundGunNames.Count == 0)
                return;

            var gunStatsType = AccessTools.TypeByName("ZombieEstate2.GunStats");
            var gunNameProp = AccessTools.Property(gunStatsType, "GunName");
            if (gunNameProp == null)
                return;

            var removed = 0;
            for (var i = gunList.Count - 1; i >= 0; i--)
            {
                var item = gunList[i];
                var name = item == null ? null : gunNameProp.GetValue(item, null) as string;
                if (string.IsNullOrWhiteSpace(name) || !stagedSpriteBoundGunNames.Contains(name))
                    continue;
                gunList.RemoveAt(i);
                removed++;
            }

            if (uidReverseObj is System.Collections.IDictionary uidReverse)
            {
                foreach (var name in stagedSpriteBoundGunNames)
                    uidReverse.Remove(name);
            }

            if (uidForwardObj is System.Collections.IDictionary uidForward)
            {
                var removeKeys = new List<object>();
                foreach (System.Collections.DictionaryEntry entry in uidForward)
                {
                    if (entry.Value is string name && stagedSpriteBoundGunNames.Contains(name))
                        removeKeys.Add(entry.Key);
                }
                foreach (var key in removeKeys)
                    uidForward.Remove(key);
            }

            if (removed > 0)
                log?.LogInfo($"Removed {removed} preloaded sprite-bound gun(s) before legacy reload.");
        }

        private static void DeduplicateGunListByName(System.Collections.IList gunList, object uidForwardObj, object uidReverseObj)
        {
            if (gunList == null)
                return;

            var gunStatsType = AccessTools.TypeByName("ZombieEstate2.GunStats");
            var gunNameProp = AccessTools.Property(gunStatsType, "GunName");
            if (gunNameProp == null)
                return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var removed = 0;

            for (var i = gunList.Count - 1; i >= 0; i--)
            {
                var item = gunList[i];
                var name = item == null ? null : gunNameProp.GetValue(item, null) as string;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var key = name.Trim();
                if (seen.Contains(key))
                {
                    gunList.RemoveAt(i);
                    removed++;
                    continue;
                }

                seen.Add(key);
            }

            if (removed > 0)
                log?.LogInfo($"Deduplicated gun registry entries by GunName: removed {removed} duplicate(s).");

            EnsureGunUids(uidForwardObj, uidReverseObj, seen);
        }

        private static void RemoveSpriteBoundBulletsFromRuntime(System.Collections.IDictionary statsDict, object uidForwardObj, object uidReverseObj)
        {
            if (statsDict == null || stagedSpriteBoundBulletNames == null || stagedSpriteBoundBulletNames.Count == 0)
                return;

            var removed = 0;
            foreach (var name in stagedSpriteBoundBulletNames.ToArray())
            {
                if (statsDict.Contains(name))
                {
                    statsDict.Remove(name);
                    removed++;
                }
            }

            if (uidReverseObj is System.Collections.IDictionary uidReverse)
            {
                foreach (var name in stagedSpriteBoundBulletNames)
                    uidReverse.Remove(name);
            }

            if (uidForwardObj is System.Collections.IDictionary uidForward)
            {
                var removeKeys = new List<object>();
                foreach (System.Collections.DictionaryEntry entry in uidForward)
                {
                    if (entry.Value is string name && stagedSpriteBoundBulletNames.Contains(name))
                        removeKeys.Add(entry.Key);
                }
                foreach (var key in removeKeys)
                    uidForward.Remove(key);
            }

            if (removed > 0)
                log?.LogInfo($"Removed {removed} preloaded sprite-bound bullet(s) before legacy reload.");
        }

        private static void DeduplicateBulletStatsByName(System.Collections.IDictionary statsDict, object uidForwardObj, object uidReverseObj)
        {
            if (statsDict == null)
                return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var toRemove = new List<object>();

            foreach (var key in statsDict.Keys)
            {
                if (key is not string name || string.IsNullOrWhiteSpace(name))
                    continue;

                var normalized = name.Trim();
                if (seen.Contains(normalized))
                {
                    toRemove.Add(key);
                    continue;
                }

                seen.Add(normalized);
            }

            foreach (var key in toRemove)
                statsDict.Remove(key);

            if (toRemove.Count > 0)
                log?.LogInfo($"Deduplicated bullet registry entries by name: removed {toRemove.Count} duplicate(s).");

            EnsureBulletUids(uidForwardObj, uidReverseObj, seen);
        }

        private static void EnsureGunUids(object uidForwardObj, object uidReverseObj, HashSet<string> names)
        {
            if (uidForwardObj is not System.Collections.IDictionary uidForward || uidReverseObj is not System.Collections.IDictionary uidReverse)
                return;

            var max = short.MinValue;
            foreach (var key in uidForward.Keys)
            {
                try
                {
                    var value = Convert.ToInt16(key);
                    if (value > max)
                        max = value;
                }
                catch
                {
                }
            }

            var next = max < 0 ? (short)0 : (short)(max + 1);
            foreach (var name in names)
            {
                if (uidReverse.Contains(name))
                    continue;
                var uid = next++;
                uidForward[uid] = name;
                uidReverse[name] = uid;
            }
        }

        private static void EnsureBulletUids(object uidForwardObj, object uidReverseObj, HashSet<string> names)
        {
            if (uidForwardObj is not System.Collections.IDictionary uidForward || uidReverseObj is not System.Collections.IDictionary uidReverse)
                return;

            var max = -1;
            foreach (var key in uidForward.Keys)
            {
                try
                {
                    var value = Convert.ToInt32(key);
                    if (value > max)
                        max = value;
                }
                catch
                {
                }
            }

            var next = max < 0 ? 0 : max + 1;
            foreach (var name in names)
            {
                if (uidReverse.Contains(name))
                    continue;
                var uid = next++;
                uidForward[uid] = name;
                uidReverse[name] = uid;
            }
        }

        private static bool PopulateCharactersPrefix(object __instance, object[] __args)
        {
            try
            {
                TryRunLegacySpriteMerge(null, __instance);

                if (__instance == null || __args == null || __args.Length < 2 || !(__args[0] is System.Collections.IList list))
                    return true;
                TryInvokeLegacyLoadCustomCharacters(list);

                var t = __instance.GetType();
                var widthField = t.GetField("mWidth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var heightField = t.GetField("mHeight", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var itemsField = t.GetField("mItems", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (widthField == null || heightField == null || itemsField == null)
                    return true;

                var itemsArray = itemsField.GetValue(__instance) as Array;
                var width = (int)(widthField.GetValue(__instance) ?? 0);
                var height = (int)(heightField.GetValue(__instance) ?? 0);
                var originalWidth = itemsArray != null && itemsArray.Rank == 2 ? itemsArray.GetLength(0) : 0;
                var originalHeight = itemsArray != null && itemsArray.Rank == 2 ? itemsArray.GetLength(1) : 0;

                if (width <= 0 && itemsArray != null && itemsArray.Rank == 2)
                    width = itemsArray.GetLength(0);
                if (height <= 0 && itemsArray != null && itemsArray.Rank == 2)
                    height = itemsArray.GetLength(1);

                if (width <= 0 || list.Count <= 0)
                {
                    log?.LogInfo($"PopulateCharacters prefix pass-through (width={width}, listCount={list.Count}).");
                    return true;
                }

                var requiredHeight = (int)Math.Ceiling((double)list.Count / width);
                var targetHeight = Math.Max(height, requiredHeight);
                log?.LogInfo($"PopulateCharacters prefix: list={list.Count}, width={width}, heightField={height}, itemsDims={originalWidth}x{originalHeight}, requiredHeight={requiredHeight}, targetHeight={targetHeight}.");

                var itemsType = itemsField.FieldType;
                if (!itemsType.IsArray || itemsType.GetArrayRank() != 2)
                {
                    log?.LogWarning("PopulateCharacters prefix could not expand grid because mItems is not a 2D array.");
                    return true;
                }

                var elementType = itemsType.GetElementType();
                if (elementType == null)
                    return true;

                var currentHeight = itemsArray != null && itemsArray.Rank == 2 ? itemsArray.GetLength(1) : height;
                if (targetHeight <= currentHeight)
                {
                    log?.LogInfo($"PopulateCharacters prefix no-op (currentHeight={currentHeight}, targetHeight={targetHeight}).");
                    return true;
                }

                var newGrid = Array.CreateInstance(elementType, width, targetHeight);
                if (itemsArray != null && itemsArray.Rank == 2)
                {
                    var copyWidth = Math.Min(width, itemsArray.GetLength(0));
                    var copyHeight = Math.Min(targetHeight, itemsArray.GetLength(1));
                    for (var x = 0; x < copyWidth; x++)
                    {
                        for (var y = 0; y < copyHeight; y++)
                        {
                            newGrid.SetValue(itemsArray.GetValue(x, y), x, y);
                        }
                    }
                }

                itemsField.SetValue(__instance, newGrid);
                heightField.SetValue(__instance, targetHeight);
                log?.LogInfo($"Expanded character grid from height {currentHeight} to {targetHeight} for {list.Count} characters.");
                return true;
            }
            catch (Exception ex)
            {
                log?.LogWarning($"PopulateCharacters prefix failed: {ex.Message}");
                return true;
            }
        }

        private static Exception PopulateCharactersFinalizer(Exception __exception)
        {
            if (__exception is IndexOutOfRangeException)
            {
                log?.LogWarning("Suppressed PopulateCharacters IndexOutOfRangeException.");
                return null;
            }
            return __exception;
        }
    }
}
