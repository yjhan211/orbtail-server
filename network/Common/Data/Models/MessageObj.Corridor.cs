#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    /// <summary>
    /// 종소리 이벤트 정보 (게임 시작 기준 상대 시간)
    /// </summary>
    [MessagePackObject]
    public class BellEvent
    {
        [Key("s")] public int StartOffsetSec { get; set; } // 게임 시작 후 시작 시간 (초)
        [Key("d")] public int DurationSec { get; set; } // 지속 시간 (초)
    }

}
