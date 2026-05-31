using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace ZE2.ModLoader
{
    internal sealed class ModRecord
    {
        public string Id;
        public string Name;
        public string Folder;
        public string ManifestPath;
        public bool Enabled;
        public int LoadOrder;
        public readonly List<ModOption> Options = new List<ModOption>();
    }

    internal sealed class ModOption
    {
        public string Key;
        public string Name;
        public string Type;
        public string DefaultValue;
        public readonly List<string> Choices = new List<string>();
        public string Value;
    }

    internal static class ModManagerState
    {
        private static readonly string GameRoot = AppContext.BaseDirectory;
        private static readonly string ConfigPath = Path.Combine(GameRoot, "BepInEx", "config", "ZE2.ModManager.xml");

        public static List<ModRecord> Discover()
        {
            XDocument state = LoadState();
            Dictionary<string, XElement> stateMods = state.Root == null
                ? new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase)
                : state.Root.Elements("Mod")
                    .Where(x => !string.IsNullOrWhiteSpace((string)x.Attribute("Id")))
                    .GroupBy(x => (string)x.Attribute("Id"), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

            List<ModRecord> records = new List<ModRecord>();
            HashSet<string> seenManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in GetModRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string folder in Directory.GetDirectories(root))
                {
                    string manifest = Path.Combine(folder, "mod.xml");
                    if (!File.Exists(manifest) || !seenManifests.Add(Path.GetFullPath(manifest)))
                    {
                        continue;
                    }

                    ModRecord record = TryReadManifest(folder, manifest);
                    if (record == null)
                    {
                        continue;
                    }

                    XElement saved;
                    if (stateMods.TryGetValue(record.Id, out saved))
                    {
                        bool enabled;
                        int order;
                        if (bool.TryParse((string)saved.Attribute("Enabled"), out enabled))
                        {
                            record.Enabled = enabled;
                        }
                        if (int.TryParse((string)saved.Attribute("LoadOrder"), out order))
                        {
                            record.LoadOrder = order;
                        }

                        foreach (ModOption option in record.Options)
                        {
                            XElement savedOption = saved.Elements("Option")
                                .FirstOrDefault(x => string.Equals((string)x.Attribute("Key"), option.Key, StringComparison.OrdinalIgnoreCase));
                            if (savedOption != null)
                            {
                                option.Value = (string)savedOption.Attribute("Value") ?? option.Value;
                            }
                        }
                    }

                    records.Add(record);
                }
            }

            int fallback = 10000;
            foreach (ModRecord record in records.Where(x => x.LoadOrder <= 0).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                record.LoadOrder = fallback++;
            }

            return records
                .OrderBy(x => x.LoadOrder)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static void Save(List<ModRecord> records)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            XDocument doc = new XDocument(new XElement("Ze2ModManager",
                records.Select((record, index) => new XElement("Mod",
                    new XAttribute("Id", record.Id ?? ""),
                    new XAttribute("Name", record.Name ?? ""),
                    new XAttribute("Folder", record.Folder ?? ""),
                    new XAttribute("Enabled", record.Enabled),
                    new XAttribute("LoadOrder", index + 1),
                    record.Options.Select(option => new XElement("Option",
                        new XAttribute("Key", option.Key ?? ""),
                        new XAttribute("Name", option.Name ?? option.Key ?? ""),
                        new XAttribute("Type", option.Type ?? "String"),
                        new XAttribute("Value", option.Value ?? option.DefaultValue ?? "")))))));
            doc.Save(ConfigPath);

            for (int i = 0; i < records.Count; i++)
            {
                records[i].LoadOrder = i + 1;
                ApplyEnabledToManifest(records[i]);
            }
        }

        public static string ConfigFile
        {
            get { return ConfigPath; }
        }

        private static IEnumerable<string> GetModRoots()
        {
            yield return Path.Combine(GameRoot, "Mods");
            yield return Path.Combine(GameRoot, "BepInEx", "plugins", "Mods");
        }

        private static XDocument LoadState()
        {
            try
            {
                return File.Exists(ConfigPath) ? XDocument.Load(ConfigPath) : new XDocument(new XElement("Ze2ModManager"));
            }
            catch
            {
                return new XDocument(new XElement("Ze2ModManager"));
            }
        }

        private static ModRecord TryReadManifest(string folder, string manifestPath)
        {
            try
            {
                XDocument doc = XDocument.Load(manifestPath);
                XElement root = doc.Root;
                if (root == null)
                {
                    return null;
                }

                string rootName = root.Name.LocalName;
                string id = Text(root, "Id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = (string)root.Attribute("id") ?? (string)root.Attribute("name") ?? Path.GetFileName(folder);
                }

                string name = Text(root, "Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = (string)root.Attribute("name") ?? id;
                }

                bool enabled = true;
                string enabledText = Text(root, "Enabled");
                if (string.Equals(rootName, "Ze2XmlMod", StringComparison.OrdinalIgnoreCase))
                {
                    enabledText = (string)root.Attribute("enabled") ?? enabledText;
                }
                bool.TryParse(enabledText, out enabled);
                if (string.IsNullOrWhiteSpace(enabledText))
                {
                    enabled = true;
                }

                ModRecord record = new ModRecord
                {
                    Id = id.Trim(),
                    Name = name.Trim(),
                    Folder = folder,
                    ManifestPath = manifestPath,
                    Enabled = enabled
                };

                foreach (XElement option in root.Descendants("Option"))
                {
                    ModOption modOption = new ModOption
                    {
                        Key = ((string)option.Attribute("Key") ?? (string)option.Attribute("key") ?? "").Trim(),
                        Name = ((string)option.Attribute("Name") ?? (string)option.Attribute("name") ?? "").Trim(),
                        Type = ((string)option.Attribute("Type") ?? (string)option.Attribute("type") ?? "String").Trim(),
                        DefaultValue = ((string)option.Attribute("Default") ?? (string)option.Attribute("default") ?? "").Trim()
                    };
                    if (string.IsNullOrWhiteSpace(modOption.Key))
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(modOption.Name))
                    {
                        modOption.Name = modOption.Key;
                    }

                    string choices = (string)option.Attribute("Choices") ?? (string)option.Attribute("choices") ?? "";
                    foreach (string choice in choices.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        modOption.Choices.Add(choice.Trim());
                    }
                    foreach (XElement choice in option.Elements("Choice"))
                    {
                        string value = ((string)choice.Attribute("Value") ?? choice.Value ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            modOption.Choices.Add(value);
                        }
                    }
                    modOption.Value = modOption.DefaultValue;
                    record.Options.Add(modOption);
                }

                return record;
            }
            catch
            {
                return null;
            }
        }

        private static string Text(XElement root, string name)
        {
            XElement element = root.Element(name);
            return element == null ? null : element.Value;
        }

        private static void ApplyEnabledToManifest(ModRecord record)
        {
            try
            {
                XDocument doc = XDocument.Load(record.ManifestPath);
                XElement root = doc.Root;
                if (root == null)
                {
                    return;
                }

                if (string.Equals(root.Name.LocalName, "Ze2XmlMod", StringComparison.OrdinalIgnoreCase))
                {
                    root.SetAttributeValue("enabled", record.Enabled ? "true" : "false");
                }
                else
                {
                    XElement enabled = root.Element("Enabled");
                    if (enabled == null)
                    {
                        enabled = new XElement("Enabled");
                        root.AddFirst(enabled);
                    }
                    enabled.Value = record.Enabled ? "true" : "false";
                }

                doc.Save(record.ManifestPath);
            }
            catch
            {
            }
        }
    }
}
