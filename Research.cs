using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TheShack.Recycler
{
    // Something in the inventory the player can't craft yet.
    internal sealed class ResearchInfo
    {
        public ItemDrop.ItemData Item;
        public Recipe Recipe;              // the item's recipe: the list entry, and what gets learned
        public string Prefab;              // progress is kept per item prefab
        public int Batch;                  // what one research uses up: one craft's worth for stacks, else 1
        public int Done;                   // researches so far
        public int Needed;
        public CraftingStation CraftedAt;  // where it's crafted once learned; null = no station
        public string LearnsName;          // what gets learned: the item itself, or what it's made from (mead base, dough)
        public string MadeIn;              // set when it's made from LearnsName in a fermenter or oven (localized station name)
    }

    // Researching uses up the item (nothing comes back) and counts towards learning its recipe. The count lives in the
    // character's custom data, so it is per character and travels with them between worlds, like known recipes do.
    internal static class Research
    {
        private const string KeyPrefix = "theshack.recycler.research.";

        // Player.AddKnownRecipe is private; it is what the game itself calls, and it shows the "New recipe" message.
        private static readonly MethodInfo addKnownRecipe =
            AccessTools.Method(typeof(Player), "AddKnownRecipe", new[] { typeof(Recipe) });

        public static ResearchInfo Evaluate(Player player, ItemDrop.ItemData item)
        {
            if (player == null || item == null || Settings.IsResearchExcluded(item.m_shared.m_itemType))
            {
                return null;
            }
            // What to learn: the item's own recipe, or, for things a fermenter or oven makes (meads, bread), the recipe
            // of what goes in (mead base, dough). One craft of that makes `perCraft` of the finished item.
            List<Recipe> recipes = Recycling.RecipesFor(item);
            int perCraft = 1;
            string madeIn = null;
            if (recipes == null && TryMadeFrom(item.m_shared.m_name, out ItemDrop source, out perCraft, out madeIn))
            {
                recipes = Recycling.RecipesFor(source.m_itemData.m_shared.m_name);
            }
            if (recipes == null)
            {
                return null; // nobody can craft it, so there is nothing to learn
            }
            Recipe chosen = null;
            foreach (Recipe recipe in recipes)
            {
                if (chosen == null || recipe.m_amount < chosen.m_amount)
                {
                    chosen = recipe;
                }
            }
            string learns = chosen.m_item.m_itemData.m_shared.m_name;
            if (player.IsRecipeKnown(learns) || Settings.IsBlockedItem(chosen.m_item.name) ||
                (item.m_dropPrefab != null && Settings.IsBlockedItem(item.m_dropPrefab.name)))
            {
                return null;
            }
            return new ResearchInfo
            {
                Item = item,
                Recipe = chosen,
                Prefab = chosen.m_item.name,
                Batch = item.m_shared.m_maxStackSize > 1 ? Math.Max(1, chosen.m_amount) * Math.Max(1, perCraft) : 1,
                Done = Progress(player, chosen.m_item.name),
                Needed = Math.Max(1, Settings.ResearchCount.Value),
                CraftedAt = chosen.GetRequiredStation(1),
                LearnsName = Localization.instance.Localize(learns),
                MadeIn = madeIn
            };
        }

        // Finished item (shared name) -> what a fermenter or cooking station / oven makes it from, how many one input
        // makes, and that station's name. Read from the game's prefabs once per ZNetScene.
        private static readonly Dictionary<string, List<(ItemDrop from, int produced, string station)>> madeFrom =
            new Dictionary<string, List<(ItemDrop, int, string)>>();
        private static ZNetScene madeFromScene;

        private static bool TryMadeFrom(string sharedName, out ItemDrop source, out int produced, out string station)
        {
            source = null;
            produced = 1;
            station = null;
            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
            {
                return false;
            }
            if (scene != madeFromScene)
            {
                madeFromScene = scene;
                madeFrom.Clear();
                foreach (GameObject prefab in scene.m_prefabs)
                {
                    if (prefab == null)
                    {
                        continue;
                    }
                    Piece piece = prefab.GetComponent<Piece>();
                    string name = Localization.instance.Localize(piece != null ? piece.m_name : prefab.name);
                    Fermenter fermenter = prefab.GetComponent<Fermenter>();
                    if (fermenter != null)
                    {
                        foreach (Fermenter.ItemConversion c in fermenter.m_conversion)
                        {
                            AddMadeFrom(c.m_from, c.m_to, c.m_producedItems, name);
                        }
                    }
                    CookingStation cooking = prefab.GetComponent<CookingStation>();
                    if (cooking != null)
                    {
                        foreach (CookingStation.ItemConversion c in cooking.m_conversion)
                        {
                            AddMadeFrom(c.m_from, c.m_to, 1, name);
                        }
                    }
                }
            }
            if (!madeFrom.TryGetValue(sharedName, out var sources))
            {
                return false;
            }
            foreach (var s in sources)
            {
                if (Recycling.RecipesFor(s.from.m_itemData.m_shared.m_name) != null)
                {
                    (source, produced, station) = (s.from, Math.Max(1, s.produced), s.station);
                    return true; // raw meat on a cooking station has no recipe; dough for the oven does
                }
            }
            return false;
        }

        private static void AddMadeFrom(ItemDrop from, ItemDrop to, int produced, string station)
        {
            if (from == null || to == null)
            {
                return;
            }
            string key = to.m_itemData.m_shared.m_name;
            if (!madeFrom.TryGetValue(key, out var list))
            {
                madeFrom[key] = list = new List<(ItemDrop, int, string)>();
            }
            list.Add((from, produced, station));
        }

        public static int Progress(Player player, string prefab) =>
            player.m_customData.TryGetValue(KeyPrefix + prefab, out string text) && int.TryParse(text, out int done) ? done : 0;

        // How many can go into one research right now: stacks count every unequipped stack of that item.
        public static int Available(Player player, ResearchInfo info)
        {
            if (info.Item.m_shared.m_maxStackSize <= 1)
            {
                return Recycling.IsInUse(player, info.Item) ? 0 : 1;
            }
            int total = 0;
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_name == info.Item.m_shared.m_name && !Recycling.IsInUse(player, item))
                {
                    total += item.m_stack;
                }
            }
            return total;
        }

        public static bool DoResearch(Player player, CraftingStation station, ItemDrop.ItemData item)
        {
            Inventory inventory = player.GetInventory();
            if (!inventory.ContainsItem(item) || !Settings.IsResearchStation(station))
            {
                return false;
            }
            ResearchInfo info = Evaluate(player, item);
            if (info == null)
            {
                return false;
            }
            if (Available(player, info) < info.Batch)
            {
                player.Message(MessageHud.MessageType.Center, info.Batch > 1
                    ? $"One research needs {info.Batch} (unequipped)." : "Unequip it before researching it.");
                return false;
            }
            if (!station.CheckUsable(player, showMessage: true))
            {
                return false;
            }

            string name = Localization.instance.Localize(item.m_shared.m_name);
            if (!UseUp(player, item, info.Batch))
            {
                return false;
            }
            int done = info.Done + 1;
            if (done >= info.Needed)
            {
                player.m_customData.Remove(KeyPrefix + info.Prefab);
                addKnownRecipe.Invoke(player, new object[] { info.Recipe });
                string where = info.CraftedAt != null ? $" at the {Localization.instance.Localize(info.CraftedAt.m_name)}" : "";
                player.Message(MessageHud.MessageType.Center, $"Research complete: you can now craft {info.LearnsName}{where}!");
            }
            else
            {
                player.m_customData[KeyPrefix + info.Prefab] = done.ToString();
                player.Message(MessageHud.MessageType.Center, $"Researched {name}: {done} / {info.Needed}");
            }
            Plugin.Log.LogDebug($"Researched {info.Prefab} x{info.Batch}: {done}/{info.Needed}");
            station.m_craftItemEffects.Create(player.transform.position, Quaternion.identity);
            return true;
        }

        // Gear: the selected item. Stacks: the selected stack first, then other unequipped stacks of the same item.
        private static bool UseUp(Player player, ItemDrop.ItemData selected, int amount)
        {
            Inventory inventory = player.GetInventory();
            if (selected.m_shared.m_maxStackSize <= 1)
            {
                return inventory.RemoveItem(selected);
            }
            List<ItemDrop.ItemData> stacks = new List<ItemDrop.ItemData>();
            if (!Recycling.IsInUse(player, selected))
            {
                stacks.Add(selected);
            }
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item != selected && item.m_shared.m_name == selected.m_shared.m_name && !Recycling.IsInUse(player, item))
                {
                    stacks.Add(item);
                }
            }
            foreach (ItemDrop.ItemData stack in stacks)
            {
                int take = Math.Min(amount, stack.m_stack);
                if (take > 0 && (take >= stack.m_stack ? inventory.RemoveItem(stack) : inventory.RemoveItem(stack, take)))
                {
                    amount -= take;
                }
                if (amount <= 0)
                {
                    return true;
                }
            }
            return amount <= 0;
        }
    }
}
