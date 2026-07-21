using System;
using System.Collections.Generic;
using MessagePack;
using network.common;
using network.common.data.models;

namespace network.helpers;

/// <summary>
/// Splits roster payloads into messages that fit the fixed network packet budget.
/// </summary>
public static class GameResultPacketChunker
{
    private static readonly int PayloadBudget = Config.BUFFER_SIZE - Config.HEADER_SIZE;

    public static List<G_TO_C_PLAYER_ELIMINATED> CreateEliminationChunks(
        long playerId,
        long attackerPlayerId,
        EliminationReason reason,
        IReadOnlyList<GameResultPlayerInfo> resultPlayers)
    {
        var chunks = new List<G_TO_C_PLAYER_ELIMINATED>();
        var current = CreateEliminationChunk(playerId, attackerPlayerId, reason, chunks.Count);

        foreach (var player in resultPlayers ?? Array.Empty<GameResultPlayerInfo>())
        {
            current.ResultPlayers.Add(player);
            if (GetPayloadSize(current) <= PayloadBudget) continue;

            current.ResultPlayers.RemoveAt(current.ResultPlayers.Count - 1);
            if (current.ResultPlayers.Count == 0)
                throw new InvalidOperationException($"A single result entry exceeds the {PayloadBudget}B packet payload budget.");

            chunks.Add(current);
            current = CreateEliminationChunk(playerId, attackerPlayerId, reason, chunks.Count);
            current.ResultPlayers.Add(player);
        }

        chunks.Add(current);
        FinalizeEliminationChunks(chunks);
        return chunks;
    }

    public static List<G_TO_C_GAME_RESULT> CreateGameResultChunks(
        long winnerId,
        bool isTimeout,
        IReadOnlyList<GameResultPlayerInfo> players)
    {
        var chunks = new List<G_TO_C_GAME_RESULT>();
        var current = CreateGameResultChunk(winnerId, isTimeout, chunks.Count);

        foreach (var player in players ?? Array.Empty<GameResultPlayerInfo>())
        {
            current.Players.Add(player);
            if (GetPayloadSize(current) <= PayloadBudget) continue;

            current.Players.RemoveAt(current.Players.Count - 1);
            if (current.Players.Count == 0)
                throw new InvalidOperationException($"A single result entry exceeds the {PayloadBudget}B packet payload budget.");

            chunks.Add(current);
            current = CreateGameResultChunk(winnerId, isTimeout, chunks.Count);
            current.Players.Add(player);
        }

        chunks.Add(current);
        FinalizeGameResultChunks(chunks);
        return chunks;
    }

    private static G_TO_C_PLAYER_ELIMINATED CreateEliminationChunk(
        long playerId,
        long attackerPlayerId,
        EliminationReason reason,
        int chunkIndex)
    {
        return new G_TO_C_PLAYER_ELIMINATED
        {
            PlayerId = playerId,
            AttackerPlayerId = attackerPlayerId,
            Reason = reason,
            ResultPlayers = [],
            ResultChunkIndex = chunkIndex,
            IsResultEnd = false
        };
    }

    private static G_TO_C_GAME_RESULT CreateGameResultChunk(long winnerId, bool isTimeout, int chunkIndex)
    {
        return new G_TO_C_GAME_RESULT
        {
            WinnerId = winnerId,
            IsTimeout = isTimeout,
            Players = [],
            ResultChunkIndex = chunkIndex,
            IsResultEnd = false
        };
    }

    private static void FinalizeEliminationChunks(List<G_TO_C_PLAYER_ELIMINATED> chunks)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].ResultChunkIndex = i;
            chunks[i].IsResultEnd = i == chunks.Count - 1;
            EnsureFits(GetPayloadSize(chunks[i]), i);
        }
    }

    private static void FinalizeGameResultChunks(List<G_TO_C_GAME_RESULT> chunks)
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            chunks[i].ResultChunkIndex = i;
            chunks[i].IsResultEnd = i == chunks.Count - 1;
            EnsureFits(GetPayloadSize(chunks[i]), i);
        }
    }

    private static int GetPayloadSize(G_TO_C_PLAYER_ELIMINATED message) => MessagePackSerializer.Serialize(message).Length;
    private static int GetPayloadSize(G_TO_C_GAME_RESULT message) => MessagePackSerializer.Serialize(message).Length;

    private static void EnsureFits(int payloadSize, int chunkIndex)
    {
        if (payloadSize > PayloadBudget)
            throw new InvalidOperationException($"Result chunk {chunkIndex} exceeds the {PayloadBudget}B packet payload budget.");
    }
}