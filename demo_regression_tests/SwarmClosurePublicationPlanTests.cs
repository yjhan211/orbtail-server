using game_server.sessions;
using game_server.matches.field;
using System.Collections.Immutable;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SwarmClosurePublicationPlanTests
{
    [Fact]
    public void Recipients_PreserveReferencesOrderAndDuplicatesAfterSourceChanges()
    {
        var first = TestGameSessionServices.CreateRecipientSession();
        var second = TestGameSessionServices.CreateRecipientSession();
        var recipients = new List<GameClientSession> { second, first, second };
        var outbound = new SwarmFieldStateOutbound(1001, recipients.ToImmutableArray());

        recipients.Clear();

        Assert.Equal(3, outbound.Recipients.Length);
        Assert.Same(second, outbound.Recipients[0]);
        Assert.Same(first, outbound.Recipients[1]);
        Assert.Same(second, outbound.Recipients[2]);
    }

    [Fact]
    public void ImmutableOutboundPlan_FreezesInventoryAndRecipientOrder()
    {
        var mutableItem = new InGameItemInfo
        {
            ItemUid = 7001,
            ItemId = 107000010,
            Count = 0,
            GiftState = GiftState.Received
        };
        GameClientSession[] sessions = Enumerable.Range(0, 3).Select(_ => TestGameSessionServices.CreateRecipientSession()).ToArray();
        ImmutableArray<GameClientSession> recipients = [sessions[2], sessions[0], sessions[1]];
        SwarmInGameItemSnapshot itemSnapshot = SwarmInGameItemSnapshot.Capture(mutableItem);
        var plan = new SwarmClosurePublicationPlan(
            51_001,
            [new SwarmInventoryUpdateOutbound(itemSnapshot, recipients)]);

        mutableItem.ItemUid = 9999;
        mutableItem.ItemId = 1;
        mutableItem.Count = 5;
        mutableItem.GiftState = GiftState.None;

        SwarmInventoryUpdateOutbound outbound =
            Assert.IsType<SwarmInventoryUpdateOutbound>(Assert.Single(plan.Outbound));
        Assert.Equal([sessions[2], sessions[0], sessions[1]], outbound.Recipients.ToArray());
        Assert.Equal(7001, outbound.Item.ItemUid);
        Assert.Equal(107000010, outbound.Item.ItemId);
        Assert.Equal(0, outbound.Item.Count);
        Assert.Equal(GiftState.Received, outbound.Item.GiftState);
        InGameItemInfo restored = outbound.Item.ToModel();
        Assert.Equal(7001, restored.ItemUid);
        Assert.Equal(GiftState.Received, restored.GiftState);
    }

    [Fact]
    public void ImmutableOutboundPlan_PreservesClosureWireOrderAndScalarPayloads()
    {
        ImmutableArray<GameClientSession> allRecipients = [TestGameSessionServices.CreateRecipientSession(), TestGameSessionServices.CreateRecipientSession()];
        var plan = new SwarmClosurePublicationPlan(
            51_010,
            [
                new SwarmFieldStateOutbound(1_001, allRecipients),
                new SwarmClosureWarningOutbound(AreaType.S2Classroom1, 15, 2_002, allRecipients),
                new SwarmAreaClosedOutbound(AreaType.S2Classroom1, allRecipients),
                new SwarmDoorStateOutbound(3003, allRecipients),
                new SwarmInventoryUpdateOutbound(
                    new SwarmInGameItemSnapshot(4004, 107000010, 0, GiftState.None),
                    [allRecipients[1]]),
                new SwarmRingVfxOutbound(5005, 1.5f, 2.5f, 3.5f, 1, 5005, 2, [allRecipients[1], allRecipients[0]])
            ]);

        Assert.Collection(
            plan.Outbound,
            field => Assert.Equal(1_001, Assert.IsType<SwarmFieldStateOutbound>(field).StartedAtUnixMs),
            warning =>
            {
                SwarmClosureWarningOutbound value = Assert.IsType<SwarmClosureWarningOutbound>(warning);
                Assert.Equal(AreaType.S2Classroom1, value.Area);
                Assert.Equal(15, value.SecondsRemaining);
                Assert.Equal(2_002, value.ClosureAtUnixMs);
            },
            closed => Assert.Equal(
                AreaType.S2Classroom1,
                Assert.IsType<SwarmAreaClosedOutbound>(closed).Area),
            door => Assert.Equal(3003, Assert.IsType<SwarmDoorStateOutbound>(door).DoorId),
            inventory => Assert.Equal(
                4004,
                Assert.IsType<SwarmInventoryUpdateOutbound>(inventory).Item.ItemUid),
            ring =>
            {
                SwarmRingVfxOutbound value = Assert.IsType<SwarmRingVfxOutbound>(ring);
                Assert.Equal(5005, value.OwnerPlayerId);
                Assert.Equal(1.5f, value.CenterX);
                Assert.Equal(2.5f, value.CenterY);
                Assert.Equal(3.5f, value.Radius);
                Assert.Equal(1, value.Kind);
                Assert.Equal(5005, value.VictimPlayerId);
                Assert.Equal(2, value.FromOrdinal);
                Assert.Equal([allRecipients[1], allRecipients[0]], value.Recipients.ToArray());
            });
    }
}
