internal readonly record struct SnapSettingsState(
    int OuterRange,
    float OuterStrength,
    float FarStrengthFactor,
    float HipfireStrengthFactor,
    float StartStrength,
    float Height,
    float StrengthRampTime,
    int InnerInterpolationTypeIndex,
    int DistanceInterpolationTypeIndex)
{
    public static SnapSettingsState Default => new(1, 0f, 0.5f, 1f, 0f, 0f, 0f, 0, 0);
}

internal static class SnapConfigCatalog
{
    public const string SnapModeKey = "snap";
    public const string OuterRangeKey = "snapOuterRange";
    public const string OuterStrengthKey = "snapOuterStrength";
    public const string FarStrengthFactorKey = "snapFarStrengthFactor";
    public const string HipfireStrengthFactorKey = "snapHipfireStrengthFactor";
    public const string StartStrengthKey = "snapStartStrength";
    public const string HeightKey = "snapHeight";
    public const string StrengthRampTimeKey = "snapStrengthRampTime";
    public const string InnerInterpolationTypeKey = "snapInnerInterpolationType";
    public const string DistanceInterpolationTypeKey = "snapDistanceInterpolationType";

    public static SnapSettingsState Read(
        ConfigRepository repository, string configPath, int selectedModelSize, int displayHeightLimit)
    {
        var snapOuterRangeMax = Math.Max(selectedModelSize, displayHeightLimit);
        var outerRange = Math.Clamp(
            repository.TryReadInt(configPath, OuterRangeKey) ?? SnapSettingsState.Default.OuterRange,
            selectedModelSize,
            snapOuterRangeMax);
        var state = new SnapSettingsState(
            outerRange,
            repository.TryReadFloat(configPath, OuterStrengthKey) ?? 0f,
            repository.TryReadFloat(configPath, FarStrengthFactorKey) ?? SnapSettingsState.Default.FarStrengthFactor,
            repository.TryReadFloat(configPath, HipfireStrengthFactorKey) ?? SnapSettingsState.Default.HipfireStrengthFactor,
            repository.TryReadFloat(configPath, StartStrengthKey) ?? 0f,
            repository.TryReadFloat(configPath, HeightKey) ?? 0f,
            repository.TryReadFloat(configPath, StrengthRampTimeKey) ?? 0f,
            AimAssistOptionCatalog.ResolveInterpolationType(repository.TryReadString(configPath, InnerInterpolationTypeKey)),
            AimAssistOptionCatalog.ResolveInterpolationType(repository.TryReadString(configPath, DistanceInterpolationTypeKey)));
        return Normalized(state, selectedModelSize, snapOuterRangeMax);
    }

    public static SnapSettingsState Normalized(SnapSettingsState s, int selectedModelSize, int snapOuterRangeMax)
    {
        var outerRange = Math.Clamp(s.OuterRange, selectedModelSize, snapOuterRangeMax);
        var startStrength = Math.Clamp(s.StartStrength, 0f, 1f);
        return new SnapSettingsState(
            outerRange,
            MathF.Max(Math.Clamp(s.OuterStrength, 0f, 1f), startStrength),
            MathF.Max(Math.Clamp(s.FarStrengthFactor, 0f, 1f), startStrength),
            Math.Clamp(s.HipfireStrengthFactor, 0f, 1f),
            startStrength,
            Math.Clamp(s.Height, 0f, 1f),
            Math.Clamp(s.StrengthRampTime, 0f, 1f),
            Math.Clamp(s.InnerInterpolationTypeIndex, 0, AimAssistOptionCatalog.SnapInterpolationTypeOptions.Length - 1),
            Math.Clamp(s.DistanceInterpolationTypeIndex, 0, AimAssistOptionCatalog.SnapInterpolationTypeOptions.Length - 1));
    }

    public static void Write(ConfigRepository repository, string configPath, SnapSettingsState s)
    {
        repository.TryWriteInt(configPath, OuterRangeKey, s.OuterRange);
        repository.TryWriteFloat(configPath, OuterStrengthKey, s.OuterStrength);
        repository.TryWriteFloat(configPath, FarStrengthFactorKey, s.FarStrengthFactor);
        repository.TryWriteFloat(configPath, HipfireStrengthFactorKey, s.HipfireStrengthFactor);
        repository.TryWriteFloat(configPath, StartStrengthKey, s.StartStrength);
        repository.TryWriteFloat(configPath, HeightKey, s.Height);
        repository.TryWriteFloat(configPath, StrengthRampTimeKey, s.StrengthRampTime);
        if (s.InnerInterpolationTypeIndex >= 0
            && s.InnerInterpolationTypeIndex < AimAssistOptionCatalog.SnapInterpolationTypeOptions.Length)
        {
            repository.TryWriteString(
                configPath,
                InnerInterpolationTypeKey,
                AimAssistOptionCatalog.SnapInterpolationTypeOptions[s.InnerInterpolationTypeIndex]);
        }

        if (s.DistanceInterpolationTypeIndex >= 0
            && s.DistanceInterpolationTypeIndex < AimAssistOptionCatalog.SnapInterpolationTypeOptions.Length)
        {
            repository.TryWriteString(
                configPath,
                DistanceInterpolationTypeKey,
                AimAssistOptionCatalog.SnapInterpolationTypeOptions[s.DistanceInterpolationTypeIndex]);
        }
    }
}
