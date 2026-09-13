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

        public static Vector3f MoveTowardsXY(Vector3f current, Vector3f target, float maxDistance)
        {
            float dx = target.X - current.X;
            float dy = target.Y - current.Y;
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);
            if (distance == 0f || maxDistance <= 0f)
            {
                return new Vector3f(current.X, current.Y, 0f);
            }
            float step = Math.Min(maxDistance, distance);
            return new Vector3f(current.X + dx / distance * step, current.Y + dy / distance * step, 0f);
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

    /// <summary>
    /// Vector3f 확장 메소드
    /// </summary>
    public static class Vector3fExtensions
    {
        /// <summary>
        /// Unity Vector3를 Vector3f로 변환
        /// </summary>
        public static Vector3f ToVector3f(this UnityEngine.Vector3 v)
        {
            return new Vector3f(v.x, v.y, v.z);
        }

        /// <summary>
        /// Vector3f를 Unity Vector3로 변환
        /// </summary>
        public static UnityEngine.Vector3 ToUnityVector3(this Vector3f v)
        {
            return new UnityEngine.Vector3(v.X, v.Y, v.Z);
        }
    }
}
