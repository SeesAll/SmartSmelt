using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SmartSmelt", "SeesAll", "1.1.6")]
    [Description("Preset-based accelerated smelting with instant sync, adaptive scaling, and smart fuel pull.")]
    public class SmartSmelt : RustPlugin
    {
        #region Configuration

        private Configuration _config;
        private readonly List<string> _cachedWhitelistFragmentsLower = new List<string>();
        private bool _cachedWhitelistAllowsAnyRefinery;
        private ItemDefinition _charcoalDefinition;

        private class Configuration
        {
            public bool Enabled = true;
            public int ConfigVersion = 1;
            public string Preset = "10x";
            public string PresetOptions = "2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, instant";

public bool AutoTuneEnabled = true;
public int AveragePopulation = 100;
public string AutoTuneBias = "Balanced";
public bool AutoTuneWriteToConfig = true;

public bool EnableOreSplitting = true;
            public List<string> OvenWhitelist = new List<string>
            {
                "furnace",
                "furnace.large",
                "refinery",
                "electric.furnace"
            };
            public bool ForceStartCookingOnToggle = true;
            public bool VerboseTrackingLogs = false;
            public bool VerboseCycleLogs = false;
            public bool AdaptiveScaling = true;
            public int AdaptiveMinOvensPerTick = 100;
            public int AdaptiveMaxOvensPerTick = 800;
            public bool DynamicTickInterval = true;
            public float DynamicMinGlobalLoopInterval = 0.10f;
            public float DynamicMaxGlobalLoopInterval = 0.25f;
            public int DynamicLowOvenCount = 100;
            public int DynamicHighOvenCount = 1200;
            public float FixedGlobalLoopInterval = 0.25f;
            public bool AutoPullFuelFromPlayer = true;
            public float AutoPullFuelBufferPercent = 0.1f;
            public bool EnableElectricFurnaceNativeScaling = true;
            public float ElectricFurnaceThroughputScale = 2.0f;
            public float ElectricFurnaceCycleSpeedScale = 0.5f;
            public bool ProduceCharcoalFromFuel = true;
            public float CharcoalPerWood = 0.75f;
            public string CharcoalOverflowMode = "Skip";
            public bool Debug = false;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new Configuration();
            RebuildRuntimeCaches();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();

            if (MigrateConfigSchema())
            {

                base.LoadConfig();
            }
            try
            {
                _config = Config.ReadObject<Configuration>() ?? new Configuration();
            }
            catch
            {
                PrintWarning("Config was invalid; generating a new one.");
                _config = new Configuration();
            }

            if (NormalizeConfig())
                SaveConfig();

            RebuildRuntimeCaches();
            RefreshEffectiveScheduling(allowWriteToConfig: true);
        }
        protected override void SaveConfig() => Config.WriteObject(_config, true);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "<color=#ff6b6b>SmartSmelt:</color> You don't have permission to use this command.",
                ["DebugTitle"] = "<color=#9be7ff>SmartSmelt Debug</color> v{0}",
                ["DebugLine1"] = "Enabled: {0} | Preset: {1} | Multiplier: {2}x",
                ["DebugLine2"] = "Tracked ovens: {0} | Ovens/tick cap: {1} (Adaptive: {2})",
                ["DebugLine3"] = "Global loop interval: {0:0.000}s | Desired: {1:0.000}s (Dynamic: {2})",
                ["ConsoleDebug"] = "SmartSmelt Debug v{0} | Enabled={1} Preset={2} Mult={3}x | Tracked={4} Cap={5} | Interval={6:0.000}s Desired={7:0.000}s",
                ["InfoLine1"] = "SmartSmelt v{0} | Preset={1} | Enabled={2} | OreSplitting={3} | AutoPullFuel={4} (Buffer={5:0.###}%)",
                ["InfoLine2"] = "AutoTune={0} (AvgPop={1}, Bias={2}, WriteToConfig={3})",
                ["InfoLine3"] = "TrackedOvens={0} | PerTickCap={1} (Adaptive={2}) | LoopInterval={3:0.000}s (Desired={4:0.000}s, Dynamic={5})",
                ["ConsoleInfo"] = "SmartSmelt v{0} | Preset={1} | Enabled={2} | AutoPullFuel={3} (Buffer={4:0.###}%)\nTrackedOvens={5} | PerTickCap={6} (Adaptive={7}) | LoopInterval={8:0.000}s (Desired={9:0.000}s, Dynamic={10})"
            }, this);
        }

        private string Msg(string key, string userId = null) => lang.GetMessage(key, this, userId);

        private bool MigrateConfigSchema()
        {
            try
            {
                var raw = Config.ReadObject<Dictionary<string, object>>();
                if (raw == null) return false;

                int version = 0;
                if (raw.TryGetValue("ConfigVersion", out var vObj))
                {
                    try { version = Convert.ToInt32(vObj); } catch { version = 0; }
                }

                bool changed = false;

                if (version < 1)
                {
                    if (raw.Remove("AutoPullFuelTargetAmount")) changed = true;
                    if (raw.Remove("AutoPullFuelMaxPullPerInteraction")) changed = true;
                    if (raw.Remove("AutoPullFuelExactNeeded")) changed = true;

                    raw["ConfigVersion"] = 1;
                    changed = true;
                }

                if (!raw.ContainsKey("ConfigVersion"))
                {
                    raw["ConfigVersion"] = 1;
                    changed = true;
                }

                if (!changed) return false;

                Config.WriteObject(raw, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RebuildRuntimeCaches()
        {
            _cachedWhitelistFragmentsLower.Clear();
            _cachedWhitelistAllowsAnyRefinery = false;

            if (_config?.OvenWhitelist != null)
            {
                for (int i = 0; i < _config.OvenWhitelist.Count; i++)
                {
                    var fragment = (_config.OvenWhitelist[i] ?? string.Empty).Trim().ToLowerInvariant();
                    if (fragment.Length == 0) continue;

                    _cachedWhitelistFragmentsLower.Add(fragment);
                    if (fragment.Contains("refinery_small"))
                        _cachedWhitelistAllowsAnyRefinery = true;
                }
            }

            _charcoalDefinition = ItemManager.FindItemDefinition("charcoal");
        }

        private bool NormalizeConfig()
        {
            bool changed = false;

            if (_config == null)
            {
                _config = new Configuration();
                return true;
            }

            if (_config.ConfigVersion != 1)
            {
                _config.ConfigVersion = 1;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.PresetOptions))
            {
                _config.PresetOptions = "2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, instant";
                changed = true;
            }

            if (_config.AutoPullFuelBufferPercent < 0f)
            {
                _config.AutoPullFuelBufferPercent = 0f;
                changed = true;
            }
            else if (_config.AutoPullFuelBufferPercent > 10f)
            {
                _config.AutoPullFuelBufferPercent = 10f;
                changed = true;
            }

            var p = (_config.Preset ?? "10x").Trim();
            if (p.Length == 0) p = "10x";
            p = p.ToLowerInvariant();

            if (p == "2") p = "2x";
            if (p == "3") p = "3x";
            if (p == "5") p = "5x";
            if (p == "10") p = "10x";
            if (p == "25") p = "25x";
            if (p == "50") p = "50x";
            if (p == "100") p = "100x";
            if (p == "1000") p = "1000x";
            if (p == "inst" || p == "instant") p = "instant";

            if (p != "2x" && p != "3x" && p != "5x" && p != "10x" && p != "25x" && p != "50x" && p != "100x" && p != "1000x" && p != "instant")
            {
                PrintWarning($"Unknown Preset '{_config.Preset}', defaulting to 10x.");
                p = "10x";
                changed = true;
            }

            if (!string.Equals(_config.Preset, p, StringComparison.Ordinal))
            {
                _config.Preset = p;
                changed = true;
            }

if (_config.AveragePopulation < 0) { _config.AveragePopulation = 0; changed = true; }
if (_config.AveragePopulation > 5000) { _config.AveragePopulation = 5000; changed = true; }

var bias = (_config.AutoTuneBias ?? "Balanced").Trim();
if (bias.Length == 0) bias = "Balanced";

if (bias.Equals("balanced", StringComparison.OrdinalIgnoreCase)) bias = "Balanced";
else if (bias.Equals("performance", StringComparison.OrdinalIgnoreCase)) bias = "Performance";
else if (bias.Equals("responsiveness", StringComparison.OrdinalIgnoreCase)) bias = "Responsiveness";
else
{
    PrintWarning($"Unknown AutoTuneBias '{_config.AutoTuneBias}', defaulting to Balanced.");
    bias = "Balanced";
    changed = true;
}

if (!string.Equals(_config.AutoTuneBias, bias, StringComparison.Ordinal))
{
    _config.AutoTuneBias = bias;
    changed = true;
}

            if (_config.OvenWhitelist == null)
            {
                _config.OvenWhitelist = new List<string> { "furnace", "furnace.large", "refinery", "electric.furnace" };
                changed = true;
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cleaned = new List<string>();
                foreach (var s in _config.OvenWhitelist)
                {
                    var v = (s ?? string.Empty).Trim();
                    if (v.Length == 0) continue;
                    if (seen.Add(v)) cleaned.Add(v);
                }

                if (cleaned.Count == 0)
                    cleaned.AddRange(new[] { "furnace", "furnace.large", "refinery", "electric.furnace" });

                if (cleaned.Count != _config.OvenWhitelist.Count)
                    changed = true;

                _config.OvenWhitelist = cleaned;
            }

            if (_config.CharcoalPerWood < 0f) { _config.CharcoalPerWood = 0f; changed = true; }
            else if (_config.CharcoalPerWood > 2f) { _config.CharcoalPerWood = 2f; changed = true; }

            if (_config.ElectricFurnaceThroughputScale < 0.25f) { _config.ElectricFurnaceThroughputScale = 0.25f; changed = true; }
            else if (_config.ElectricFurnaceThroughputScale > 10f) { _config.ElectricFurnaceThroughputScale = 10f; changed = true; }

            if (_config.ElectricFurnaceCycleSpeedScale < 0.1f) { _config.ElectricFurnaceCycleSpeedScale = 0.1f; changed = true; }
            else if (_config.ElectricFurnaceCycleSpeedScale > 2f) { _config.ElectricFurnaceCycleSpeedScale = 2f; changed = true; }

            var com = (_config.CharcoalOverflowMode ?? "Skip").Trim();
            if (com.Length == 0) com = "Skip";
            com = com.Equals("Pause", StringComparison.OrdinalIgnoreCase) ? "Pause" : "Skip";
            if (_config.CharcoalOverflowMode != com) { _config.CharcoalOverflowMode = com; changed = true; }

            return changed;
        }

private bool _effAdaptiveScaling;
private int _effAdaptiveMinOvensPerTick;
private int _effAdaptiveMaxOvensPerTick;
private bool _effDynamicTickInterval;
private float _effDynamicMinGlobalLoopInterval;
private float _effDynamicMaxGlobalLoopInterval;
private int _effDynamicLowOvenCount;
private int _effDynamicHighOvenCount;
private float _effFixedGlobalLoopInterval;
private readonly HashSet<ulong> _pendingFuelRecalc = new HashSet<ulong>();
private readonly Dictionary<ulong, bool> _pendingAutomationToggleStates = new Dictionary<ulong, bool>();
private void RefreshEffectiveScheduling(bool allowWriteToConfig)
{
    if (_config == null) return;

    _effAdaptiveScaling = _config.AdaptiveScaling;
    _effAdaptiveMinOvensPerTick = _config.AdaptiveMinOvensPerTick;
    _effAdaptiveMaxOvensPerTick = _config.AdaptiveMaxOvensPerTick;

    _effDynamicTickInterval = _config.DynamicTickInterval;
    _effDynamicMinGlobalLoopInterval = _config.DynamicMinGlobalLoopInterval;
    _effDynamicMaxGlobalLoopInterval = _config.DynamicMaxGlobalLoopInterval;
    _effDynamicLowOvenCount = _config.DynamicLowOvenCount;
    _effDynamicHighOvenCount = _config.DynamicHighOvenCount;

    _effFixedGlobalLoopInterval = _config.FixedGlobalLoopInterval;

    if (!_config.AutoTuneEnabled)
        return;

    int pop = Mathf.Clamp(_config.AveragePopulation, 0, 5000);
    int[] anchors = { 10, 25, 50, 100, 200, 300, 400, 500 };
    int anchor = anchors[0];
    int bestDist = Math.Abs(pop - anchor);
    for (int i = 1; i < anchors.Length; i++)
    {
        int d = Math.Abs(pop - anchors[i]);
        if (d < bestDist) { bestDist = d; anchor = anchors[i]; }
    }

    int baseMin;
    int baseMax;
    float baseMinInterval;
    float baseMaxInterval;
    int baseLowCount;
    int baseHighCount;
    float baseFixed;

    switch (anchor)
    {
        case 10:
            baseMin = 300; baseMax = 900; baseMinInterval = 0.05f; baseMaxInterval = 0.15f; baseLowCount = 50; baseHighCount = 300; baseFixed = 0.15f;
            break;
        case 25:
            baseMin = 250; baseMax = 900; baseMinInterval = 0.07f; baseMaxInterval = 0.18f; baseLowCount = 75; baseHighCount = 400; baseFixed = 0.18f;
            break;
        case 50:
            baseMin = 200; baseMax = 800; baseMinInterval = 0.10f; baseMaxInterval = 0.22f; baseLowCount = 100; baseHighCount = 600; baseFixed = 0.22f;
            break;
        case 100:
            baseMin = 160; baseMax = 700; baseMinInterval = 0.12f; baseMaxInterval = 0.25f; baseLowCount = 150; baseHighCount = 900; baseFixed = 0.25f;
            break;
        case 200:
            baseMin = 140; baseMax = 650; baseMinInterval = 0.14f; baseMaxInterval = 0.28f; baseLowCount = 200; baseHighCount = 1100; baseFixed = 0.28f;
            break;
        case 300:
            baseMin = 120; baseMax = 600; baseMinInterval = 0.15f; baseMaxInterval = 0.32f; baseLowCount = 250; baseHighCount = 1300; baseFixed = 0.32f;
            break;
        case 400:
            baseMin = 110; baseMax = 550; baseMinInterval = 0.16f; baseMaxInterval = 0.35f; baseLowCount = 300; baseHighCount = 1500; baseFixed = 0.35f;
            break;
        default:
            baseMin = 100; baseMax = 500; baseMinInterval = 0.18f; baseMaxInterval = 0.40f; baseLowCount = 350; baseHighCount = 1700; baseFixed = 0.40f;
            break;
    }

    float ovensFactor = 1.0f;
    float intervalFactor = 1.0f;

    switch ((_config.Preset ?? "10x").ToLowerInvariant())
    {
        case "2x": ovensFactor = 1.15f; intervalFactor = 0.90f; break;
        case "3x": ovensFactor = 1.10f; intervalFactor = 0.92f; break;
        case "5x": ovensFactor = 1.05f; intervalFactor = 0.95f; break;
        case "10x": ovensFactor = 1.00f; intervalFactor = 1.00f; break;
        case "25x": ovensFactor = 0.85f; intervalFactor = 1.10f; break;
        case "50x": ovensFactor = 0.75f; intervalFactor = 1.20f; break;
        case "100x": ovensFactor = 0.65f; intervalFactor = 1.30f; break;
        case "1000x": ovensFactor = 0.45f; intervalFactor = 1.60f; break;
        case "instant": ovensFactor = 0.35f; intervalFactor = 1.80f; break;
    }

    string bias = (_config.AutoTuneBias ?? "Balanced").Trim();
    if (bias.Equals("Responsiveness", StringComparison.OrdinalIgnoreCase))
    {
        ovensFactor *= 1.10f;
        intervalFactor *= 0.90f;
    }
    else if (bias.Equals("Performance", StringComparison.OrdinalIgnoreCase))
    {
        ovensFactor *= 0.90f;
        intervalFactor *= 1.10f;
    }

    int tunedMin = Mathf.Clamp(Mathf.RoundToInt(baseMin * ovensFactor), 25, 5000);
    int tunedMax = Mathf.Clamp(Mathf.RoundToInt(baseMax * ovensFactor), tunedMin, 10000);

    float tunedMinInterval = Mathf.Clamp(baseMinInterval * intervalFactor, 0.03f, 2f);
    float tunedMaxInterval = Mathf.Clamp(baseMaxInterval * intervalFactor, tunedMinInterval, 2f);
    float tunedFixed = Mathf.Clamp(baseFixed * intervalFactor, 0.03f, 2f);

    _effAdaptiveScaling = true;
    _effAdaptiveMinOvensPerTick = tunedMin;
    _effAdaptiveMaxOvensPerTick = tunedMax;
    _effDynamicTickInterval = true;
    _effDynamicMinGlobalLoopInterval = tunedMinInterval;
    _effDynamicMaxGlobalLoopInterval = tunedMaxInterval;
    _effDynamicLowOvenCount = baseLowCount;
    _effDynamicHighOvenCount = baseHighCount;
    _effFixedGlobalLoopInterval = tunedFixed;

    if (!allowWriteToConfig || !_config.AutoTuneWriteToConfig)
        return;

    bool changed = false;
    if (_config.AdaptiveScaling != _effAdaptiveScaling) { _config.AdaptiveScaling = _effAdaptiveScaling; changed = true; }
    if (_config.AdaptiveMinOvensPerTick != _effAdaptiveMinOvensPerTick) { _config.AdaptiveMinOvensPerTick = _effAdaptiveMinOvensPerTick; changed = true; }
    if (_config.AdaptiveMaxOvensPerTick != _effAdaptiveMaxOvensPerTick) { _config.AdaptiveMaxOvensPerTick = _effAdaptiveMaxOvensPerTick; changed = true; }

    if (_config.DynamicTickInterval != _effDynamicTickInterval) { _config.DynamicTickInterval = _effDynamicTickInterval; changed = true; }
    if (!Mathf.Approximately(_config.DynamicMinGlobalLoopInterval, _effDynamicMinGlobalLoopInterval)) { _config.DynamicMinGlobalLoopInterval = _effDynamicMinGlobalLoopInterval; changed = true; }
    if (!Mathf.Approximately(_config.DynamicMaxGlobalLoopInterval, _effDynamicMaxGlobalLoopInterval)) { _config.DynamicMaxGlobalLoopInterval = _effDynamicMaxGlobalLoopInterval; changed = true; }
    if (_config.DynamicLowOvenCount != _effDynamicLowOvenCount) { _config.DynamicLowOvenCount = _effDynamicLowOvenCount; changed = true; }
    if (_config.DynamicHighOvenCount != _effDynamicHighOvenCount) { _config.DynamicHighOvenCount = _effDynamicHighOvenCount; changed = true; }

    if (!Mathf.Approximately(_config.FixedGlobalLoopInterval, _effFixedGlobalLoopInterval)) { _config.FixedGlobalLoopInterval = _effFixedGlobalLoopInterval; changed = true; }

    if (changed)
        SaveConfig();

    RefreshCachedPreset();
}

private void QueueFuelRecalcNextTick(BaseOven oven, BasePlayer player)
{
    if (!_config.Enabled) return;
    if (!_config.AutoPullFuelFromPlayer) return;
    if (oven == null || oven.IsDestroyed) return;
    if (player == null || !player.IsConnected) return;

    ulong id = 0ul;
    if (oven.net != null)
        id = oven.net.ID.Value;

    if (id == 0ul)
    {
        NextTick(() =>
        {
            if (oven == null || oven.IsDestroyed) return;
            if (player == null || !player.IsConnected) return;
            TryAutoPullFuel(oven, player);
        });
        return;
    }

    if (_pendingFuelRecalc.Add(id))
    {
        NextTick(() =>
        {
            _pendingFuelRecalc.Remove(id);

            if (oven == null || oven.IsDestroyed) return;
            if (player == null || !player.IsConnected) return;

            TryAutoPullFuel(oven, player);
        });
    }
}

#endregion

        #region Presets

        private struct PresetTuning
        {
            public float CycleSeconds;
            public int MaxTotalConsumedPerCycle;
            public int MaxConsumedPerStackPerCycle;

            public float BaselineWoodPerSecond;
        }

        private PresetTuning GetPreset(string preset)
        {

            const float baselineWoodSmall = 0.5f;
            const float baselineWoodLarge = 1.0f;
            const float baselineWoodOther = 0.5f;

            switch ((preset ?? "10x").ToLowerInvariant())
            {
                case "2x":
                    return new PresetTuning { CycleSeconds = 0.5f, MaxTotalConsumedPerCycle = 20, MaxConsumedPerStackPerCycle = 10, BaselineWoodPerSecond = baselineWoodOther };
                case "3x":
                    return new PresetTuning { CycleSeconds = 0.5f, MaxTotalConsumedPerCycle = 30, MaxConsumedPerStackPerCycle = 15, BaselineWoodPerSecond = baselineWoodOther };
                case "5x":
                    return new PresetTuning { CycleSeconds = 0.5f, MaxTotalConsumedPerCycle = 50, MaxConsumedPerStackPerCycle = 25, BaselineWoodPerSecond = baselineWoodOther };
                case "10x":
                    return new PresetTuning { CycleSeconds = 0.5f, MaxTotalConsumedPerCycle = 100, MaxConsumedPerStackPerCycle = 50, BaselineWoodPerSecond = baselineWoodOther };
case "25x":
                    return new PresetTuning { CycleSeconds = 0.25f, MaxTotalConsumedPerCycle = 500, MaxConsumedPerStackPerCycle = 250, BaselineWoodPerSecond = baselineWoodOther };
case "50x":
                    return new PresetTuning { CycleSeconds = 0.2f, MaxTotalConsumedPerCycle = 1000, MaxConsumedPerStackPerCycle = 500, BaselineWoodPerSecond = baselineWoodOther };
case "instant":
                    return new PresetTuning { CycleSeconds = 0.1f, MaxTotalConsumedPerCycle = int.MaxValue, MaxConsumedPerStackPerCycle = int.MaxValue, BaselineWoodPerSecond = baselineWoodOther };
                case "100x":
                    return new PresetTuning { CycleSeconds = 0.1f, MaxTotalConsumedPerCycle = 2000, MaxConsumedPerStackPerCycle = 1000, BaselineWoodPerSecond = baselineWoodOther };
                case "1000x":
                    return new PresetTuning { CycleSeconds = 0.05f, MaxTotalConsumedPerCycle = 20000, MaxConsumedPerStackPerCycle = 10000, BaselineWoodPerSecond = baselineWoodOther };
                default:
                    return new PresetTuning { CycleSeconds = 0.5f, MaxTotalConsumedPerCycle = 100, MaxConsumedPerStackPerCycle = 50, BaselineWoodPerSecond = baselineWoodOther };
            }
        }

        private int GetMultiplier(string preset)
        {
            switch ((preset ?? "10x").ToLowerInvariant())
            {
                case "2x": return 2;
                case "3x": return 3;
                case "5x": return 5;
                case "10x": return 10;
                case "25x": return 25;
                case "50x": return 50;
                case "instant": return 1000000;
                case "100x": return 100;
                case "1000x": return 1000;
                default: return 10;
            }
        }

        private string _cachedPresetKey = null;
        private PresetTuning _cachedPresetTuning;
        private int _cachedPresetMultiplier = 10;
        private bool _cachedIsInstantPreset = false;

        private void RefreshCachedPreset()
        {

            string preset = (_config?.Preset ?? "10x").Trim();
            string key = preset.ToLowerInvariant();

            if (key == _cachedPresetKey)
                return;

            _cachedPresetKey = key;
            _cachedPresetTuning = GetPreset(preset);
            _cachedPresetMultiplier = GetMultiplier(preset);
            _cachedIsInstantPreset = string.Equals(key, "instant", StringComparison.Ordinal);
        }

        private PresetTuning GetEffectivePresetForKind(PresetTuning basePreset, OvenKind kind)
        {
            if (_config == null || kind != OvenKind.ElectricFurnace || !_config.EnableElectricFurnaceNativeScaling)
                return basePreset;

            if (basePreset.MaxTotalConsumedPerCycle == int.MaxValue || basePreset.MaxConsumedPerStackPerCycle == int.MaxValue)
                return basePreset;

            var tuned = basePreset;
            tuned.CycleSeconds = Mathf.Clamp(basePreset.CycleSeconds * _config.ElectricFurnaceCycleSpeedScale, 0.02f, 2f);

            long totalScaled = (long)Mathf.RoundToInt(basePreset.MaxTotalConsumedPerCycle * _config.ElectricFurnaceThroughputScale);
            long stackScaled = (long)Mathf.RoundToInt(basePreset.MaxConsumedPerStackPerCycle * _config.ElectricFurnaceThroughputScale);

            if (totalScaled < 1L) totalScaled = 1L;
            if (stackScaled < 1L) stackScaled = 1L;
            if (totalScaled > int.MaxValue) totalScaled = int.MaxValue;
            if (stackScaled > int.MaxValue) stackScaled = int.MaxValue;

            tuned.MaxTotalConsumedPerCycle = (int)totalScaled;
            tuned.MaxConsumedPerStackPerCycle = (int)stackScaled;
            return tuned;
        }

        #endregion

        #region State

        private readonly Dictionary<ulong, OvenTracker> _active = new Dictionary<ulong, OvenTracker>();

        private int _activeVersion = 0;
        private int _tmpTrackerIdsBuiltForVersion = -1;

        private void MarkActiveChanged()
        {
            _activeVersion++;
            _tmpTrackerIdsBuiltForVersion = -1;
            if (_activeVersion == int.MaxValue) _activeVersion = 0;
        }

        private Timer _globalTimer;

        private float _currentGlobalLoopInterval = 0.25f;
        private bool _rescheduleQueued;

        private readonly List<ulong> _tmpTrackerIds = new List<ulong>(256);

        private const int DefaultMaxOvensPerGlobalTick = 200;
        private int _globalCursor = 0;

        private int GetOvensPerGlobalTickCap(int trackedCount)
        {
            if (!_effAdaptiveScaling) return DefaultMaxOvensPerGlobalTick;
            int cap = trackedCount;
            if (cap < _effAdaptiveMinOvensPerTick) cap = _effAdaptiveMinOvensPerTick;
            if (cap > _effAdaptiveMaxOvensPerTick) cap = _effAdaptiveMaxOvensPerTick;
            return cap;
        }

        private float ComputeGlobalLoopInterval(int trackedCount)
        {
            if (_config == null) return 0.25f;

            if (!_effDynamicTickInterval)
                return Mathf.Clamp(_effFixedGlobalLoopInterval, 0.05f, 2f);

            float min = Mathf.Clamp(_effDynamicMinGlobalLoopInterval, 0.05f, 2f);
            float max = Mathf.Clamp(_effDynamicMaxGlobalLoopInterval, 0.05f, 2f);
            if (max < min) { var t = min; min = max; max = t; }

            int low = Math.Max(0, _effDynamicLowOvenCount);
            int high = Math.Max(low + 1, _effDynamicHighOvenCount);

            float t01 = Mathf.InverseLerp(low, high, trackedCount);
            return Mathf.Lerp(min, max, t01);
        }

        private void EnsureGlobalTimer(float interval)
        {
            interval = Mathf.Clamp(interval, 0.05f, 2f);

            if (_globalTimer == null)
            {
                _currentGlobalLoopInterval = interval;
                _globalTimer = timer.Every(_currentGlobalLoopInterval, GlobalTick);
                return;
            }

            if (Mathf.Abs(_currentGlobalLoopInterval - interval) < 0.05f)
                return;

            if (_rescheduleQueued)
                return;

            _rescheduleQueued = true;
            timer.Once(0f, () =>
            {
                _rescheduleQueued = false;
                _globalTimer?.Destroy();
                _globalTimer = null;
                _currentGlobalLoopInterval = interval;
                _globalTimer = timer.Every(_currentGlobalLoopInterval, GlobalTick);
            });
        }

        private const string PermAdmin = "smartsmelt.admin";
        private const string PermDebug = "smartsmelt.debug";

private class OvenTracker
{
    public BaseOven Oven;
    public OvenKind Kind;
    public bool GateOnWood;
    public float NextTickAt;
    public int Cycles;
    public int OffCycles;
    public float CharcoalRemainder;
    public float FuelDebt;
    public float LastBalanceTime;
    public readonly List<Item> InputsBuffer = new List<Item>(8);
}

        #endregion

        #region Hooks

private void OnServerInitialized()
{
    RefreshEffectiveScheduling(allowWriteToConfig: true);

    _active.Clear(); MarkActiveChanged();
    permission.RegisterPermission(PermAdmin, this);
    permission.RegisterPermission(PermDebug, this);
    _globalTimer?.Destroy();
    _globalTimer = null;
    EnsureGlobalTimer(ComputeGlobalLoopInterval(0));
    timer.Once(1f, ScanAndTrackRunningOvens);
}

private void Unload()
{
    _globalTimer?.Destroy();
    _globalTimer = null;
    _active.Clear(); MarkActiveChanged();
}
        private void OnEntityKill(BaseNetworkable ent)
        {
            var oven = ent as BaseOven;
            if (oven == null) return;
            StopTracking(oven);
        }

        private void OnOvenToggle(BaseOven oven)
        {
            OnOvenToggle(oven, null);
        }

        private void OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (!_config.Enabled || oven == null) return;

            if (!IsWhitelistedSmeltingOven(oven))
            {
                if (_config.Debug)
                    Puts($"Ignoring oven (not whitelisted): {oven.ShortPrefabName}");
                return;
            }

            NextTick(() =>
            {
                if (oven == null || oven.IsDestroyed) return;

                if (oven.IsOn())
                {
                    StartTracking(oven);

                    if (_config.ForceStartCookingOnToggle)
                    {
                        timer.Once(0.2f, () =>
                        {
                            if (oven == null || oven.IsDestroyed) return;
                            if (!oven.IsOn())
                            {
                                if (_config.Debug)
                                    Puts("Toggle ON but oven still OFF after 0.2s. Attempting StartCooking().");
                                TryStartCooking(oven);
                            }
                        });
                    }
                }
                else
                {
                    StopTracking(oven);
                }
            });
        }

private void OnEntityFlagsChanged(BaseEntity entity, BaseEntity.Flags flag, bool oldState, bool newState)
{
    if (!_config.Enabled || entity == null) return;
    if (flag != BaseEntity.Flags.On) return;
    if (oldState == newState) return;
    HandleAutomationOvenToggle(entity, newState);
}

private void OnEntityFlagsChanged(BaseEntity entity, BaseEntity.Flags oldFlags, BaseEntity.Flags newFlags)
{
    if (!_config.Enabled || entity == null) return;
    bool oldOn = (oldFlags & BaseEntity.Flags.On) != 0;
    bool newOn = (newFlags & BaseEntity.Flags.On) != 0;
    if (oldOn == newOn) return;

    HandleAutomationOvenToggle(entity, newOn);
}
private void HandleAutomationOvenToggle(BaseEntity entity, bool newOnState)
{
    var oven = entity as BaseOven;
    if (oven == null) return;
    if (!IsWhitelistedSmeltingOven(oven)) return;
    ulong id = oven.net != null ? oven.net.ID.Value : 0ul;
    if (id == 0ul)
    {
        NextTick(() =>
        {
            if (oven == null || oven.IsDestroyed) return;

            if (newOnState && oven.IsOn())
                StartTracking(oven);
            else if (!newOnState && !oven.IsOn())
                StopTracking(oven);
        });
        return;
    }

    if (_pendingAutomationToggleStates.ContainsKey(id))
    {
        _pendingAutomationToggleStates[id] = newOnState;
        return;
    }
    _pendingAutomationToggleStates[id] = newOnState;
    NextTick(() =>
    {
        if (!_pendingAutomationToggleStates.TryGetValue(id, out var desiredState))
            return;

        _pendingAutomationToggleStates.Remove(id);

        if (oven == null || oven.IsDestroyed) return;

        if (desiredState && oven.IsOn())
            StartTracking(oven);
        else if (!desiredState && !oven.IsOn())
            StopTracking(oven);
    });
}
private void OnOvenCook(BaseOven oven)
{
    EnsureTrackedFromCook(oven);
}
private void OnOvenCook(BaseOven oven, Item fuel, ItemModBurnable burnable)
{
    EnsureTrackedFromCook(oven);
}
private void EnsureTrackedFromCook(BaseOven oven)
{
    if (!_config.Enabled || oven == null || oven.IsDestroyed) return;
    if (!oven.IsOn()) return;
    if (!IsWhitelistedSmeltingOven(oven)) return;
    StartTracking(oven);
}
        #region Commands
        [ChatCommand("ss.debug")]
        private void CmdSmartSmeltDebug(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!permission.UserHasPermission(player.UserIDString, PermDebug))
            {
                player.ChatMessage(Msg("NoPermission", player.UserIDString));
                return;
            }
            int tracked = _active?.Count ?? 0;
            int cap = GetOvensPerGlobalTickCap(tracked);
            float desiredInterval = ComputeGlobalLoopInterval(tracked);
            string presetName = _config?.Preset ?? "unknown";
            int mult = GetMultiplier(presetName);
            player.ChatMessage(string.Format(Msg("DebugTitle", player.UserIDString), Version));
            player.ChatMessage(string.Format(Msg("DebugLine1", player.UserIDString), _config?.Enabled ?? false, presetName, mult));
            player.ChatMessage(string.Format(Msg("DebugLine2", player.UserIDString), tracked, cap, _effAdaptiveScaling));
            player.ChatMessage(string.Format(Msg("DebugLine3", player.UserIDString), _currentGlobalLoopInterval, desiredInterval, _config?.DynamicTickInterval ?? false));
        }

        [ConsoleCommand("ss.debug")]
        private void CCmdSmartSmeltDebug(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermDebug))
            {
                arg.ReplyWith(Msg("NoPermission", player.UserIDString));
                return;
            }
            int tracked = _active?.Count ?? 0;
            int cap = GetOvensPerGlobalTickCap(tracked);
            float desiredInterval = ComputeGlobalLoopInterval(tracked);
            string presetName = _config?.Preset ?? "unknown";
            int mult = GetMultiplier(presetName);
            arg.ReplyWith(string.Format(Msg("ConsoleDebug"), Version, _config?.Enabled ?? false, presetName, mult, tracked, cap, _currentGlobalLoopInterval, desiredInterval));
        }
        [ChatCommand("ss.info")]
        private void CmdSmartSmeltInfo(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var desiredInterval = ComputeGlobalLoopInterval(_active.Count);
            var cap = GetOvensPerGlobalTickCap(_active.Count);

            SendReply(player, string.Format(Msg("InfoLine1", player.UserIDString), Version, _config.Preset, _config.Enabled, _config.EnableOreSplitting, _config.AutoPullFuelFromPlayer, _config.AutoPullFuelBufferPercent));
            SendReply(player, string.Format(Msg("InfoLine2", player.UserIDString), _config.AutoTuneEnabled, _config.AveragePopulation, _config.AutoTuneBias, _config.AutoTuneWriteToConfig));
            SendReply(player, string.Format(Msg("InfoLine3", player.UserIDString), _active.Count, cap, _effAdaptiveScaling, _currentGlobalLoopInterval, desiredInterval, _effDynamicTickInterval));
        }
        [ConsoleCommand("ss.info")]
        private void ConsoleSmartSmeltInfo(ConsoleSystem.Arg arg)
        {
            var desiredInterval = ComputeGlobalLoopInterval(_active.Count);
            var cap = GetOvensPerGlobalTickCap(_active.Count);

            arg.ReplyWith(string.Format(Msg("ConsoleInfo"), Version, _config.Preset, _config.Enabled, _config.AutoPullFuelFromPlayer, _config.AutoPullFuelBufferPercent, _active.Count, cap, _effAdaptiveScaling, _currentGlobalLoopInterval, desiredInterval, _effDynamicTickInterval));
        }

[ChatCommand("smeltstats")]
        private void CmdSmeltStats(BasePlayer player, string command, string[] args)
        {

            CmdSmartSmeltDebug(player, "ss.debug", args);
        }

        [ConsoleCommand("smeltstats")]
        private void ConsoleSmeltStats(ConsoleSystem.Arg arg)
        {

            CCmdSmartSmeltDebug(arg);
        }

        #endregion

object CanMoveItem(Item item, PlayerInventory inventory, ItemContainerId targetContainerId, int targetSlotIndex, int splitAmount)
{
    try
    {
        if (!_config.Enabled) return null;
        if (item?.info == null || inventory == null) return null;

        var player = inventory.GetComponent<BasePlayer>();
        if (player == null) return null;

        var oven = inventory.loot?.entitySource as BaseOven;
        if (oven == null || oven.IsDestroyed) return null;
        if (!IsWhitelistedSmeltingOven(oven)) return null;

        var kind = GetKind(oven);
        if (kind == OvenKind.Unknown) return null;

        var targetContainer = inventory.FindContainer(targetContainerId);
        if (targetContainer != null && !(targetContainer.entityOwner is BaseOven)) return null;

        var original = item.GetRootContainer();
        if (original == null || (original.entityOwner is BaseOven)) return null;

        string sn = item.info.shortname;

        if (kind == OvenKind.SmallRefinery)
        {
            if (sn != "crude.oil") return null;
            return HandleRefineryCrudeInsertion(oven, item, splitAmount, player);
        }

        if (sn != "metal.ore" && sn != "sulfur.ore" && sn != "hq.metal.ore") return null;

        if (!_config.EnableOreSplitting)
        {
            QueueFuelRecalcNextTick(oven, player);
            return null;
        }

        return DistributeOreIntoInputSlots(oven, item, splitAmount, player);
    }
    catch (Exception ex)
    {
        if (_config != null && _config.Debug)
            PrintWarning($"CanMoveItem(ItemContainerId) exception: {ex}");
        return null;
    }
}

private void TryAutoPullFuel(BaseOven oven, BasePlayer player)
{
    if (!_config.Enabled) return;
    if (!_config.AutoPullFuelFromPlayer) return;
    if (oven == null || oven.IsDestroyed) return;
    if (player == null || !player.IsConnected) return;

    var kind = GetKind(oven);
    string fuelShortname = GetFuelShortname(kind);
    if (string.IsNullOrEmpty(fuelShortname)) return;

    var container = oven.inventory;
    if (container == null) return;

    int requiredFuel = CalculateRequiredFuelForCurrentInput(oven, kind);
    if (requiredFuel > 0)
    {
        float pct = Mathf.Max(0f, _config.AutoPullFuelBufferPercent) / 100f;
        if (pct > 0f) requiredFuel = Mathf.CeilToInt(requiredFuel * (1f + pct));
    }
    if (requiredFuel <= 0) return;

    var fuelScan = ScanFuelSlots(container, fuelShortname, kind);
    int need = requiredFuel - fuelScan.CurrentFuelAmount;
    if (need <= 0) return;
    if (fuelScan.RemainingCapacity <= 0) return;

    int available = CountPlayerItem(player, fuelShortname);
    if (available <= 0) return;

    int toMove = Mathf.Min(need, available);
    toMove = Mathf.Min(toMove, fuelScan.RemainingCapacity);
    if (toMove <= 0) return;

    int moved = PullFuelIntoSlots(player, container, fuelShortname, toMove, fuelScan.Slots);
    if (moved > 0)
    {
        container.MarkDirty();
        oven.SendNetworkUpdateImmediate();

        player.inventory?.ServerUpdate(0f);
        player.SendNetworkUpdateImmediate();
    }
}

private sealed class FuelSlotScanResult
{
    public readonly List<int> Slots = new List<int>(2);
    public int CurrentFuelAmount;
    public int RemainingCapacity;
}

private FuelSlotScanResult ScanFuelSlots(ItemContainer container, string fuelShortname, OvenKind kind)
{
    var result = new FuelSlotScanResult();
    if (container == null || string.IsNullOrEmpty(fuelShortname)) return result;

    int desired = kind == OvenKind.LargeFurnace ? 2 : 1;
    int stackable = GetStackableAmount(fuelShortname);
    if (stackable <= 0) stackable = 1000;

    if (container.itemList != null)
    {
        for (int i = 0; i < container.itemList.Count; i++)
        {
            var it = container.itemList[i];
            if (it?.info == null) continue;
            if (!string.Equals(it.info.shortname, fuelShortname, StringComparison.Ordinal)) continue;

            result.CurrentFuelAmount += it.amount;
            if (!result.Slots.Contains(it.position))
                result.Slots.Add(it.position);
        }
    }

    if (container.capacity > 0 && result.Slots.Count < desired && !result.Slots.Contains(0))
        result.Slots.Add(0);
    if (kind == OvenKind.LargeFurnace && container.capacity > 1 && result.Slots.Count < desired && !result.Slots.Contains(1))
        result.Slots.Add(1);

    if (result.Slots.Count == 0)
        result.Slots.Add(-1);

    for (int i = 0; i < result.Slots.Count; i++)
    {
        int pos = result.Slots[i];
        var existing = pos >= 0 ? container.GetSlot(pos) : null;
        int existingAmt = existing != null && existing.info != null && string.Equals(existing.info.shortname, fuelShortname, StringComparison.Ordinal)
            ? existing.amount
            : 0;
        result.RemainingCapacity += Mathf.Max(0, stackable - existingAmt);
    }

    return result;
}

private int CountPlayerItem(BasePlayer player, string shortname)
{
    if (player == null || string.IsNullOrEmpty(shortname)) return 0;
    return CountItem(player.inventory?.containerBelt, shortname) + CountItem(player.inventory?.containerMain, shortname);
}

private string GetFuelShortname(OvenKind kind)
{
    switch (kind)
    {
        case OvenKind.SmallFurnace:
        case OvenKind.LargeFurnace:
            return "wood";
        case OvenKind.SmallRefinery:
            return "wood";
        default:

            return null;
    }
}

private int GetStackableAmount(string shortname)
{
    if (string.IsNullOrEmpty(shortname)) return 0;
    var def = ItemManager.FindItemDefinition(shortname);
    return def != null ? def.stackable : 0;
}

private int CalculateRequiredFuelForCurrentInput(BaseOven oven, OvenKind kind)
{
    var container = oven?.inventory;
    if (container == null) return 0;

    int minSlot = oven._inputSlotIndex;
    int maxSlot = oven._inputSlotIndex + oven.inputSlots - 1;
    if (minSlot < 0) minSlot = 0;
    if (maxSlot >= container.capacity) maxSlot = container.capacity - 1;

    int metal = 0, sulfur = 0, hqm = 0, crude = 0;

    for (int i = minSlot; i <= maxSlot; i++)
    {
        var it = container.GetSlot(i);
        if (it?.info == null || it.amount <= 0) continue;

        string sn = it.info.shortname;
        if (sn == "metal.ore") metal += it.amount;
        else if (sn == "sulfur.ore") sulfur += it.amount;
        else if (sn == "hq.metal.ore") hqm += it.amount;
        else if (sn == "crude.oil") crude += it.amount;
    }

    float required = 0f;

    if (kind == OvenKind.SmallRefinery)
    {

        const float refineryCompletionOverheadWoodPerCrude = 0.02f;
        required += crude * ((20f / 9f) + refineryCompletionOverheadWoodPerCrude);
    }
    else if (kind == OvenKind.LargeFurnace)
    {

        required += metal * (1f / 3f);
        required += sulfur * (1f / 6f);
        required += hqm * (2f / 3f);
    }
    else
    {

        required += metal * (5f / 3f);
        required += sulfur * (5f / 6f);
        required += hqm * (10f / 3f);
    }

    return Mathf.CeilToInt(required);
}

private List<int> GetFuelSlotPositions(ItemContainer container, string fuelShortname, OvenKind kind)
{
    var slots = new List<int>();
    if (container == null) return slots;

    if (container.itemList != null)
    {
        for (int i = 0; i < container.itemList.Count; i++)
        {
            var it = container.itemList[i];
            if (it?.info == null) continue;
            if (!string.Equals(it.info.shortname, fuelShortname, StringComparison.Ordinal)) continue;
            if (!slots.Contains(it.position)) slots.Add(it.position);
        }
    }

    int desired = (kind == OvenKind.LargeFurnace) ? 2 : 1;

    if (container.capacity > 0 && slots.Count < desired && !slots.Contains(0)) slots.Add(0);
    if (kind == OvenKind.LargeFurnace && container.capacity > 1 && slots.Count < desired && !slots.Contains(1)) slots.Add(1);

    if (slots.Count == 0) slots.Add(-1);
    return slots;
}

private int PullFuelIntoSlots(BasePlayer player, ItemContainer to, string fuelShortname, int amount, List<int> fuelSlots)
{
    if (player == null || to == null) return 0;
    if (amount <= 0) return 0;

    int slotCount = Mathf.Max(1, fuelSlots?.Count ?? 0);

    int baseAmt = amount / slotCount;
    int rem = amount - baseAmt * slotCount;

    int movedTotal = 0;

    for (int si = 0; si < slotCount && movedTotal < amount; si++)
    {
        int want = baseAmt + (si < rem ? 1 : 0);
        if (want <= 0) continue;

        int pos = fuelSlots[si];

        int moved = 0;
        moved += PullFromContainer(player.inventory.containerBelt, to, fuelShortname, want, pos);
        moved += PullFromContainer(player.inventory.containerMain, to, fuelShortname, want - moved, pos);

        movedTotal += moved;
    }

    return movedTotal;
}

private int PullFromContainer(ItemContainer from, ItemContainer to, string shortname, int amount, int preferredPosition = -1)
{
    if (from == null || to == null) return 0;
    if (amount <= 0) return 0;

    int moved = 0;

    var snapshot = new List<Item>(from.itemList);

    for (int i = 0; i < snapshot.Count && moved < amount; i++)
    {
        var it = snapshot[i];
        if (it == null || it.amount <= 0 || it.info == null) continue;
        if (!string.Equals(it.info.shortname, shortname, StringComparison.Ordinal)) continue;

        int take = Mathf.Min(it.amount, amount - moved);
        if (take <= 0) continue;

        Item moving = it;
        if (take < it.amount)
        {
            moving = it.SplitItem(take);
            if (moving == null) continue;
        }

        if (preferredPosition >= 0)
        {

            if (!moving.MoveToContainer(to, preferredPosition, true) && !moving.MoveToContainer(to))
            {

                if (moving != it)
                {
                    it.amount += moving.amount;
                    moving.Remove();
                }
                break;
            }
        }
        else
        {
            if (!moving.MoveToContainer(to))
            {

            if (moving != it)
            {
                it.amount += moving.amount;
                moving.Remove();
            }
            break;
            }
        }

        moved += take;
    }

    return moved;
}

private int GetRefineryFinishableCrudeAmount(BaseOven oven, BasePlayer player)
{
    if (oven == null || oven.IsDestroyed) return 0;

    var container = oven.inventory;
    if (container == null) return 0;
    const float woodPerCrude = 20f / 9f;
    const float refineryCompletionOverheadWoodPerCrude = 0.02f;
    float pct = Mathf.Max(0f, _config.AutoPullFuelBufferPercent) / 100f;
    float effectiveWoodPerCrude = (woodPerCrude + refineryCompletionOverheadWoodPerCrude) * (1f + pct);
    if (effectiveWoodPerCrude <= 0f) return 0;
    var fuelScan = ScanFuelSlots(container, "wood", OvenKind.SmallRefinery);
    int availableWood = CountPlayerItem(player, "wood");
    int pullableWood = Mathf.Min(availableWood, fuelScan.RemainingCapacity);
    int totalWoodPotential = fuelScan.CurrentFuelAmount + pullableWood;
    if (totalWoodPotential <= 0) return 0;

    return Mathf.Max(0, Mathf.FloorToInt(totalWoodPotential / effectiveWoodPerCrude));
}

private object HandleRefineryCrudeInsertion(BaseOven oven, Item item, int splitAmount, BasePlayer actorPlayer)
{
    try
    {
        var container = oven?.inventory;
        if (container == null || item?.info == null) return null;
        if (!string.Equals(item.info.shortname, "crude.oil", StringComparison.Ordinal)) return null;

        int minSlot = oven._inputSlotIndex;
        int maxSlot = oven._inputSlotIndex + oven.inputSlots - 1;
        if (minSlot < 0) minSlot = 0;
        if (maxSlot >= container.capacity) maxSlot = container.capacity - 1;

        int existingCrude = 0;
        for (int i = minSlot; i <= maxSlot; i++)
        {
            var it = container.GetSlot(i);
            if (it == null) continue;

            var isn = it.info?.shortname;
            if (isn == null) continue;
            if (!string.Equals(isn, "crude.oil", StringComparison.Ordinal))
                return null;

            existingCrude += it.amount;
        }

        int itemAmount = (splitAmount > 0) ? Math.Min(splitAmount, item.amount) : item.amount;
        if (itemAmount <= 0) return null;

        int maxFinishableTotalCrude = GetRefineryFinishableCrudeAmount(oven, actorPlayer);
        int maxAdditionalByFuel = Math.Max(0, maxFinishableTotalCrude - existingCrude);
        if (maxAdditionalByFuel <= 0)
        {
            if (actorPlayer != null)
                TryAutoPullFuel(oven, actorPlayer);
            return true;
        }

        int slots = Math.Max(1, oven.inputSlots);
        long capLong = (long)Math.Max(0, item.info.stackable) * (long)slots;
        if (capLong > int.MaxValue) capLong = int.MaxValue;
        int cap = (int)capLong;

        int maxAdditionalBySpace = Math.Max(0, cap - existingCrude);
        int allowedToMove = Math.Min(itemAmount, Math.Min(maxAdditionalByFuel, maxAdditionalBySpace));
        if (allowedToMove <= 0)
        {
            if (actorPlayer != null)
                TryAutoPullFuel(oven, actorPlayer);
            return true;
        }

        int totalAmount = existingCrude + allowedToMove;
        int baseAmt = totalAmount / slots;
        int rem = totalAmount - baseAmt * slots;

        int totalMoved = 0;
        for (int si = 0; si < slots; si++)
        {
            int slotIndex = minSlot + si;
            if (slotIndex > maxSlot) break;

            int target = baseAmt + (si < rem ? 1 : 0);
            var cur = container.GetSlot(slotIndex);

            int curAmt = 0;
            if (cur != null)
            {
                if (cur.info != item.info) return null;
                curAmt = cur.amount;
            }

            int delta = target - curAmt;
            if (delta <= 0) continue;

            if (cur == null)
            {
                var newItem = ItemManager.Create(item.info, delta, item.skin);
                if (newItem == null) continue;
                if (!newItem.MoveToContainer(container, slotIndex, allowStack: false))
                {
                    newItem.Remove();
                    continue;
                }
            }
            else
            {
                cur.amount += delta;
                cur.MarkDirty();
            }

            totalMoved += delta;
            if (totalMoved >= allowedToMove) break;
        }

        if (totalMoved <= 0) return null;

        var ownerPlayer = actorPlayer ?? item.GetOwnerPlayer();
        if (totalMoved >= item.amount)
            item.Remove();
        else
        {
            item.amount -= totalMoved;
            item.MarkDirty();
        }

        try { item.parent?.MarkDirty(); } catch { }

        if (ownerPlayer != null)
        {
            try { ownerPlayer.inventory?.ServerUpdate(0f); } catch { }
            try { ownerPlayer.SendNetworkUpdateImmediate(); } catch { }
        }

        container.MarkDirty();
        oven.SendNetworkUpdateImmediate();

        if (actorPlayer != null) TryAutoPullFuel(oven, actorPlayer);
        else if (ownerPlayer != null) QueueFuelRecalcNextTick(oven, ownerPlayer);

        return true;
    }
    catch (Exception ex)
    {
        if (_config != null && _config.Debug)
            PrintWarning($"HandleRefineryCrudeInsertion exception: {ex}");
        return null;
    }
}
private object DistributeOreIntoInputSlots(BaseOven oven, Item item, int splitAmount, BasePlayer actorPlayer)
{
    try
    {
        var container = oven?.inventory;
        if (container == null || item?.info == null) return null;

                int minSlot = oven._inputSlotIndex;
        int maxSlot = oven._inputSlotIndex + oven.inputSlots - 1;
        if (minSlot < 0) minSlot = 0;
        if (maxSlot >= container.capacity) maxSlot = container.capacity - 1;

        string sn = item.info.shortname;
        if (sn != "metal.ore" && sn != "sulfur.ore" && sn != "hq.metal.ore") return null;

        for (int i = minSlot; i <= maxSlot; i++)
        {
            var it = container.GetSlot(i);
            if (it == null) continue;

            var isn = it.info?.shortname;
            if (isn == null) continue;

            if (isn == "metal.ore" || isn == "sulfur.ore" || isn == "hq.metal.ore")
            {
                if (isn != sn)
                {
                    if (actorPlayer != null) QueueFuelRecalcNextTick(oven, actorPlayer);
                    return null;
                }
            }
        }

        int slots = Math.Max(1, oven.inputSlots);
        int itemAmount = (splitAmount > 0) ? Math.Min(splitAmount, item.amount) : item.amount;

        int existingTotal = 0;
        for (int i = minSlot; i <= maxSlot; i++)
        {
            var it = container.GetSlot(i);
            if (it != null && it.info == item.info) existingTotal += it.amount;
        }

        long capLong = (long)Math.Max(0, item.info.stackable) * (long)slots;
        if (capLong > int.MaxValue) capLong = int.MaxValue;
        int cap = (int)capLong;
        int totalAmount = Math.Min(existingTotal + itemAmount, cap);
        if (totalAmount <= existingTotal) return null;

        int baseAmt = totalAmount / slots;
        int rem = totalAmount - baseAmt * slots;

        int totalMoved = 0;

        for (int si = 0; si < slots; si++)
        {
            int slotIndex = minSlot + si;
            if (slotIndex > maxSlot) break;

            int target = baseAmt + (si < rem ? 1 : 0);
            var cur = container.GetSlot(slotIndex);

            int curAmt = 0;
            if (cur != null)
            {
                if (cur.info != item.info) return null;
                curAmt = cur.amount;
            }

            int delta = target - curAmt;
            if (delta <= 0) continue;

            if (cur == null)
            {
                var newItem = ItemManager.Create(item.info, delta, item.skin);
                if (newItem == null) continue;
                if (!newItem.MoveToContainer(container, slotIndex, allowStack: false))
                {
                    newItem.Remove();
                    continue;
                }
            }
            else
            {
                cur.amount += delta;
                cur.MarkDirty();
            }

            totalMoved += delta;
            if (totalMoved >= itemAmount) break;
        }

        if (totalMoved <= 0) return null;

        var ownerPlayer = actorPlayer ?? item.GetOwnerPlayer();

        if (totalMoved >= item.amount)
        {
            item.Remove();
        }
        else
        {
            item.amount -= totalMoved;
            item.MarkDirty();
        }

        try { item.parent?.MarkDirty(); } catch { }

        if (ownerPlayer != null)
        {
            try { ownerPlayer.inventory?.ServerUpdate(0f); } catch { }
            try { ownerPlayer.SendNetworkUpdateImmediate(); } catch { }
        }

        container.MarkDirty();
        oven.SendNetworkUpdateImmediate();

        if (actorPlayer != null) TryAutoPullFuel(oven, actorPlayer);
        else if (ownerPlayer != null) QueueFuelRecalcNextTick(oven, ownerPlayer);

        return true;
    }
    catch
    {
        return null;
    }
}

object CanMoveItem(Item item, PlayerInventory playerInventory, ItemContainer targetContainer, int targetSlot, int amount)
{
    try
    {
        if (!_config.Enabled) return null;
        if (item?.info == null || targetContainer == null) return null;

        var owner = targetContainer.entityOwner as BaseOven;
        if (owner == null || owner.IsDestroyed) return null;
        if (!IsWhitelistedSmeltingOven(owner)) return null;

        var kind = GetKind(owner);
        if (kind == OvenKind.Unknown) return null;

        var src = item.parent;
        if (src != null && src.entityOwner is BaseOven) return null;

        string sn = item.info.shortname;
        var player = playerInventory?.GetComponent<BasePlayer>();

        if (kind == OvenKind.SmallRefinery)
        {
            if (sn != "crude.oil") return null;
            return HandleRefineryCrudeInsertion(owner, item, amount, player);
        }

        if (sn != "metal.ore" && sn != "sulfur.ore" && sn != "hq.metal.ore") return null;

        return DistributeOreIntoInputSlots(owner, item, amount, player);
    }
    catch (Exception ex)
    {
        if (_config != null && _config.Debug)
            PrintWarning($"CanMoveItem(ItemContainer) exception: {ex}");

        return null;
    }
}

        #endregion
        #region Tracking
        private void ScanAndTrackRunningOvens()
        {
            if (!_config.Enabled) return;

            int tracked = 0;

            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var oven = entity as BaseOven;
                if (oven == null || oven.IsDestroyed) continue;
                if (!oven.IsOn()) continue;
                if (!IsWhitelistedSmeltingOven(oven)) continue;

                StartTracking(oven);
                tracked++;
            }

            if (_config.Debug)
                Puts($"Scan complete. Tracking {tracked} running ovens.");
        }

private void StartTracking(BaseOven oven)
{
    if (oven == null || oven.net == null) return;

    ulong id = oven.net.ID.Value;
    if (_active.TryGetValue(id, out var existing))
    {
        existing.Oven = oven;
        return;
    }

    var kind = GetKind(oven);
    if (kind == OvenKind.Unknown) return;

    var tracker = new OvenTracker
    {
        Oven = oven,
        Kind = kind,
        GateOnWood = (kind == OvenKind.SmallFurnace || kind == OvenKind.LargeFurnace || kind == OvenKind.SmallRefinery),
        Cycles = 0,
        OffCycles = 0,
        CharcoalRemainder = 0f,
        FuelDebt = 0f,
        LastBalanceTime = 0f
    };

        RefreshCachedPreset();
    var preset = GetEffectivePresetForKind(_cachedPresetTuning, kind);
    tracker.LastBalanceTime = Time.realtimeSinceStartup;
    tracker.NextTickAt = tracker.LastBalanceTime + UnityEngine.Random.Range(0f, Mathf.Max(0.05f, preset.CycleSeconds));

    _active[id] = tracker; MarkActiveChanged();

    if (_config.VerboseTrackingLogs)
        Puts($"Tracking oven {oven.ShortPrefabName} ({id}) using preset {_config.Preset}.");
}

private void StopTracking(BaseOven oven)
{
    if (oven == null || oven.net == null) return;

    ulong id = oven.net.ID.Value;
    if (_active.Remove(id))
    {
        MarkActiveChanged();
        if (_config.Debug)
            Puts($"Stopped tracking oven {oven.ShortPrefabName} ({id})");
    }
}
        #endregion
        #region Core Loop

        private enum OvenKind
        {
            Unknown,
            SmallFurnace,
            LargeFurnace,
            ElectricFurnace,
            SmallRefinery
        }

        private OvenKind GetKind(BaseOven oven)
        {
            if (oven == null) return OvenKind.Unknown;

            var sn = (oven.ShortPrefabName ?? string.Empty).ToLowerInvariant();

            if (sn == "refinery" || sn == "refinery_small_deployed" || sn == "smallrefinery") return OvenKind.SmallRefinery;
            if (sn == "electric.furnace" || sn == "electricfurnace") return OvenKind.ElectricFurnace;
            if (sn == "furnace.large" || sn == "largefurnace") return OvenKind.LargeFurnace;
            if (sn == "furnace" || sn == "smallfurnace") return OvenKind.SmallFurnace;

            if (sn.Contains("refinery")) return OvenKind.SmallRefinery;
            if (sn.Contains("electric") && sn.Contains("furnace")) return OvenKind.ElectricFurnace;
            if (sn.Contains("large") && sn.Contains("furnace")) return OvenKind.LargeFurnace;
            if (sn.Contains("furnace")) return OvenKind.SmallFurnace;

            return OvenKind.Unknown;
        }

        private bool IsWhitelistedSmeltingOven(BaseOven oven)
        {
            if (oven == null) return false;

            var name = (oven.ShortPrefabName ?? string.Empty).ToLowerInvariant();
            for (int i = 0; i < _cachedWhitelistFragmentsLower.Count; i++)
            {
                if (name.Contains(_cachedWhitelistFragmentsLower[i]))
                    return true;
            }

            if (name.Contains("refinery") && _cachedWhitelistAllowsAnyRefinery)
                return true;

            return false;
        }

private void GlobalTick()
{
    if (!_config.Enabled) return;

        RefreshCachedPreset();
    var preset = _cachedPresetTuning;
    int multiplier = _cachedPresetMultiplier;
    bool isInstantPreset = _cachedIsInstantPreset;
    float now = Time.realtimeSinceStartup;

    if (_active.Count == 0) return;

    if (_tmpTrackerIdsBuiltForVersion != _activeVersion || _tmpTrackerIds.Count != _active.Count)
    {
        _tmpTrackerIds.Clear();
        foreach (var kv in _active)
            _tmpTrackerIds.Add(kv.Key);
        _tmpTrackerIdsBuiltForVersion = _activeVersion;
    }

    if (_tmpTrackerIds.Count == 0) return;

    int processed = 0;
    int count = _tmpTrackerIds.Count;
    int startIndex = (_globalCursor >= 0 ? _globalCursor : 0) % count;

    int cap = GetOvensPerGlobalTickCap(count);

    for (int step = 0; step < count && processed < cap; step++)
    {
        int idx = (startIndex + step) % count;
        ulong id = _tmpTrackerIds[idx];

        if (!_active.TryGetValue(id, out var tracker) || tracker == null)
            continue;

        var oven = tracker.Oven;
        if (oven == null || oven.IsDestroyed || oven.net == null)
        {
            _active.Remove(id); MarkActiveChanged();
            continue;
        }

        if (!oven.IsOn())
        {
            tracker.OffCycles++;
            if (tracker.OffCycles >= 50)
            {
                _active.Remove(id);
                MarkActiveChanged();
            }
            continue;
        }
        tracker.OffCycles = 0;

        if (now < tracker.NextTickAt)
            continue;

        var effectivePreset = GetEffectivePresetForKind(preset, tracker.Kind);
        tracker.NextTickAt = now + Mathf.Max(0.05f, effectivePreset.CycleSeconds);

        TickOven(tracker, effectivePreset, multiplier, isInstantPreset);
        processed++;
    }

    EnsureGlobalTimer(ComputeGlobalLoopInterval(count));

    _globalCursor = (startIndex + 1) % count;
}

        private void TickOven(OvenTracker tracker, PresetTuning preset, int multiplier, bool isInstantPreset)
        {
            var oven = tracker?.Oven;
            if (oven == null || oven.IsDestroyed)
                return;

            try
            {
                var container = oven.inventory;
                if (container == null) return;
                var kind = tracker.Kind;
                if (kind == OvenKind.Unknown) return;
                tracker.OffCycles = 0;
                int totalConsumed = 0;
                int metalConsumed = 0;
                int sulfurConsumed = 0;
                int hqmConsumed = 0;
                int crudeConsumed = 0;
                int remainingBudget = preset.MaxTotalConsumedPerCycle;
                var inputs = tracker.InputsBuffer;
                inputs.Clear();
                foreach (var it in GetSmeltableInputs(container, kind))
                    inputs.Add(it);
                int safetyPasses = 3;
                bool gateOnWood = tracker.GateOnWood;
                int woodAvailable = gateOnWood ? CountItem(container, "wood") : int.MaxValue;
                float woodAvailableFloat = woodAvailable;

                if (gateOnWood && _config.ProduceCharcoalFromFuel && string.Equals(_config.CharcoalOverflowMode, "Pause", StringComparison.OrdinalIgnoreCase))
                {
                    if (!CanGiveOutput(container, "charcoal", 1))
                    {
                        if (_config.Debug)
                            Puts($"{oven.ShortPrefabName}: Charcoal output blocked (Pause mode). Skipping accelerated smelting this cycle.");
                        return;
                    }

                }

                while (remainingBudget > 0 && inputs.Count > 0 && safetyPasses-- > 0)
                {

                    int active = 0;
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var it = inputs[i];
                        if (it != null && it.amount > 0) active++;
                    }
                    if (active == 0) break;

                    int budgetBefore = remainingBudget;

                    for (int i = 0; i < inputs.Count && remainingBudget > 0; i++)
                    {
                        var input = inputs[i];
                        if (input == null || input.amount <= 0) continue;

                        int canTake = 1;

                        if (canTake > preset.MaxConsumedPerStackPerCycle) canTake = preset.MaxConsumedPerStackPerCycle;
                        if (canTake > input.amount) canTake = input.amount;
                        if (canTake > remainingBudget) canTake = remainingBudget;

                        if (gateOnWood)
                        {
                            float wpi = GetWoodPerInput(kind, input.info.shortname);
                            if (wpi > 0f)
                            {
                                int maxByFuel = Mathf.FloorToInt(woodAvailableFloat / wpi);
                                if (maxByFuel <= 0) canTake = 0;
                                else if (canTake > maxByFuel) canTake = maxByFuel;
                            }
                        }

                        if (canTake <= 0) continue;

                        if (!TryConvertInput(kind, container, input, canTake, out int consumedNow))
                            continue;

                        totalConsumed += consumedNow;
                        remainingBudget -= consumedNow;

                        var sn = input.info.shortname;
                        if (sn == "metal.ore") metalConsumed += consumedNow;
                        else if (sn == "sulfur.ore") sulfurConsumed += consumedNow;
                        else if (sn == "hq.metal.ore") hqmConsumed += consumedNow;
                        else if (sn == "crude.oil") crudeConsumed += consumedNow;

                        if (gateOnWood)
                        {
                            float wpi = GetWoodPerInput(kind, sn);
                            if (wpi > 0f)
                            {
                                woodAvailableFloat -= (consumedNow * wpi);
                                ConsumeWoodDebt(tracker, container, (consumedNow * wpi));
                            }
                        }
                    }

                    if (remainingBudget <= 0) break;

                    int totalCap = 0;
                    int[] caps = PoolGetIntArray(inputs.Count);
                    try
                    {
                        for (int i = 0; i < inputs.Count; i++)
                        {
                            var input = inputs[i];
                            int cap = 0;
                            if (input != null && input.amount > 0)
                            {
                                cap = Mathf.Min(input.amount, preset.MaxConsumedPerStackPerCycle);
                                cap = Mathf.Min(cap, remainingBudget);
                                if (gateOnWood)
                                {
                                    float wpi = GetWoodPerInput(kind, input.info.shortname);
                                    if (wpi > 0f)
                                    {
                                        int maxByFuel = Mathf.FloorToInt(woodAvailableFloat / wpi);
                                        if (maxByFuel <= 0) cap = 0;
                                        else if (cap > maxByFuel) cap = maxByFuel;
                                    }
                                }
                            }
                            caps[i] = cap;
                            totalCap += cap;
                        }

                        if (totalCap <= 0) break;

                        int remainingToAllocate = remainingBudget;
                        int[] allocs = PoolGetIntArray(inputs.Count);
                        try
                        {
                            int allocated = 0;
                            for (int i = 0; i < inputs.Count; i++)
                            {
                                int cap = caps[i];
                                if (cap <= 0) { allocs[i] = 0; continue; }

                                int alloc = (int)((long)remainingBudget * (long)cap / (long)totalCap);
                                if (alloc > cap) alloc = cap;
                                allocs[i] = alloc;
                                allocated += alloc;
                            }

                            remainingToAllocate = remainingBudget - allocated;

                            if (remainingToAllocate > 0)
                            {
                                for (int pass = 0; pass < inputs.Count && remainingToAllocate > 0; pass++)
                                {
                                    for (int i = 0; i < inputs.Count && remainingToAllocate > 0; i++)
                                    {
                                        int cap = caps[i];
                                        if (cap <= 0) continue;
                                        if (allocs[i] >= cap) continue;
                                        allocs[i]++;
                                        remainingToAllocate--;
                                    }
                                }
                            }

                            for (int i = 0; i < inputs.Count && remainingBudget > 0; i++)
                            {
                                int alloc = allocs[i];
                                if (alloc <= 0) continue;

                                var input = inputs[i];
                                if (input == null || input.amount <= 0) continue;

                                int canTake = alloc;
                                if (canTake > input.amount) canTake = input.amount;
                                if (canTake > preset.MaxConsumedPerStackPerCycle) canTake = preset.MaxConsumedPerStackPerCycle;
                                if (canTake > remainingBudget) canTake = remainingBudget;

                                if (gateOnWood)
                                {
                                    float wpi = GetWoodPerInput(kind, input.info.shortname);
                                    if (wpi > 0f)
                                    {
                                        int maxByFuel = Mathf.FloorToInt(woodAvailableFloat / wpi);
                                        if (maxByFuel <= 0) canTake = 0;
                                        else if (canTake > maxByFuel) canTake = maxByFuel;
                                    }
                                }

                                if (canTake <= 0) continue;

                                if (!TryConvertInput(kind, container, input, canTake, out int consumedNow))
                                    continue;

                                totalConsumed += consumedNow;
                                remainingBudget -= consumedNow;

                                var sn = input.info.shortname;
                                if (sn == "metal.ore") metalConsumed += consumedNow;
                                else if (sn == "sulfur.ore") sulfurConsumed += consumedNow;
                                else if (sn == "hq.metal.ore") hqmConsumed += consumedNow;
                                else if (sn == "crude.oil") crudeConsumed += consumedNow;

                                if (gateOnWood)
                                {
                                    float wpi = GetWoodPerInput(kind, sn);
                                    if (wpi > 0f)
                                    {
                                        woodAvailableFloat -= (consumedNow * wpi);
                                        ConsumeWoodDebt(tracker, container, (consumedNow * wpi));
                                    }
                                }
                            }
                        }
                        finally
                        {
                            PoolReturnIntArray(allocs);
                        }
                    }
                    finally
                    {
                        PoolReturnIntArray(caps);
                    }

                    if (remainingBudget == budgetBefore) break;
                }

                if (kind != OvenKind.ElectricFurnace && kind != OvenKind.SmallRefinery)
                {
                    ConsumeIdleFuel(tracker, kind, container, preset, multiplier, totalConsumed, isInstantPreset);
                }

                tracker.Cycles++;

                if (_config.Debug && totalConsumed > 0)
                    if (_config.VerboseCycleLogs) Puts($"{oven.ShortPrefabName} processed {totalConsumed} items this cycle (cycle #{tracker.Cycles}).");
            }
            catch (Exception ex)
            {
                PrintWarning($"TickOven exception: {ex}");
            }
        }

        #endregion
        #region Small Pools

        private static readonly Stack<int[]> _intArrayPool = new Stack<int[]>();

        private static int[] PoolGetIntArray(int size)
        {
            while (_intArrayPool.Count > 0)
            {
                var arr = _intArrayPool.Pop();
                if (arr != null && arr.Length >= size) return arr;
            }
            return new int[size];
        }

        private static void PoolReturnIntArray(int[] arr)
        {
            if (arr == null) return;

            if (_intArrayPool.Count < 32)
                _intArrayPool.Push(arr);
        }

        #endregion
        #region Fuel + Charcoal (strict fuel-gated work + idle burn)

        private const float SmallFurnace_WoodPerMetalOre = 1.6666667f;
        private const float SmallFurnace_WoodPerSulfurOre = 0.8333333f;
        private const float SmallFurnace_WoodPerHQMOre = 3.3350000f;
        private const float LargeFurnace_WoodPerMetalOre = 0.3333333f;
        private const float LargeFurnace_WoodPerSulfurOre = 0.1666667f;
        private const float LargeFurnace_WoodPerHQMOre = 0.6666667f;
        private const float Refinery_WoodPerCrudeOil = 2.2222222f;
        private float GetWoodPerInput(OvenKind kind, string inputShortname)
        {
            if (string.IsNullOrEmpty(inputShortname)) return 0f;

            if (kind == OvenKind.SmallRefinery)
            {
                return inputShortname == "crude.oil" ? Refinery_WoodPerCrudeOil : 0f;
            }

            bool large = kind == OvenKind.LargeFurnace;

            switch (inputShortname)
            {
                case "metal.ore":
                    return large ? LargeFurnace_WoodPerMetalOre : SmallFurnace_WoodPerMetalOre;
                case "sulfur.ore":
                    return large ? LargeFurnace_WoodPerSulfurOre : SmallFurnace_WoodPerSulfurOre;
                case "hq.metal.ore":
                    return large ? LargeFurnace_WoodPerHQMOre : SmallFurnace_WoodPerHQMOre;
                default:
                    return 0f;
            }
        }

        private int CountItem(ItemContainer container, string shortname)
        {
            if (container?.itemList == null) return 0;
            int total = 0;
            foreach (var it in container.itemList)
            {
                if (it?.info == null || it.amount <= 0) continue;
                if (string.Equals(it.info.shortname, shortname, StringComparison.OrdinalIgnoreCase))
                    total += it.amount;
            }
            return total;
        }

        private void ConsumeWoodDebt(OvenTracker tracker, ItemContainer container, float woodNeededFloat)
        {
            if (tracker == null || container == null || woodNeededFloat <= 0f) return;

            tracker.FuelDebt += woodNeededFloat;

            int desiredRemove = Mathf.FloorToInt(tracker.FuelDebt);
            if (desiredRemove <= 0) return;

            if (_config.ProduceCharcoalFromFuel && string.Equals(_config.CharcoalOverflowMode, "Pause", StringComparison.OrdinalIgnoreCase))
            {
                int maxCharcoalCapacity = GetAdditionalCapacity(container, "charcoal");
                if (maxCharcoalCapacity <= 0)
                    return;

                float ratio = Mathf.Max(0f, _config.CharcoalPerWood);
                if (ratio > 0f)
                {

                    float availableForCharcoal = maxCharcoalCapacity - tracker.CharcoalRemainder;
                    if (availableForCharcoal <= 0f)
                        return;

                    int maxWoodByCapacity = Mathf.FloorToInt(availableForCharcoal / ratio);
                    if (maxWoodByCapacity <= 0)
                        return;

                    if (desiredRemove > maxWoodByCapacity)
                        desiredRemove = maxWoodByCapacity;
                }
            }

            int removed = RemoveFuel(container, "wood", desiredRemove);
            tracker.FuelDebt -= removed;

            if (removed > 0 && _config.ProduceCharcoalFromFuel)
            {
                float ratio = Mathf.Max(0f, _config.CharcoalPerWood);
                float exact = removed * ratio + tracker.CharcoalRemainder;

                int charcoalToGive = Mathf.FloorToInt(exact);
                tracker.CharcoalRemainder = exact - charcoalToGive;

                if (charcoalToGive > 0)
                {
                    if (!TryGiveOutput(container, "charcoal", charcoalToGive))
                    {
                        if (_config.Debug)
                            Puts("Charcoal output blocked; skipping charcoal minting this tick.");
                    }
                }
            }
        }

        private void ConsumeIdleFuel(
            OvenTracker tracker,
            OvenKind kind,
            ItemContainer container,
            PresetTuning preset,
            int multiplier,
            int itemsSmeltedThisTick,
            bool isInstantPreset)
        {
            if (tracker == null || container?.itemList == null) return;

            if (isInstantPreset) return;

            if (itemsSmeltedThisTick > 0) return;

            bool isSmallFurnace = kind == OvenKind.SmallFurnace;
            bool isLargeFurnace = kind == OvenKind.LargeFurnace;
            bool isRefinery = kind == OvenKind.SmallRefinery;

            if (!isSmallFurnace && !isLargeFurnace && !isRefinery)
                return;

            float wpi = 0f;
            if (isRefinery) wpi = GetWoodPerInput(kind, "crude.oil");
            else wpi = GetWoodPerInput(kind, "metal.ore");

            if (wpi <= 0f) return;

            float now = Time.realtimeSinceStartup;
            float elapsed = tracker.LastBalanceTime > 0f ? Mathf.Max(0.01f, now - tracker.LastBalanceTime) : Mathf.Max(0.01f, preset.CycleSeconds);
            tracker.LastBalanceTime = now;

            float cycleSeconds = Mathf.Max(0.01f, preset.CycleSeconds);
            float targetThisTick = (preset.MaxTotalConsumedPerCycle * wpi) * (elapsed / cycleSeconds);
            if (targetThisTick <= 0f) return;

            ConsumeWoodDebt(tracker, container, targetThisTick);
        }

        private int RemoveFuel(ItemContainer container, string shortname, int amount)
        {
            int remaining = amount;
            int removed = 0;

            for (int i = 0; i < container.capacity && remaining > 0; i++)
            {
                var it = container.GetSlot(i);
                if (it?.info == null || it.amount <= 0) continue;
                if (!string.Equals(it.info.shortname, shortname, StringComparison.OrdinalIgnoreCase)) continue;

                int take = Mathf.Min(it.amount, remaining);
                it.UseItem(take);
                removed += take;
                remaining -= take;
            }

            return removed;
        }

#endregion

        #region Smelt conversion

        private IEnumerable<Item> GetSmeltableInputs(ItemContainer container, OvenKind kind)
        {
            if (container?.itemList == null) yield break;

            foreach (var it in container.itemList)
            {
                if (it?.info == null || it.amount <= 0) continue;

                var sn = it.info.shortname;

                if (kind == OvenKind.SmallRefinery)
                {
                    if (sn == "crude.oil")
                        yield return it;
                }
                else
                {
                    if (sn == "metal.ore" || sn == "sulfur.ore" || sn == "hq.metal.ore")
                        yield return it;
                }
            }
        }

        private bool TryConvertInput(OvenKind kind, ItemContainer container, Item input, int toConsume, out int consumed)
        {
            consumed = 0;
            if (container == null || input?.info == null || toConsume <= 0) return false;

            string inSn = input.info.shortname;
            string outSn;
            int outPerIn;

            if (kind == OvenKind.SmallRefinery)
            {
                if (inSn != "crude.oil") return false;
                outSn = "lowgradefuel";
                outPerIn = 3;
            }
            else
            {
                switch (inSn)
                {
                    case "metal.ore":
                        outSn = "metal.fragments";
                        outPerIn = 1;
                        break;
                    case "sulfur.ore":
                        outSn = "sulfur";
                        outPerIn = 1;
                        break;
                    case "hq.metal.ore":
                        outSn = "metal.refined";
                        outPerIn = 1;
                        break;
                    default:
                        return false;
                }
            }

            long outAmountL = (long)toConsume * outPerIn;
            if (outAmountL > int.MaxValue) outAmountL = int.MaxValue;
            int outAmount = (int)outAmountL;

            if (!TryGiveOutput(container, outSn, outAmount))
                return false;
            input.UseItem(toConsume);
            consumed = toConsume;
            return true;
        }

        private bool TryGiveOutput(ItemContainer container, string shortname, int amount)
        {
            if (container == null || amount <= 0) return true;

            var def = ItemManager.FindItemDefinition(shortname);
            if (def == null) return false;

            int remaining = amount;

            foreach (var it in container.itemList)
            {
                if (it?.info == null) continue;
                if (it.info.itemid != def.itemid) continue;
                if (it.amount >= it.MaxStackable()) continue;

                int can = Mathf.Min(remaining, it.MaxStackable() - it.amount);
                it.amount += can;
                it.MarkDirty();
                remaining -= can;
                if (remaining <= 0) return true;
            }

            while (remaining > 0)
            {
                int give = Mathf.Min(remaining, def.stackable);

                var created = ItemManager.Create(def, give);
                if (created == null) return false;

                if (!created.MoveToContainer(container))
                {
                    created.Remove();
                    return false;
                }

                remaining -= give;
            }

            return true;
        }
        private int GetAdditionalCapacity(ItemContainer container, string shortname)
        {
            if (container == null) return 0;

            ItemDefinition def = null;
            if (string.Equals(shortname, "charcoal", StringComparison.Ordinal))
                def = _charcoalDefinition;

            if (def == null)
                def = ItemManager.FindItemDefinition(shortname);

            if (def == null) return 0;

            int stackSize = def.stackable;
            if (stackSize <= 0) return 0;

            int capacity = 0;

            foreach (var it in container.itemList)
            {
                if (it?.info == null) continue;
                if (it.info.itemid != def.itemid) continue;
                if (it.amount >= stackSize) continue;
                capacity += (stackSize - it.amount);
            }

            int usedSlots = container.itemList.Count;
            int emptySlots = Mathf.Max(0, container.capacity - usedSlots);
            capacity += emptySlots * stackSize;

            return capacity;
        }

        private bool CanGiveOutput(ItemContainer container, string shortname, int amount)
        {
            if (amount <= 0) return true;
            return GetAdditionalCapacity(container, shortname) >= amount;
        }

        #endregion
        #region StartCooking

        private bool TryStartCooking(BaseOven oven)
        {
            if (oven == null || oven.IsDestroyed) return false;
            try
            {
                oven.StartCooking();
                return true;
            }
            catch (Exception ex)
            {
                PrintWarning($"StartCooking() failed: {ex.Message}");
            }
            return false;
        }
        #endregion
    }
}
