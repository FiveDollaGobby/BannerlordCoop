using Common.Messaging;
using Common.Util;
using GameInterface.Registry.Auto;
using GameInterface.Services.Armies.Messages;
using GameInterface.Services.Armies.Patches;
using GameInterface.Services.MobileParties.Extensions;
using GameInterface.Services.ObjectManager;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace GameInterface.Services.Armies;

public interface IArmyDisbander
{
    MobileParty[] Disband(Army army, Army.ArmyDispersionReason reason);
}

internal sealed class ArmyDisbander : IArmyDisbander
{
    private readonly IMessageBroker messageBroker;
    private readonly IObjectManager objectManager;

    public ArmyDisbander(IMessageBroker messageBroker, IObjectManager objectManager)
    {
        this.messageBroker = messageBroker;
        this.objectManager = objectManager;
    }

    public MobileParty[] Disband(Army army, Army.ArmyDispersionReason reason)
    {
        if (army._armyIsDispersing)
            return Array.Empty<MobileParty>();

        var parties = GetLinkedParties(army);
        if (!objectManager.Contains(army) && parties.Length == 0)
            return Array.Empty<MobileParty>();

        var releasedParties = new List<MobileParty>();
        CampaignEventDispatcher.Instance.OnArmyDispersed(
            army,
            reason,
            parties.Any(party => party.IsPlayerParty()));
        army._armyIsDispersing = true;

        try
        {
            foreach (var party in parties)
            {
                if (party.Army != null && !ReferenceEquals(party.Army, army))
                {
                    army._parties.Remove(party);
                    continue;
                }

                if (!army._parties.Contains(party))
                    army._parties.Add(party);
                if (party.Army == null)
                    party._army = army;

                messageBroker.Publish(party, new MobilePartyInArmyRemoved(army, party, null));
                ArmyPatches.RemoveMobilePartyInArmyImmediate(party, army, null);
                party.ArmyPositionAdder = Vec2.Zero;
                releasedParties.Add(party);
            }

            army._parties.Clear();
            army.Kingdom = null;
            army._hourlyTickEvent?.DeletePeriodicEvent();
            army._tickEvent?.DeletePeriodicEvent();
        }
        finally
        {
            army._armyIsDispersing = false;
        }

        if (objectManager.Contains(army))
            messageBroker.Publish(army, new InstanceDestroyed<Army>(army));

        return releasedParties.ToArray();
    }

    private static MobileParty[] GetLinkedParties(Army army)
    {
        var parties = new HashSet<MobileParty>(
            army.Parties.Where(party => party != null));
        var campaignParties = Campaign.Current?.CampaignObjectManager?.MobileParties ??
            Enumerable.Empty<MobileParty>();

        foreach (var party in campaignParties)
        {
            if (party == null)
                continue;

            if (ReferenceEquals(party.Army, army) ||
                (party.Army == null &&
                 ReferenceEquals(party.AttachedTo, army.LeaderParty)))
            {
                parties.Add(party);
            }
        }

        return parties.ToArray();
    }
}
