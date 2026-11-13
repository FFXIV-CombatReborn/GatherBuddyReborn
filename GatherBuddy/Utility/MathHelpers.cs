using System;
using System.Numerics;

namespace GatherBuddy.Utilities;

public static class VectorExtensions
{
    public static Vector2 ToVector2(this Vector3 vector3)
        => new(vector3.X, vector3.Z);

    public static Vector3 ToVector3(this Vector2 vector2, float y)
        => new(vector2.X, y, vector2.Y);
}

public struct Angle(float radians = 0)
{
    public float Rad = radians;
    public const float RadToDeg = 180f / MathF.PI;
    public const float DegToRad = MathF.PI / 180f;
    public const float HalfPi = MathF.PI / 2f;
    public const float DoublePI = MathF.Tau;

    public readonly float Deg => Rad * RadToDeg;

    public static Angle FromDirection(Vector2 dir) => new(MathF.Atan2(dir.X, dir.Y));
    public static Angle FromDirectionXZ(Vector3 dir) => new(MathF.Atan2(dir.X, dir.Z));
    public readonly Vector2 ToDirection() => new(Sin(), Cos());
    public readonly Vector3 ToDirectionXZ() => new(Sin(), 0, Cos());

    public static bool operator ==(Angle l, Angle r) => l.Rad == r.Rad;
    public static bool operator !=(Angle l, Angle r) => l.Rad != r.Rad;
    public static Angle operator +(Angle a, Angle b) => new(a.Rad + b.Rad);
    public static Angle operator -(Angle a, Angle b) => new(a.Rad - b.Rad);
    public static Angle operator -(Angle a) => new(-a.Rad);
    public static Angle operator *(Angle a, float b) => new(a.Rad * b);
    public static Angle operator *(float a, Angle b) => new(a * b.Rad);
    public static Angle operator /(Angle a, float b) => new(a.Rad / b);
    public static bool operator >(Angle a, Angle b) => a.Rad > b.Rad;
    public static bool operator <(Angle a, Angle b) => a.Rad < b.Rad;
    public static bool operator >=(Angle a, Angle b) => a.Rad >= b.Rad;
    public static bool operator <=(Angle a, Angle b) => a.Rad <= b.Rad;

    public readonly Angle Abs() => new(Math.Abs(Rad));
    public readonly float Sin() => MathF.Sin(Rad);
    public readonly float Cos() => MathF.Cos(Rad);
    public readonly float Tan() => MathF.Tan(Rad);
    public static Angle Asin(float x) => new(MathF.Asin(x));
    public static Angle Acos(float x) => new(MathF.Acos(x));
    public readonly Angle Round(float roundToNearestDeg) => (MathF.Round(Deg / roundToNearestDeg) * roundToNearestDeg).Degrees();

    public readonly Angle Normalized()
    {
        var r = Rad;
        while (r < -MathF.PI)
        {
            r += 2 * MathF.PI;
        }

        while (r > MathF.PI)
        {
            r -= 2 * MathF.PI;
        }

        return new(r);
    }

    public readonly bool AlmostEqual(Angle other, float epsRad) => Math.Abs((this - other).Normalized().Rad) <= epsRad;

    public readonly bool Equals(Angle other) => Rad == other.Rad;
    public override readonly bool Equals(object? obj) => obj is Angle angle && Equals(angle);
    public override readonly int GetHashCode() => Rad.GetHashCode();
    public override readonly string ToString() => Deg.ToString("f0");
}

public static class AngleExtensions
{
    public static Angle Radians(this float radians) => new(radians);
    public static Angle Degrees(this float degrees) => new(degrees * Angle.DegToRad);
    public static Angle Degrees(this int degrees) => new(degrees * Angle.DegToRad);
}
