using Common;
using Common.Logging;
using Common.Messaging;
using GameInterface.Services.Settlements.Messages;
using HarmonyLib;
using Helpers;
using Serilog;
using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace GameInterface.Services.Settlements.Patches.Disable;

[HarmonyPatch(typeof(PlayerTownVisitCampaignBehavior))]
internal class DisablePlayerTownVisitCampaignBehavior
{
    private const int MaxGarrisonReadyChecks = 300;
    private static readonly ILogger Logger = LogManager.GetLogger<DisablePlayerTownVisitCampaignBehavior>();
    private static readonly Dictionary<string, DateTime> PendingGarrisonRequests = new Dictionary<string, DateTime>();
    private static readonly HashSet<string> WaitingForGarrison = new HashSet<string>();

    /// <summary>
    /// Disables entering the arena from the town menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_town_town_arena_on_consequence")]
    static bool DisableArena()
    {
        return true;
    }

    /// <summary>
    /// Disables entering the tavern from the town menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_town_town_tavern_on_consequence")]
    static bool DisableTavern()
    {
        return true;
    }

    /// <summary>
    /// Disables entering the jail from the castle menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_castle_dungeon_on_consequence")]
    static bool DisableCastleDungeon()
    {
        return false;
    }

    /// <summary>
    /// Disables entering the jail from the town menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_town_dungeon_on_consequence")]
    static bool DisableTownDungeon()
    {
        return false;
    }

    /// <summary>
    /// Disables entering the market from the town menu
    /// </summary>
    //[HarmonyPrefix]
    //[HarmonyPatch("game_menu_town_town_market_on_consequence")]
    //static bool DisableTownMarket()
    //{
    //    return false;
    //}

    /// <summary>
    /// Allows entering the town center from the town menu (location-sync handles it as a P2P instance).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_town_town_streets_on_consequence")]
    static bool DisableTownCenter()
    {
        return true;
    }

    /// <summary>
    /// Allows entering the town hall from the town menu (location-sync handles it as a P2P instance).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_town_lordshall_on_consequence")]
    static bool DisableTownHall()
    {
        return true;
    }

    /// <summary>
    /// Disables entering the town keep from the town menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_town_go_to_keep_on_consequence")]
    static bool DisableTownKeep()
    {
        return true;
    }

    /// <summary>
    /// Allows entering the village center from the village menu (location-sync handles it as a P2P instance).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_village_village_center_on_consequence")]
    static bool DisableVillageCenter()
    {
        return true;
    }

    /// <summary>
    /// Allows entering the castle hall from the castle menu (location-sync handles it as a P2P instance).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_castle_lordshall_on_consequence")]
    static bool DisableCastleHall()
    {
        return true;
    }

    /// <summary>
    /// Disables entering the garrison management from the town menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_leave_troops_garrison_on_consequece")]
    static bool DisableGarrison()
    {
        return true;
    }

    /// <summary>
    /// Disables entering the town management from the town menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_ui_town_manage_town_on_consequence")]
    static bool DisableTownManagement()
    {
        return true;
    }

    /// <summary>
    /// Disables entering the castle management from the castle menu
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_ui_town_castle_manage_town_on_consequence")]
    static bool DisableCastleManagement()
    {
        return true;
    }

    /// <summary>
    /// Allows the castle walk-around from the castle menu (location-sync handles it as a P2P instance).
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch("game_menu_castle_take_a_walk_around_the_castle_on_consequence")]
    static bool DisableCastleWalkAround()
    {
        return true;
    }

    [HarmonyPatch(nameof(PlayerTownVisitCampaignBehavior.game_menu_manage_garrison_on_consequence))]
    [HarmonyPrefix]
    private static bool Prefix(PlayerTownVisitCampaignBehavior __instance)
    {
        Settlement currentSettlement = Hero.MainHero.CurrentSettlement;
        if (currentSettlement.Town.GarrisonParty != null)
        {
            PartyScreenHelper.OpenScreenAsManageTroops(currentSettlement.Town.GarrisonParty);
            return false;
        }
        RequestGarrison(__instance, currentSettlement);
        WaitForGarrison(currentSettlement, isDonate: false);
        return false;
    }

    [HarmonyPatch("game_menu_manage_garrison_on_condition")]
    [HarmonyPostfix]
    private static void ManageGarrisonConditionPostfix(PlayerTownVisitCampaignBehavior __instance)
    {
        if (!ModInformation.IsClient)
            return;

        var mainHero = Hero.MainHero;
        var settlement = mainHero?.CurrentSettlement;
        if (settlement?.Town == null)
            return;

        if (settlement.Town.GarrisonParty != null)
        {
            PendingGarrisonRequests.Remove(settlement.StringId);
            return;
        }

        if (!ReferenceEquals(settlement.OwnerClan, mainHero.Clan))
            return;

        RequestGarrison(__instance, settlement);
    }

    [HarmonyPatch(nameof(PlayerTownVisitCampaignBehavior.game_menu_leave_troops_garrison_on_consequece))]
    [HarmonyPrefix]
    private static bool Prefixx(PlayerTownVisitCampaignBehavior __instance)
    {
        Settlement currentSettlement = Hero.MainHero.CurrentSettlement;
        if (currentSettlement.Town.GarrisonParty != null)
        {
            PartyScreenHelper.OpenScreenAsDonateGarrisonWithCurrentSettlement();
            return false;
        }
        RequestGarrison(__instance, currentSettlement);
        WaitForGarrison(currentSettlement, isDonate: true);
        return false;
    }

    private static void RequestGarrison(PlayerTownVisitCampaignBehavior instance, Settlement settlement)
    {
        var now = DateTime.UtcNow;
        if (PendingGarrisonRequests.TryGetValue(settlement.StringId, out var retryAt) &&
            retryAt > now)
        {
            return;
        }

        PendingGarrisonRequests[settlement.StringId] = now.AddSeconds(5);
        Logger.Information("Requesting missing garrison for {SettlementId}", settlement.StringId);
        MessageBroker.Instance.Publish(instance, new NewGarrisonParty(settlement));
    }

    private static void WaitForGarrison(Settlement settlement, bool isDonate)
    {
        if (!WaitingForGarrison.Add(settlement.StringId))
            return;

        CheckGarrisonReady(settlement, isDonate, checks: 0);
    }

    private static void CheckGarrisonReady(Settlement settlement, bool isDonate, int checks)
    {
        if (settlement.Town.GarrisonParty != null)
        {
            PendingGarrisonRequests.Remove(settlement.StringId);
            WaitingForGarrison.Remove(settlement.StringId);
            if (isDonate)
                PartyScreenHelper.OpenScreenAsDonateGarrisonWithCurrentSettlement();
            else
                PartyScreenHelper.OpenScreenAsManageTroops(settlement.Town.GarrisonParty);
            return;
        }

        if (Hero.MainHero?.CurrentSettlement != settlement)
        {
            WaitingForGarrison.Remove(settlement.StringId);
            return;
        }

        if (checks >= MaxGarrisonReadyChecks)
        {
            WaitingForGarrison.Remove(settlement.StringId);
            Logger.Warning("Garrison for {SettlementId} was not created in time", settlement.StringId);
            return;
        }

        GameThread.EnqueueSafe(() => CheckGarrisonReady(settlement, isDonate, checks + 1));
    }
    [HarmonyPatch(nameof(PlayerTownVisitCampaignBehavior.game_menu_town_on_init))]
    [HarmonyPrefix]
    private static bool GameMenuTownOnInitPrefix(PlayerTownVisitCampaignBehavior __instance, MenuCallbackArgs args)
    {
        PlayerTownVisitCampaignBehavior.SetIntroductionText(Settlement.CurrentSettlement, false);
        PlayerTownVisitCampaignBehavior.UpdateMenuLocations(args.MenuContext.GameMenu.StringId);
        Settlement currentSettlement = Settlement.CurrentSettlement;
        if (MenuHelper.CheckAndOpenNextLocation(args))
        {
            return false;
        }
        args.MenuTitle = new TextObject("{=mVKcvY2U}Town Center", null);
        return false;
    }
    /// <summary>
    /// Disables entering the village buy goods from the village menu
    /// </summary>
    //[HarmonyPrefix]
    //[HarmonyPatch("game_menu_ui_village_buy_good_on_consequence")]
    //static bool DisableVillageBuyGoods()
    //{
    //    return false;
    //}

    /// <summary>
    /// Disables entering the town recruitment from the town menu
    /// </summary>
    //[HarmonyPrefix]
    //[HarmonyPatch("game_menu_ui_recruit_volunteers_on_consequence")]
    //static bool DisableTroopRecruitment()
    //{
    //    return false;
    //}

    /// <summary>
    /// Disables entering the town stash from the town menu
    /// </summary>
    //[HarmonyPrefix]
    //[HarmonyPatch("game_menu_town_keep_open_stash_on_consequence")]
    //static bool DisableTownStash()
    //{
    //    return false;
    //}
}