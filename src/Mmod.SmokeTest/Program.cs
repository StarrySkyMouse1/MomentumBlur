using System.Text.Json;
using Mmod.Core.Models;
using Mmod.Core.Native;
using Mmod.Core.Services;
using Mmod.SmokeTest;

// ---- recording state machine (deterministic fakes, plan §16/§17) ----
if (args.Length >= 1 && args[0] == "recording")
{
    var failures = new List<string>();
    await new RecordingStateTests().RunAllAsync(failures);
    if (failures.Count > 0)
    {
        foreach (var f in failures)
            Console.WriteLine("REC_FAIL " + f);
        Console.WriteLine($"RECORDING_FAIL count={failures.Count}");
        return 60;
    }
    Console.WriteLine("RECORDING_OK");
    return 0;
}

// ---- settings compatibility + normalization ----
if (args.Length >= 1 && args[0] == "settings")
{
    const string oldJson = """
        {
          "captureMode": 0,
          "supersamplingMultiplier": 10,
          "exposure": 0.5,
          "obsCaptureFramerate": 120
        }
        """;
    var old = JsonSerializer.Deserialize<UserSettings>(oldJson, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
    SettingsMigration.Normalize(old);
    if (old.VideoProcessing is null || old.VideoProcessing.Modules.Any(m => m.Enabled))
    {
        Console.WriteLine("SETTINGS_FAIL: old JSON must produce all-off modules");
        return 10;
    }
    if (old.MotionBlurWeightMode != MotionBlurWeightMode.LegacyGaussianExposure)
    {
        Console.WriteLine("SETTINGS_FAIL: old JSON must default to Legacy exposure");
        return 11;
    }

    // unknown module must be preserved but ignored by the mapper
    var withUnknown = new VideoProcessingSettings
    {
        PresetId = "off",
        Modules =
        [
            new VideoProcessingModuleConfig { Id = "future-module-xyz", Enabled = true, Order = 0, Parameters = new() { ["x"] = 1 } },
        ],
    };
    var normalizedUnknown = VideoProcessorCatalog.Normalize(withUnknown);
    if (!normalizedUnknown.Modules.Any(m => m.Id == "future-module-xyz"))
    {
        Console.WriteLine("SETTINGS_FAIL: unknown module dropped");
        return 12;
    }
    if (NativeProcessingMapper.Map(normalizedUnknown).Count != 0)
    {
        Console.WriteLine("SETTINGS_FAIL: unknown module leaked into native descriptors");
        return 13;
    }

    // out-of-range parameter clamp
    var outOfRange = new VideoProcessingSettings
    {
        PresetId = "off",
        Modules =
        [
            new VideoProcessingModuleConfig
            {
                Id = VideoProcessorCatalog.MotionAdaptiveDetail,
                Enabled = true,
                Order = 0,
                Parameters = new() { ["strength"] = 99, ["motion-threshold"] = 0.08, ["edge-protection"] = 0.6 },
            },
        ],
    };
    var clamped = VideoProcessorCatalog.Normalize(outOfRange);
    var strengthConfig = clamped.Modules.First(m => m.Id == VideoProcessorCatalog.MotionAdaptiveDetail);
    if (Math.Abs(strengthConfig.Parameters["strength"] - 1.0) > 1e-9)
    {
        Console.WriteLine("SETTINGS_FAIL: parameter not clamped");
        return 14;
    }

    // preset detection: recommended preset round trip
    var recommended = VideoProcessingPresetService.Apply(VideoProcessingPresetIds.BilibiliLowBitrate);
    if (VideoProcessingPresetService.DetectPresetId(recommended) != VideoProcessingPresetIds.BilibiliLowBitrate)
    {
        Console.WriteLine("SETTINGS_FAIL: recommended preset not detected");
        return 15;
    }
    var off = VideoProcessingPresetService.Apply(VideoProcessingPresetIds.Off);
    if (VideoProcessingPresetService.DetectPresetId(off) != VideoProcessingPresetIds.Off)
    {
        Console.WriteLine("SETTINGS_FAIL: off preset not detected");
        return 16;
    }

    // deep copy: mutating the source must not affect the clone
    var source = VideoProcessingPresetService.Apply(VideoProcessingPresetIds.BilibiliLowBitrate);
    var clone = source.Clone();
    source.Modules.First(m => m.Id == VideoProcessorCatalog.MotionAdaptiveDetail).Enabled = false;
    source.Modules.First(m => m.Id == VideoProcessorCatalog.MotionAdaptiveDetail).Parameters["strength"] = 0;
    if (!clone.Modules.First(m => m.Id == VideoProcessorCatalog.MotionAdaptiveDetail).Enabled)
    {
        Console.WriteLine("SETTINGS_FAIL: deep copy broken (enabled flag)");
        return 17;
    }
    if (Math.Abs(clone.Modules.First(m => m.Id == VideoProcessorCatalog.MotionAdaptiveDetail).Parameters["strength"] - 0.35) > 1e-9)
    {
        Console.WriteLine("SETTINGS_FAIL: deep copy broken (parameters)");
        return 18;
    }

    // ---- S1: disk-safety percentage contract ----
    // 1. Missing field in old JSON and fresh settings both default to 10.
    if (old.DiskSafetyFreePercent != 10)
    {
        Console.WriteLine("SETTINGS_FAIL: old JSON must default DiskSafetyFreePercent to 10");
        return 60;
    }
    if (new UserSettings().DiskSafetyFreePercent != 10)
    {
        Console.WriteLine("SETTINGS_FAIL: new UserSettings must default DiskSafetyFreePercent to 10");
        return 61;
    }
    // 2. Below 0 normalizes to 0 (also through the UserSettings migration path).
    if (DiskSafetyPolicy.NormalizeSafetyPercent(-1) != 0)
    {
        Console.WriteLine("SETTINGS_FAIL: -1 must normalize to 0");
        return 62;
    }
    var migratedNegative = new UserSettings { DiskSafetyFreePercent = -1 };
    SettingsMigration.Normalize(migratedNegative);
    if (migratedNegative.DiskSafetyFreePercent != 0)
    {
        Console.WriteLine("SETTINGS_FAIL: UserSettings migration must normalize -1 to 0");
        return 63;
    }
    // 3. 0 stays 0 and evaluates as Disabled.
    if (DiskSafetyPolicy.NormalizeSafetyPercent(0) != 0 ||
        DiskSafetyPolicy.Evaluate(8L * 1024 * 1024 * 1024, 1, 0).State != DiskSafetyState.Disabled)
    {
        Console.WriteLine("SETTINGS_FAIL: 0 must stay 0 and produce Disabled");
        return 64;
    }
    // 4/5. 10 and 50 stay unchanged.
    if (DiskSafetyPolicy.NormalizeSafetyPercent(10) != 10 || DiskSafetyPolicy.NormalizeSafetyPercent(50) != 50)
    {
        Console.WriteLine("SETTINGS_FAIL: 10 and 50 must stay unchanged");
        return 65;
    }
    // 6. 51 normalizes to 50.
    if (DiskSafetyPolicy.NormalizeSafetyPercent(51) != 50)
    {
        Console.WriteLine("SETTINGS_FAIL: 51 must normalize to 50");
        return 66;
    }
    // 7/8. Warning lines: 10 -> 15, 50 -> 55.
    if (DiskSafetyPolicy.CalculateWarningPercent(10) != 15 || DiskSafetyPolicy.CalculateWarningPercent(50) != 55)
    {
        Console.WriteLine("SETTINGS_FAIL: warning line must be safety + 5");
        return 67;
    }
    // 9. 98 is first normalized to 50 (snapshot path), warning line 55.
    var snapshot98 = SettingsMigration.NormalizeSnapshot(new RenderSettingsSnapshot(10, 0.5, "R:\\", "C:\\Videos", "C:\\Game", false, 60, 0, DiskSafetyFreePercent: 98));
    if (snapshot98.DiskSafetyFreePercent != 50 ||
        DiskSafetyPolicy.CalculateWarningPercent(snapshot98.DiskSafetyFreePercent) != 55)
    {
        Console.WriteLine("SETTINGS_FAIL: 98 must normalize to 50 with a 55% warning line");
        return 68;
    }
    // 10-12. Byte thresholds: 10% of 8 / 16 / 32 GiB.
    const long gib = 1024L * 1024 * 1024;
    if (DiskSafetyPolicy.CalculateThresholdBytes(8 * gib, 10) != 858_993_459L ||
        DiskSafetyPolicy.CalculateThresholdBytes(16 * gib, 10) != 1_717_986_918L ||
        DiskSafetyPolicy.CalculateThresholdBytes(32 * gib, 10) != 3_435_973_836L)
    {
        Console.WriteLine("SETTINGS_FAIL: 10% byte threshold incorrect");
        return 69;
    }
    // 13. Exactly at the safety line is Critical (inclusive <=).
    if (DiskSafetyPolicy.Evaluate(1000, 100, 10).State != DiskSafetyState.Critical)
    {
        Console.WriteLine("SETTINGS_FAIL: exactly at safety line must be Critical");
        return 70;
    }
    // 14. Exactly at the warning line is Warning (inclusive <=).
    if (DiskSafetyPolicy.Evaluate(1000, 150, 10).State != DiskSafetyState.Warning)
    {
        Console.WriteLine("SETTINGS_FAIL: exactly at warning line must be Warning");
        return 71;
    }
    // 15. Above the warning line is Normal.
    if (DiskSafetyPolicy.Evaluate(1000, 151, 10).State != DiskSafetyState.Normal)
    {
        Console.WriteLine("SETTINGS_FAIL: above warning line must be Normal");
        return 72;
    }
    // 16. Invalid total capacity / negative free bytes are Unavailable.
    if (DiskSafetyPolicy.Evaluate(0, 100, 10).State != DiskSafetyState.Unavailable ||
        DiskSafetyPolicy.Evaluate(-5, 100, 10).State != DiskSafetyState.Unavailable ||
        DiskSafetyPolicy.Evaluate(1000, -1, 10).State != DiskSafetyState.Unavailable)
    {
        Console.WriteLine("SETTINGS_FAIL: invalid capacity must be Unavailable");
        return 73;
    }
    // Full pure-data snapshot model computed from the same policy.
    var health = DiskSafetyPolicy.EvaluateSnapshot("R:\\", 16 * gib, 1_717_986_918L, 10, DateTimeOffset.UtcNow);
    if (health.State != DiskSafetyState.Critical ||
        health.TotalBytes != 16 * gib ||
        health.FreeBytes != 1_717_986_918L ||
        health.UsedBytes != 15_461_882_266L ||
        health.SafetyBytes != 1_717_986_918L ||
        health.WarningBytes != 2_576_980_377L ||
        health.WarningPercent != 15 ||
        health.SampledAt == default)
    {
        Console.WriteLine("SETTINGS_FAIL: disk health snapshot model inconsistent");
        return 74;
    }

    // ---- S1 Repair: quality-processing backend contract ----
    // The four backend states carry the stable contract values.
    if (Convert.ToInt32(ProcessingBackend.Unknown) != 0 ||
        Convert.ToInt32(ProcessingBackend.Disabled) != 1 ||
        Convert.ToInt32(ProcessingBackend.Gpu) != 2 ||
        Convert.ToInt32(ProcessingBackend.CpuFallback) != 3)
    {
        Console.WriteLine("SETTINGS_FAIL: ProcessingBackend stable values broken");
        return 75;
    }
    // Disabled, Gpu and CpuFallback are each independently expressible, all four distinct.
    var backendValues = new[] { ProcessingBackend.Unknown, ProcessingBackend.Disabled, ProcessingBackend.Gpu, ProcessingBackend.CpuFallback };
    if (backendValues.Distinct().Count() != 4)
    {
        Console.WriteLine("SETTINGS_FAIL: ProcessingBackend values not distinct");
        return 76;
    }
    // PerformancePreflightResult carries each valid processing state.
    var preflight = new PerformancePreflightResult(120, 60, 60, 1.0, 8, 4096, ProcessingBackend.Disabled, EncoderBackend.Hardware, PerformancePreflightRating.Pass, DateTimeOffset.UtcNow);
    if (preflight.QualityBackend != ProcessingBackend.Disabled)
    {
        Console.WriteLine("SETTINGS_FAIL: preflight cannot carry Disabled");
        return 77;
    }
    if ((preflight with { QualityBackend = ProcessingBackend.Gpu }).QualityBackend != ProcessingBackend.Gpu)
    {
        Console.WriteLine("SETTINGS_FAIL: preflight cannot carry Gpu");
        return 78;
    }
    var gpuPreflight = preflight with { QualityBackend = ProcessingBackend.CpuFallback };
    if (gpuPreflight.QualityBackend != ProcessingBackend.CpuFallback)
    {
        Console.WriteLine("SETTINGS_FAIL: preflight cannot carry CpuFallback");
        return 79;
    }
    // JSON round trip must preserve QualityBackend.
    var preflightRoundTrip = JsonSerializer.Deserialize<PerformancePreflightResult>(JsonSerializer.Serialize(gpuPreflight))!;
    if (preflightRoundTrip.QualityBackend != ProcessingBackend.CpuFallback ||
        Math.Abs(preflightRoundTrip.ConsumptionRatio - 1.0) > 1e-9 ||
        preflightRoundTrip.Rating != PerformancePreflightRating.Pass)
    {
        Console.WriteLine("SETTINGS_FAIL: PerformancePreflightResult round trip broken");
        return 80;
    }

    Console.WriteLine("SETTINGS_OK");
    return 0;
}

// ---- old RenderSettingsSnapshot JSON compatibility ----
if (args.Length >= 1 && args[0] == "snapshot")
{
    const string oldSnapshotJson = """
        {
          "supersamplingMultiplier": 10,
          "exposure": 0.5,
          "watchDirectory": "R:\\",
          "outputDirectory": "C:\\Videos",
          "gameRootPath": "C:\\Game",
          "hideHud": false,
          "outputFramerate": 60,
          "targetBitrate": 140000000
        }
        """;
    var snapshot = JsonSerializer.Deserialize<RenderSettingsSnapshot>(oldSnapshotJson)!;
    var normalized = SettingsMigration.NormalizeSnapshot(snapshot);
    if (normalized.MotionBlurMode != MotionBlurWeightMode.LegacyGaussianExposure)
    {
        Console.WriteLine("SNAPSHOT_FAIL: old snapshot must default to Legacy");
        return 20;
    }
    if (normalized.VideoProcessing is null || normalized.VideoProcessing.Modules.Any(m => m.Enabled))
    {
        Console.WriteLine("SNAPSHOT_FAIL: old snapshot must default all modules off");
        return 21;
    }
    if (normalized.TargetBitrate > 120_000_000)
    {
        Console.WriteLine("SNAPSHOT_FAIL: target bitrate not clamped to the native ceiling");
        return 22;
    }
    if (normalized.DiskSafetyFreePercent != 10)
    {
        Console.WriteLine("SNAPSHOT_FAIL: old snapshot JSON must default DiskSafetyFreePercent to 10");
        return 24;
    }

    // round trip with new fields
    var full = normalized with
    {
        MotionBlurMode = MotionBlurWeightMode.ShutterAngle,
        ShutterAngle = 330,
        VideoProcessing = VideoProcessingPresetService.Apply(VideoProcessingPresetIds.BilibiliLowBitrate),
        DiskSafetyFreePercent = 25,
    };
    var round = JsonSerializer.Deserialize<RenderSettingsSnapshot>(JsonSerializer.Serialize(full))!;
    var roundNormalized = SettingsMigration.NormalizeSnapshot(round);
    if (roundNormalized.MotionBlurMode != MotionBlurWeightMode.ShutterAngle ||
        Math.Abs(roundNormalized.ShutterAngle - 330) > 1e-9 ||
        !roundNormalized.VideoProcessing!.Modules.Any(m => m.Enabled) ||
        roundNormalized.DiskSafetyFreePercent != 25)
    {
        Console.WriteLine("SNAPSHOT_FAIL: new snapshot round trip broken");
        return 23;
    }

    Console.WriteLine("SNAPSHOT_OK");
    return 0;
}

// ---- motion blur weights ----
if (args.Length >= 1 && args[0] == "weights")
{
    // Shutter 360° on N samples must sum to 1 with N active samples.
    foreach (var n in new[] { 1, 4, 10, 16 })
    {
        var w360 = CpuProcessingReference.BuildShutterWeights(n, 360);
        var sum360 = w360.Sum();
        if (Math.Abs(sum360 - 1f) > 1e-6 || w360.Count(v => v > 0) != n)
        {
            Console.WriteLine($"WEIGHTS_FAIL: 360° N={n} sum={sum360} active={w360.Count(v => v > 0)}");
            return 30;
        }
    }

    // Shutter 180°: active count ≈ N/2, window centered, sum = 1.
    foreach (var n in new[] { 4, 10, 16 })
    {
        var w180 = CpuProcessingReference.BuildShutterWeights(n, 180);
        var sum180 = w180.Sum();
        var active180 = w180.Count(v => v > 0);
        var expectedActive = (int)Math.Round(n * 180 / 360.0, MidpointRounding.AwayFromZero);
        if (Math.Abs(sum180 - 1f) > 1e-6 || active180 != expectedActive)
        {
            Console.WriteLine($"WEIGHTS_FAIL: 180° N={n} sum={sum180} active={active180} expected={expectedActive}");
            return 31;
        }
    }

    // Legacy Gaussian: unchanged historical behaviour (same sigma formula).
    var legacy = CpuProcessingReference.BuildGaussianWeights(10, 0.5);
    if (Math.Abs(legacy.Sum() - 1f) > 1e-6)
    {
        Console.WriteLine("WEIGHTS_FAIL: legacy weights do not sum to 1");
        return 32;
    }
    // Legacy must be a peaked Gaussian (center > edges).
    if (!(legacy[5] > legacy[0] && legacy[5] > legacy[9]))
    {
        Console.WriteLine("WEIGHTS_FAIL: legacy weights not peaked");
        return 33;
    }
    // 180° shutter on N=10 must NOT be identical to legacy exposure 0.5 (semantics differ).
    var w180_10 = CpuProcessingReference.BuildShutterWeights(10, 180);
    var legacy_10 = CpuProcessingReference.BuildGaussianWeights(10, 0.5);
    if (w180_10.SequenceEqual(legacy_10))
    {
        Console.WriteLine("WEIGHTS_FAIL: shutter must differ from legacy gaussian");
        return 34;
    }

    Console.WriteLine("WEIGHTS_OK");
    return 0;
}

// ---- C# CPU reference effect math ----
if (args.Length >= 1 && args[0] == "processing")
{
    const int w = 64;
    const int h = 64;
    const int px = w * h * 3;

    float[] MakeSolid(byte v)
    {
        var buf = new float[px];
        for (var i = 0; i < px; i++) buf[i] = v;
        return buf;
    }

    float[] MakeCheckerboard(byte a, byte b)
    {
        var buf = new float[px];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var pi = (y * w + x) * 3;
                var v = ((x + y) & 1) == 0 ? a : b;
                buf[pi] = buf[pi + 1] = buf[pi + 2] = v;
            }
        return buf;
    }

    float HighFreqEnergy(float[] img)
    {
        var energy = 0f;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var pi = (y * w + x) * 3;
                var cx = Math.Clamp(x + 1, 0, w - 1);
                var cy = Math.Clamp(y + 1, 0, h - 1);
                energy += Math.Abs(img[pi] - img[(y * w + cx) * 3]);
                energy += Math.Abs(img[pi] - img[(cy * w + x) * 3]);
            }
        return energy;
    }

    float MaxAbsDelta(float[] a, float[] b)
    {
        var max = 0f;
        for (var i = 0; i < px; i++) max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    // 1) Solid frame: low-pass and deband must be ~identity.
    {
        var solid = MakeSolid(128);
        var lowpass = new float[px];
        var deband = new float[px];
        CpuProcessingReference.ApplyMicroDetailLowPass(solid, lowpass, w, h, 0.25f, 1f);
        CpuProcessingReference.ApplyDebandNoDither(solid, deband, w, h, 0.3f, 0.06f);
        if (MaxAbsDelta(solid, lowpass) > 0.01f || MaxAbsDelta(solid, deband) > 0.01f)
        {
            Console.WriteLine($"PROC_FAIL: solid frame changed low={MaxAbsDelta(solid, lowpass):F3} deband={MaxAbsDelta(solid, deband):F3}");
            return 40;
        }
    }

    // 2) Checkerboard: low-pass must reduce high-frequency energy.
    {
        var checker = MakeCheckerboard(30, 220);
        var lowpass = new float[px];
        CpuProcessingReference.ApplyMicroDetailLowPass(checker, lowpass, w, h, 1.0f, 1f);
        var before = HighFreqEnergy(checker);
        var after = HighFreqEnergy(lowpass);
        if (!(after < before * 0.5))
        {
            Console.WriteLine($"PROC_FAIL: checkerboard energy before={before:F0} after={after:F0}");
            return 41;
        }
    }

    // 3) Motion-adaptive: static region keeps more detail than moving region.
    {
        var cur = MakeCheckerboard(30, 220);
        var prevStatic = MakeCheckerboard(30, 220);           // identical -> no motion
        var prevMoving = MakeCheckerboard(220, 30);           // inverted  -> strong motion
        var outStatic = new float[px];
        var outMoving = new float[px];
        CpuProcessingReference.ApplyMotionAdaptiveDetail(cur, prevStatic, outStatic, w, h, 1.0f, 0.08f, 0.0f);
        CpuProcessingReference.ApplyMotionAdaptiveDetail(cur, prevMoving, outMoving, w, h, 1.0f, 0.08f, 0.0f);
        var detailStatic = HighFreqEnergy(outStatic);
        var detailMoving = HighFreqEnergy(outMoving);
        if (!(detailMoving < detailStatic * 0.7))
        {
            Console.WriteLine($"PROC_FAIL: motion-adaptive static={detailStatic:F0} moving={detailMoving:F0}");
            return 42;
        }
    }

    // 4) Deband: gradient smoothing must not add noise (deterministic, monotone output).
    {
        var gradient = new float[px];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var pi = (y * w + x) * 3;
                var v = 40f + (x + y) * 0.7f;
                gradient[pi] = gradient[pi + 1] = gradient[pi + 2] = v;
            }
        var debanded = new float[px];
        CpuProcessingReference.ApplyDebandNoDither(gradient, debanded, w, h, 0.5f, 0.06f);
        // No random noise: every output must be reproducible and within the input range.
        var reproducible = new float[px];
        CpuProcessingReference.ApplyDebandNoDither(gradient, reproducible, w, h, 0.5f, 0.06f);
        if (MaxAbsDelta(debanded, reproducible) > 1e-6)
        {
            Console.WriteLine("PROC_FAIL: deband not deterministic (noise injected?)");
            return 43;
        }
        for (var i = 0; i < px; i++)
        {
            if (debanded[i] < gradient[i] - 2f || debanded[i] > gradient[i] + 2f)
            {
                Console.WriteLine("PROC_FAIL: deband overshoots");
                return 44;
            }
        }
    }

    // 5) Temporal shimmer: large motion must not mix in the previous frame.
    {
        var cur = MakeSolid(200);
        var prevDifferent = MakeSolid(10);
        var shimmer = new float[px];
        CpuProcessingReference.ApplyTemporalShimmerReduction(cur, prevDifferent, shimmer, w, h, 1.0f, 0.05f);
        if (MaxAbsDelta(shimmer, cur) > 1f)
        {
            Console.WriteLine($"PROC_FAIL: shimmer ghosting on large motion delta={MaxAbsDelta(shimmer, cur):F2}");
            return 45;
        }
    }

    // 6) Mapper: stable native effect ids + parameter order.
    {
        var descs = NativeProcessingMapper.Map(recommendedForMapper());
        var types = descs.Select(d => d.EffectType).OrderBy(x => x).ToList();
        if (!types.SequenceEqual([(int)NativeProcessingMapper.NativeEffectType.MotionAdaptiveDetail, (int)NativeProcessingMapper.NativeEffectType.MicroDetailLowPass]))
        {
            Console.WriteLine($"PROC_FAIL: mapper types {string.Join(",", types)}");
            return 46;
        }
        var motion = descs.First(d => d.EffectType == (int)NativeProcessingMapper.NativeEffectType.MotionAdaptiveDetail);
        if (motion.P0 <= 0f || motion.P1 <= 0f || motion.P2 <= 0f)
        {
            Console.WriteLine("PROC_FAIL: mapper p0..p2 (strength/threshold/edge) not mapped");
            return 47;
        }
    }

    Console.WriteLine("PROCESSING_OK");
    return 0;

    static VideoProcessingSettings recommendedForMapper() =>
        VideoProcessingPresetService.Apply(VideoProcessingPresetIds.BilibiliLowBitrate);
}

// ---- native session smoke with quality effects enabled ----
if (args.Length >= 1 && args[0] == "native-effects")
{
    var neOutDir = Path.Combine(Path.GetTempPath(), "mmod_smoke");
    Directory.CreateDirectory(neOutDir);
    var neOutputPath = Path.Combine(neOutDir, $"effects_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

    const int neW = 320;
    const int neH = 180;
    const int neBlend = 4;
    const int neOutputFrames = 12;

    // Enable every V1 effect so each GPU shader actually dispatches at runtime.
    var allEffects = VideoProcessorCatalog.Normalize(null);
    foreach (var module in allEffects.Modules)
        module.Enabled = true;
    var effects = NativeProcessingMapper.Map(allEffects);
    if (effects.Count != 4)
    {
        Console.WriteLine($"NATIVE_EFFECTS_FAIL: expected 4 mapped effects, got {effects.Count}");
        return 52;
    }
    using var neSession = NativeBlendSession.Create(
        neW, neH, neBlend, 0.5f, 60, neOutputPath,
        new NativeSessionOptions(
            MotionBlurMode: MotionBlurWeightMode.ShutterAngle,
            ShutterAngle: 270,
            Effects: effects,
            TargetBitrate: 0));

    var (enabled, cpuFallback) = neSession.GetProcessingStatus();
    Console.WriteLine($"EFFECTS enabled={enabled} cpuFallback={cpuFallback}");
    if (!enabled)
    {
        Console.WriteLine("NATIVE_EFFECTS_FAIL: effects not enabled in native session");
        return 50;
    }

    // M3: the versioned backend ABI must report the actual processing path
    // (Gpu when the GPU pipeline is live, CpuFallback otherwise — never
    // Unknown/Disabled for enabled effects) and Software encoding, since the
    // live capture path disables hardware MFTs.
    var (processing, encoder) = neSession.GetBackends();
    Console.WriteLine($"BACKENDS processing={processing} encoder={encoder}");
    if (processing is not ProcessingBackend.Gpu and not ProcessingBackend.CpuFallback)
    {
        Console.WriteLine($"NATIVE_EFFECTS_FAIL: unexpected processing backend {processing}");
        return 53;
    }
    if (encoder != EncoderBackend.Software)
    {
        Console.WriteLine($"NATIVE_EFFECTS_FAIL: expected Software encoder (hardware MFT disabled), got {encoder}");
        return 54;
    }

    var neFrame = new byte[neW * neH * 4];
    for (var of = 0; of < neOutputFrames * neBlend; of++)
    {
        var shade = (byte)((of * 17) % 255);
        for (var i = 0; i < neW * neH; i++)
        {
            neFrame[i * 4 + 0] = shade;
            neFrame[i * 4 + 1] = (byte)(255 - shade);
            neFrame[i * 4 + 2] = (byte)((of * 5) % 255);
            neFrame[i * 4 + 3] = 255;
        }
        neSession.SubmitBgra(neFrame, neW * 4);
    }

    neSession.Finish();
    var neInfo = new FileInfo(neOutputPath);
    Console.WriteLine(neInfo.Exists
        ? $"NATIVE_EFFECTS_OK size={neInfo.Length} progress={neSession.GetProgress()}"
        : "NATIVE_EFFECTS_FAIL: output missing");
    return neInfo.Exists && neInfo.Length > 1000 ? 0 : 51;
}

if (args.Length >= 3 && args[0] == "netcon")
{
    await using var client = new MomentumNetConClient();
    await client.ConnectAsync(int.Parse(args[1]), args[2], TimeSpan.FromSeconds(10), CancellationToken.None);
    await client.ExecuteAsync("echo MMOD_NETCON_SMOKE_OK", TimeSpan.FromSeconds(10), CancellationToken.None);
    Console.WriteLine("NETCON_OK");
    return 0;
}

if (args.Length >= 3 && args[0] == "control")
{
    await using var game = new MomentumProcessController();
    game.NetCon.OutputReceived += line => Console.WriteLine("NETCON " + line);
    await game.StartAsync(args[1], CancellationToken.None);
    Console.WriteLine("CONTROL_AUTH_OK");
    if (args.Length >= 4)
    {
        var replayRoot = Path.Combine(Path.GetFullPath(args[1]), "momentum");
        var relative = Path.GetRelativePath(replayRoot, Path.GetFullPath(args[3])).Replace("\\", "/").Replace("\"", string.Empty);
        Console.WriteLine("--- CONSOLE SCRIPT ---");
        Console.WriteLine(MomentumReplaySession.BuildManualConsoleScript(args[2], relative));
        Console.WriteLine("--- END SCRIPT ---");
        await MomentumReplaySession.ChangeMapAsync(
            game.NetCon,
            args[2],
            line => Console.WriteLine("STEP " + line),
            CancellationToken.None);
        await MomentumReplaySession.StartWatchAsync(
            game.NetCon,
            relative,
            line => Console.WriteLine("STEP " + line),
            CancellationToken.None);
        Console.WriteLine("CONTROL_REPLAY_WATCH_OK");
    }
    else
    {
        await MomentumReplaySession.ChangeMapAsync(game.NetCon, args[2], line => Console.WriteLine("STEP " + line), CancellationToken.None);
        Console.WriteLine("CONTROL_MAP_OK");
    }
    await game.CloseOwnedAsync(CancellationToken.None);
    return 0;
}

if (args.Length >= 2 && args[0] == "catalog")
{
    var result = new ReplayCatalogService().Scan(args[1]);
    var compatible = result.Records.Count(x => x.IsCompatible);
    var incompatible = result.Records.Count - compatible;
    Console.WriteLine($"CATALOG records={result.Records.Count} compatible={compatible} incompatible={incompatible} issues={result.Issues.Count}");
    foreach (var issue in result.Issues.Take(10)) Console.WriteLine($"ISSUE {issue.FilePath}: {issue.Message}");
    return result.Records.Count > 0 && result.Issues.Count == 0 ? 0 : 3;
}

if (args.Length >= 3 && args[0] == "concat")
{
    var output = Path.Combine(Path.GetTempPath(), "mmod_smoke", $"concat_{DateTime.Now:HHmmss}.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    NativeMp4Concatenator.Concatenate([args[1], args[2]], output);
    Mp4MergeService.Validate(output);
    Console.WriteLine($"CONCAT_OK path={output} size={new FileInfo(output).Length}");
    return 0;
}

if (args.Length >= 1 && args[0] == "repository")
{
    var db = Path.Combine(Path.GetTempPath(), "mmod_smoke", $"tasks_{Guid.NewGuid():N}.db");
    var repo = new RenderTaskRepository(db);
    var processing = VideoProcessingPresetService.Apply(VideoProcessingPresetIds.BilibiliLowBitrate);
    var settings = new RenderSettingsSnapshot(10, 0.5, @"R:\", @"C:\Videos", @"C:\Game", false, 60, 140_000_000,
        MotionBlurWeightMode.ShutterAngle, 330, processing);
    var id = repo.CreateTask(new NewRenderTask("map", "player", 1, @"C:\Videos\out.mp4", settings, [new NewRenderNode(@"C:\record.mtv", 1, 0, 12.3, 738)]));
    var task = repo.GetTasks().Single(x => x.Id == id);
    var node = repo.GetNodes(id).Single();
    if (task.Status != RenderTaskStatus.Pending || node.ExpectedTickCount != 738) return 4;
    repo.UpdateTaskStatus(id, RenderTaskStatus.Running);
    var reopened = new RenderTaskRepository(db);
    if (reopened.GetTasks().Single().Status != RenderTaskStatus.Paused || reopened.GetNodes(id).Single().Status != RenderNodeStatus.Pending) return 5;

    // snapshot round trip through the DB must preserve the quality config
    var stored = JsonSerializer.Deserialize<RenderSettingsSnapshot>(reopened.GetTasks().Single().SettingsJson)!;
    var storedNormalized = SettingsMigration.NormalizeSnapshot(stored);
    if (storedNormalized.MotionBlurMode != MotionBlurWeightMode.ShutterAngle ||
        Math.Abs(storedNormalized.ShutterAngle - 330) > 1e-9 ||
        !storedNormalized.VideoProcessing!.Modules.Any(m => m.Enabled))
    {
        Console.WriteLine("REPOSITORY_FAIL: quality config lost in task SettingsJson");
        return 6;
    }
    Console.WriteLine("REPOSITORY_OK");
    return 0;
}

if (args.Length >= 2 && args[0] == "obs")
{
    var input = args[1];
    var output = Path.Combine(Path.GetTempPath(), "mmod_smoke", $"obs_out_{DateTime.Now:HHmmss}.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    var settings = new UserSettings
    {
        SupersamplingMultiplier = 2,
        ObsCaptureFramerate = 60,
        Exposure = 0.5,
        MotionBlurWeightMode = MotionBlurWeightMode.ShutterAngle,
        ShutterAngle = 270,
        VideoProcessing = VideoProcessingPresetService.Apply(VideoProcessingPresetIds.BilibiliLowBitrate),
    };
    Console.WriteLine($"OBS in={input}");
    await new ObsSynthesisService().RunAsync(
        input,
        output,
        settings,
        new Progress<ObsSynthesisService.Progress>(p => Console.WriteLine($"progress {p.Done}/{p.Total}")),
        CancellationToken.None);
    var info = new FileInfo(output);
    Console.WriteLine(info.Exists ? $"OBS_OK size={info.Length}" : "OBS_FAIL");
    return info.Exists && info.Length > 500 ? 0 : 2;
}

var smokeOutDir = Path.Combine(Path.GetTempPath(), "mmod_smoke");
Directory.CreateDirectory(smokeOutDir);
var smokeOutputPath = Path.Combine(smokeOutDir, $"smoke_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

const int smokeW = 320;
const int smokeH = 180;
const int smokeBlend = 4;
const int smokeOutputFrames = 30;

Console.WriteLine($"Output: {smokeOutputPath}");
using var smokeSession = NativeBlendSession.Create(
    smokeW, smokeH, smokeBlend, 0.5f, 60, smokeOutputPath);

var smokeFrame = new byte[smokeW * smokeH * 4];
for (var of = 0; of < smokeOutputFrames * smokeBlend; of++)
{
    var shade = (byte)((of * 7) % 255);
    for (var i = 0; i < smokeW * smokeH; i++)
    {
        smokeFrame[i * 4 + 0] = shade;
        smokeFrame[i * 4 + 1] = (byte)(255 - shade);
        smokeFrame[i * 4 + 2] = 128;
        smokeFrame[i * 4 + 3] = 255;
    }

    smokeSession.SubmitBgra(smokeFrame, smokeW * 4);
}

smokeSession.Finish();
var smokeInfo = new FileInfo(smokeOutputPath);
Console.WriteLine(smokeInfo.Exists
    ? $"OK size={smokeInfo.Length} bytes progress={smokeSession.GetProgress()}"
    : "FAIL: output missing");
return smokeInfo.Exists && smokeInfo.Length > 1000 ? 0 : 1;
