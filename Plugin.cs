using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace TheShack.Recycler
{
    // Adds a Recycle tab to the chosen crafting stations (workbench, forge and stonecutter by default). It lists what
    // in your inventory was crafted at that station (or needs no station at all) and turns it back into materials:
    //
    //   - Gear (weapons, armour, tools) gives back what it cost to craft plus, by default, every upgrade it has had.
    //     Gear asks Yes/No first (a per-player setting).
    //   - Things another Recycle station makes are listed greyed out with that station's name.
    //   - By default only things you know how to craft can be recycled, so recycling never hands out materials from
    //     tiers you haven't reached.
    //   - A Research tab (workbench by default) uses up something you can't craft yet; after 5 researches you learn
    //     its recipe. Progress is saved in the character.
    //   - Stackable things (arrows, bolts, bombs, nails, bronze) are recycled by the craft: a click recycles what one
    //     craft makes (20 arrows), Shift+click the whole stack. Less than a full craft gives back its share, rounded down.
    //   - Returns are Flat (a fixed percentage, 100% by default) or Luck (rolls per material, improved by the
    //     Recycling skill). Nothing ever gives back more than the item cost to make, so crafting and recycling can
    //     never multiply materials.
    //
    // Recycling happens on the player's own inventory, like crafting. The settings are admin-only and come from the
    // server when the server has this mod (Jotunn config sync).
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Minor)]
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "theshack.recycler";
        public const string PluginName = "Shack Recycler";
        public const string PluginVersion = "1.1.1";

        internal static ManualLogSource Log;
        internal static Skills.SkillType RecyclingSkill = Skills.SkillType.None;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            RegisterSkill();
            new Harmony(PluginGuid).PatchAll(Assembly.GetExecutingAssembly());
            Log.LogInfo($"{PluginName} {PluginVersion} ready (returns: {Settings.Mode.Value}).");
        }

        private static void RegisterSkill()
        {
            try
            {
                RecyclingSkill = SkillManager.Instance.AddSkill(new SkillConfig
                {
                    Identifier = PluginGuid + ".recycling",
                    Name = "Recycling",
                    Description = "Improves your luck when recycling with luck rolls: fewer items crumble and more jackpots.",
                    Icon = LoadSprite("skill-icon.png"),
                    IncreaseStep = 1f
                });
            }
            catch (Exception e)
            {
                Log.LogError("Could not register the Recycling skill; luck rolls will ignore it. " + e);
            }
        }

        private static Sprite LoadSprite(string resource)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                {
                    return null;
                }
                MemoryStream bytes = new MemoryStream();
                stream.CopyTo(bytes);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.LoadImage(bytes.ToArray());
                return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
            }
        }
    }

    public enum ReturnMode
    {
        Flat,
        Luck
    }

    public enum ConfirmMode
    {
        Never,
        Gear,
        Everything
    }

    internal static class Settings
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<string> Stations;
        public static ConfigEntry<bool> StationFreeItems;
        public static ConfigEntry<bool> RequireStationLevel;
        public static ConfigEntry<bool> OnlyKnownRecipes;
        public static ConfigEntry<string> ExcludedItemTypes;
        public static ConfigEntry<string> BlockedItems;
        public static ConfigEntry<string> BlockedMaterials;

        public static ConfigEntry<ReturnMode> Mode;
        public static ConfigEntry<int> FlatPercent;
        public static ConfigEntry<bool> IncludeUpgrades;
        public static ConfigEntry<bool> DurabilityAffectsReturn;

        public static ConfigEntry<ConfirmMode> Confirm;

        public static ConfigEntry<float> CrumbleWeight;
        public static ConfigEntry<float> PoorWeight;
        public static ConfigEntry<float> NormalWeight;
        public static ConfigEntry<float> GoodWeight;
        public static ConfigEntry<float> JackpotWeight;
        public static ConfigEntry<string> PoorRange;
        public static ConfigEntry<string> NormalRange;
        public static ConfigEntry<string> GoodRange;

        public static ConfigEntry<bool> SkillEnabled;
        public static ConfigEntry<float> SkillXp;
        public static ConfigEntry<float> SkillCrumbleReduction;
        public static ConfigEntry<float> SkillJackpotBonus;

        public static ConfigEntry<bool> ResearchEnabled;
        public static ConfigEntry<string> ResearchStations;
        public static ConfigEntry<int> ResearchCount;
        public static ConfigEntry<string> ResearchExcludedTypes;

        // Bumped on every settings change (including a sync from the server) so parsed lists and cached results refresh.
        public static int Version;

        private static int parsedVersion = -1;
        private static HashSet<string> stations;
        private static HashSet<string> researchStations;
        private static HashSet<ItemDrop.ItemData.ItemType> excludedTypes;
        private static HashSet<ItemDrop.ItemData.ItemType> researchExcludedTypes;
        private static HashSet<string> blockedItems;
        private static HashSet<string> blockedMaterials;

        public static void Bind(ConfigFile config)
        {
            int order = 100;
            ConfigEntry<T> Bind<T>(string section, string key, T value, string description, AcceptableValueBase range = null) =>
                config.Bind(section, key, value, new ConfigDescription(description, range,
                    new ConfigurationManagerAttributes { IsAdminOnly = true, Order = order-- }));

            // Each player's own choice; not synced from the server.
            Confirm = config.Bind("0 - This PC", "Ask before recycling", ConfirmMode.Gear,
                new ConfigDescription("Ask Yes/No before recycling. Gear = anything that doesn't stack (tools, weapons, armour). " +
                    "Everything = stacks too. Never = recycle on click.", null, new ConfigurationManagerAttributes { Order = order-- }));

            Enabled = Bind("1 - General", "Enabled", true, "Show the Recycle tab at the stations below.");
            Stations = Bind("1 - General", "Stations", "$piece_workbench, $piece_forge, $piece_stonecutter",
                "Crafting stations that get a Recycle tab, comma separated. Use the station's name token or prefab name, e.g. " +
                "$piece_workbench, $piece_forge, $piece_stonecutter, $piece_artisanstation, $piece_blackforge, $piece_magetable. " +
                "Each station recycles what it crafts.");
            StationFreeItems = Bind("1 - General", "Station-free items", true,
                "Items that need no crafting station (hammer, torch, club...) can be recycled at any of the stations above.");
            RequireStationLevel = Bind("1 - General", "Require station level", true,
                "Recycling needs the station level that crafting (or upgrading to) the item's level needs.");
            OnlyKnownRecipes = Bind("1 - General", "Only known recipes", true,
                "Only things you know how to craft can be recycled. Others show greyed out; research them to learn them. " +
                "Keeps recycling from handing out materials from tiers you haven't reached.");
            ExcludedItemTypes = Bind("1 - General", "Excluded item types", "Consumable",
                "Item types that are never recycled, comma separated (Consumable = food, meads and potions). " +
                "Other types: Material, Ammo, AmmoNonEquipable, OneHandedWeapon, TwoHandedWeapon, TwoHandedWeaponLeft, Bow, " +
                "Shield, Helmet, Chest, Legs, Shoulder, Utility, Tool, Torch, Trinket, Misc.");
            BlockedItems = Bind("1 - General", "Blocked items", "",
                "Item prefab names that are never recycled, comma separated (e.g. SwordIron, ArrowWood).");
            BlockedMaterials = Bind("1 - General", "Blocked materials", "",
                "Material prefab names that are never given back, comma separated (e.g. DragonTear). The item can still be recycled.");

            Mode = Bind("2 - Returns", "Mode", ReturnMode.Flat,
                "Flat: always the percentage below. Luck: each material is rolled (crumbled, poor, normal, good, jackpot), and the Recycling skill improves the odds.");
            FlatPercent = Bind("2 - Returns", "Flat percent", 100,
                "Flat mode: share of the crafting cost given back. 100 = everything it cost to make.", new AcceptableValueRange<int>(0, 100));
            IncludeUpgrades = Bind("2 - Returns", "Include upgrades", true,
                "Upgraded items also give back what each upgrade cost. Off: only what the item cost to craft at level 1.");
            DurabilityAffectsReturn = Bind("2 - Returns", "Durability affects return", false,
                "Worn items give back less: 50% plus 50% times the remaining durability. Applies to both modes (not to a Luck jackpot).");

            CrumbleWeight = Bind("3 - Luck", "Crumble weight", 20f, "Luck mode: weight of 'crumbled' (nothing back).", new AcceptableValueRange<float>(0f, 1000f));
            PoorWeight = Bind("3 - Luck", "Poor weight", 30f, "Luck mode: weight of 'poor'.", new AcceptableValueRange<float>(0f, 1000f));
            NormalWeight = Bind("3 - Luck", "Normal weight", 30f, "Luck mode: weight of 'normal'.", new AcceptableValueRange<float>(0f, 1000f));
            GoodWeight = Bind("3 - Luck", "Good weight", 15f, "Luck mode: weight of 'good'.", new AcceptableValueRange<float>(0f, 1000f));
            JackpotWeight = Bind("3 - Luck", "Jackpot weight", 5f, "Luck mode: weight of 'jackpot' (everything back).", new AcceptableValueRange<float>(0f, 1000f));
            PoorRange = Bind("3 - Luck", "Poor range", "0.25-0.5", "Luck mode: share of the cost a 'poor' roll gives back (min-max, 0-1).");
            NormalRange = Bind("3 - Luck", "Normal range", "0.5-0.75", "Luck mode: share of the cost a 'normal' roll gives back (min-max, 0-1).");
            GoodRange = Bind("3 - Luck", "Good range", "0.75-0.99", "Luck mode: share of the cost a 'good' roll gives back (min-max, 0-1).");

            SkillEnabled = Bind("4 - Skill", "Enabled", true,
                "Luck mode: recycling trains the Recycling skill, and the skill improves the odds. Levels are kept when this is off.");
            SkillXp = Bind("4 - Skill", "XP per recycle", 10f,
                "Luck mode: skill XP per recycled item (times its level) or per craft's worth of a stack.", new AcceptableValueRange<float>(0f, 100f));
            SkillCrumbleReduction = Bind("4 - Skill", "Crumble reduction at 100", 0.5f,
                "Luck mode: at skill 100 the crumble weight shrinks by this share (scales with level); the removed weight goes to 'good'.",
                new AcceptableValueRange<float>(0f, 1f));
            SkillJackpotBonus = Bind("4 - Skill", "Jackpot bonus at 100", 1f,
                "Luck mode: at skill 100 the jackpot weight grows by this share (1 = doubled; scales with level).", new AcceptableValueRange<float>(0f, 10f));

            ResearchEnabled = Bind("5 - Research", "Enabled", true,
                "A Research tab at the stations below. Researching uses up an item you can't craft yet (nothing comes back); " +
                "after enough researches you learn its recipe. Stacks use one craft's worth per research (20 arrows).");
            ResearchStations = Bind("5 - Research", "Stations", "$piece_workbench",
                "Crafting stations that get a Research tab, comma separated (name token or prefab name). " +
                "Any item can be researched there, whichever station crafts it.");
            ResearchCount = Bind("5 - Research", "Researches needed", 5,
                "How many times an item must be researched to learn its recipe.", new AcceptableValueRange<int>(1, 50));
            ResearchExcludedTypes = Bind("5 - Research", "Excluded item types", "",
                "Item types that can't be researched, comma separated (same names as the recycling list). Empty = anything " +
                "with a recipe, including food and meads. Meads and baked food teach the recipe they're made from " +
                "(mead base, dough). 'Blocked items' applies to research too.");

            config.SettingChanged += (sender, args) => Version++;
        }

        public static bool IsStationAllowed(CraftingStation station)
        {
            if (!Enabled.Value || station == null || !station.m_hasCraftTab || station.m_upgrader)
            {
                return false;
            }
            Parse();
            return stations.Contains(Normalize(station.m_name)) || stations.Contains(Normalize(Utils.GetPrefabName(station.gameObject)));
        }

        public static bool IsResearchStation(CraftingStation station)
        {
            if (!ResearchEnabled.Value || station == null || !station.m_hasCraftTab || station.m_upgrader)
            {
                return false;
            }
            Parse();
            return researchStations.Contains(Normalize(station.m_name)) || researchStations.Contains(Normalize(Utils.GetPrefabName(station.gameObject)));
        }

        public static bool IsExcludedType(ItemDrop.ItemData.ItemType type)
        {
            Parse();
            return excludedTypes.Contains(type);
        }

        public static bool IsResearchExcluded(ItemDrop.ItemData.ItemType type)
        {
            Parse();
            return researchExcludedTypes.Contains(type);
        }

        public static bool IsBlockedItem(string prefab)
        {
            Parse();
            return prefab != null && blockedItems.Contains(Normalize(prefab));
        }

        public static bool IsBlockedMaterial(string prefab)
        {
            Parse();
            return prefab != null && blockedMaterials.Contains(Normalize(prefab));
        }

        public static (float min, float max) Range(ConfigEntry<string> entry)
        {
            string[] parts = (entry.Value ?? "").Split('-');
            float min, max;
            if (parts.Length != 2 || !TryFraction(parts[0], out min) || !TryFraction(parts[1], out max))
            {
                // Fall back to the default and say so once per bad value.
                parts = ((string)entry.DefaultValue).Split('-');
                TryFraction(parts[0], out min);
                TryFraction(parts[1], out max);
                if (warnedRanges.Add(entry.Value ?? ""))
                {
                    Plugin.Log.LogWarning($"{entry.Definition.Key} '{entry.Value}' is not 'min-max' with 0-1 fractions; using {entry.DefaultValue}.");
                }
            }
            return min <= max ? (min, max) : (max, min);
        }

        private static readonly HashSet<string> warnedRanges = new HashSet<string>();

        private static bool TryFraction(string text, out float value)
        {
            bool ok = float.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
            value = Mathf.Clamp01(value);
            return ok;
        }

        private static string Normalize(string name) => (name ?? "").Trim().TrimStart('$').ToLowerInvariant();

        private static void Parse()
        {
            if (parsedVersion == Version)
            {
                return;
            }
            parsedVersion = Version;
            stations = NameSet(Stations.Value);
            researchStations = NameSet(ResearchStations.Value);
            blockedItems = NameSet(BlockedItems.Value);
            blockedMaterials = NameSet(BlockedMaterials.Value);
            excludedTypes = TypeSet(ExcludedItemTypes);
            researchExcludedTypes = TypeSet(ResearchExcludedTypes);
        }

        private static HashSet<ItemDrop.ItemData.ItemType> TypeSet(ConfigEntry<string> entry)
        {
            HashSet<ItemDrop.ItemData.ItemType> set = new HashSet<ItemDrop.ItemData.ItemType>();
            foreach (string name in Split(entry.Value))
            {
                if (Enum.TryParse(name, true, out ItemDrop.ItemData.ItemType type))
                {
                    set.Add(type);
                }
                else
                {
                    Plugin.Log.LogWarning($"{entry.Definition.Section} / {entry.Definition.Key}: '{name}' is not an item type; ignored.");
                }
            }
            return set;
        }

        private static HashSet<string> NameSet(string list)
        {
            HashSet<string> set = new HashSet<string>();
            foreach (string name in Split(list))
            {
                set.Add(Normalize(name));
            }
            return set;
        }

        private static IEnumerable<string> Split(string list) =>
            (list ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0);
    }
}
