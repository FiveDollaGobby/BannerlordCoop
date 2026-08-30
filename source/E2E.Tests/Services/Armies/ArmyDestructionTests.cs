using Common.Messaging;
using E2E.Tests.Environment;
using E2E.Tests.Util;
using GameInterface.Services.Armies;
using GameInterface.Services.Armies.Messages;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;
using Xunit.Abstractions;

namespace E2E.Tests.Services.Armies;

public class ArmyDestructionTests : IDisposable
{
    E2ETestEnvironment TestEnvironment { get; }
    public ArmyDestructionTests(ITestOutputHelper output)
    {
        TestEnvironment = new E2ETestEnvironment(output);
    }

    public void Dispose()
    {
        TestEnvironment.Dispose();
    }

    [Fact]
    public void ServerDestroyArmy_SyncAllClients()
    {
        // Arrange
        var server = TestEnvironment.Server;

        string? armyId = null;
        server.Call(() =>
        {

            var kingdom = GameObjectCreator.CreateInitializedObject<Kingdom>();
            var mobileParty = GameObjectCreator.CreateInitializedObject<MobileParty>();


            var army = new Army(kingdom, mobileParty, Army.ArmyTypes.Patrolling);

            Assert.True(server.ObjectManager.TryGetId(army, out armyId));
        });

        Assert.NotNull(armyId);

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.True(client.ObjectManager.TryGetObject<Army>(armyId, out var _));
        }

        // Act
        server.Call(() =>
        {
            Assert.True(server.ObjectManager.TryGetObject<Army>(armyId, out var army));

            DisbandArmyAction.ApplyByObjectiveFinished(army);
        }, new[] { AccessTools.Method(typeof(PartyBase), nameof(PartyBase.UpdateVisibilityAndInspected)) });

        // Assert
        Assert.False(server.ObjectManager.TryGetObject<Army>(armyId, out var _));

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.False(client.ObjectManager.TryGetObject<Army>(armyId, out var _));
        }
    }

    [Fact]
    public void ClientDestroyArmy_DoesNothing()
    {
        // Arrange
        var server = TestEnvironment.Server;
        var client1 = TestEnvironment.Clients.First();

        string? armyId = null;
        server.Call(() =>
        {

            var kingdom = GameObjectCreator.CreateInitializedObject<Kingdom>();
            var mobileParty = GameObjectCreator.CreateInitializedObject<MobileParty>();

            var army = new Army(kingdom, mobileParty, Army.ArmyTypes.Patrolling);

            Assert.True(server.ObjectManager.TryGetId(army, out armyId));
        });

        Assert.NotNull(armyId);

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.True(client.ObjectManager.TryGetObject<Army>(armyId, out var _));
        }

        // Act
        client1.Call(() =>
        {
            Assert.True(client1.ObjectManager.TryGetObject<Army>(armyId, out var army));

            DisbandArmyAction.ApplyByObjectiveFinished(army);
        });

        // Assert
        Assert.True(server.ObjectManager.TryGetObject<Army>(armyId, out var _));

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.True(client.ObjectManager.TryGetObject<Army>(armyId, out var _));
        }
    }

    [Fact]
    public void ServerDisbandArmyWithoutMainParty_SyncAllClients()
    {
        var server = TestEnvironment.Server;
        string? armyId = null;
        string? leaderPartyId = null;

        server.Call(() =>
        {
            var kingdom = GameObjectCreator.CreateInitializedObject<Kingdom>();
            var leaderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var army = new Army(kingdom, leaderParty, Army.ArmyTypes.Patrolling);

            Assert.True(server.ObjectManager.TryGetId(army, out armyId));
            Assert.True(server.ObjectManager.TryGetId(leaderParty, out leaderPartyId));
            Campaign.Current.MainParty = null;
            Assert.Null(MobileParty.MainParty);

            server.Resolve<IArmyDisbander>().Disband(army, Army.ArmyDispersionReason.Unknown);

            Assert.Null(leaderParty.Army);
            Assert.False(server.ObjectManager.TryGetObject<Army>(armyId, out _));
        });

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.False(client.ObjectManager.TryGetObject<Army>(armyId, out _));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(leaderPartyId, out var leaderParty));
            Assert.Null(leaderParty.Army);
        }
    }

    [Fact]
    public void ServerDisbandArmy_ClearsPartiesMissingFromArmyMemberList()
    {
        var server = TestEnvironment.Server;
        string? armyId = null;
        string? followerId = null;

        server.Call(() =>
        {
            var kingdom = GameObjectCreator.CreateInitializedObject<Kingdom>();
            var leaderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var follower = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var army = new Army(kingdom, leaderParty, Army.ArmyTypes.Patrolling);
            follower._army = army;
            follower.AttachedTo = leaderParty;
            follower.ArmyPositionAdder = new Vec2(2f, 3f);

            Assert.DoesNotContain(follower, army._parties);
            Assert.True(server.ObjectManager.TryGetId(army, out armyId));
            Assert.True(server.ObjectManager.TryGetId(follower, out followerId));
        });

        foreach (var client in TestEnvironment.Clients)
        {
            client.Call(() =>
            {
                Assert.True(client.ObjectManager.TryGetObject<Army>(armyId, out var army));
                Assert.True(client.ObjectManager.TryGetObject<MobileParty>(followerId, out var follower));
                follower._army = army;
                follower.AttachedTo = army.LeaderParty;
                follower.ArmyPositionAdder = new Vec2(2f, 3f);
                army._parties.Remove(follower);
            });
        }

        server.Call(() =>
        {
            Assert.True(server.ObjectManager.TryGetObject<Army>(armyId, out var army));
            Assert.True(server.ObjectManager.TryGetObject<MobileParty>(followerId, out var follower));

            var disbander = server.Resolve<IArmyDisbander>();
            var releasedParties = disbander.Disband(army, Army.ArmyDispersionReason.Unknown);
            var releasedAgain = disbander.Disband(army, Army.ArmyDispersionReason.Unknown);

            Assert.Contains(follower, releasedParties);
            Assert.Empty(releasedAgain);
            Assert.Null(follower.Army);
            Assert.Null(follower.AttachedTo);
            Assert.Equal(Vec2.Zero, follower.ArmyPositionAdder);
            Assert.False(server.ObjectManager.TryGetObject<Army>(armyId, out _));
        });

        foreach (var client in TestEnvironment.Clients)
        {
            Assert.False(client.ObjectManager.TryGetObject<Army>(armyId, out _));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(followerId, out var follower));
            Assert.Null(follower.Army);
            Assert.Null(follower.AttachedTo);
            Assert.Equal(Vec2.Zero, follower.ArmyPositionAdder);
        }
    }

    [Fact]
    public void MissingArmyRemoval_DetachesStaleClientParty()
    {
        var client = TestEnvironment.Clients.First();
        string? armyId = null;
        string? followerId = null;

        TestEnvironment.Server.Call(() =>
        {
            var kingdom = GameObjectCreator.CreateInitializedObject<Kingdom>();
            var leaderParty = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var follower = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var army = new Army(kingdom, leaderParty, Army.ArmyTypes.Patrolling);

            Assert.True(TestEnvironment.Server.ObjectManager.TryGetId(army, out armyId));
            Assert.True(TestEnvironment.Server.ObjectManager.TryGetId(follower, out followerId));
        });

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Army>(armyId, out var army));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(followerId, out var follower));
            follower._army = army;
            follower.AttachedTo = army.LeaderParty;
            follower.ArmyPositionAdder = new Vec2(4f, 5f);
            Assert.True(client.ObjectManager.Remove(army));

            client.Resolve<IMessageBroker>().Publish(
                this,
                new NetworkRemovePartyInArmy(armyId, followerId, string.Empty));

            Assert.Null(follower.Army);
            Assert.Null(follower.AttachedTo);
            Assert.Equal(Vec2.Zero, follower.ArmyPositionAdder);
        });
    }

    [Fact]
    public void MissingArmyRemoval_PreservesAnotherRegisteredArmy()
    {
        var client = TestEnvironment.Clients.First();
        string? oldArmyId = null;
        string? newArmyId = null;
        string? followerId = null;

        TestEnvironment.Server.Call(() =>
        {
            var kingdom = GameObjectCreator.CreateInitializedObject<Kingdom>();
            var oldLeader = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var newLeader = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var follower = GameObjectCreator.CreateInitializedObject<MobileParty>();
            var oldArmy = new Army(kingdom, oldLeader, Army.ArmyTypes.Patrolling);
            var newArmy = new Army(kingdom, newLeader, Army.ArmyTypes.Patrolling);

            Assert.True(TestEnvironment.Server.ObjectManager.TryGetId(oldArmy, out oldArmyId));
            Assert.True(TestEnvironment.Server.ObjectManager.TryGetId(newArmy, out newArmyId));
            Assert.True(TestEnvironment.Server.ObjectManager.TryGetId(follower, out followerId));
        });

        client.Call(() =>
        {
            Assert.True(client.ObjectManager.TryGetObject<Army>(oldArmyId, out var oldArmy));
            Assert.True(client.ObjectManager.TryGetObject<Army>(newArmyId, out var newArmy));
            Assert.True(client.ObjectManager.TryGetObject<MobileParty>(followerId, out var follower));
            follower._army = null;
            follower.AttachedTo = newArmy.LeaderParty;
            Assert.True(client.ObjectManager.Remove(oldArmy));

            client.Resolve<IMessageBroker>().Publish(
                this,
                new NetworkRemovePartyInArmy(oldArmyId, followerId, string.Empty));

            Assert.Same(newArmy, follower.Army);
            Assert.Same(newArmy.LeaderParty, follower.AttachedTo);
            Assert.Contains(follower, newArmy._parties);
        });
    }
}
