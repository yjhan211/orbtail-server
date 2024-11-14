using network.common;
using network.common.data;
using network.common.data.models;

namespace network.helpers;

public static class MapHelper
{
    private static int _totalServerNum;
    private static readonly Dictionary<string, int> _partByPositionKey = new();
    private static readonly Dictionary<MapId, Dictionary<int, List<string>>> _positionListByMapPart = new();
    private static readonly List<Cell> _partPivotList = [];
    
    private static readonly Dictionary<string, (MapId, Cell, bool)> _instancePortalInfo = new();

    public static void Initialize(int totalServerNum)
    {
        _totalServerNum = totalServerNum;
        InitializeCommonMapData();
    }

    private static void InitializeCommonMapData()
    {
        CalculatePartPivots();
        foreach (var mapId in GameMapData.GetCommonMapList())
        {
            _positionListByMapPart[mapId] = new Dictionary<int, List<string>>();
            var partNumber = 1;

            foreach (var cellList in _partPivotList.Select(partPivotCell => partPivotCell.GetBoundCellList()))
            {
                _positionListByMapPart[mapId][partNumber] = [];

                foreach (var positionKey in cellList.Select(cell => CreatePartKey(mapId, cell))
                             .Where(positionKey => !_partByPositionKey.TryGetValue(positionKey, out _)))
                {
                    _partByPositionKey.Add(positionKey, partNumber);
                    _positionListByMapPart[mapId][partNumber].Add(positionKey);
                }

                partNumber++;
            }
        }
    }

    private static void CalculatePartPivots()
    {
        const int colXOffset = 6;
        const int colYOffset = -6;
        const int rowXOffset = 6;
        const int rowYOffset = 20;
        var baseCell = new Cell(25, 85);
        
        switch (_totalServerNum)
        {
            case 40:
                for (var row = 0; row < 4; row++)
                {
                    var rowBaseX = baseCell.X + (row * rowXOffset);
                    var rowBaseY = baseCell.Y + (row * rowYOffset);
                    
                    for (var col = 0; col < 10; col++)
                    {
                        _partPivotList.Add(new Cell(
                            rowBaseX + (col * colXOffset),
                            rowBaseY + (col * colYOffset)
                        ));
                    }
                }
                break;
                
            case 20:
            case 2:
                for (var row = 0; row < 4; row++)
                {
                    var rowBaseX = baseCell.X + (row * rowXOffset);
                    var rowBaseY = baseCell.Y + (row * rowYOffset);
                    
                    for (var col = 0; col < 5; col++)
                    {
                        _partPivotList.Add(new Cell(
                            rowBaseX + (col * colXOffset),
                            rowBaseY + (col * colYOffset)
                        ));
                    }
                }
                break;
                
            case 10:
                for (var row = 0; row < 2; row++)
                {
                    var rowBaseX = baseCell.X + (row * rowXOffset);
                    var rowBaseY = baseCell.Y + (row * rowYOffset);
                    
                    for (var col = 0; col < 5; col++)
                    {
                        _partPivotList.Add(new Cell(
                            rowBaseX + (col * colXOffset),
                            rowBaseY + (col * colYOffset)
                        ));
                    }
                }
                break;
                
            case 5:
                for (var col = 0; col < 5; col++)
                {
                    _partPivotList.Add(new Cell(
                        baseCell.X + (col * colXOffset),
                        baseCell.Y + (col * colYOffset)
                    ));
                }
                break;
                
            default:
                throw new Exception("Invalid total server number");
        }
    }

    public static List<string> GetPositionListByMapByPart(MapId mapId, int part) => 
        _positionListByMapPart[mapId][part];

    public static string CreatePartKey(MapId mapId, Cell cell)
    {
        return $"{mapId}|{cell.X},{cell.Y}";
    }

    public static string CreatePartKey(MapId mapId, long mapSubId)
    {
        return $"{mapId}|{mapSubId}";
    }

    public static Cell CreateCell(string partKey)
    {
        var split = partKey.Split("|");
        var position = split[1].Split(",");
        return new Cell(int.Parse(position[0]), int.Parse(position[1]));
    }

    public static List<int> GetBoundServerList(MapId mapId, Cell cell)
    {
        return cell.GetBoundCellList()
            .Select(boundCell => CreatePartKey(mapId, boundCell))
            .Select(GetManageServerId)
            .Where(serverId => serverId != 0)
            .Distinct()
            .ToList();
    }

    public static int GetManageServerId(string positionKey)
    {
        if (!_partByPositionKey.TryGetValue(positionKey, out var partNumber))
            return 0;

        return _totalServerNum switch
        {
            40 => partNumber,
            20 => ((partNumber - 1) / 2) + 1,
            10 => ((partNumber - 1) / 4) + 1,
            5 => ((partNumber - 1) / 8) + 1,
            2 => partNumber <= 20 ? 1 : 2,
            _ => throw new Exception("Invalid TotalServerNum")
        };
    }

    public static int GetManageServerId(long mapSubId)
    {
        return (int)((mapSubId - 1) % _totalServerNum) + 1;
    }

    public static int GetManagePartByPositionKey(string positionKey)
    {
        if (!_partByPositionKey.TryGetValue(positionKey, out var partNumber))
            throw new Exception($"Can't find part_number for position_key: {positionKey}");

        var serverId = GetManageServerId(positionKey);
        
        return _totalServerNum switch
        {
            40 => partNumber,
            20 => ((serverId - 1) * 2) + 1,
            10 => ((serverId - 1) * 4) + 1,
            5 => ((serverId - 1) * 8) + 1,
            2 => serverId == 1 ? 1 : 21,
            _ => throw new Exception("Invalid TotalServerNum")
        };
    }

    public static List<int> GetManagePartList(int gameServerId)
    {
        var result = new List<int>();
        
        switch (_totalServerNum)
        {
            case 40:
                result.Add(gameServerId);
                break;
            case 20:
                var start20 = (gameServerId - 1) * 2 + 1;
                result.Add(start20);
                result.Add(start20 + 1);
                break;
            case 10:
                var start10 = (gameServerId - 1) * 4 + 1;
                for (var i = 0; i < 4; i++)
                    result.Add(start10 + i);
                break;
            case 5:
                var start5 = (gameServerId - 1) * 8 + 1;
                for (var i = 0; i < 8; i++)
                    result.Add(start5 + i);
                break;
            case 2:
                var start2 = gameServerId == 1 ? 1 : 21;
                for (var i = 0; i < 20; i++)
                    result.Add(start2 + i);
                break;
            default:
                throw new Exception("Invalid TotalServerNum");
        }
        
        return result;
    }
}