using MessagePack;
using network.common;
using network.common.data.models;

namespace network.packets;

// ========== 탈출 절차 프로토콜 ==========

public static partial class PacketMaker
{
    public static Packet G_TO_C_EXIT_STEP_INFO(int groupId, int currentStepOrder, int totalStepCount, bool isCompleted, long lastAdvancedBy = 0)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXIT_STEP_INFO);
        G_TO_C_EXIT_STEP_INFO body = new()
        {
            GroupId = groupId,
            CurrentStepOrder = currentStepOrder,
            TotalStepCount = totalStepCount,
            IsCompleted = isCompleted,
            LastAdvancedBy = lastAdvancedBy
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_EXIT_ADVANCE_RESULT(bool success, ErrorCode errorCode, bool escaped, int newStepOrder)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXIT_ADVANCE_RESULT);
        G_TO_C_EXIT_ADVANCE_RESULT body = new()
        {
            Success = success,
            ErrorCode = errorCode,
            Escaped = escaped,
            NewStepOrder = newStepOrder
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_EXIT_STEP_UPDATE(long advancedByPlayerId, int newStepOrder, bool escaped)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_EXIT_STEP_UPDATE);
        G_TO_C_EXIT_STEP_UPDATE body = new()
        {
            AdvancedByPlayerId = advancedByPlayerId,
            NewStepOrder = newStepOrder,
            Escaped = escaped
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }

    public static Packet G_TO_C_RETURN_TO_LOBBY_RESULT(bool success, ErrorCode errorCode)
    {
        var packet = Packet.Create((int)Protocol.G_TO_C_RETURN_TO_LOBBY_RESULT);
        G_TO_C_RETURN_TO_LOBBY_RESULT body = new()
        {
            Success = success,
            ErrorCode = errorCode
        };

        packet.SetBody(MessagePackSerializer.Serialize(body));
        return packet;
    }
}
