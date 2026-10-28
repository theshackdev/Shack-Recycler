using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TheShack.Recycler
{
    internal enum Mode
    {
        None,
        Recycle,
        Research
    }

    // The Recycle and Research tabs sit to the right of Craft and Upgrade. While one of them is open neither game tab
    // is the active one (both are interactable); that is how the game and My Little UI see a third tab, and MLU hides
    // its craft filters for it.
    //
    // The game's own list and description panel are reused. UpdateRecipeList is replaced by a list of what in the
    // inventory can be recycled (or researched) here, and the description is rewritten after the game's UpdateRecipe
    // each frame. A copy of the craft button covers the real one while a tab is open, so a click never goes through
    // the game's OnCraftPressed (or the mods that hook it). OnCraftPressed is also blocked while a tab is open, in
    // case a gamepad presses the real button.
    [HarmonyPatch(typeof(InventoryGui))]
    internal static class RecycleTab
    {
        private static Mode mode = Mode.None;
        private static Button recycleTab;
        private static Button researchTab;
        private static Vector2 firstSlot;   // where the first of our tabs goes (right of Upgrade)
        private static Vector2 slotStep;    // Craft to Upgrade
        private static Button button;
        private static TMP_Text buttonText;
        private static UITooltip buttonTooltip;

        private static readonly Type pairType = AccessTools.Inner(typeof(InventoryGui), "RecipeDataPair");
        private static readonly PropertyInfo pairItem = AccessTools.Property(pairType, "ItemData");
        private static readonly PropertyInfo pairElement = AccessTools.Property(pairType, "InterfaceElement");
        private static readonly FieldInfo availableRecipes = AccessTools.Field(typeof(InventoryGui), "m_availableRecipes");
        private static readonly FieldInfo selectedRecipe = AccessTools.Field(typeof(InventoryGui), "m_selectedRecipe");
        private static readonly AccessTools.FieldRef<InventoryGui, float> craftTimer =
            AccessTools.FieldRefAccess<InventoryGui, float>("m_craftTimer");
        private static readonly AccessTools.FieldRef<InventoryGui, float> listBaseSize =
            AccessTools.FieldRefAccess<InventoryGui, float>("m_recipeListBaseSize");
        private static readonly AccessTools.FieldRef<InventoryGui, Color> levelBaseColor =
            AccessTools.FieldRefAccess<InventoryGui, Color>("m_minStationLevelBasecolor");
        private static readonly MethodInfo updateCraftingPanel = AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel");
        private static readonly MethodInfo addRecipeToList = AccessTools.Method(typeof(InventoryGui), "AddRecipeToList");
        private static readonly MethodInfo setActiveGroup =
            AccessTools.Method(typeof(InventoryGui), "SetActiveGroup", new[] { typeof(UIGroupHandler), typeof(bool) });

        // The selected item's RecycleInfo, kept between frames (UpdateRecipe runs every frame).
        private static ItemDrop.ItemData cachedItem;
        private static CraftingStation cachedStation;
        private static int cachedQuality;
        private static int cachedVersion = -1;
        private static RecycleInfo cachedInfo;

        private static ItemDrop.ItemData pendingItem;
        private static float pendingSince;

        private static readonly HashSet<string> loggedErrors = new HashSet<string>();

        private struct Entry
        {
            public ItemDrop.ItemData Item;
            public Recipe Recipe;
            public bool Can;
            public int Rank;       // sort order: 0 can, 1 blocked here, 2 another station
            public string Name;
            public string Note;    // shown small after the name
        }

        [HarmonyPostfix]
        [HarmonyPatch("Awake")]
        private static void CreateUi(InventoryGui __instance)
        {
            try
            {
                RectTransform craftPos = (RectTransform)__instance.m_tabCraft.transform;
                RectTransform upgradePos = (RectTransform)__instance.m_tabUpgrade.transform;
                slotStep = upgradePos.anchoredPosition - craftPos.anchoredPosition;
                firstSlot = upgradePos.anchoredPosition + slotStep;
                recycleTab = CreateTab(__instance, "ShackRecyclerTab", "Recycle", Mode.Recycle);
                researchTab = CreateTab(__instance, "ShackResearchTab", "Research", Mode.Research);
                CreateButton(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Could not add the Recycle and Research tabs: " + e);
                recycleTab = researchTab = null;
                button = null;
            }
        }

        private static Button CreateTab(InventoryGui gui, string objectName, string label, Mode opens)
        {
            GameObject copy = UnityEngine.Object.Instantiate(gui.m_tabUpgrade.gameObject, gui.m_tabUpgrade.transform.parent);
            copy.name = objectName;
            StripCopy(copy, keepGamepad: false);
            ((RectTransform)copy.transform).anchoredPosition = firstSlot;

            Button tab = copy.GetComponent<Button>();
            tab.onClick = new Button.ButtonClickedEvent();
            tab.onClick.AddListener(() => Open(gui, opens));
            tab.interactable = true;
            TMP_Text text = copy.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                text.text = label;
            }
            copy.SetActive(false);
            return tab;
        }

        private static void CreateButton(InventoryGui gui)
        {
            GameObject copy = UnityEngine.Object.Instantiate(gui.m_craftButton.gameObject, gui.m_craftButton.transform.parent);
            copy.name = "ShackRecyclerButton";
            copy.transform.SetSiblingIndex(gui.m_craftButton.transform.GetSiblingIndex() + 1); // drawn on top
            StripCopy(copy, keepGamepad: true);

            button = copy.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent(); // drops the copied link to OnCraftPressed
            button.onClick.AddListener(() => OnButtonClicked(gui));
            buttonText = copy.GetComponentInChildren<TMP_Text>(true);
            buttonTooltip = copy.GetComponent<UITooltip>();
            copy.SetActive(false);
        }

        // Localize would put the copied label back to "Upgrade"/"Craft"; a tab's gamepad hint would press it
        // together with the Upgrade tab.
        private static void StripCopy(GameObject copy, bool keepGamepad)
        {
            foreach (Localize localize in copy.GetComponentsInChildren<Localize>(true))
            {
                UnityEngine.Object.DestroyImmediate(localize);
            }
            if (!keepGamepad)
            {
                foreach (UIGamePad pad in copy.GetComponentsInChildren<UIGamePad>(true))
                {
                    UnityEngine.Object.DestroyImmediate(pad);
                }
            }
        }

        private static bool Allowed(Mode m, CraftingStation station) =>
            m == Mode.Recycle ? Settings.IsStationAllowed(station) : m == Mode.Research && Settings.IsResearchStation(station);

        private static void Open(InventoryGui gui, Mode opening)
        {
            if (mode == opening || !Allowed(opening, Player.m_localPlayer?.GetCurrentCraftingStation()))
            {
                return;
            }
            mode = opening;
            setActiveGroup.Invoke(gui, new object[] { gui.m_uiGroups[3], true });
            gui.m_tabCraft.interactable = true;
            gui.m_tabUpgrade.interactable = true;
            SetTabStates();
            cachedVersion = -1;
            updateCraftingPanel.Invoke(gui, new object[] { true });
        }

        private static void Close(InventoryGui gui, bool selectCraftTab)
        {
            mode = Mode.None;
            if (selectCraftTab)
            {
                gui.m_tabCraft.interactable = false;
                gui.m_tabUpgrade.interactable = true;
            }
            SetTabStates();
            if (button != null)
            {
                button.gameObject.SetActive(false);
            }
        }

        private static void SetTabStates()
        {
            if (recycleTab != null)
            {
                recycleTab.interactable = mode != Mode.Recycle;
            }
            if (researchTab != null)
            {
                researchTab.interactable = mode != Mode.Research;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("OnTabCraftPressed")]
        private static void OnCraftTab(InventoryGui __instance)
        {
            if (mode != Mode.None)
            {
                Close(__instance, selectCraftTab: false);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("OnTabUpgradePressed")]
        private static void OnUpgradeTab(InventoryGui __instance)
        {
            if (mode != Mode.None)
            {
                Close(__instance, selectCraftTab: false);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch("Hide")]
        private static void OnHide(InventoryGui __instance)
        {
            if (mode != Mode.None)
            {
                Close(__instance, selectCraftTab: true);
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch("OnCraftPressed")]
        private static bool BlockCraftWhileOpen() => mode == Mode.None;

        [HarmonyPrefix]
        [HarmonyPatch("UpdateCraftingPanel")]
        private static void LeaveIfNotAllowed(InventoryGui __instance)
        {
            if (mode != Mode.None && !Allowed(mode, Player.m_localPlayer?.GetCurrentCraftingStation()))
            {
                Close(__instance, selectCraftTab: true);
            }
        }

        // Shows the tabs this station has, side by side right of Upgrade.
        [HarmonyPostfix]
        [HarmonyPatch("UpdateCraftingPanel")]
        private static void ShowTabs()
        {
            if (recycleTab == null || researchTab == null)
            {
                return;
            }
            CraftingStation station = Player.m_localPlayer?.GetCurrentCraftingStation();
            bool recycle = Settings.IsStationAllowed(station);
            bool research = Settings.IsResearchStation(station);
            recycleTab.gameObject.SetActive(recycle);
            researchTab.gameObject.SetActive(research);
            ((RectTransform)recycleTab.transform).anchoredPosition = firstSlot;
            ((RectTransform)researchTab.transform).anchoredPosition = recycle ? firstSlot + slotStep : firstSlot;
            SetTabStates();
        }

        [HarmonyPrefix]
        [HarmonyPatch("UpdateRecipeList")]
        private static bool BuildList(InventoryGui __instance)
        {
            if (mode == Mode.None)
            {
                return true;
            }
            try
            {
                FillList(__instance);
            }
            catch (Exception e)
            {
                LogOnce("list", e);
            }
            return false;
        }

        private static void FillList(InventoryGui gui)
        {
            IList list = (IList)availableRecipes.GetValue(gui);
            foreach (object pair in list)
            {
                GameObject element = (GameObject)pairElement.GetValue(pair);
                if (element != null)
                {
                    UnityEngine.Object.Destroy(element);
                }
            }
            list.Clear();

            Player player = Player.m_localPlayer;
            CraftingStation station = player != null ? player.GetCurrentCraftingStation() : null;
            List<Entry> entries = station == null ? new List<Entry>()
                : mode == Mode.Recycle ? RecycleEntries(player, station) : ResearchEntries(player);
            entries.Sort((a, b) =>
            {
                int c = a.Rank.CompareTo(b.Rank);
                if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                if (c == 0) c = b.Item.m_quality.CompareTo(a.Item.m_quality);
                if (c == 0) c = b.Item.m_stack.CompareTo(a.Item.m_stack);
                return c;
            });

            foreach (Entry entry in entries)
            {
                addRecipeToList.Invoke(gui, new object[] { player, entry.Recipe, entry.Item, entry.Can });
                Dress((GameObject)pairElement.GetValue(list[list.Count - 1]), entry);
            }
            gui.m_recipeListRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                Mathf.Max(listBaseSize(gui), list.Count * gui.m_recipeListSpace));
        }

        private static List<Entry> RecycleEntries(Player player, CraftingStation station)
        {
            List<Entry> entries = new List<Entry>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                RecycleInfo info = Recycling.EvaluateAnywhere(item, station);
                if (info == null)
                {
                    continue;
                }
                bool known = Recycling.RecipeAllowed(player, item);
                bool can = info.Elsewhere == null && known && !Recycling.IsInUse(player, item) && LevelOk(station, info);
                entries.Add(new Entry
                {
                    Item = item,
                    Recipe = info.Recipe,
                    Can = can,
                    Rank = can ? 0 : info.Elsewhere == null ? 1 : 2,
                    Name = Localization.instance.Localize(item.m_shared.m_name),
                    Note = !known ? "not known" : info.Elsewhere != null ? Localization.instance.Localize(info.Elsewhere.m_name) : null
                });
            }
            return entries;
        }

        // One entry per gear item, one per stackable item (its stacks are used together).
        private static List<Entry> ResearchEntries(Player player)
        {
            List<Entry> entries = new List<Entry>();
            HashSet<string> stackables = new HashSet<string>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems())
            {
                if (item.m_shared.m_maxStackSize > 1 && (Recycling.IsInUse(player, item) || !stackables.Add(item.m_shared.m_name)))
                {
                    continue;
                }
                ResearchInfo info = Research.Evaluate(player, item);
                if (info == null)
                {
                    continue;
                }
                bool can = Research.Available(player, info) >= info.Batch;
                entries.Add(new Entry
                {
                    Item = item,
                    Recipe = info.Recipe,
                    Can = can,
                    Rank = can ? 0 : 1,
                    Name = Localization.instance.Localize(item.m_shared.m_name),
                    Note = $"{info.Done}/{info.Needed}"
                });
            }
            return entries;
        }

        // The game's row shows the recipe; show the inventory stack instead.
        private static void Dress(GameObject element, Entry entry)
        {
            Image icon = element.transform.Find("icon")?.GetComponent<Image>();
            if (icon != null)
            {
                icon.sprite = entry.Item.GetIcon();
                icon.color = entry.Can ? Color.white : new Color(1f, 1f, 1f, 0.4f);
            }
            TMP_Text name = element.transform.Find("name")?.GetComponent<TMP_Text>();
            if (name != null)
            {
                name.text = entry.Item.m_stack > 1 ? $"{entry.Name} x{entry.Item.m_stack}" : entry.Name;
                if (entry.Note != null)
                {
                    name.text += $" <size=80%>({entry.Note})</size>";
                }
            }
            Transform level = element.transform.Find("QualityLevel");
            if (level != null)
            {
                level.gameObject.SetActive(entry.Item.m_shared.m_maxQuality > 1);
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch("UpdateRecipe")]
        private static void ShowDetails(InventoryGui __instance, Player player)
        {
            if (button == null)
            {
                return;
            }
            if (mode == Mode.None)
            {
                if (button.gameObject.activeSelf)
                {
                    button.gameObject.SetActive(false);
                }
                return;
            }
            try
            {
                bool crafting = craftTimer(__instance) >= 0f; // a craft started before switching tabs; its progress bar shows
                button.gameObject.SetActive(!crafting);
                __instance.m_craftButton.interactable = false;
                __instance.m_variantButton.gameObject.SetActive(false);
                __instance.m_qualityPanel.gameObject.SetActive(false);

                ItemDrop.ItemData item = (ItemDrop.ItemData)pairItem.GetValue(selectedRecipe.GetValue(__instance));
                CraftingStation station = player.GetCurrentCraftingStation();
                if (item == null || station == null || !player.GetInventory().ContainsItem(item))
                {
                    DescribeNothing(__instance);
                }
                else if (mode == Mode.Recycle)
                {
                    DescribeRecycle(__instance, player, station, item, crafting);
                }
                else
                {
                    DescribeResearch(__instance, player, station, item, crafting);
                }
            }
            catch (Exception e)
            {
                LogOnce("describe", e);
            }
        }

        private static void DescribeRecycle(InventoryGui gui, Player player, CraftingStation station, ItemDrop.ItemData item, bool crafting)
        {
            RecycleInfo info = Info(item, station);
            if (info == null)
            {
                DescribeNothing(gui);
                return;
            }
            string name = Localization.instance.Localize(item.m_shared.m_name);
            int count = Count(item, info.Batch);
            double durability = Recycling.DurabilityFactor(item);
            bool luck = Settings.Mode.Value == ReturnMode.Luck;
            bool inUse = Recycling.IsInUse(player, item);
            bool known = Recycling.RecipeAllowed(player, item);
            bool levelOk = LevelOk(station, info);
            bool usable = station.CheckUsable(player, showMessage: false);
            bool elsewhere = info.Elsewhere != null; // the level would be the other station's

            ShowItem(gui, item, name, $"Recycle {name}", count);
            gui.m_recipeDecription.text = Tooltip(item) + "\n\n" + RecycleSummary(player, item, info, durability, luck, inUse, known);

            GameObject[] slots = gui.m_recipeRequirementList;
            for (int i = 0; i < slots.Length; i++)
            {
                if (i < info.Yields.Count)
                {
                    Yield yield = info.Yields[i];
                    int max = yield.Max(count);
                    ShowSlot(slots[i].transform, yield.Resource.m_itemData,
                        luck ? (max > 0 ? $"0-{max}" : "0") : Recycling.FlatAmount(yield, count, durability).ToString());
                }
                else
                {
                    InventoryGui.HideRequirement(slots[i].transform);
                }
            }

            gui.m_minStationLevelIcon.gameObject.SetActive(!elsewhere && info.RequiredLevel > 0);
            if (!elsewhere && info.RequiredLevel > 0)
            {
                gui.m_minStationLevelText.text = info.RequiredLevel.ToString();
                gui.m_minStationLevelText.color = levelOk ? levelBaseColor(gui) : Color.red;
            }

            button.interactable = !elsewhere && known && !crafting && !inUse && levelOk && usable;
            buttonText.text = count > 1 ? $"Recycle x{count}" : "Recycle";
            SetTooltip(elsewhere ? $"Recycle this at the {Localization.instance.Localize(info.Elsewhere.m_name)}."
                : !known ? "You don't know how to craft this yet."
                : inUse ? "Unequip it first."
                : !levelOk ? Localization.instance.Localize("$msg_missingrequirement")
                : !usable ? Localization.instance.Localize("$msg_missingstation")
                : "");
        }

        private static void DescribeResearch(InventoryGui gui, Player player, CraftingStation station, ItemDrop.ItemData item, bool crafting)
        {
            ResearchInfo info = Research.Evaluate(player, item);
            if (info == null)
            {
                DescribeNothing(gui);
                return;
            }
            string name = Localization.instance.Localize(item.m_shared.m_name);
            int available = Research.Available(player, info);
            bool enough = available >= info.Batch;
            bool usable = station.CheckUsable(player, showMessage: false);

            ShowItem(gui, item, name, $"Research {name} ({info.Done}/{info.Needed})", 1);
            gui.m_recipeDecription.text = Tooltip(item) + "\n\n" + ResearchSummary(info, name, available);

            // The one "requirement": what a research uses up.
            GameObject[] slots = gui.m_recipeRequirementList;
            for (int i = 0; i < slots.Length; i++)
            {
                if (i == 0)
                {
                    ShowSlot(slots[i].transform, item, info.Batch.ToString(), enough ? Color.white : Color.red);
                }
                else
                {
                    InventoryGui.HideRequirement(slots[i].transform);
                }
            }
            gui.m_minStationLevelIcon.gameObject.SetActive(false);

            button.interactable = enough && !crafting && usable;
            buttonText.text = "Research";
            SetTooltip(!enough ? (info.Batch > 1 ? $"One research needs {info.Batch} (unequipped)." : "Unequip it first.")
                : !usable ? Localization.instance.Localize("$msg_missingstation")
                : "");
        }

        private static void DescribeNothing(InventoryGui gui)
        {
            gui.m_recipeIcon.enabled = false;
            gui.m_recipeName.enabled = false;
            gui.m_recipeDecription.enabled = true;
            gui.m_recipeDecription.text = mode == Mode.Research
                ? "Nothing in your inventory to research.\n\nResearch things you can't craft yet. Each research uses one up " +
                  $"(nothing comes back); after {Math.Max(1, Settings.ResearchCount.Value)} you learn to craft it."
                : "Nothing in your inventory can be recycled here.\n\nThis station recycles what it crafts" +
                  (Settings.StationFreeItems.Value ? ", and things that need no station (hammer, torch...)." : ".");
            gui.m_itemCraftType.gameObject.SetActive(false);
            gui.m_minStationLevelIcon.gameObject.SetActive(false);
            foreach (GameObject slot in gui.m_recipeRequirementList)
            {
                InventoryGui.HideRequirement(slot.transform);
            }
            button.interactable = false;
            buttonText.text = mode == Mode.Research ? "Research" : "Recycle";
            SetTooltip("");
        }

        private static void ShowItem(InventoryGui gui, ItemDrop.ItemData item, string name, string craftType, int count)
        {
            gui.m_recipeIcon.enabled = true;
            gui.m_recipeIcon.sprite = item.GetIcon();
            gui.m_recipeName.enabled = true;
            gui.m_recipeName.text = item.m_stack > 1 ? $"{name} x{item.m_stack}" : name;
            gui.m_recipeDecription.enabled = true;
            gui.m_itemCraftType.gameObject.SetActive(true);
            gui.m_itemCraftType.text = item.m_shared.m_maxQuality > 1 ? $"{craftType} (level {item.m_quality})"
                : count > 1 ? $"{craftType} x{count}" : craftType;
        }

        private static string Tooltip(ItemDrop.ItemData item) =>
            Localization.instance.Localize(ItemDrop.ItemData.GetTooltip(item, item.m_quality, false, Game.m_worldLevel));

        private static void ShowSlot(Transform slot, ItemDrop.ItemData shown, string amountText, Color? amountColor = null)
        {
            Image icon = slot.Find("res_icon").GetComponent<Image>();
            TMP_Text name = slot.Find("res_name").GetComponent<TMP_Text>();
            TMP_Text amount = slot.Find("res_amount").GetComponent<TMP_Text>();
            icon.gameObject.SetActive(true);
            name.gameObject.SetActive(true);
            amount.gameObject.SetActive(true);
            icon.sprite = shown.GetIcon();
            icon.color = Color.white;
            name.text = Localization.instance.Localize(shown.m_shared.m_name);
            amount.text = amountText;
            amount.color = amountColor ?? Color.white;
            UITooltip tooltip = slot.GetComponent<UITooltip>();
            if (tooltip != null)
            {
                tooltip.m_text = name.text;
            }
        }

        private static void SetTooltip(string text)
        {
            if (buttonTooltip != null)
            {
                buttonTooltip.m_text = text;
            }
        }

        private static string RecycleSummary(Player player, ItemDrop.ItemData item, RecycleInfo info, double durability, bool luck, bool inUse, bool known)
        {
            StringBuilder text = new StringBuilder();
            if (info.Elsewhere != null)
            {
                text.Append($"<color=yellow>Recycle this at the {Localization.instance.Localize(info.Elsewhere.m_name)}.</color>\n");
            }
            if (!known)
            {
                text.Append("<color=yellow>You don't know how to craft this yet, so it can't be recycled.");
                if (Settings.ResearchEnabled.Value)
                {
                    ResearchInfo research = Research.Evaluate(player, item);
                    if (research != null)
                    {
                        text.Append($" Research it to learn it ({research.Done}/{research.Needed}).");
                    }
                }
                text.Append("</color>\n");
            }
            if (luck)
            {
                float[] weights = Recycling.LuckWeights(player);
                float total = Mathf.Max(0.0001f, weights[0] + weights[1] + weights[2] + weights[3] + weights[4]);
                string Pct(int i) => Mathf.RoundToInt(100f * weights[i] / total) + "%";
                text.Append($"<color=orange>Recycling (luck):</color> each material rolls {Pct(0)} crumbled, {Pct(1)} poor, " +
                    $"{Pct(2)} normal, {Pct(3)} good, {Pct(4)} jackpot.");
                if (Settings.SkillEnabled.Value && Plugin.RecyclingSkill != Skills.SkillType.None)
                {
                    text.Append($" Recycling skill: {Mathf.FloorToInt(player.GetSkillLevel(Plugin.RecyclingSkill))}.");
                }
            }
            else
            {
                text.Append($"<color=orange>Recycling:</color> gives back {Mathf.Clamp(Settings.FlatPercent.Value, 0, 100)}% of what it cost to make.");
            }
            if (item.m_quality > 1)
            {
                text.Append(Settings.IncludeUpgrades.Value ? " Upgrades included." : " Upgrade materials are not given back.");
            }
            if (durability < 1.0)
            {
                text.Append($" Worn: {Mathf.RoundToInt((float)durability * 100f)}% of that.");
            }
            if (item.m_shared.m_maxStackSize > 1)
            {
                text.Append($"\nClick recycles {Math.Min(item.m_stack, info.Batch)} (one craft makes {info.Batch}); hold Shift to recycle all {item.m_stack}.");
                if (info.Batch > 1)
                {
                    text.Append(" Less than a full craft gives back its share, rounded down.");
                }
            }
            if (inUse)
            {
                text.Append("\n<color=red>Equipped: unequip it first.</color>");
            }
            return text.ToString();
        }

        private static string ResearchSummary(ResearchInfo info, string name, int available)
        {
            StringBuilder text = new StringBuilder();
            text.Append($"<color=orange>Research:</color> {info.Done} of {info.Needed} done. Each research uses up ");
            text.Append(info.Batch > 1 ? $"{info.Batch} (one craft's worth)" : "the item");
            text.Append("; nothing comes back.");
            text.Append($" After {info.Needed} you can craft {info.LearnsName}");
            text.Append(info.CraftedAt != null ? $" at the {Localization.instance.Localize(info.CraftedAt.m_name)}" : "");
            text.Append(info.MadeIn != null ? $"; it becomes {name} in the {info.MadeIn}." : ".");
            if (available < info.Batch)
            {
                text.Append(info.Batch > 1
                    ? $"\n<color=red>You have {available} unequipped; one research needs {info.Batch}.</color>"
                    : "\n<color=red>Equipped: unequip it first.</color>");
            }
            return text.ToString();
        }

        private static void OnButtonClicked(InventoryGui gui)
        {
            Player player = Player.m_localPlayer;
            if (mode == Mode.None || player == null || craftTimer(gui) >= 0f)
            {
                return;
            }
            ItemDrop.ItemData item = (ItemDrop.ItemData)pairItem.GetValue(selectedRecipe.GetValue(gui));
            CraftingStation station = player.GetCurrentCraftingStation();
            if (item == null || station == null)
            {
                return;
            }
            string name = Localization.instance.Localize(item.m_shared.m_name);
            bool gear = item.m_shared.m_maxStackSize <= 1;
            ConfirmMode confirm = Settings.Confirm.Value;
            bool ask = confirm == ConfirmMode.Everything || (confirm == ConfirmMode.Gear && gear);
            Mode clickedIn = mode;

            if (mode == Mode.Recycle)
            {
                RecycleInfo info = Info(item, station);
                if (info == null || info.Elsewhere != null)
                {
                    return;
                }
                int count = Count(item, info.Batch);
                string what = item.m_shared.m_maxQuality > 1 ? $"{name} (level {item.m_quality})" : count > 1 ? $"{name} x{count}" : name;
                Run(gui, ask, "Recycle?", $"Recycle {what}?\n\n{RecycleBack(info, item, count)}", what, clickedIn,
                    () => Recycling.Recycle(player, player.GetCurrentCraftingStation(), item, count));
            }
            else
            {
                ResearchInfo info = Research.Evaluate(player, item);
                if (info == null)
                {
                    return;
                }
                string what = item.m_shared.m_maxQuality > 1 ? $"{name} (level {item.m_quality})" : info.Batch > 1 ? $"{name} x{info.Batch}" : name;
                Run(gui, ask, "Research?", $"Research {what}? It will be used up, and nothing comes back.\n\n" +
                    $"Research {info.Done + 1} of {info.Needed}.", what, clickedIn,
                    () => Research.DoResearch(player, player.GetCurrentCraftingStation(), item));
            }
        }

        private static string RecycleBack(RecycleInfo info, ItemDrop.ItemData item, int count)
        {
            bool luck = Settings.Mode.Value == ReturnMode.Luck;
            double durability = Recycling.DurabilityFactor(item);
            List<string> back = new List<string>();
            foreach (Yield yield in info.Yields)
            {
                int amount = luck ? yield.Max(count) : Recycling.FlatAmount(yield, count, durability);
                if (amount > 0)
                {
                    back.Add($"{amount} {Localization.instance.Localize(yield.Resource.m_itemData.m_shared.m_name)}");
                }
            }
            return back.Count == 0 ? "You get nothing back." : (luck ? "You get back up to: " : "You get back: ") + string.Join(", ", back);
        }

        // Asks first when needed (the game's own Yes/No popup; if that isn't available, a second click within a few
        // seconds confirms), then acts. Everything is checked again on Yes: the inventory may have closed meanwhile.
        private static void Run(InventoryGui gui, bool ask, string header, string text, string what, Mode clickedIn, Func<bool> action)
        {
            void Act()
            {
                if (mode != clickedIn || Player.m_localPlayer == null || !InventoryGui.IsVisible() || craftTimer(gui) >= 0f)
                {
                    return;
                }
                try
                {
                    if (action())
                    {
                        cachedVersion = -1;
                        updateCraftingPanel.Invoke(gui, new object[] { false });
                    }
                }
                catch (Exception e)
                {
                    LogOnce("action", e);
                }
            }

            if (!ask)
            {
                Act();
                return;
            }
            if (!UnifiedPopup.IsAvailable())
            {
                ItemDrop.ItemData item = (ItemDrop.ItemData)pairItem.GetValue(selectedRecipe.GetValue(gui));
                if (pendingItem == item && Time.time - pendingSince < 4f)
                {
                    pendingItem = null;
                    Act();
                }
                else
                {
                    pendingItem = item;
                    pendingSince = Time.time;
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"Click again to confirm: {what}");
                }
                return;
            }
            UnifiedPopup.Push(new YesNoPopup(header, text,
                () =>
                {
                    UnifiedPopup.Pop();
                    Act();
                },
                () => UnifiedPopup.Pop(),
                localizeText: false, coverBackground: true));
        }

        // A click recycles one craft's worth; Shift (or the left stick on a gamepad, like multi-craft) the whole stack.
        private static int Count(ItemDrop.ItemData item, int batch)
        {
            bool all = UnityInput.Current.GetKey(KeyCode.LeftShift) || UnityInput.Current.GetKey(KeyCode.RightShift) || ZInput.GetButton("JoyLStick");
            return all ? item.m_stack : Math.Min(item.m_stack, batch);
        }

        private static bool LevelOk(CraftingStation station, RecycleInfo info) =>
            !Settings.RequireStationLevel.Value || station.GetLevel() >= info.RequiredLevel;

        private static RecycleInfo Info(ItemDrop.ItemData item, CraftingStation station)
        {
            if (item != cachedItem || station != cachedStation || item.m_quality != cachedQuality || cachedVersion != Settings.Version)
            {
                cachedItem = item;
                cachedStation = station;
                cachedQuality = item.m_quality;
                cachedVersion = Settings.Version;
                cachedInfo = Recycling.EvaluateAnywhere(item, station);
            }
            return cachedInfo;
        }

        private static void LogOnce(string where, Exception e)
        {
            if (loggedErrors.Add(where + e.GetType().Name + e.Message))
            {
                Plugin.Log.LogError($"[{where}] {e}");
            }
        }
    }
}
