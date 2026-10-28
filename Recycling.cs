using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TheShack.Recycler
{
    // One material an item gives back. PerUnit is what one item (one arrow, one sword at its level) cost to make.
    internal sealed class Yield
    {
        public ItemDrop Resource;
        public string Prefab;
        public double PerUnit;

        // The most that recycling `count` items may ever give: the crafting cost, rounded down.
        public int Max(int count) => (int)Math.Floor(PerUnit * count + 1e-6);
    }

    internal sealed class RecycleInfo
    {
        public ItemDrop.ItemData Item;
        public Recipe Recipe;       // the recipe this station crafts it with (list entry, station level)
        public int Batch;           // how many one craft makes; a click recycles this many
        public int RequiredLevel;   // 0 when the item needs no station
        public CraftingStation Elsewhere; // set when another Recycle station makes it; it can't be recycled here
        public readonly List<Yield> Yields = new List<Yield>();
    }

    internal enum Outcome
    {
        Crumbled,
        Poor,
        Normal,
        Good,
        Jackpot
    }

    internal static class Recycling
    {
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> hiddenLeft =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_hiddenLeftItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> hiddenRight =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_hiddenRightItem");

        // Recipes by the shared item name they make (ObjectDB.GetRecipe matches the same way). Rebuilt when another
        // mod adds recipes or a new ObjectDB loads.
        private static readonly Dictionary<string, List<Recipe>> recipesByItem = new Dictionary<string, List<Recipe>>();
        private static ObjectDB recipesFrom;
        private static int recipesCount = -1;

        public static bool IsInUse(Player player, ItemDrop.ItemData item) =>
            item.m_equipped || item == hiddenLeft(player) || item == hiddenRight(player);

        // With "Only known recipes" on, the player must know how to craft the item (the game's own known-recipe list).
        public static bool RecipeAllowed(Player player, ItemDrop.ItemData item) =>
            !Settings.OnlyKnownRecipes.Value || player.IsRecipeKnown(item.m_shared.m_name);

        public static RecycleInfo Evaluate(ItemDrop.ItemData item, CraftingStation station)
        {
            if (item == null || station == null || Settings.IsExcludedType(item.m_shared.m_itemType))
            {
                return null;
            }
            List<Recipe> recipes = RecipesFor(item);
            if (recipes == null)
            {
                return null;
            }

            int quality = Math.Max(1, item.m_quality);
            Recipe atStation = null;
            Recipe stationFree = null;
            foreach (Recipe recipe in recipes)
            {
                if (recipe.m_requireOnlyOneIngredient)
                {
                    return null; // can't tell which ingredient went in
                }
                CraftingStation required = recipe.GetRequiredStation(quality);
                if (required == null)
                {
                    stationFree = stationFree ?? recipe;
                }
                else if (required.m_name == station.m_name && (atStation == null || recipe.m_amount < atStation.m_amount))
                {
                    atStation = recipe; // the smallest craft, so a click recycles the least
                }
            }
            Recipe chosen = atStation ?? (Settings.StationFreeItems.Value ? stationFree : null);
            if (chosen == null || Settings.IsBlockedItem(chosen.m_item.name))
            {
                return null;
            }

            RecycleInfo info = new RecycleInfo
            {
                Item = item,
                Recipe = chosen,
                Batch = item.m_shared.m_maxStackSize > 1 ? Math.Max(1, chosen.m_amount) : 1,
                RequiredLevel = chosen.GetRequiredStation(quality) != null ? chosen.GetRequiredStationLevel(quality) : 0
            };
            AddYields(info, recipes, quality);
            AddPackedStonecutter(info, item);
            return info.Yields.Count > 0 ? info : null;
        }

        // Shack Workbench Pack: a backpack with a stonecutter packed in also gives back what packing it cost. That's the
        // add-on recipe at the stonecutter, which "crafts" the item below; its recovered materials are the stonecutter's
        // build cost (the backpack it only checks for isn't recovered). Read from the game, so it follows that mod.
        private const string BenchCutterData = "theshack.bench.stonecutter";
        private const string BenchCutterItem = "ShackBenchStonecutter";

        private static void AddPackedStonecutter(RecycleInfo info, ItemDrop.ItemData item)
        {
            if (item.m_customData == null || !item.m_customData.TryGetValue(BenchCutterData, out string packed) || packed != "1" ||
                ObjectDB.instance == null)
            {
                return;
            }
            Recipe addOn = ObjectDB.instance.m_recipes.Find(r => r && r.m_item && r.m_item.name == BenchCutterItem);
            foreach (Piece.Requirement req in addOn ? addOn.m_resources ?? Array.Empty<Piece.Requirement>() : Array.Empty<Piece.Requirement>())
            {
                if (req?.m_resItem == null || !req.m_recover || req.m_amount <= 0 || Settings.IsBlockedMaterial(req.m_resItem.name))
                {
                    continue;
                }
                Yield yield = info.Yields.Find(y => y.Prefab == req.m_resItem.name);
                if (yield != null)
                {
                    yield.PerUnit += req.m_amount;
                }
                else
                {
                    info.Yields.Add(new Yield { Resource = req.m_resItem, Prefab = req.m_resItem.name, PerUnit = req.m_amount });
                }
            }
        }

        // Like Evaluate, but when this station doesn't make the item, finds another Recycle station that does
        // (RecycleInfo.Elsewhere), so the list can say where to take it.
        public static RecycleInfo EvaluateAnywhere(ItemDrop.ItemData item, CraftingStation station)
        {
            RecycleInfo info = Evaluate(item, station);
            if (info != null || item == null || station == null)
            {
                return info;
            }
            List<Recipe> recipes = RecipesFor(item);
            if (recipes == null)
            {
                return null;
            }
            foreach (Recipe recipe in recipes)
            {
                CraftingStation other = recipe.GetRequiredStation(Math.Max(1, item.m_quality));
                if (other != null && other.m_name != station.m_name && Settings.IsStationAllowed(other))
                {
                    info = Evaluate(item, other);
                    if (info != null)
                    {
                        info.Elsewhere = other;
                        return info;
                    }
                }
            }
            return null;
        }

        // Per material, the cheapest any recipe makes one item for. With two recipes (Bronze and Bronze x5) neither can
        // be crafted and recycled for a profit; a material that one recipe doesn't use is never given back.
        private static void AddYields(RecycleInfo info, List<Recipe> recipes, int quality)
        {
            Dictionary<string, Yield> cheapest = null;
            foreach (Recipe recipe in recipes)
            {
                Dictionary<string, Yield> costs = new Dictionary<string, Yield>();
                foreach (Piece.Requirement req in recipe.m_resources ?? Array.Empty<Piece.Requirement>())
                {
                    if (req == null || req.m_resItem == null || !req.m_recover || req.m_upgraderResource)
                    {
                        continue;
                    }
                    int total = 0;
                    int levels = Settings.IncludeUpgrades.Value ? quality : 1;
                    for (int level = 1; level <= levels; level++)
                    {
                        total += req.GetAmount(level); // the craft, then each upgrade
                    }
                    if (total <= 0)
                    {
                        continue;
                    }
                    string prefab = req.m_resItem.name;
                    double perUnit = (double)total / Math.Max(1, recipe.m_amount);
                    if (costs.TryGetValue(prefab, out Yield same))
                    {
                        same.PerUnit += perUnit;
                    }
                    else
                    {
                        costs[prefab] = new Yield { Resource = req.m_resItem, Prefab = prefab, PerUnit = perUnit };
                    }
                }

                if (cheapest == null)
                {
                    cheapest = costs;
                    continue;
                }
                foreach (string prefab in new List<string>(cheapest.Keys))
                {
                    if (costs.TryGetValue(prefab, out Yield other))
                    {
                        cheapest[prefab].PerUnit = Math.Min(cheapest[prefab].PerUnit, other.PerUnit);
                    }
                    else
                    {
                        cheapest.Remove(prefab);
                    }
                }
            }

            // In the station recipe's material order, like the crafting panel shows it.
            foreach (Piece.Requirement req in info.Recipe.m_resources ?? Array.Empty<Piece.Requirement>())
            {
                if (req?.m_resItem == null || cheapest == null || !cheapest.TryGetValue(req.m_resItem.name, out Yield yield))
                {
                    continue;
                }
                cheapest.Remove(req.m_resItem.name);
                if (yield.PerUnit > 0 && !Settings.IsBlockedMaterial(yield.Prefab))
                {
                    info.Yields.Add(yield);
                }
            }
        }

        internal static List<Recipe> RecipesFor(ItemDrop.ItemData item) => RecipesFor(item.m_shared.m_name);

        internal static List<Recipe> RecipesFor(string sharedName)
        {
            ObjectDB db = ObjectDB.instance;
            if (db == null)
            {
                return null;
            }
            if (db != recipesFrom || db.m_recipes.Count != recipesCount)
            {
                recipesByItem.Clear();
                foreach (Recipe recipe in db.m_recipes)
                {
                    if (recipe == null || !recipe.m_enabled || recipe.m_item == null)
                    {
                        continue;
                    }
                    string name = recipe.m_item.m_itemData.m_shared.m_name;
                    if (!recipesByItem.TryGetValue(name, out List<Recipe> list))
                    {
                        recipesByItem[name] = list = new List<Recipe>();
                    }
                    list.Add(recipe);
                }
                recipesFrom = db;
                recipesCount = db.m_recipes.Count;
            }
            return recipesByItem.TryGetValue(sharedName, out List<Recipe> found) ? found : null;
        }

        public static double DurabilityFactor(ItemDrop.ItemData item)
        {
            if (!Settings.DurabilityAffectsReturn.Value || !item.m_shared.m_useDurability)
            {
                return 1.0;
            }
            float percent = item.GetDurabilityPercentage();
            return float.IsNaN(percent) || float.IsInfinity(percent) ? 1.0 : 0.5 + 0.5 * Mathf.Clamp01(percent);
        }

        public static int FlatAmount(Yield yield, int count, double durability)
        {
            double share = Mathf.Clamp(Settings.FlatPercent.Value, 0, 100) / 100.0 * durability;
            return Math.Min(yield.Max(count), (int)Math.Floor(yield.PerUnit * count * share + 1e-6));
        }

        public static float[] LuckWeights(Player player)
        {
            float skill = Settings.SkillEnabled.Value && Plugin.RecyclingSkill != Skills.SkillType.None && player != null
                ? player.GetSkillFactor(Plugin.RecyclingSkill) : 0f;
            float moved = Settings.CrumbleWeight.Value * skill * Settings.SkillCrumbleReduction.Value;
            float[] weights =
            {
                Settings.CrumbleWeight.Value - moved,
                Settings.PoorWeight.Value,
                Settings.NormalWeight.Value,
                Settings.GoodWeight.Value + moved,
                Settings.JackpotWeight.Value * (1f + skill * Settings.SkillJackpotBonus.Value)
            };
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = Mathf.Max(0f, weights[i]);
            }
            return weights;
        }

        private static Outcome Roll(float[] weights)
        {
            float total = 0f;
            foreach (float w in weights)
            {
                total += w;
            }
            if (total <= 0f)
            {
                return Outcome.Crumbled;
            }
            float pick = UnityEngine.Random.Range(0f, total);
            for (int i = 0; i < weights.Length; i++)
            {
                if (pick < weights[i])
                {
                    return (Outcome)i;
                }
                pick -= weights[i];
            }
            return Outcome.Normal;
        }

        private static int LuckAmount(Yield yield, int count, double durability, Outcome outcome)
        {
            int max = yield.Max(count);
            if (outcome == Outcome.Jackpot)
            {
                return max;
            }
            (float min, float most) range;
            switch (outcome)
            {
                case Outcome.Poor: range = Settings.Range(Settings.PoorRange); break;
                case Outcome.Normal: range = Settings.Range(Settings.NormalRange); break;
                case Outcome.Good: range = Settings.Range(Settings.GoodRange); break;
                default: return 0;
            }
            double value = yield.PerUnit * count * UnityEngine.Random.Range(range.min, range.most) * durability;
            int whole = (int)Math.Floor(value);
            if (UnityEngine.Random.value < value - whole)
            {
                whole++; // round up or down by the fraction, so small amounts aren't always lost
            }
            return Mathf.Clamp(whole, 0, max);
        }

        // Recycles `count` of the item at the station. The item leaves the inventory before anything is given back.
        public static bool Recycle(Player player, CraftingStation station, ItemDrop.ItemData item, int count)
        {
            Inventory inventory = player.GetInventory();
            if (!inventory.ContainsItem(item) || !Settings.IsStationAllowed(station))
            {
                return false;
            }
            RecycleInfo info = Evaluate(item, station);
            if (info == null)
            {
                return false;
            }
            if (IsInUse(player, item))
            {
                player.Message(MessageHud.MessageType.Center, "Unequip it before recycling it.");
                return false;
            }
            if (!RecipeAllowed(player, item))
            {
                player.Message(MessageHud.MessageType.Center, "You don't know how to craft this yet. Research it first.");
                return false;
            }
            if (Settings.RequireStationLevel.Value && station.GetLevel() < info.RequiredLevel)
            {
                player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
                return false;
            }
            if (!station.CheckUsable(player, showMessage: true))
            {
                return false;
            }

            count = Mathf.Clamp(count, 1, item.m_stack);
            double durability = DurabilityFactor(item);
            bool luck = Settings.Mode.Value == ReturnMode.Luck;
            float[] weights = luck ? LuckWeights(player) : null;
            List<KeyValuePair<ItemDrop, int>> gains = new List<KeyValuePair<ItemDrop, int>>();
            bool jackpot = false;
            foreach (Yield yield in info.Yields)
            {
                int amount;
                if (luck)
                {
                    Outcome outcome = Roll(weights);
                    jackpot |= outcome == Outcome.Jackpot;
                    amount = LuckAmount(yield, count, durability, outcome);
                }
                else
                {
                    amount = FlatAmount(yield, count, durability);
                }
                if (amount > 0)
                {
                    gains.Add(new KeyValuePair<ItemDrop, int>(yield.Resource, amount));
                }
            }

            string name = Localization.instance.Localize(item.m_shared.m_name);
            Sprite icon = item.GetIcon();
            int quality = item.m_quality;
            bool removed = count >= item.m_stack ? inventory.RemoveItem(item) : inventory.RemoveItem(item, count);
            if (!removed)
            {
                return false;
            }

            foreach (KeyValuePair<ItemDrop, int> gain in gains)
            {
                Give(player, gain.Key, gain.Value);
                player.Message(MessageHud.MessageType.TopLeft, "$msg_added " + gain.Key.m_itemData.m_shared.m_name, gain.Value, gain.Key.m_itemData.GetIcon());
            }
            string result = gains.Count == 0 ? " - it crumbled to nothing" : jackpot ? " - jackpot!" : "";
            player.Message(MessageHud.MessageType.Center, $"Recycled {name}{(count > 1 ? " x" + count : "")}{result}");
            Plugin.Log.LogDebug($"Recycled {item.m_shared.m_name} x{count} (q{quality}) at {station.m_name}: " +
                string.Join(", ", gains.ConvertAll(g => g.Key.name + " x" + g.Value)));

            station.m_craftItemEffects.Create(player.transform.position, Quaternion.identity);
            if (luck && Settings.SkillEnabled.Value && Plugin.RecyclingSkill != Skills.SkillType.None)
            {
                float units = item.m_shared.m_maxStackSize > 1 ? (float)count / info.Batch : quality;
                player.RaiseSkill(Plugin.RecyclingSkill, Settings.SkillXp.Value * Mathf.Max(0.1f, units));
            }
            return true;
        }

        // Into the inventory as far as it fits; the rest drops at the player's feet.
        private static void Give(Player player, ItemDrop resource, int amount)
        {
            Inventory inventory = player.GetInventory();
            int stackSize = Math.Max(1, resource.m_itemData.m_shared.m_maxStackSize);
            while (amount > 0)
            {
                ItemDrop.ItemData data = resource.m_itemData.Clone();
                data.m_dropPrefab = resource.gameObject;
                data.m_stack = Math.Min(amount, stackSize);
                data.m_worldLevel = Game.m_worldLevel;
                data.m_durability = data.GetMaxDurability();
                amount -= data.m_stack;
                // On failure AddItem leaves what didn't fit in m_stack (part of a stackable may already be in).
                if (!inventory.AddItem(data) && data.m_stack > 0)
                {
                    Transform t = player.transform;
                    ItemDrop.DropItem(data, data.m_stack, t.position + t.forward * 0.7f + Vector3.up, t.rotation);
                }
            }
        }
    }
}
