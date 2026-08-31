// ReSharper disable All
using System;
using System.Collections.Generic;
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

        public bool Equals(Cell? other)
        {
            return other != null && X == other.X && Y == other.Y;
        }

        public override bool Equals(object? obj)
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

        public static Cell Clone(Cell? cell)
        {
            if (ReferenceEquals(cell, null)) return new Cell(0, 0);
            return new Cell(cell.X, cell.Y);
        }

        public Cell Clone()
        {
            return new Cell(X, Y);
        }

        public static bool operator ==(Cell? left, Cell? right)
        {
            return EqualityComparer<Cell?>.Default.Equals(left, right);
        }

        public static bool operator !=(Cell? left, Cell? right)
        {
            return !(left == right);
        }

        public int GetDistance(Cell targetCell)
        {
            int dx = targetCell.X - X;
            int dy = targetCell.Y - Y;

            if ((dx > 0 && dy > 0) || (dx < 0 && dy < 0))
            {
                return Math.Max(Math.Abs(dx), Math.Abs(dy));
            }
            else if ((dx > 0 && dy < 0) || (dx < 0 && dy > 0))
            {
                return Math.Max(Math.Abs(dx), Math.Abs(dy));
            }
            else
            {
                return Math.Abs(dx) + Math.Abs(dy);
            }
        }

        public List<Cell> GetAdjacentCells()
        {
            var result = new List<Cell>
            {
                new Cell(X, Y + 1),     // 상
                new Cell(X, Y - 1),     // 하
                new Cell(X - 1, Y),     // 좌
                new Cell(X + 1, Y),     // 우
                new Cell(X - 1, Y + 1), // 좌상
                new Cell(X + 1, Y + 1), // 우상
                new Cell(X - 1, Y - 1), // 좌하
                new Cell(X + 1, Y - 1)  // 우하
            };

            return result;
        }
    }
}
