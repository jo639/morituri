namespace Morituri.Sim.Match;

/// <summary>
/// 2D 위치/방향 (B: 2D-lite 전술, 문서[8]). 원형 핏 아레나 좌표 — 중심 (0,0).
/// X축 = 옛 1D 교전축(접근/후퇴), Y축 = 측면(선회). 거리는 유클리드.
/// </summary>
public readonly record struct Vec2(float X, float Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);

    public float Length => MathF.Sqrt(X * X + Y * Y);

    public Vec2 Normalized()
    {
        float l = Length;
        return l > 1e-6f ? new Vec2(X / l, Y / l) : new Vec2(0f, 0f);
    }

    /// <summary>반시계 90° 회전 — 선회(접선) 이동 방향.</summary>
    public Vec2 Perp() => new(-Y, X);

    public static float Dist(Vec2 a, Vec2 b) => (a - b).Length;
}
