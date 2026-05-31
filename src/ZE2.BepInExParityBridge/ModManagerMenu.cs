using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ZombieEstate2;

namespace ZE2.BepInExParityBridge
{
    internal sealed class ModManagerMenu : Menu
    {
        private const int PageSize = 5;
        private List<ModRecord> mods = new List<ModRecord>();
        private int page;
        private string status = "Changes are applied on the next game launch.";

        public ModManagerMenu()
            : base(true, new Vector2(Global.ScreenRect.Width / 2f, Global.ScreenRect.Height / 2f - 205f))
        {
        }

        public override void Setup()
        {
            title = "Mods";
            MenuBG = Global.MenuBG;
            DrawBGPixel = true;
            Refresh();
        }

        public override void UpdateMenu()
        {
            base.UpdateMenu();
        }

        public override void DrawMenu(SpriteBatch spriteBatch)
        {
            base.DrawMenu(spriteBatch);
            Rectangle panel = new Rectangle(Global.ScreenRect.Width / 2 + 180, Global.ScreenRect.Height / 2 - 160, 420, 280);
            spriteBatch.Draw(Global.Pixel, panel, Color.White);
            Rectangle inner = new Rectangle(panel.X + 2, panel.Y + 2, panel.Width - 4, panel.Height - 4);
            spriteBatch.Draw(Global.Pixel, inner, Color.Black * 0.9f);

            string text = "A toggles a mod. Left/Right changes load order. Configurable mods expose an Options entry. Enabled and order changes take effect after restart.\n\n"
                + status + "\n\nState file:\n" + ModManagerState.ConfigFile;
            Shadow.DrawString(text, Global.StoreFontSmall, new Vector2(inner.X + 12, inner.Y + 12), 1, Color.White, spriteBatch);
        }

        private void Refresh()
        {
            mods = ModManagerState.Discover();
            RebuildItems();
        }

        private void RebuildItems()
        {
            RESET();
            int start = page * PageSize;
            List<ModRecord> visible = mods.Skip(start).Take(PageSize).ToList();
            if (visible.Count == 0)
            {
                AddToMenu("No XML mods found", delegate { Refresh(); }, "No mod.xml files were found in the supported mod roots.");
            }
            else
            {
                foreach (ModRecord record in visible)
                {
                    ModRecord captured = record;
                    MenuItem item = AddToMenu(ModText(record), delegate
                    {
                        captured.Enabled = !captured.Enabled;
                        status = captured.Name + " is now " + (captured.Enabled ? "enabled" : "disabled") + ". Save and restart to apply.";
                        RebuildItems();
                    }, true);
                    item.SelectedFunction_Picker = delegate(bool positive)
                    {
                        MoveMod(captured, positive ? 1 : -1);
                    };
                    item.Description = "A: enable/disable. Left/Right: move in load order. Folder: " + captured.Folder;
                }
            }

            if (mods.Any(x => x.Options.Count > 0))
            {
                AddToMenu("Configure Mods", delegate
                {
                    MenuManager.PushMenu(new ModConfigIndexMenu(mods));
                }, "Edit options declared by mods under <Options>. Values are stored for mods to read.");
            }

            if (mods.Count > PageSize)
            {
                AddToMenu("Page " + (page + 1) + "/" + Math.Max(1, (mods.Count + PageSize - 1) / PageSize), delegate
                {
                    page++;
                    if (page * PageSize >= mods.Count)
                    {
                        page = 0;
                    }
                    RebuildItems();
                }, "Cycle through mod pages.");
            }

            AddToMenu("Save Changes", delegate
            {
                ModManagerState.Save(mods);
                status = "Saved. Restart the game to reload enabled mods and load order.";
                RebuildItems();
            }, "Writes enabled flags to mod.xml and saves load order/config values.");
        }

        private void MoveMod(ModRecord record, int delta)
        {
            int index = mods.IndexOf(record);
            int next = Math.Max(0, Math.Min(mods.Count - 1, index + delta));
            if (index == next)
            {
                return;
            }

            mods.RemoveAt(index);
            mods.Insert(next, record);
            for (int i = 0; i < mods.Count; i++)
            {
                mods[i].LoadOrder = i + 1;
            }
            status = "Moved " + record.Name + " to load order " + record.LoadOrder + ".";
            page = Math.Max(0, Math.Min(page, (mods.Count - 1) / PageSize));
            RebuildItems();
        }

        private static string ModText(ModRecord record)
        {
            return (record.Enabled ? "[ON] " : "[OFF] ") + record.LoadOrder.ToString("00") + " " + record.Name;
        }
    }

    internal sealed class ModConfigIndexMenu : Menu
    {
        private readonly List<ModRecord> mods;

        public ModConfigIndexMenu(List<ModRecord> mods)
            : base(true, new Vector2(Global.ScreenRect.Width / 2f, Global.ScreenRect.Height / 2f - 190f))
        {
            this.mods = mods;
            RESET();
            Setup();
        }

        public override void Setup()
        {
            title = "Mod Options";
            MenuBG = Global.MenuBG;
            DrawBGPixel = true;
            if (mods == null)
            {
                return;
            }
            foreach (ModRecord record in mods.Where(x => x.Options.Count > 0))
            {
                ModRecord captured = record;
                AddToMenu(record.Name, delegate
                {
                    MenuManager.PushMenu(new ModConfigMenu(mods, captured));
                }, record.Options.Count + " option(s).");
            }
        }
    }

    internal sealed class ModConfigMenu : Menu
    {
        private readonly List<ModRecord> mods;
        private readonly ModRecord mod;

        public ModConfigMenu(List<ModRecord> mods, ModRecord mod)
            : base(true, new Vector2(Global.ScreenRect.Width / 2f, Global.ScreenRect.Height / 2f - 190f))
        {
            this.mods = mods;
            this.mod = mod;
            RESET();
            Setup();
        }

        public override void Setup()
        {
            if (mod == null)
            {
                title = "Mod Options";
                MenuBG = Global.MenuBG;
                DrawBGPixel = true;
                return;
            }
            title = mod.Name;
            MenuBG = Global.MenuBG;
            DrawBGPixel = true;
            foreach (ModOption option in mod.Options)
            {
                ModOption captured = option;
                MenuItem item = AddToMenu(OptionText(option), delegate
                {
                    StepOption(captured, true);
                }, true);
                item.SelectedFunction_Picker = delegate(bool positive)
                {
                    StepOption(captured, positive);
                };
                item.Description = "Stored as " + captured.Key + " in ZE2.ModManager.xml.";
            }
            AddToMenu("Save Options", delegate
            {
                ModManagerState.Save(mods);
            }, "Save option values. Mods can read these values from the mod manager config file.");
        }

        private void StepOption(ModOption option, bool positive)
        {
            string type = option.Type == null ? "String" : option.Type.ToLowerInvariant();
            if (type == "bool" || type == "boolean")
            {
                bool current;
                bool.TryParse(option.Value, out current);
                option.Value = (!current).ToString().ToLowerInvariant();
            }
            else if (option.Choices.Count > 0)
            {
                int index = option.Choices.FindIndex(x => string.Equals(x, option.Value, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    index = 0;
                }
                index += positive ? 1 : -1;
                if (index < 0)
                {
                    index = option.Choices.Count - 1;
                }
                if (index >= option.Choices.Count)
                {
                    index = 0;
                }
                option.Value = option.Choices[index];
            }
            else
            {
                int number;
                if (int.TryParse(option.Value, out number))
                {
                    option.Value = (number + (positive ? 1 : -1)).ToString();
                }
            }
            RESET();
            Setup();
        }

        private static string OptionText(ModOption option)
        {
            return option.Name + ": " + (string.IsNullOrWhiteSpace(option.Value) ? option.DefaultValue : option.Value);
        }
    }
}
