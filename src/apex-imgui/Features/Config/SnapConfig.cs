internal readonly record struct SnapSettingsState(
    int OuterRange,
    float OuterStrengthFactor,
    float SizeGainThreshold,
    float HipfireStrengthFactor,
    float StartStrength,
    float Height,
    float StrengthRampTime,
    int InnerInterpolationTypeIndex)
{
    public static SnapSettingsState Default => new(1, 0.5f, 1f, 1f, 0f, 0f, 0f, 0);
}

internal static class SnapConfigCatalog
{
    public const string SnapModeKey = "snap";
    public const string OuterRangeKey = "snapOuterRange";
    public const string OuterStrengthFactorKey = "snapOuterStrengthFactor";
    public const string SizeGainThresholdKey = "snapSizeGainThreshold";
    public const string HipfireStrengthFactorKey = "snapHipfireStrengthFactor";
    public const string StartStrengthKey = "snapStartStrength";
    public const string HeightKey = "snapHeight";
    public const string StrengthRampTimeKey = "snapStrengthRampTime";
    public const string InnerInterpolationTypeKey = "snapInnerInterpolationType";

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
            repository.TryReadFloat(configPath, OuterStrengthFactorKey) ?? SnapSettingsState.Default.OuterStrengthFactor,
            repository.TryReadFloat(configPath, SizeGainThresholdKey) ?? SnapSettingsState.Default.SizeGainThreshold,
            repository.TryReadFloat(configPath, HipfireStrengthFactorKey) ?? SnapSettingsState.Default.HipfireStrengthFactor,
            repository.TryReadFloat(configPath, StartStrengthKey) ?? 0f,
            repository.TryReadFloat(configPath, HeightKey) ?? 0f,
            repository.TryReadFloat(configPath, StrengthRampTimeKey) ?? 0f,
            AimAssistOptionCatalog.ResolveInterpolationType(repository.TryReadString(configPath, InnerInterpolationTypeKey)));
        return Normalized(state, selectedModelSize, snapOuterRangeMax);
    }

    public static SnapSettingsState Normalized(SnapSettingsState s, int selectedModelSize, int snapOuterRangeMax)
    {
        var outerRange = Math.Clamp(s.OuterRange, selectedModelSize, snapOuterRangeMax);
        var startStrength = Math.Clamp(s.StartStrength, 0f, 1f);
        return new SnapSettingsState(
            outerRange,
            Math.Clamp(s.OuterStrengthFactor, 0f, 1f),
            Math.Clamp(s.SizeGainThreshold, 0f, 1f),
            Math.Clamp(s.HipfireStrengthFactor, 0f, 1f),
            startStrength,
            Math.Clamp(s.Height, 0f, 1f),
            Math.Clamp(s.StrengthRampTime, 0f, 1f),
            Math.Clamp(s.InnerInterpolationTypeIndex, 0, AimAssistOptionCatalog.SnapInterpolationTypeOptions.Length - 1));
    }

    public static void Write(ConfigRepository repository, string configPath, SnapSettingsState s)
    {
        repository.TryWriteInt(configPath, OuterRangeKey, s.OuterRange);
        repository.TryWriteFloat(configPath, OuterStrengthFactorKey, s.OuterStrengthFactor);
        repository.TryWriteFloat(configPath, SizeGainThresholdKey, s.SizeGainThreshold);
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
    }
}
