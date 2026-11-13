internal sealed class SmartCoreAimAssistService
{
    private readonly SmartCoreActivationEvaluator _activationEvaluator = new();
    private readonly SmartCoreTargetSelector _targetSelector = new();

    public SmartCoreAimAssistResult Evaluate(in SmartCoreAimAssistContext context)
    {
        if (!_activationEvaluator.IsActive(context))
        {
            return SmartCoreAimAssistResult.Inactive;
        }

        if (!_targetSelector.TrySelectTarget(context, out var box))
        {
            return SmartCoreAimAssistResult.Inactive;
        }

        if (!SmartCoreStickMapper.TryMap(context, box, out var rightX, out var rightY))
        {
            return SmartCoreAimAssistResult.Inactive;
        }

        return new SmartCoreAimAssistResult(true, rightX, rightY);
    }
}

internal readonly record struct AimAssistParams(
    bool IsEnabled,
    SnapMode SnapMode,
    int SnapOuterRange,
    float SnapOuterStrengthFactor,
    float SnapSizeGainThreshold,
    float SnapHipfireStrengthFactor,
    float SnapStartStrength,
    float SnapHeight,
    float SnapStrengthRampTime,
    int SnapInnerInterpolationTypeIndex);

internal readonly record struct GamepadBindings(
    int AimBindingIndex,
    int FireBindingIndex,
    int VoiceBindingIndex,
    string VoiceCustomKey,
    int TouchpadLeftBindingIndex,
    int TouchpadRightBindingIndex,
    string TouchpadLeftCustomKey,
    string TouchpadRightCustomKey);

internal readonly record struct WeaponPolicy(
    RapidFireStrategy RapidFireStrategy,
    int RapidFireHz,
    string[] AimSnapWeapons,
    string[] RapidFireWeapons,
    string[] ReleaseFireWeapons);

internal readonly record struct SmartCoreAimAssistConfigState(
    AimAssistParams AimAssist,
    GamepadBindings Bindings,
    WeaponPolicy WeaponPolicy,
    MacroRuntimeState? Macro)
{
    public static SmartCoreAimAssistConfigState Disabled => new(
        new AimAssistParams(
            IsEnabled: false,
            SnapMode: SnapMode.Fire,
            SnapOuterRange: 1,
            SnapOuterStrengthFactor: 0.5f,
            SnapSizeGainThreshold: 1f,
            SnapHipfireStrengthFactor: 0f,
            SnapStartStrength: 0f,
            SnapHeight: 0f,
            SnapStrengthRampTime: 0f,
            SnapInnerInterpolationTypeIndex: 0),
        new GamepadBindings(
            AimBindingIndex: GamepadBindingCatalog.DefaultAimIndex,
            FireBindingIndex: GamepadBindingCatalog.DefaultFireIndex,
            VoiceBindingIndex: GamepadBindingCatalog.DefaultTouchpadLeftIndex,
            VoiceCustomKey: "V",
            TouchpadLeftBindingIndex: GamepadBindingCatalog.DefaultTouchpadLeftIndex,
            TouchpadRightBindingIndex: GamepadBindingCatalog.DefaultTouchpadRightIndex,
            TouchpadLeftCustomKey: GamepadBindingCatalog.DefaultCustomKeyboardKeyName,
            TouchpadRightCustomKey: GamepadBindingCatalog.DefaultCustomKeyboardKeyName),
        new WeaponPolicy(
            RapidFireStrategy: RapidFireStrategy.WeaponBased,
            RapidFireHz: 25,
            AimSnapWeapons: Array.Empty<string>(),
            RapidFireWeapons: Array.Empty<string>(),
            ReleaseFireWeapons: Array.Empty<string>()),
        null);
}

internal readonly struct SmartCoreAimAssistContext
{
    public readonly bool IsEnabled;
    public readonly SnapMode SnapMode;
    public readonly int SnapOuterRange;
    public readonly float SnapOuterStrengthFactor;
    public readonly float SnapSizeGainThreshold;
    public readonly float SnapHipfireStrengthFactor;
    public readonly float SnapStartStrength;
    public readonly float SnapHeight;
    public readonly int SnapInnerInterpolationTypeIndex;
    public readonly int AimBindingIndex;
    public readonly int FireBindingIndex;
    public readonly bool IsAimSnapOverrideWeapon;
    public readonly float FireStrengthRampMultiplier;
    public readonly SdlGamepadInputSnapshot Input;
    public readonly OnnxDebugBox[] Boxes;

    public SmartCoreAimAssistContext(
        bool isEnabled,
        SnapMode snapMode,
        int snapOuterRange,
        float snapOuterStrengthFactor,
        float snapSizeGainThreshold,
        float snapHipfireStrengthFactor,
        float snapStartStrength,
        float snapHeight,
        int snapInnerInterpolationTypeIndex,
        int aimBindingIndex,
        int fireBindingIndex,
        bool isAimSnapOverrideWeapon,
        float fireStrengthRampMultiplier,
        SdlGamepadInputSnapshot input,
        OnnxDebugBox[] boxes)
    {
        IsEnabled = isEnabled;
        SnapMode = snapMode;
        SnapOuterRange = snapOuterRange;
        SnapOuterStrengthFactor = snapOuterStrengthFactor;
        SnapSizeGainThreshold = snapSizeGainThreshold;
        SnapHipfireStrengthFactor = snapHipfireStrengthFactor;
        SnapStartStrength = snapStartStrength;
        SnapHeight = snapHeight;
        SnapInnerInterpolationTypeIndex = snapInnerInterpolationTypeIndex;
        AimBindingIndex = aimBindingIndex;
        FireBindingIndex = fireBindingIndex;
        IsAimSnapOverrideWeapon = isAimSnapOverrideWeapon;
        FireStrengthRampMultiplier = fireStrengthRampMultiplier;
        Input = input;
        Boxes = boxes;
    }
}

internal readonly struct SmartCoreAimAssistResult
{
    public readonly bool IsActive;
    public readonly short RightX;
    public readonly short RightY;

    public SmartCoreAimAssistResult(bool isActive, short rightX, short rightY)
    {
        IsActive = isActive;
        RightX = rightX;
        RightY = rightY;
    }

    public static SmartCoreAimAssistResult Inactive => new(false, 0, 0);
}

internal readonly struct SmartCoreDetectionState
{
    public readonly OnnxDebugBox[] Boxes;
    public readonly DateTime ReceivedAtUtc;

    public SmartCoreDetectionState(OnnxDebugBox[] boxes, DateTime receivedAtUtc)
    {
        Boxes = boxes;
        ReceivedAtUtc = receivedAtUtc;
    }

    public static SmartCoreDetectionState Empty => new(Array.Empty<OnnxDebugBox>(), DateTime.MinValue);
}

internal interface IAimAssistDetectionSink
{
    void SetAimAssistDetections(in SmartCoreDetectionState state);
}

internal sealed class SmartCoreActivationEvaluator
{
    public bool IsActive(in SmartCoreAimAssistContext context)
    {
        if (!context.IsEnabled || context.Boxes is null || context.Boxes.Length == 0)
        {
            return false;
        }

        var firePressed = GamepadBindingCatalog.IsPressed(context.FireBindingIndex, context.Input);
        var aimPressed = GamepadBindingCatalog.IsPressed(context.AimBindingIndex, context.Input);
        return SnapActivationPolicy.IsActive(context.IsAimSnapOverrideWeapon, context.SnapMode, firePressed, aimPressed);
    }
}

internal readonly record struct SnapStrengthEvaluation(
    bool IsInRange,
    float Distance,
    float Strength);

internal sealed class SmartCoreStickMapper
{
    public static SnapStrengthEvaluation EvaluateStrength(
        in SmartCoreAimAssistContext context,
        in OnnxDebugBox box)
    {
        var snapHeight = Math.Clamp(context.SnapHeight, 0f, 1f);
        var targetY = box.Y + box.H * (0.5f - snapHeight);
        var centerX = box.InputWidth * 0.5f;
        var centerY = box.InputHeight * 0.5f;
        var dx = box.X - centerX;
        var dy = targetY - centerY;
        var distance = MathF.Sqrt(dx * dx + dy * dy);

        var outerDiameterModel = MathF.Max(1f, MathF.Min(box.InputWidth, box.InputHeight));
        var outerRadiusModel = outerDiameterModel * 0.5f;
        // 小圈直径跟随最优先吸附目标：直径 = 目标框宽度 * 3
        var innerRadiusModel = MathF.Max(0f, box.W) * 1.5f;
        var isInsideCore = distance <= innerRadiusModel;
        if (distance <= 0.001f || distance > outerRadiusModel)
        {
            return new SnapStrengthEvaluation(false, distance, 0f);
        }

        var startStrength = Math.Clamp(context.SnapStartStrength, 0f, 1f);
        // 目标尺寸增益：框宽越大，输出越高；3W 达到配置的采集范围比例时满增益。
        var sizeGainThreshold = Math.Clamp(context.SnapSizeGainThreshold, 0f, 1f);
        var fullGainWidth = outerDiameterModel * sizeGainThreshold;
        var sizeGain = fullGainWidth <= 0.001f
            ? (box.W > 0f ? 1f : 0f)
            : Math.Clamp(box.W * 3f / fullGainWidth, 0f, 1f);
        var outerStrengthFactor = Math.Clamp(context.SnapOuterStrengthFactor, 0f, 1f);

        var isAiming = GamepadBindingCatalog.IsPressed(context.AimBindingIndex, context.Input);
        float relativeStrength;
        if (isInsideCore)
        {
            var t = innerRadiusModel <= 0.001f ? 1f : Math.Clamp(distance / innerRadiusModel, 0f, 1f);
            var curveT = SnapInterpolation.EvaluateNormalized(t, context.SnapInnerInterpolationTypeIndex);
            relativeStrength = Lerp(startStrength, 1f, curveT);
        }
        else
        {
            relativeStrength = isAiming ? outerStrengthFactor : 1f;
        }

        var strength = sizeGain * relativeStrength;
        if (!isAiming)
        {
            strength *= Math.Clamp(context.SnapHipfireStrengthFactor, 0f, 1f);
        }

        return new SnapStrengthEvaluation(true, distance, Math.Clamp(strength, 0f, 1f));
    }

    public static bool TryMap(in SmartCoreAimAssistContext context, in OnnxDebugBox box, out short rightX, out short rightY)
    {
        rightX = 0;
        rightY = 0;

        var evaluation = EvaluateStrength(context, box);
        if (!evaluation.IsInRange)
        {
            return false;
        }

        var strength = evaluation.Strength * Math.Clamp(context.FireStrengthRampMultiplier, 0f, 1f);
        if (strength <= 0f)
        {
            return false;
        }

        var snapHeight = Math.Clamp(context.SnapHeight, 0f, 1f);
        var targetY = box.Y + box.H * (0.5f - snapHeight);
        var centerX = box.InputWidth * 0.5f;
        var centerY = box.InputHeight * 0.5f;
        var dx = box.X - centerX;
        var dy = targetY - centerY;
        var invDistance = 1f / evaluation.Distance;
        var normalizedX = dx * invDistance;
        var normalizedY = dy * invDistance;
        var outputX = normalizedX * strength;
        var outputY = normalizedY * strength;
        rightX = ToStickAxis(outputX);
        rightY = ToStickAxis(outputY);
        return rightX != 0 || rightY != 0;
    }

    private static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    private static short ToStickAxis(float normalized)
    {
        var scaled = normalized * short.MaxValue;
        return (short)Math.Clamp((int)MathF.Round(scaled), short.MinValue, short.MaxValue);
    }
}

internal sealed class SmartCoreTargetSelector
{
    public static int SelectTargetIndex(IReadOnlyList<OnnxDebugBox>? boxes)
    {
        if (boxes is null || boxes.Count == 0)
        {
            return -1;
        }

        var inputWidth = boxes[0].InputWidth;
        var inputHeight = boxes[0].InputHeight;
        if (inputWidth <= 0 || inputHeight <= 0)
        {
            return -1;
        }

        var centerX = inputWidth * 0.5f;
        var centerY = inputHeight * 0.5f;
        var bestIndex = -1;
        var bestDistanceSquared = float.MaxValue;
        for (var i = 0; i < boxes.Count; i++)
        {
            var candidate = boxes[i];
            var dx = candidate.X - centerX;
            var dy = candidate.Y - centerY;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            bestIndex = i;
        }

        if (bestIndex < 0)
        {
            return -1;
        }

        return bestIndex;
    }

    public bool TrySelectTarget(in SmartCoreAimAssistContext context, out OnnxDebugBox box)
    {
        box = default;
        var bestIndex = SelectTargetIndex(context.Boxes);
        if (bestIndex < 0)
        {
            return false;
        }

        box = context.Boxes[bestIndex];
        return true;
    }
}
