using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using game_server.sessions;
using Xunit;

namespace demo_regression_tests;

/// <summary>
///     프로토콜 배선 가드 (#246 가드레일).
///     전수 감사(#238)에서 '층별 부분 삭제'가 반쪽 배선 20여 개를 만든 재발을 막는다:
///     서버가 실전송하는 G_TO_C에 클라 수신 케이스가 있는지, 클라가 실전송하는 C_TO_G에
///     서버 핸들러 등록이 있는지, CSV 로더 목록과 csv/ 폴더가 일치하는지를 소스 스캔으로 잠근다.
/// </summary>
public class ProtocolWiringGuardTests
{
    /// <summary>
    ///     의도된 휴면 배선 — 새 항목 추가는 보존 결정과 함께 주석으로 사유를 남길 것.
    /// </summary>
    private static readonly HashSet<string> DormantClientSenders = new()
    {
        // 비어 있음 — 마니또 세대 휴면 배선은 #276에서 전부 삭제됨.
    };

    private static readonly HashSet<string> DormantServerSenders = new()
    {
        // 비어 있음 — 마니또 세대 보존 결정은 #276에서 철회·삭제됨.
    };

    [Fact]
    public void EveryServerSentGameProtocolHasAClientCase()
    {
        string root = FindRepositoryRoot();
        string serverSource = ReadAllSources(Path.Combine(root, "game_server"));
        string clientSwitch = File.ReadAllText(Path.Combine(
            root, "client", "Assets", "Scripts", "GameUser.Protocol.cs"));

        var sent = new SortedSet<string>();
        foreach (Match m in Regex.Matches(serverSource, @"Protocol\.(G_TO_C_[A-Z0-9_]+)"))
            sent.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(serverSource, @"PacketMaker\.(G_TO_C_[A-Z0-9_]+)\("))
            sent.Add(m.Groups[1].Value);

        var missing = sent
            .Where(p => !DormantServerSenders.Contains(p))
            .Where(p => !clientSwitch.Contains($"Protocol.{p}"))
            .ToList();

        Assert.True(missing.Count == 0,
            "서버가 참조하는 G_TO_C 프로토콜에 클라 수신 케이스가 없다 (매 발생 시 클라 LogError):\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void EveryClientSentGameProtocolHasAServerHandler()
    {
        string root = FindRepositoryRoot();
        string clientSource = ReadAllSources(Path.Combine(root, "client", "Assets", "Scripts"));
        string serverSource = ReadAllSources(Path.Combine(root, "game_server", "Sessions"));

        var sent = new SortedSet<string>();
        foreach (Match m in Regex.Matches(clientSource, @"Protocol\.(C_TO_G_[A-Z0-9_]+)"))
            sent.Add(m.Groups[1].Value);

        var registered = new HashSet<string>();
        foreach (Match m in Regex.Matches(serverSource, @"RegisterHandler\(Protocol\.(C_TO_G_[A-Z0-9_]+)"))
            registered.Add(m.Groups[1].Value);

        var missing = sent
            .Where(p => !DormantClientSenders.Contains(p))
            .Where(p => !registered.Contains(p))
            .ToList();

        Assert.True(missing.Count == 0,
            "클라가 참조하는 C_TO_G 프로토콜에 서버 핸들러 등록이 없다 (송신 시 에러 응답):\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void MatchRosterIsSentBeforeAreaAndJoinSnapshots()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "game_server", "Sessions", "GameClientSession.cs"));
        int roster = source.IndexOf("PacketMaker.G_TO_C_MATCH_ROSTER", StringComparison.Ordinal);
        int area = source.IndexOf("_playerMovement.InitializeSpawn(matchingSpawnCell)", StringComparison.Ordinal);
        int join = source.IndexOf("await BroadcastPlayerJoin()", StringComparison.Ordinal);
        Assert.True(roster >= 0 && roster < area && roster < join);
        string entry = File.ReadAllText(Path.Combine(root, "game_server", "Services", "GameMatchEntryService.cs"));
        Assert.True(entry.IndexOf("if (runtime.Composition is", StringComparison.Ordinal) <
                    entry.IndexOf("MatchRosterBuilder.CreateBotIds", StringComparison.Ordinal));
    }

    [Fact]
    public void GameConnectionLivenessUsesNetworkTimeoutOnly()
    {
        string root = FindRepositoryRoot();
        string gameServer = File.ReadAllText(Path.Combine(root, "game_server", "GameServer.cs"));
        string gameSession = File.ReadAllText(Path.Combine(
            root, "game_server", "Sessions", "GameClientSession.cs"));
        string gameConnection = File.ReadAllText(Path.Combine(
            root, "game_server", "Sessions", "GameClientSession.cs"));

        Assert.DoesNotContain("HeartbeatCheckIntervalSeconds", gameServer);
        Assert.DoesNotContain("StartHeartbeatChecker", gameServer);
        Assert.DoesNotContain("CheckHeartbeatTimeouts", gameServer);
        Assert.DoesNotContain("HeartbeatTimeoutSeconds", gameSession);
        Assert.DoesNotContain("_lastHeartbeatTime", gameSession);
        Assert.DoesNotContain("IsHeartbeatTimedOut", gameConnection);

        // Heartbeat packets remain ordinary inbound traffic. TcpConnection touches the shared
        // ConnectionTimeouts idle window before dispatching them to this response handler.
        Assert.Contains("RegisterHandler(Protocol.C_TO_G_HEART_BEAT", gameSession);
        Assert.Contains("PacketMaker.G_TO_C_HEART_BEAT", gameConnection);
    }

    [Fact]
    public void NetworkServiceBindsOnlyFullyConstructedSessionsToTcpConnections()
    {
        string root = FindRepositoryRoot();
        string sessionBase = File.ReadAllText(Path.Combine(
            root, "network", "Core", "SessionBase.cs"));
        string networkService = File.ReadAllText(Path.Combine(
            root, "network", "Core", "NetworkService.cs"));

        Assert.DoesNotContain("Connection.SetSession(this)", sessionBase);

        int createIndex = networkService.IndexOf(
            "var session = sessionFactory(connection);",
            StringComparison.Ordinal);
        int bindIndex = networkService.IndexOf(
            "connection.SetSession(session);",
            StringComparison.Ordinal);

        Assert.True(createIndex >= 0, "NetworkService must create the session through SessionFactory.");
        Assert.True(bindIndex > createIndex, "NetworkService must bind the session only after construction succeeds.");
    }

    [Fact]
    public void CsvFolderMatchesLoaderRegistrations()
    {
        string root = FindRepositoryRoot();
        string loaderSource = File.ReadAllText(Path.Combine(
            root, "network", "Common", "Data", "Helpers", "GameDataHelper.cs"));

        var registered = new SortedSet<string>();
        foreach (Match m in Regex.Matches(loaderSource, "\"([a-z0-9_]+\\.csv)\""))
            registered.Add(m.Groups[1].Value);

        var onDisk = new SortedSet<string>(
            Directory.GetFiles(Path.Combine(root, "network", "Common", "csv"), "*.csv")
                .Select(Path.GetFileName)!);

        var loadedButMissing = registered.Except(onDisk).ToList();
        var orphanFiles = onDisk.Except(registered).ToList();

        Assert.True(loadedButMissing.Count == 0,
            "GameDataHelper가 등록했지만 csv/ 폴더에 없는 파일 (부팅 실패 위험):\n" +
            string.Join("\n", loadedButMissing));
        Assert.True(orphanFiles.Count == 0,
            "csv/ 폴더에 있지만 어떤 로더도 읽지 않는 고아 파일 (삭제하거나 로더에 등록할 것):\n" +
            string.Join("\n", orphanFiles));
    }

    private static string ReadAllSources(string rootPath)
    {
        var builder = new System.Text.StringBuilder();
        foreach (string file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.EndsWith("MessagePackGenerated.cs", StringComparison.Ordinal))
            {
                continue;
            }

            builder.AppendLine(File.ReadAllText(file));
        }

        return builder.ToString();
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
