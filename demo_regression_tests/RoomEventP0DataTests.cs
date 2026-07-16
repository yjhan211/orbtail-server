using System.Text.Json;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class RoomEventP0DataTests
{
    [Fact]
    public void OnlyBroadcastEventUsesLatestP0ContributionContract()
    {
        Initialize();

        var roomEvent = GameRoomEventData.Get(188003);
        Assert.True(roomEvent.UsesWorldState);
        Assert.Equal(120, roomEvent.DurationSeconds);
        Assert.Equal(10, roomEvent.TargetContribution);
        Assert.Equal(5, roomEvent.MaxContributionPerPlayer);
        Assert.Equal(2, roomEvent.MaxTimeExtensions);
        Assert.Equal(15, roomEvent.TimeExtensionSeconds);
        Assert.Equal(2, roomEvent.MaxManualResponses);
        Assert.Equal(["soundproof", "record", "release"], roomEvent.ResponseTagPool);

        Assert.All(
            new[] { 188001, 188002, 188004, 188005 },
            eventId => Assert.False(GameRoomEventData.Get(eventId).UsesWorldState));
        Assert.Equal("억지로 연다", GameRoomEventData.Get(188004).Choices[0].Label.Kr);
    }

    [Fact]
    public void BroadcastKeepsExactlyFourServerAuthoredActions()
    {
        Initialize();
        var choices = GameRoomEventData.Get(188003).Choices;

        Assert.Equal(4, choices.Count);
        Assert.Equal([1, 2, 3, 4], choices.Select(choice => choice.SortOrder));
        Assert.Equal(
            ["contribute_material", "contribute_crafted_item", "extend_time", "manual_response"],
            choices.Select(choice => choice.WorldEffectId));
        Assert.Equal([1, 1, 0, 0], choices.Select(choice => choice.ConsumeItemCount));
        Assert.Equal(-15, choices[2].StaminaDelta);
        Assert.Equal(10, choices[3].MentalDelta);
    }

    [Fact]
    public void EveryResponseTagHasFourItemsAndAllPowerTiers()
    {
        Initialize();


        foreach (string tag in new[] { "soundproof", "record", "release" })
        {
            var items = GameRoomEventResponseItemData.GetAll()
                .Where(item => item.ResponseTags.Contains(tag))
                .ToList();

            Assert.True(items.Count >= 4, $"{tag} has only {items.Count} response items");
            Assert.Contains(items, item => item.ResponsePower == 1);
            Assert.Contains(items, item => item.ResponsePower == 3);
            Assert.Contains(items, item => item.ResponsePower == 5);
        }
    }

    [Fact]
    public void EveryResponseTagHasTwoStockAreasAndAtLeastTwelveInitialPower()
    {
        Initialize();
        string poolPath = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv", "area_item_pool.csv");
        var areaPools = CsvHelper.LoadCsv(poolPath)
            .Select(row => new
            {
                AreaType = int.Parse(row["area_type"]),
                ItemIds = JsonSerializer.Deserialize<List<int>>(row["item_id_list"]) ?? []
            })
            .ToList();

        // Survivor Royale replaces the legacy room-event stock table with its finite battle-loot pool.
        // The exact Survivor pool is covered by SurvivorRegionalItemPoolTests.
        if (areaPools.SelectMany(area => area.ItemIds).Any(itemId => itemId is >= 107000003 and <= 107000014))
        {
            Assert.All(areaPools.SelectMany(area => area.ItemIds), itemId => Assert.NotNull(GameItemData.Get(itemId)));
            return;
        }
        foreach (string tag in new[] { "soundproof", "record", "release" })
        {
            var profiles = GameRoomEventResponseItemData.GetAll()
                .Where(item => item.ResponseTags.Contains(tag))
                .ToDictionary(item => item.ItemId);
            var contributingAreas = areaPools
                .Where(area => area.ItemIds.Any(profiles.ContainsKey))
                .ToList();
            int totalPower = contributingAreas.Sum(area =>
                area.ItemIds.Where(profiles.ContainsKey).Sum(itemId => profiles[itemId].ResponsePower));

            Assert.True(contributingAreas.Count >= 2, $"{tag} is stocked in fewer than two areas");
            Assert.True(totalPower >= 12, $"{tag} initial stock has only {totalPower} response power");
        }
    }
    [Fact]
    public void ConsumeItemCountRejectsValuesOutsideZeroOrOne()
    {
        var headers = new[] { "choice_id", "event_id", "sort_order", "consume_item_count" };
        var values = new[] { "1", "188003", "1", "2" };
        var row = new CsvRow(headers, values);

        Assert.Throws<InvalidDataException>(() => RoomEventChoiceInfoData.CreateFromData(row));
    }

    [Fact]
    public void CanonicalAndUnityCsvCopiesAreByteIdentical()
    {
        string repoRoot = FindRepositoryRoot();
        foreach (string fileName in new[]
                 {
                     "room_event_master.csv",
                     "room_event_choice.csv",
                     "room_event_response_item.csv"
                 })
        {
            byte[] canonical = File.ReadAllBytes(Path.Combine(repoRoot, "network", "Common", "csv", fileName));
            foreach (string relativeDirectory in new[]
                     {
                         Path.Combine("client", "Assets", "Scripts", "Common", "csv"),
                         Path.Combine("client", "Assets", "Resources", "Common", "csv"),
                         Path.Combine("client", "Assets", "StreamingAssets", "Common", "csv")
                     })
            {
                string mirrorDirectory = Path.Combine(repoRoot, relativeDirectory);
                if (!Directory.Exists(mirrorDirectory))
                    continue;

                Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(mirrorDirectory, fileName)));
            }
        }
    }

    private static void Initialize()
    {
        string csvPath = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        GameRoomEventData.Initialize(
            CsvHelper.LoadCsv(Path.Combine(csvPath, "room_event_master.csv")),
            CsvHelper.LoadCsv(Path.Combine(csvPath, "room_event_choice.csv")));
        GameRoomEventResponseItemData.Initialize(
            CsvHelper.LoadCsv(Path.Combine(csvPath, "room_event_response_item.csv")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }
}
