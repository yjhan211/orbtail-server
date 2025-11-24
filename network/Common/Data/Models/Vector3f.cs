// ReSharper disable All
using System;
using MessagePack;

namespace network.common.data.models
{
    /// <summary>
    /// 3D 벡터 (float) - 자유 이동 좌표
    /// </summary>
    [MessagePackObject]
    public class Vector3f : IMessagePackObject, IEquatable<Vector3f>
    {
        public Vector3f()
        {
            X = 0;
            Y = 0;
            Z = 0;
        }

        public Vector3f(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        [Key("x")] public float X { get; set; }
        [Key("y")] public float Y { get; set; }
        [Key("z")] public float Z { get; set; }

        public float Magnitude()
        {
            return (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        }

        public float SqrMagnitude()
        {
            return X * X + Y * Y + Z * Z;
        }

        public Vector3f Normalized()
        {
            float mag = Magnitude();
            if (mag > 0.00001f)
            {
                return new Vector3f(X / mag, Y / mag, Z / mag);
            }
            return new Vector3f(0, 0, 0);
        }

        public static Vector3f operator +(Vector3f a, Vector3f b)
        {
            return new Vector3f(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        public static Vector3f operator -(Vector3f a, Vector3f b)
        {
            return new Vector3f(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        }

        public static Vector3f operator *(Vector3f a, float scalar)
        {
            return new Vector3f(a.X * scalar, a.Y * scalar, a.Z * scalar);
        }

        public static float Distance(Vector3f a, Vector3f b)
        {
            return (a - b).Magnitude();
        }

        public bool Equals(Vector3f? other)
        {
            if (other == null) return false;
            const float epsilon = 0.00001f;
            return Math.Abs(X - other.X) < epsilon &&
                   Math.Abs(Y - other.Y) < epsilon &&
                   Math.Abs(Z - other.Z) < epsilon;
        }

        public override bool Equals(object? obj)
        {
            return obj is Vector3f other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Z);
        }

        public override string ToString()
        {
            return $"Vector3f({X:F2}, {Y:F2}, {Z:F2})";
        }

        public static Vector3f Zero => new Vector3f(0, 0, 0);
    }
}
