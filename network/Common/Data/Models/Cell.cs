// ReSharper disable All
#pragma warning disable CS8765 // 매개 변수 형식의 null 허용 여부가 재정의된 멤버와 일치하지 않음(null 허용 여부 특성 때문일 수 있음)
#pragma warning disable CS8604 // 가능한 null 참조 인수입니다.
#pragma warning disable CS8767 // 매개 변수 형식에서 참조 형식의 null 허용 여부가 암시적으로 구현된 멤버와 일치하지 않음(null 허용 여부 특성 때문일 수 있음)

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data.helpers;
using UnityEngine;

namespace network.common.data.models
{
    [MessagePackObject]
    public class Cell : IMessagePackObject, IEquatable<Cell>
    {
        public Cell(int x, int y)
        {
            X = x;
            Y = y;
        }

        [Key("x")] public int X { get; set; }

        [Key("y")] public int Y { get; set; }

        public bool Equals(Cell other)
        {
            return other != null && X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            if (obj is Cell other)
            {
                return X == other.X && Y == other.Y;
            }
            return false;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y);
        }
        
        public override string ToString()
        {
            return $"Cell({X},{Y})";
        }

        public static Cell Clone(Cell cell)
        {
            return new Cell(cell.X, cell.Y);
        }

        public Cell Clone()
        {
            return new Cell(X, Y);
        }

        public static bool operator ==(Cell? left, Cell? right)
        {
            return EqualityComparer<Cell>.Default.Equals(left, right);
        }

        public static bool operator !=(Cell? left, Cell? right)
        {
            return !(left == right);
        }

        public int GetDistance(Cell targetCell)
        {
            var deltaX = Math.Abs(X - targetCell.X);
            var deltaY = Math.Abs(Y - targetCell.Y);

            return deltaX + deltaY;
        }

        public DirectionType GetDirection(Cell targetCell)
        {
            var deltaX = X - targetCell.X;
            var deltaY = Y - targetCell.Y;

            if (deltaX > 0 && deltaY == 0) return DirectionType.TOP_LEFT;
            if (deltaX < 0 && deltaY == 0) return DirectionType.TOP_RIGHT;
            if (deltaX == 0 && deltaY < 0) return DirectionType.BOTTOM_LEFT;
            if (deltaX == 0 && deltaY > 0) return DirectionType.BOTTOM_RIGHT;

            return DirectionType.NONE;
        }

        public Cell GetNextCell(DirectionType direction)
        {
            var clone = Clone(this);
            switch (direction)
            {
                case DirectionType.TOP_LEFT:
                    clone.Y += 1;
                    break;

                case DirectionType.TOP_RIGHT:
                    clone.X += 1;
                    break;

                case DirectionType.BOTTOM_LEFT:
                    clone.X -= 1;
                    break;

                case DirectionType.BOTTOM_RIGHT:
                    clone.Y -= 1;
                    break;
                
                case DirectionType.TOP:
                    clone.X += 1;
                    clone.Y += 1;
                    break;
                
                case DirectionType.BOTTOM:
                    clone.X -= 1;
                    clone.Y -= 1;
                    break;
                
                case DirectionType.LEFT:
                    clone.X -= 1;
                    clone.Y += 1;
                    break;
                
                case DirectionType.RIGHT:
                    clone.X += 1;
                    clone.Y -= 1;
                    break;
            }

            return clone;
        }

        public List<Cell> GetBoundCellList()
        {
            var result = new List<Cell>();
            result.Add(new Cell(X - 16, Y - 8));

            var minX = X - 15;
            var maxX = X + 22;
            var minY = Y - 8;
            var maxY = Y - 9;

            var line = 0;
            for (var x = minX; x <= maxX; x++)
            {
                line += 1;

                if (line <= 1)
                {
                    minY -= 1;
                    maxY += 2;
                }
                else if (line <= 8)
                {
                    minY -= 1;
                    maxY += 1;
                }
                else if (line <= 28)
                {
                    minY += 1;
                    maxY += 1;
                }
                else
                {
                    minY += 1;
                    maxY -= 1;
                }

                for (var y = minY; y <= maxY; y++)
                {
                    var cell = new Cell(x, y);
                    result.Add(cell);
                }
            }

            return result;
        }
        
        public List<Cell> GetAdjacentCells()
        {
            var result = new List<Cell>
            {
                new Cell(X, Y + 1),
                new Cell(X, Y - 1),
                new Cell(X - 1, Y),
                new Cell(X + 1, Y)
            };

            return result;
        }
        
        public Cell GetNearestCell(List<Cell> cellList)
        {
            if (cellList.Count == 0)
            {
                return this;
            }

            Cell nearestCell = cellList[0];
            int minDistance = GetDistance(nearestCell);

            foreach (Cell cell in cellList)
            {
                int distance = GetDistance(cell);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestCell = cell;
                }
            }

            return nearestCell;
        }
    }
}