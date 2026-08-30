using network.common;
using network.common.data;

namespace game_server.services;

public static class PassiveBuffUtility
{
    public static int GetValuePercent(IEnumerable<int> activeBuffIds, BuffSubType subType)
    {
        int total = 0;
        foreach (int buffId in activeBuffIds ?? Enumerable.Empty<int>())
        {
            BuffInfoData buff;
            try
            {
                buff = GameBuffData.Get(buffId);
            }
            catch
            {
                continue;
            }

            if (buff.SubType != subType) continue;
            total += GameBuffData.GetDefaultPassiveBuffValuePercent(buffId);
        }

        return Math.Clamp(total, 0, 95);
    }

    public static int ApplyIncrease(int value, IEnumerable<int> activeBuffIds, BuffSubType subType)
    {
        int percent = GetValuePercent(activeBuffIds, subType);
        if (value == 0 || percent <= 0) return value;

        int magnitude = Math.Abs(value);
        int adjusted = magnitude + Math.Max(1, (int)Math.Ceiling(magnitude * percent / 100.0));
        return value < 0 ? -adjusted : adjusted;
    }

}
