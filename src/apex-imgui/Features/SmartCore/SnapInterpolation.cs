internal static class SnapInterpolation
{
    public const int CubicEaseOutIndex = 0;
    public const int QuadraticEaseOutIndex = 1;
    public const int SineEaseOutIndex = 2;
    public const int LinearIndex = 3;
    public const int SineEaseInIndex = 4;
    public const int QuadraticEaseInIndex = 5;
    public const int CubicEaseInIndex = 6;
    public const int SmoothstepIndex = 7;
    public const int SineEaseInOutIndex = 8;
    public const int QuadraticEaseInOutIndex = 9;
    public const int CubicEaseInOutIndex = 10;

    public static float EvaluateNormalized(float t, int typeIndex)
    {
        t = Math.Clamp(t, 0f, 1f);
        return typeIndex switch
        {
            CubicEaseOutIndex => 1f - MathF.Pow(1f - t, 3f),
            QuadraticEaseOutIndex => 1f - (1f - t) * (1f - t),
            SineEaseOutIndex => MathF.Sin(t * MathF.PI * 0.5f),
            SineEaseInIndex => 1f - MathF.Cos(t * MathF.PI * 0.5f),
            QuadraticEaseInIndex => t * t,
            CubicEaseInIndex => t * t * t,
            SmoothstepIndex => t * t * (3f - 2f * t),
            SineEaseInOutIndex => -(MathF.Cos(MathF.PI * t) - 1f) * 0.5f,
            QuadraticEaseInOutIndex => t < 0.5f
                ? 2f * t * t
                : 1f - (MathF.Pow(-2f * t + 2f, 2f) * 0.5f),
            CubicEaseInOutIndex => t < 0.5f
                ? 4f * t * t * t
                : 1f - (MathF.Pow(-2f * t + 2f, 3f) * 0.5f),
            _ => t
        };
    }
}
