using Common.Messaging;
using Common.Network;
using E2E.Tests.Util;
using GameInterface.Services.MapEventParties;
using GameInterface.Services.Party.Data;
using GameInterface.Services.Party.Messages;
using GameInterface.Services.Players;
using GameInterface.Services.Players.Data;
using GameInterface.Services.Settlements.Messages;
using GameInterface.Services.TroopRosters.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using Xunit.Abstractions;

namespace E2E.Tests.Services.PartyComponents;
public class GarrisonPartyComponentTests : SyncTestBase
{
    string ComponentId;
    public GarrisonPartyComponentTests(ITestOutputHelper output) : base(output)
    {
        ComponentId = TestEnvironment.CreateRegisteredObject<GarrisonPartyComponent>();
        TestEnvironment.CreateRegisteredObject<Settlement>();

    }

    [Fact]
    public void Server_GarrisonPartyComponent_Properties()
    {
        Server.ObjectManager.TryGetObject(ComponentId, out GarrisonPartyComponent component);
        component.Settlement = null;
        TestEnvironment.AssertReferenceProperty<GarrisonPartyComponent, Settlement>(nameof(GarrisonPartyComponent.Settlement));
    }

    [Fact]
    public void ServerCreateParty_SyncAllClients()
    {
        // Arrange
        var server = TestEnvironment.Server;

        // Act
        string? partyId = null;
        string? settlementId = null;

        server.Call(() =>
        {
            var settlement = GameObjectCreator.CreateInitializedObject<Settlement>();
            settlement.Town = GameObjectCreator.CreateInitializedObject<Town>();

            var newSettlement = GameObjectCreator.CreateInitializedObject<Settlement>();

            var newParty = GarrisonPartyComponent.CreateGarrisonParty("TestId", settlement);
            GarrisonPartyComponent garrison = (GarrisonPartyComponent)newParty.PartyComponent;
            garrison.Settlement = newSettlement;

            Assert.True(server.ObjectManager.TryGetId(newSettlement, out settlementId));

            Assert.True(server.ObjectManager.TryGetId(newParty, out partyId));
        });


        // Assert
        Assert.NotNull(partyId);

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(partyId, out var newParty));
            Assert.IsType<GarrisonPartyComponent>(newParty.PartyComponent);
            GarrisonPartyComponent garrison = (GarrisonPartyComponent)newParty.PartyComponent;
            Assert.True(client.ObjectManager.TryGetId(garrison.Settlement, out string clientGarrisonSettlementId));

            Assert.Equal(settlementId, clientGarrisonSettlementId);
        }
    }

    [Fact]
    public void ClientCreateParty_DoesNothing()
    {
        // Arrange
        var server = TestEnvironment.Server;
        var client1 = TestEnvironment.Clients.First();

        var settlement = GameObjectCreator.CreateInitializedObject<Settlement>();
        settlement.Town = GameObjectCreator.CreateInitializedObject<Town>();

        // Act
        PartyComponent? partyComponent = null;
        client1.Call(() =>
        {
            partyComponent = new GarrisonPartyComponent(settlement, new GarrisonPartyComponent.InitializationArgs());
        });

        Assert.NotNull(partyComponent);


        // Assert
        Assert.False(client1.ObjectManager.TryGetId(partyComponent, out var _));
    }

    [Fact]
    public void RequestMissingGarrison_CreatesOnePartyOnAllClients()
    {
        const string controllerId = "missing-garrison-player";
        string settlementId = null;
        string garrisonId = null;

        Server.Call(() =>
        {
            var settlement = GameObjectCreator.CreateInitializedObject<Settlement>();
            var town = GameObjectCreator.CreateInitializedObject<Town>();
            var playerParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            settlement.SetSettlementComponent(town);
            town.OwnerClan = playerParty.ActualClan;
            town.IsOwnerUnassigned = false;
            playerParty.CurrentSettlement = settlement;

            Assert.Null(town.GarrisonParty);
            Assert.True(Server.ObjectManager.TryGetId(settlement, out settlementId));
            Assert.True(Server.ObjectManager.TryGetId(playerParty, out var playerPartyId));
            Assert.True(Server.ObjectManager.TryGetId(playerParty.LeaderHero, out var heroId));
            Assert.True(Server.ObjectManager.TryGetId(playerParty.ActualClan, out var clanId));
            Assert.True(Server.Resolve<IPlayerManager>().AddPlayer(
                new Player(controllerId, heroId, playerPartyId, clanId, null)));
        });

        var client = Clients.First();
        TestEnvironment.ConnectRegisteredPlayer(client, controllerId);
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            client.Resolve<IMessageBroker>().Publish(this, new NewGarrisonParty(settlement));
        });
        TestEnvironment.FlushCoalescer();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.NotNull(settlement.Town.GarrisonParty);
            Assert.True(Server.ObjectManager.TryGetId(settlement.Town.GarrisonParty, out garrisonId));
        });

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            client.Resolve<IMessageBroker>().Publish(this, new NewGarrisonParty(settlement));
        });
        TestEnvironment.FlushCoalescer();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetId(settlement.Town.GarrisonParty, out var currentGarrisonId));
            Assert.Equal(garrisonId, currentGarrisonId);
        });

        foreach (var otherClient in Clients)
        {
            Assert.True(otherClient.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(otherClient.ObjectManager.TryGetObject<MobileParty>(garrisonId, out var garrison));
            Assert.Same(garrison, settlement.Town.GarrisonParty);
            Assert.Same(settlement, garrison.HomeSettlement);
        }
    }

    [Fact]
    public void ManageNewGarrison_TransfersTroopOnAllClients()
    {
        const string controllerId = "manage-new-garrison-player";
        string settlementId = null;
        string playerPartyId = null;
        string mainHeroId = null;
        string characterId = null;
        string garrisonId = null;
        string garrisonPartyBaseId = null;

        Server.Call(() =>
        {
            var settlement = GameObjectCreator.CreateInitializedObject<Settlement>();
            var town = GameObjectCreator.CreateInitializedObject<Town>();
            var playerParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var character = GameObjectCreator.CreateInitializedObject<CharacterObject>();

            settlement.SetSettlementComponent(town);
            town.OwnerClan = playerParty.ActualClan;
            town.IsOwnerUnassigned = false;
            playerParty.CurrentSettlement = settlement;
            playerParty.MemberRoster.AddToCounts(character, 1);

            Assert.True(Server.ObjectManager.TryGetId(settlement, out settlementId));
            Assert.True(Server.ObjectManager.TryGetId(playerParty, out playerPartyId));
            Assert.True(Server.ObjectManager.TryGetId(playerParty.LeaderHero, out mainHeroId));
            Assert.True(Server.ObjectManager.TryGetId(playerParty.ActualClan, out var clanId));
            Assert.True(Server.ObjectManager.TryGetId(character, out characterId));
            Assert.True(Server.Resolve<IPlayerManager>().AddPlayer(
                new Player(controllerId, mainHeroId, playerPartyId, clanId, characterId)));
        });

        var client = Clients.First();
        TestEnvironment.ConnectRegisteredPlayer(client, controllerId);
        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            client.Resolve<IMessageBroker>().Publish(this, new NewGarrisonParty(settlement));
        });
        TestEnvironment.FlushCoalescer();

        Server.Call(() =>
        {
            Assert.True(Server.ObjectManager.TryGetObject<Settlement>(settlementId, out var settlement));
            Assert.True(Server.ObjectManager.TryGetId(settlement.Town.GarrisonParty, out garrisonId));
            Assert.True(Server.ObjectManager.TryGetId(settlement.Town.GarrisonParty.Party, out garrisonPartyBaseId));
        });

        var message = new NetworkCompleteDoneLogic(
            mainHeroId,
            Array.Empty<FlattenedTroop>(),
            Array.Empty<FlattenedTroop>(),
            Array.Empty<FlattenedTroop>(),
            Delta(characterId, 1),
            EmptyDelta(),
            Delta(characterId, -1),
            EmptyDelta(),
            Array.Empty<ItemRosterElement>(),
            new UpgradedTroopHistoryData(new List<UpgradedTroopHistoryElementData>()),
            garrisonPartyBaseId,
            leftPrisonerRosterId: null,
            partyGoldChangeAmount: 0,
            partyInfluenceChangeAmount: 0,
            partyMoraleChangeAmount: 0,
            doNotApplyGoldTransactions: true,
            default,
            Helpers.PartyScreenHelper.PartyScreenMode.Normal,
            new TroopRosterOrderData(new Dictionary<int, string>()));

        client.Call(() => client.Resolve<INetwork>().SendAll(message));
        TestEnvironment.FlushCoalescer();

        Server.Call(() => AssertTransferred(Server));
        foreach (var otherClient in Clients)
            AssertTransferred(otherClient);

        void AssertTransferred(E2E.Tests.Environment.Instance.EnvironmentInstance instance)
        {
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(playerPartyId, out var playerParty));
            Assert.True(instance.ObjectManager.TryGetObject<MobileParty>(garrisonId, out var garrison));
            Assert.True(instance.ObjectManager.TryGetObject<CharacterObject>(characterId, out var character));
            Assert.Equal(0, playerParty.MemberRoster.GetTroopCount(character));
            Assert.Equal(1, garrison.MemberRoster.GetTroopCount(character));
        }
    }

    private static TroopRosterData Delta(string characterId, int number) =>
        new TroopRosterData(new[] { new TroopRosterElementData(characterId, number, 0, 0) });

    private static TroopRosterData EmptyDelta() =>
        new TroopRosterData(Array.Empty<TroopRosterElementData>());
}
