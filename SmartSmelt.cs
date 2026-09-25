using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SmartSmelt", "SeesAll", "1.2.3")]
    [Description("Preset-based accelerated smelting with instant sync, adaptive scaling, and smart fuel pull.")]
    public class SmartSmelt : RustPlugin
    {
        #region Configuration

        private const string ItemWood = "wood";
        private const string ItemCharcoal = "charcoal";
        private const string ItemLowGradeFuel = "lowgradefuel";
        private const string ItemMetalFragments = "metal.fragments";
        private const string ItemSulfur = "sulfur";
        private const string ItemMetalRefined = "metal.refined";
        private const string ItemCrudeOil = "crude.oil";
        private const string ItemMetalOre = "metal.ore";
        private const string ItemSulfurOre = "sulfur.ore";
        private const string ItemHqMetalOre = "hq.metal.ore";

        private static readonly string[] DefaultOvenWhitelist = { "furnace", "furnace.large", "refinery", "electric.furnace" };

        private Configuration _config;
        private readonly List<string> _cachedWhitelistFragmentsLower = new List<string>();
        private bool _cachedWhitelistAllowsAnyRefinery;
        private ItemDefinition _charcoalDefinition;
        private ItemDefinition _woodDefinition;
        private ItemDefinition _lowGradeFuelDefinition;
        private ItemDefinition _metalFragmentsDefinition;
        private ItemDefinition _sulfurDefinition;
        private ItemDefinition _metalRefinedDefinition;
        private ItemDefinition _crudeOilDefinition;
        private ItemDefinition _metalOreDefinition;
        private ItemDefinition _sulfurOreDefinition;
        private ItemDefinition _hqMetalOreDefinition;

        private class Configuration
        {
            public bool Enabled = true;
            public int ConfigVersion = 3;
            public string Preset = "10x";
            public string PresetOptions = "2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, instant";

            public bool AutoTuneEnabled = true;
            public int AveragePopulation = 100;
            public string AutoTuneBias = "Balanced";
            public bool AutoTuneWriteToConfig = false;

            public bool EnableOreSplitting = true;
            public bool EnableMixingTableScaling = true;
            public List<string> OvenWhitelist = new List<string>(DefaultOvenWhitelist);
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
            public float AutoPullFuelBufferPercent = 0f;
            public bool ReducedWoodCostEnabled = false;
            public float WoodCostScale = 0.5f;
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

            bool shouldSaveConfig = false;

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
                shouldSaveConfig = true;
            }

            if (NormalizeConfig())
                shouldSaveConfig = true;

            if (shouldSaveConfig)
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
                ["InfoLine1"] = "SmartSmelt v{0} | Preset={1} | Enabled={2} | OreSplitting={3} | AutoPullFuel={4} (Buffer={5:0.###}%) | WoodCost={6:0.##}x",
                ["InfoLine2"] = "AutoTune={0} (AvgPop={1}, Bias={2}, WriteToConfig={3})",
                ["InfoLine3"] = "TrackedOvens={0} | PerTickCap={1} (Adaptive={2}) | LoopInterval={3:0.000}s (Desired={4:0.000}s, Dynamic={5})",
                ["ConsoleInfo"] = "SmartSmelt v{0} | Preset={1} | Enabled={2} | AutoPullFuel={3} (Buffer={4:0.###}%) | WoodCost={11:0.##}x\nTrackedOvens={5} | PerTickCap={6} (Adaptive={7}) | LoopInterval={8:0.000}s (Desired={9:0.000}s, Dynamic={10})"
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

                if (version < 2)
                {
                    // Version 1 used FuelUsageMultiplier. Preserve its effective behavior
                    // when translating to the clearer opt-in reduced-wood-cost settings.
                    if (raw.TryGetValue("FuelUsageMultiplier", out var legacyFuelMultiplier))
                    {
                        try
                        {
                            float legacyScale = Mathf.Clamp(Convert.ToSingle(legacyFuelMultiplier), 0f, 1f);

                            if (!raw.ContainsKey("WoodCostScale"))
                                raw["WoodCostScale"] = legacyScale;

                            if (!raw.ContainsKey("ReducedWoodCostEnabled"))
                                raw["ReducedWoodCostEnabled"] = legacyScale < 0.999f;
                        }
                        catch
                        {
                            PrintWarning("Could not read legacy FuelUsageMultiplier; using the new wood-cost defaults.");
                        }

                        raw.Remove("FuelUsageMultiplier");
                    }

                    raw["ConfigVersion"] = 2;
                    changed = true;
                }

                if (version < 3)
                {
                    if (!raw.ContainsKey("EnableMixingTableScaling"))
                        raw["EnableMixingTableScaling"] = true;

                    raw["ConfigVersion"] = 3;
                    changed = true;
                }

                if (!raw.ContainsKey("ConfigVersion"))
                {
                    raw["ConfigVersion"] = 3;
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

            _charcoalDefinition = ItemManager.FindItemDefinition(ItemCharcoal);
            _woodDefinition = ItemManager.FindItemDefinition(ItemWood);
            _lowGradeFuelDefinition = ItemManager.FindItemDefinition(ItemLowGradeFuel);
            _metalFragmentsDefinition = ItemManager.FindItemDefinition(ItemMetalFragments);
            _sulfurDefinition = ItemManager.FindItemDefinition(ItemSulfur);
            _metalRefinedDefinition = ItemManager.FindItemDefinition(ItemMetalRefined);
            _crudeOilDefinition = ItemManager.FindItemDefinition(ItemCrudeOil);
            _metalOreDefinition = ItemManager.FindItemDefinition(ItemMetalOre);
            _sulfurOreDefinition = ItemManager.FindItemDefinition(ItemSulfurOre);
            _hqMetalOreDefinition = ItemManager.FindItemDefinition(ItemHqMetalOre);
        }

        private ItemDefinition GetCachedItemDefinition(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return null;

            switch (shortname)
            {
                case ItemCharcoal: return _charcoalDefinition;
                case ItemWood: return _woodDefinition;
                case ItemLowGradeFuel: return _lowGradeFuelDefinition;
                case ItemMetalFragments: return _metalFragmentsDefinition;
                case ItemSulfur: return _sulfurDefinition;
                case ItemMetalRefined: return _metalRefinedDefinition;
                case ItemCrudeOil: return _crudeOilDefinition;
                case ItemMetalOre: return _metalOreDefinition;
                case ItemSulfurOre: return _sulfurOreDefinition;
                case ItemHqMetalOre: return _hqMetalOreDefinition;
                default: return ItemManager.FindItemDefinition(shortname);
            }
        }

        private bool NormalizeConfig()
        {
            bool changed = false;

            if (_config == null)
            {
                _config = new Configuration();
                return true;
            }

            // Version 3 introduced mixing-table preset scaling; bumping the version forces a
            // save so the new key appears without replacing existing administrator settings.
            if (_config.ConfigVersion != 3)
            {
                _config.ConfigVersion = 3;
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

            if (_config.WoodCostScale < 0f) { _config.WoodCostScale = 0f; changed = true; }
            else if (_config.WoodCostScale > 1f) { _config.WoodCostScale = 1f; changed = true; }

            var presetKey = ResolvePresetKey(_config.Preset);
            if (!PresetDefinitions.ContainsKey(presetKey))
            {
                PrintWarning($"Unknown Preset '{_config.Preset}', defaulting to {DefaultPresetKey}.");
                presetKey = DefaultPresetKey;
                changed = true;
            }

            if (!string.Equals(_config.Preset, presetKey, StringComparison.Ordinal))
            {
                _config.Preset = presetKey;
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
                _config.OvenWhitelist = new List<string>(DefaultOvenWhitelist);
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
                    cleaned.AddRange(DefaultOvenWhitelist);

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

            switch (ResolvePresetKey(_config.Preset))
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
        }

        private struct PresetDefinition
        {
            public PresetTuning Tuning;
            public int Multiplier;
        }

        private const string DefaultPresetKey = "10x";
        private const string InstantPresetKey = "instant";

        private static readonly Dictionary<string, string> PresetAliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["2"] = "2x",
            ["3"] = "3x",
            ["5"] = "5x",
            ["10"] = "10x",
            ["25"] = "25x",
            ["50"] = "50x",
            ["100"] = "100x",
            ["1000"] = "1000x",
            ["inst"] = InstantPresetKey
        };

        private static readonly Dictionary<string, PresetDefinition> PresetDefinitions = new Dictionary<string, PresetDefinition>(StringComparer.Ordinal)
        {
            ["2x"] = MakePreset(0.5f, 20, 10, 2),
            ["3x"] = MakePreset(0.5f, 30, 15, 3),
            ["5x"] = MakePreset(0.5f, 50, 25, 5),
            ["10x"] = MakePreset(0.5f, 100, 50, 10),
            ["25x"] = MakePreset(0.25f, 500, 250, 25),
            ["50x"] = MakePreset(0.2f, 1000, 500, 50),
            ["100x"] = MakePreset(0.1f, 2000, 1000, 100),
            ["1000x"] = MakePreset(0.05f, 20000, 10000, 1000),
            [InstantPresetKey] = MakePreset(0.1f, int.MaxValue, int.MaxValue, 1000000)
        };

        private static PresetDefinition MakePreset(float cycleSeconds, int maxTotalPerCycle, int maxPerStackPerCycle, int multiplier)
        {
            return new PresetDefinition
            {
                Tuning = new PresetTuning
                {
                    CycleSeconds = cycleSeconds,
                    MaxTotalConsumedPerCycle = maxTotalPerCycle,
                    MaxConsumedPerStackPerCycle = maxPerStackPerCycle
                },
                Multiplier = multiplier
            };
        }

        private static string ResolvePresetKey(string preset)
        {
            var key = (preset ?? DefaultPresetKey).Trim().ToLowerInvariant();
            if (key.Length == 0) key = DefaultPresetKey;
            if (PresetAliases.TryGetValue(key, out var canonical)) key = canonical;
            return key;
        }

        private PresetTuning GetPreset(string preset)
        {
            return PresetDefinitions.TryGetValue(ResolvePresetKey(preset), out var def)
                ? def.Tuning
                : PresetDefinitions[DefaultPresetKey].Tuning;
        }

        private int GetMultiplier(string preset)
        {
            return PresetDefinitions.TryGetValue(ResolvePresetKey(preset), out var def)
                ? def.Multiplier
                : PresetDefinitions[DefaultPresetKey].Multiplier;
        }

        private string _cachedPresetKey = null;
        private PresetTuning _cachedPresetTuning;
        private int _cachedPresetMultiplier = 10;
        private bool _cachedIsInstantPreset = false;

        private void RefreshCachedPreset()
        {
            string key = ResolvePresetKey(_config?.Preset);

            if (key == _cachedPresetKey)
                return;

            _cachedPresetKey = key;
            _cachedPresetTuning = GetPreset(key);
            _cachedPresetMultiplier = GetMultiplier(key);
            _cachedIsInstantPreset = string.Equals(key, InstantPresetKey, StringComparison.Ordinal);
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
        private readonly Dictionary<ulong, string> _cachedOvenPrefabNamesLower = new Dictionary<ulong, string>();

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
        private float _queuedRescheduleInterval = DefaultGlobalLoopInterval;

        private readonly List<ulong> _tmpTrackerIds = new List<ulong>(256);
        private readonly Stack<List<Item>> _itemListPool = new Stack<List<Item>>();

        private const int DefaultMaxOvensPerGlobalTick = 200;
        private const float DefaultGlobalLoopInterval = 0.25f;
        private const float MinGlobalLoopInterval = 0.05f;
        private const float MaxGlobalLoopInterval = 2f;
        private const float TimerRescheduleThreshold = 0.05f;
        private const float ForceStartCookingDelaySeconds = 0.2f;
        private const int MaxOffCyclesBeforeEvict = 50;
        private const int StartupScanEntitiesPerSlice = 500;
        private const float StartupScanSliceDelaySeconds = 0.02f;
        private int _globalCursor = 0;
        private IEnumerator<BaseNetworkable> _startupScanEnumerator;
        private int _startupScanTracked;

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
            if (_config == null) return DefaultGlobalLoopInterval;

            if (!_effDynamicTickInterval)
                return Mathf.Clamp(_effFixedGlobalLoopInterval, MinGlobalLoopInterval, MaxGlobalLoopInterval);

            float min = Mathf.Clamp(_effDynamicMinGlobalLoopInterval, MinGlobalLoopInterval, MaxGlobalLoopInterval);
            float max = Mathf.Clamp(_effDynamicMaxGlobalLoopInterval, MinGlobalLoopInterval, MaxGlobalLoopInterval);
            if (max < min) { var t = min; min = max; max = t; }

            int low = Math.Max(0, _effDynamicLowOvenCount);
            int high = Math.Max(low + 1, _effDynamicHighOvenCount);

            float t01 = Mathf.InverseLerp(low, high, trackedCount);
            return Mathf.Lerp(min, max, t01);
        }

        private void EnsureGlobalTimer(float interval)
        {
            interval = Mathf.Clamp(interval, MinGlobalLoopInterval, MaxGlobalLoopInterval);

            if (_globalTimer == null)
            {
                _currentGlobalLoopInterval = interval;
                _globalTimer = timer.Every(_currentGlobalLoopInterval, GlobalTick);
                return;
            }

            if (Mathf.Abs(_currentGlobalLoopInterval - interval) < TimerRescheduleThreshold)
                return;

            // Always remember the latest requested interval so a queued reschedule uses it
            // instead of the value captured when the reschedule was first queued.
            _queuedRescheduleInterval = interval;

            if (_rescheduleQueued)
                return;

            _rescheduleQueued = true;
            timer.Once(0f, () =>
            {
                _rescheduleQueued = false;
                _globalTimer?.Destroy();
                _globalTimer = null;
                _currentGlobalLoopInterval = Mathf.Clamp(_queuedRescheduleInterval, MinGlobalLoopInterval, MaxGlobalLoopInterval);
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
            // ItemManager is fully initialized at this point. Rebuild cached definitions so
            // a very early config load cannot leave the runtime with unresolved item entries.
            RebuildRuntimeCaches();
            RefreshEffectiveScheduling(allowWriteToConfig: true);

            _active.Clear();
            _cachedOvenPrefabNamesLower.Clear();
            MarkActiveChanged();
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
            DisposeStartupScanEnumerator();
            _active.Clear();
            _cachedOvenPrefabNamesLower.Clear();
            MarkActiveChanged();
        }
        private void OnEntityKill(BaseNetworkable ent)
        {
            var oven = ent as BaseOven;
            if (oven == null) return;
            StopTracking(oven);
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
                        timer.Once(ForceStartCookingDelaySeconds, () =>
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

        private void OnMixingTableToggle(MixingTable table, BasePlayer player)
        {
            if (_config == null || !_config.Enabled || !_config.EnableMixingTableScaling)
                return;
            if (table == null || table.IsDestroyed || table.IsOn())
                return;

            // Rust calculates the selected recipe and its full duration as the table starts.
            // Apply the preset on the next tick so those vanilla values are ready first.
            NextTick(() =>
            {
                if (table == null || table.IsDestroyed || !table.IsOn())
                    return;

                RefreshCachedPreset();
                float multiplier = Mathf.Max(1f, _cachedPresetMultiplier);
                if (multiplier <= 1f || table.RemainingMixTime <= 0f)
                    return;

                table.RemainingMixTime /= multiplier;
                table.TotalMixTime /= multiplier;
                table.SendNetworkUpdateImmediate();

                // Vanilla ticks once per second. Reschedule sub-second recipes so high and
                // Instant presets do not wait for an unnecessary full vanilla tick.
                if (table.RemainingMixTime < 1f)
                {
                    table.CancelInvoke(table.TickMix);
                    table.Invoke(table.TickMix, Mathf.Max(0.01f, table.RemainingMixTime));
                }

                if (_config.Debug)
                    Puts($"Mixing table started at {ResolvePresetKey(_config.Preset)} ({multiplier:0}x); remaining time is {table.RemainingMixTime:0.###}s.");
            });
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

            if (!permission.UserHasPermission(player.UserIDString, PermDebug))
            {
                player.ChatMessage(Msg("NoPermission", player.UserIDString));
                return;
            }

            var desiredInterval = ComputeGlobalLoopInterval(_active.Count);
            var cap = GetOvensPerGlobalTickCap(_active.Count);

            SendReply(player, string.Format(Msg("InfoLine1", player.UserIDString), Version, _config.Preset, _config.Enabled, _config.EnableOreSplitting, _config.AutoPullFuelFromPlayer, _config.AutoPullFuelBufferPercent, GetWoodCostScale()));
            SendReply(player, string.Format(Msg("InfoLine2", player.UserIDString), _config.AutoTuneEnabled, _config.AveragePopulation, _config.AutoTuneBias, _config.AutoTuneWriteToConfig));
            SendReply(player, string.Format(Msg("InfoLine3", player.UserIDString), _active.Count, cap, _effAdaptiveScaling, _currentGlobalLoopInterval, desiredInterval, _effDynamicTickInterval));
        }

        [ConsoleCommand("ss.info")]
        private void ConsoleSmartSmeltInfo(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermDebug))
            {
                arg.ReplyWith(Msg("NoPermission", player.UserIDString));
                return;
            }

            var desiredInterval = ComputeGlobalLoopInterval(_active.Count);
            var cap = GetOvensPerGlobalTickCap(_active.Count);

            arg.ReplyWith(string.Format(Msg("ConsoleInfo"), Version, _config.Preset, _config.Enabled, _config.AutoPullFuelFromPlayer, _config.AutoPullFuelBufferPercent, _active.Count, cap, _effAdaptiveScaling, _currentGlobalLoopInterval, desiredInterval, _effDynamicTickInterval, GetWoodCostScale()));
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

        private object HandleOvenInsertion(BaseOven oven, Item item, int amount, BasePlayer player)
        {
            var kind = GetKind(oven);
            if (kind == OvenKind.Unknown) return null;

            string sn = item.info.shortname;

            if (kind == OvenKind.SmallRefinery)
            {
                if (sn != ItemCrudeOil) return null;
                return HandleRefineryCrudeInsertion(oven, item, amount, player);
            }

            if (!IsOreShortname(sn)) return null;

            if (!_config.EnableOreSplitting)
            {
                if (player != null) QueueFuelRecalcNextTick(oven, player);
                return null;
            }

            return DistributeOreIntoInputSlots(oven, item, amount, player);
        }

        private object CanMoveItem(Item item, PlayerInventory inventory, ItemContainerId targetContainerId, int targetSlotIndex, int splitAmount)
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

                var targetContainer = inventory.FindContainer(targetContainerId);
                if (targetContainer != null && !(targetContainer.entityOwner is BaseOven)) return null;

                var original = item.GetRootContainer();
                if (original == null || (original.entityOwner is BaseOven)) return null;

                return HandleOvenInsertion(oven, item, splitAmount, player);
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
                    return ItemWood;
                case OvenKind.SmallRefinery:
                    return ItemWood;
                default:

                    return null;
            }
        }

        private bool IsOreShortname(string shortname)
        {
            return shortname == ItemMetalOre || shortname == ItemSulfurOre || shortname == ItemHqMetalOre;
        }

        private int GetStackableAmount(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return 0;
            var def = GetCachedItemDefinition(shortname);
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
                if (sn == ItemMetalOre) metal += it.amount;
                else if (sn == ItemSulfurOre) sulfur += it.amount;
                else if (sn == ItemHqMetalOre) hqm += it.amount;
                else if (sn == ItemCrudeOil) crude += it.amount;
            }

            // Derive everything from GetWoodPerInput so the wood-cost scale and any future
            // ratio changes apply to auto-pull amounts and actual burn identically.
            float required = 0f;
            float maxWoodPerInput = 0f;

            if (kind == OvenKind.SmallRefinery)
            {
                float crudeWpi = GetWoodPerInput(kind, ItemCrudeOil);
                required += crude * crudeWpi;
                if (crude > 0 && crudeWpi > maxWoodPerInput) maxWoodPerInput = crudeWpi;
            }
            else
            {
                float metalWpi = GetWoodPerInput(kind, ItemMetalOre);
                float sulfurWpi = GetWoodPerInput(kind, ItemSulfurOre);
                float hqmWpi = GetWoodPerInput(kind, ItemHqMetalOre);

                required += metal * metalWpi;
                required += sulfur * sulfurWpi;
                required += hqm * hqmWpi;

                if (metal > 0 && metalWpi > maxWoodPerInput) maxWoodPerInput = metalWpi;
                if (sulfur > 0 && sulfurWpi > maxWoodPerInput) maxWoodPerInput = sulfurWpi;
                if (hqm > 0 && hqmWpi > maxWoodPerInput) maxWoodPerInput = hqmWpi;
            }

            if (required <= 0f) return 0;

            // Exact accounting instead of a percentage buffer: the vanilla burn keeps
            // consuming wood underneath the plugin for the whole smelt duration, and the
            // integer fuel gate needs ceil(woodPerInput) present to smelt the final unit.
            // Both are real, flat costs — they do not scale as a tax on batch size.
            required += EstimateNativeBurnWood(kind, metal + sulfur + hqm + crude);
            required += Mathf.Ceil(maxWoodPerInput);

            return Mathf.CeilToInt(required);
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
            var snapshot = GetPooledItemList();

            try
            {
                if (from.itemList != null)
                {
                    for (int i = 0; i < from.itemList.Count; i++)
                        snapshot.Add(from.itemList[i]);
                }

                for (int i = 0; i < snapshot.Count && moved < amount; i++)
                {
                    var it = snapshot[i];
                    if (it == null || it.amount <= 0 || it.info == null) continue;
                    if (it.parent != from) continue;
                    if (it.info.shortname != shortname) continue;

                    int take = Mathf.Min(it.amount, amount - moved);
                    if (take <= 0) continue;

                    Item moving = it;
                    if (take < it.amount)
                    {
                        moving = it.SplitItem(take);
                        if (moving == null) continue;
                    }

                    bool movedToTarget;
                    if (preferredPosition >= 0)
                        movedToTarget = moving.MoveToContainer(to, preferredPosition, true) || moving.MoveToContainer(to);
                    else
                        movedToTarget = moving.MoveToContainer(to);

                    if (!movedToTarget)
                    {
                        if (moving != it)
                        {
                            it.amount += moving.amount;
                            moving.Remove();
                        }
                        break;
                    }

                    moved += take;
                }
            }
            finally
            {
                ReturnPooledItemList(snapshot);
            }

            return moved;
        }

        private List<Item> GetPooledItemList()
        {
            if (_itemListPool.Count > 0)
            {
                var list = _itemListPool.Pop();
                list.Clear();
                return list;
            }

            return new List<Item>(32);
        }

        private void ReturnPooledItemList(List<Item> list)
        {
            if (list == null) return;
            list.Clear();

            if (_itemListPool.Count < 16)
                _itemListPool.Push(list);
        }

        private int GetRefineryFinishableCrudeAmount(BaseOven oven, BasePlayer player)
        {
            if (oven == null || oven.IsDestroyed) return 0;

            var container = oven.inventory;
            if (container == null) return 0;
            float woodPerCrude = GetWoodPerInput(OvenKind.SmallRefinery, ItemCrudeOil);

            // A wood cost of zero (WoodCostScale = 0) means crude is not fuel-limited at all.
            if (woodPerCrude <= 0f) return int.MaxValue;

            float pct = Mathf.Max(0f, _config.AutoPullFuelBufferPercent) / 100f;
            float effectiveWoodPerCrude = (woodPerCrude + GetNativeBurnWoodPerUnit(OvenKind.SmallRefinery)) * (1f + pct);

            var fuelScan = ScanFuelSlots(container, ItemWood, OvenKind.SmallRefinery);
            int availableWood = CountPlayerItem(player, ItemWood);
            int pullableWood = Mathf.Min(availableWood, fuelScan.RemainingCapacity);
            int totalWoodPotential = fuelScan.CurrentFuelAmount + pullableWood;

            // Reserve the flat finish headroom the integer fuel gate needs for the last crude
            // so the insertion cap and the pull math agree on what is actually finishable.
            float usableWood = totalWoodPotential - Mathf.Ceil(woodPerCrude);
            if (usableWood <= 0f) return 0;

            return Mathf.Max(0, Mathf.FloorToInt(usableWood / effectiveWoodPerCrude));
        }

        private object HandleRefineryCrudeInsertion(BaseOven oven, Item item, int splitAmount, BasePlayer actorPlayer)
        {
            try
            {
                var container = oven?.inventory;
                if (container == null || item?.info == null) return null;
                if (item.info.shortname != ItemCrudeOil) return null;

                int minSlot, maxSlot, slots;
                if (!TryGetOvenInputSlotRange(oven, container, out minSlot, out maxSlot, out slots)) return null;

                int existingCrude = 0;
                for (int i = minSlot; i <= maxSlot; i++)
                {
                    var it = container.GetSlot(i);
                    if (it == null) continue;

                    var isn = it.info?.shortname;
                    if (isn == null) continue;
                    if (isn != ItemCrudeOil) return null;

                    existingCrude += it.amount;
                }

                int itemAmount = GetMoveAmount(item, splitAmount);
                if (itemAmount <= 0) return null;

                int maxFinishableTotalCrude = GetRefineryFinishableCrudeAmount(oven, actorPlayer);
                int maxAdditionalByFuel = Math.Max(0, maxFinishableTotalCrude - existingCrude);
                if (maxAdditionalByFuel <= 0)
                {
                    if (actorPlayer != null)
                        QueueFuelRecalcNextTick(oven, actorPlayer);
                    return true;
                }

                int cap = CalculateInputSlotCapacity(item.info, slots);
                int maxAdditionalBySpace = Math.Max(0, cap - existingCrude);
                int allowedToMove = Math.Min(itemAmount, Math.Min(maxAdditionalByFuel, maxAdditionalBySpace));
                if (allowedToMove <= 0)
                {
                    if (actorPlayer != null)
                        QueueFuelRecalcNextTick(oven, actorPlayer);
                    return true;
                }

                int totalMoved = DistributeItemEvenlyAcrossInputSlots(container, item, minSlot, maxSlot, slots, existingCrude + allowedToMove, allowedToMove);
                if (totalMoved <= 0) return null;

                FinalizeCustomInsertion(oven, container, item, totalMoved, actorPlayer, "refinery crude insertion");
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

                int minSlot, maxSlot, slots;
                if (!TryGetOvenInputSlotRange(oven, container, out minSlot, out maxSlot, out slots)) return null;

                string sn = item.info.shortname;
                if (!IsOreShortname(sn)) return null;

                for (int i = minSlot; i <= maxSlot; i++)
                {
                    var it = container.GetSlot(i);
                    if (it == null) continue;

                    var isn = it.info?.shortname;
                    if (isn == null) continue;

                    if (IsOreShortname(isn) && isn != sn)
                    {
                        if (actorPlayer != null) QueueFuelRecalcNextTick(oven, actorPlayer);
                        return null;
                    }
                }

                int itemAmount = GetMoveAmount(item, splitAmount);
                if (itemAmount <= 0) return null;

                int existingTotal = CountMatchingInputItems(container, item.info, minSlot, maxSlot);
                int cap = CalculateInputSlotCapacity(item.info, slots);
                int totalAmount = Math.Min(existingTotal + itemAmount, cap);
                if (totalAmount <= existingTotal) return null;

                int totalMoved = DistributeItemEvenlyAcrossInputSlots(container, item, minSlot, maxSlot, slots, totalAmount, itemAmount);
                if (totalMoved <= 0) return null;

                FinalizeCustomInsertion(oven, container, item, totalMoved, actorPlayer, "ore input distribution");
                return true;
            }
            catch (Exception ex)
            {
                if (_config != null && _config.Debug)
                    PrintWarning($"DistributeOreIntoInputSlots exception: {ex}");
                return null;
            }
        }

        private bool TryGetOvenInputSlotRange(BaseOven oven, ItemContainer container, out int minSlot, out int maxSlot, out int slots)
        {
            minSlot = 0;
            maxSlot = -1;
            slots = 0;

            if (oven == null || container == null || container.capacity <= 0) return false;

            minSlot = oven._inputSlotIndex;
            if (minSlot < 0) minSlot = 0;

            slots = Math.Max(1, oven.inputSlots);
            maxSlot = minSlot + slots - 1;
            if (maxSlot >= container.capacity) maxSlot = container.capacity - 1;

            if (maxSlot < minSlot) return false;
            slots = maxSlot - minSlot + 1;
            return slots > 0;
        }

        private int GetMoveAmount(Item item, int requestedAmount)
        {
            if (item == null || item.amount <= 0) return 0;
            return requestedAmount > 0 ? Math.Min(requestedAmount, item.amount) : item.amount;
        }

        private int CalculateInputSlotCapacity(ItemDefinition definition, int slots)
        {
            if (definition == null || slots <= 0) return 0;

            long capLong = (long)Math.Max(0, definition.stackable) * (long)slots;
            if (capLong > int.MaxValue) capLong = int.MaxValue;
            return (int)capLong;
        }

        private int CountMatchingInputItems(ItemContainer container, ItemDefinition definition, int minSlot, int maxSlot)
        {
            if (container == null || definition == null) return 0;

            int total = 0;
            for (int i = minSlot; i <= maxSlot; i++)
            {
                var it = container.GetSlot(i);
                if (it != null && it.info == definition)
                    total += it.amount;
            }
            return total;
        }

        private int DistributeItemEvenlyAcrossInputSlots(ItemContainer container, Item sourceItem, int minSlot, int maxSlot, int slots, int targetTotal, int maxToMove)
        {
            if (container == null || sourceItem?.info == null) return 0;
            if (slots <= 0 || targetTotal <= 0 || maxToMove <= 0) return 0;

            int baseAmt = targetTotal / slots;
            int rem = targetTotal - baseAmt * slots;
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
                    // Never merge stacks with a different skin — that would silently destroy the skin.
                    if (cur.info != sourceItem.info || cur.skin != sourceItem.skin) return totalMoved;
                    curAmt = cur.amount;
                }

                int delta = target - curAmt;
                if (delta <= 0) continue;

                if (cur == null)
                {
                    var newItem = ItemManager.Create(sourceItem.info, delta, sourceItem.skin);
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
                if (totalMoved >= maxToMove) break;
            }

            return totalMoved;
        }

        private void FinalizeCustomInsertion(BaseOven oven, ItemContainer container, Item item, int movedAmount, BasePlayer actorPlayer, string context)
        {
            var originalParent = item.parent;
            var ownerPlayer = actorPlayer ?? item.GetOwnerPlayer();

            if (movedAmount >= item.amount)
                item.Remove();
            else
            {
                item.amount -= movedAmount;
                item.MarkDirty();
            }

            SafeMarkDirty(originalParent, $"item parent after {context}");

            if (ownerPlayer != null)
            {
                SafeInventoryUpdate(ownerPlayer, $"owner inventory after {context}");
                SafePlayerNetworkUpdate(ownerPlayer, $"owner network after {context}");
            }

            container.MarkDirty();
            oven.SendNetworkUpdateImmediate();

            if (actorPlayer != null) QueueFuelRecalcNextTick(oven, actorPlayer);
            else if (ownerPlayer != null) QueueFuelRecalcNextTick(oven, ownerPlayer);
        }

        private void SafeMarkDirty(ItemContainer container, string context)
        {
            try { container?.MarkDirty(); }
            catch (Exception ex)
            {
                if (_config != null && _config.Debug)
                    PrintWarning($"SmartSmelt safe MarkDirty failed ({context}): {ex.Message}");
            }
        }

        private void SafeInventoryUpdate(BasePlayer player, string context)
        {
            try { player?.inventory?.ServerUpdate(0f); }
            catch (Exception ex)
            {
                if (_config != null && _config.Debug)
                    PrintWarning($"SmartSmelt safe inventory update failed ({context}): {ex.Message}");
            }
        }

        private void SafePlayerNetworkUpdate(BasePlayer player, string context)
        {
            try { player?.SendNetworkUpdateImmediate(); }
            catch (Exception ex)
            {
                if (_config != null && _config.Debug)
                    PrintWarning($"SmartSmelt safe player network update failed ({context}): {ex.Message}");
            }
        }

        private object CanMoveItem(Item item, PlayerInventory playerInventory, ItemContainer targetContainer, int targetSlot, int amount)
        {
            try
            {
                if (!_config.Enabled) return null;
                if (item?.info == null || targetContainer == null) return null;

                var owner = targetContainer.entityOwner as BaseOven;
                if (owner == null || owner.IsDestroyed) return null;
                if (!IsWhitelistedSmeltingOven(owner)) return null;

                var src = item.parent;
                if (src != null && src.entityOwner is BaseOven) return null;

                var player = playerInventory?.GetComponent<BasePlayer>();

                return HandleOvenInsertion(owner, item, amount, player);
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

            DisposeStartupScanEnumerator();
            _startupScanTracked = 0;
            _startupScanEnumerator = BaseNetworkable.serverEntities.GetEnumerator();
            ContinueStartupOvenScan();
        }

        private void ContinueStartupOvenScan()
        {
            if (!_config.Enabled || _startupScanEnumerator == null)
            {
                DisposeStartupScanEnumerator();
                return;
            }

            bool completed = false;
            int processedThisSlice = 0;

            try
            {
                while (processedThisSlice < StartupScanEntitiesPerSlice)
                {
                    if (!_startupScanEnumerator.MoveNext())
                    {
                        completed = true;
                        break;
                    }

                    processedThisSlice++;
                    var oven = _startupScanEnumerator.Current as BaseOven;
                    if (oven == null || oven.IsDestroyed) continue;
                    if (!oven.IsOn()) continue;
                    if (!IsWhitelistedSmeltingOven(oven)) continue;

                    StartTracking(oven);
                    _startupScanTracked++;
                }
            }
            catch (Exception ex)
            {
                completed = true;
                if (_config != null && _config.Debug)
                    PrintWarning($"Startup oven scan exception: {ex}");
            }

            if (completed)
            {
                int tracked = _startupScanTracked;
                DisposeStartupScanEnumerator();

                if (_config != null && _config.Debug)
                    Puts($"Scan complete. Tracking {tracked} running ovens.");
                return;
            }

            timer.Once(StartupScanSliceDelaySeconds, ContinueStartupOvenScan);
        }

        private void DisposeStartupScanEnumerator()
        {
            if (_startupScanEnumerator != null)
            {
                _startupScanEnumerator.Dispose();
                _startupScanEnumerator = null;
            }

            _startupScanTracked = 0;
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
            tracker.NextTickAt = tracker.LastBalanceTime + UnityEngine.Random.Range(0f, Mathf.Max(MinGlobalLoopInterval, preset.CycleSeconds));

            _active[id] = tracker; MarkActiveChanged();

            if (_config.VerboseTrackingLogs)
                Puts($"Tracking oven {oven.ShortPrefabName} ({id}) using preset {_config.Preset}.");
        }

        private void StopTracking(BaseOven oven)
        {
            if (oven == null || oven.net == null) return;

            ulong id = oven.net.ID.Value;
            _cachedOvenPrefabNamesLower.Remove(id);
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

        private string GetCachedOvenPrefabNameLower(BaseOven oven)
        {
            if (oven == null) return string.Empty;

            ulong id = oven.net != null ? oven.net.ID.Value : 0ul;
            if (id != 0ul && _cachedOvenPrefabNamesLower.TryGetValue(id, out var cached))
                return cached;

            string lowered = (oven.ShortPrefabName ?? string.Empty).ToLowerInvariant();
            if (id != 0ul)
                _cachedOvenPrefabNamesLower[id] = lowered;

            return lowered;
        }

        private OvenKind GetKind(BaseOven oven)
        {
            if (oven == null) return OvenKind.Unknown;

            var sn = GetCachedOvenPrefabNameLower(oven);

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

            var name = GetCachedOvenPrefabNameLower(oven);
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

            int step = 0;
            for (; step < count && processed < cap; step++)
            {
                int idx = (startIndex + step) % count;
                ulong id = _tmpTrackerIds[idx];

                if (!_active.TryGetValue(id, out var tracker) || tracker == null)
                    continue;

                var oven = tracker.Oven;
                if (oven == null || oven.IsDestroyed || oven.net == null)
                {
                    _active.Remove(id);
                    _cachedOvenPrefabNamesLower.Remove(id);
                    MarkActiveChanged();
                    continue;
                }

                if (!oven.IsOn())
                {
                    tracker.OffCycles++;
                    if (tracker.OffCycles >= MaxOffCyclesBeforeEvict)
                    {
                        _active.Remove(id);
                        _cachedOvenPrefabNamesLower.Remove(id);
                        MarkActiveChanged();
                    }
                    continue;
                }
                tracker.OffCycles = 0;

                if (now < tracker.NextTickAt)
                    continue;

                var effectivePreset = GetEffectivePresetForKind(preset, tracker.Kind);
                tracker.NextTickAt = now + Mathf.Max(MinGlobalLoopInterval, effectivePreset.CycleSeconds);

                TickOven(tracker, effectivePreset, multiplier, isInstantPreset);
                processed++;
            }

            EnsureGlobalTimer(ComputeGlobalLoopInterval(count));

            // Advance the cursor past everything examined this tick so ovens beyond the
            // per-tick cap are not starved when the tracked count exceeds the cap.
            _globalCursor = (startIndex + Math.Max(1, step)) % count;
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

                var inputs = tracker.InputsBuffer;
                FillSmeltableInputs(container, kind, inputs);
                if (inputs.Count == 0)
                {
                    // Furnaces still burn wood natively when they contain no ore. Add only
                    // the preset's extra burn so wood-only charcoal production stays in sync.
                    if (kind != OvenKind.ElectricFurnace && kind != OvenKind.SmallRefinery)
                        ConsumeIdleFuel(tracker, kind, container, preset, multiplier, 0, isInstantPreset);

                    tracker.Cycles++;
                    return;
                }

                bool gateOnWood = tracker.GateOnWood;
                float woodAvailableFloat = gateOnWood ? CountItem(container, ItemWood) : int.MaxValue;

                if (ShouldPauseForCharcoalOverflow(oven, container, gateOnWood))
                    return;

                int totalConsumed = 0;
                int remainingBudget = preset.MaxTotalConsumedPerCycle;
                int safetyPasses = 3;

                while (remainingBudget > 0 && inputs.Count > 0 && safetyPasses-- > 0)
                {
                    if (!HasActiveInputs(inputs)) break;

                    int budgetBefore = remainingBudget;

                    ProcessSingleItemPass(tracker, kind, container, inputs, preset, gateOnWood, ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);

                    if (remainingBudget <= 0) break;

                    ProcessAllocatedInputPass(tracker, kind, container, inputs, preset, gateOnWood, ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);

                    if (remainingBudget == budgetBefore) break;
                }

                if (kind != OvenKind.ElectricFurnace && kind != OvenKind.SmallRefinery)
                    ConsumeIdleFuel(tracker, kind, container, preset, multiplier, totalConsumed, isInstantPreset);

                tracker.Cycles++;

                if (_config.Debug && _config.VerboseCycleLogs && totalConsumed > 0)
                    Puts($"{oven.ShortPrefabName} processed {totalConsumed} items this cycle (cycle #{tracker.Cycles}).");
            }
            catch (Exception ex)
            {
                PrintWarning($"TickOven exception: {ex}");
            }
        }

        private bool ShouldPauseForCharcoalOverflow(BaseOven oven, ItemContainer container, bool gateOnWood)
        {
            if (!gateOnWood) return false;
            if (!_config.ProduceCharcoalFromFuel) return false;
            if (!string.Equals(_config.CharcoalOverflowMode, "Pause", StringComparison.OrdinalIgnoreCase)) return false;
            if (CanGiveOutput(container, ItemCharcoal, 1)) return false;

            if (_config.Debug)
                Puts($"{oven.ShortPrefabName}: Charcoal output blocked (Pause mode). Skipping accelerated smelting this cycle.");

            return true;
        }

        private bool HasActiveInputs(List<Item> inputs)
        {
            for (int i = 0; i < inputs.Count; i++)
            {
                var it = inputs[i];
                if (it != null && it.amount > 0)
                    return true;
            }
            return false;
        }

        private void ProcessSingleItemPass(
            OvenTracker tracker,
            OvenKind kind,
            ItemContainer container,
            List<Item> inputs,
            PresetTuning preset,
            bool gateOnWood,
            ref float woodAvailableFloat,
            ref int remainingBudget,
            ref int totalConsumed)
        {
            for (int i = 0; i < inputs.Count && remainingBudget > 0; i++)
            {
                var input = inputs[i];
                int canTake = GetAllowedInputTake(kind, input, 1, preset.MaxConsumedPerStackPerCycle, remainingBudget, gateOnWood, woodAvailableFloat);
                if (canTake <= 0) continue;

                ConvertAndRecordInput(tracker, kind, container, input, canTake, gateOnWood, ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);
            }
        }

        private void ProcessAllocatedInputPass(
            OvenTracker tracker,
            OvenKind kind,
            ItemContainer container,
            List<Item> inputs,
            PresetTuning preset,
            bool gateOnWood,
            ref float woodAvailableFloat,
            ref int remainingBudget,
            ref int totalConsumed)
        {
            int totalCap = 0;
            int[] caps = PoolGetIntArray(inputs.Count);
            try
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    int cap = GetAllowedInputTake(kind, input, input != null ? input.amount : 0, preset.MaxConsumedPerStackPerCycle, remainingBudget, gateOnWood, woodAvailableFloat);
                    caps[i] = cap;
                    totalCap += cap;
                }

                if (totalCap <= 0) return;

                int[] allocs = PoolGetIntArray(inputs.Count);
                try
                {
                    AllocateInputBudget(caps, allocs, inputs.Count, remainingBudget, totalCap);

                    for (int i = 0; i < inputs.Count && remainingBudget > 0; i++)
                    {
                        int alloc = allocs[i];
                        if (alloc <= 0) continue;

                        var input = inputs[i];
                        int canTake = GetAllowedInputTake(kind, input, alloc, preset.MaxConsumedPerStackPerCycle, remainingBudget, gateOnWood, woodAvailableFloat);
                        if (canTake <= 0) continue;

                        ConvertAndRecordInput(tracker, kind, container, input, canTake, gateOnWood, ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);
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
        }

        private void AllocateInputBudget(int[] caps, int[] allocs, int count, int remainingBudget, int totalCap)
        {
            int allocated = 0;
            for (int i = 0; i < count; i++)
            {
                int cap = caps[i];
                if (cap <= 0)
                {
                    allocs[i] = 0;
                    continue;
                }

                int alloc = (int)((long)remainingBudget * (long)cap / (long)totalCap);
                if (alloc > cap) alloc = cap;
                allocs[i] = alloc;
                allocated += alloc;
            }

            int remainingToAllocate = remainingBudget - allocated;
            if (remainingToAllocate <= 0) return;

            for (int pass = 0; pass < count && remainingToAllocate > 0; pass++)
            {
                for (int i = 0; i < count && remainingToAllocate > 0; i++)
                {
                    int cap = caps[i];
                    if (cap <= 0) continue;
                    if (allocs[i] >= cap) continue;
                    allocs[i]++;
                    remainingToAllocate--;
                }
            }
        }

        private int GetAllowedInputTake(
            OvenKind kind,
            Item input,
            int requested,
            int maxPerStack,
            int remainingBudget,
            bool gateOnWood,
            float woodAvailableFloat)
        {
            if (input == null || input.amount <= 0 || requested <= 0 || remainingBudget <= 0) return 0;

            int canTake = requested;
            if (canTake > maxPerStack) canTake = maxPerStack;
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

            return canTake;
        }

        private void ConvertAndRecordInput(
            OvenTracker tracker,
            OvenKind kind,
            ItemContainer container,
            Item input,
            int canTake,
            bool gateOnWood,
            ref float woodAvailableFloat,
            ref int remainingBudget,
            ref int totalConsumed)
        {
            if (!TryConvertInput(kind, container, input, canTake, out int consumedNow))
                return;

            totalConsumed += consumedNow;
            remainingBudget -= consumedNow;

            if (gateOnWood)
            {
                string sn = input.info.shortname;
                float wpi = GetWoodPerInput(kind, sn);
                if (wpi > 0f)
                {
                    float woodConsumed = consumedNow * wpi;
                    woodAvailableFloat -= woodConsumed;
                    ConsumeWoodDebt(tracker, container, woodConsumed);
                }
            }
        }

        #endregion
        #region Small Pools

        private readonly Stack<int[]> _intArrayPool = new Stack<int[]>();

        private int[] PoolGetIntArray(int size)
        {
            while (_intArrayPool.Count > 0)
            {
                var arr = _intArrayPool.Pop();
                if (arr != null && arr.Length >= size)
                {
                    Array.Clear(arr, 0, Math.Min(arr.Length, size));
                    return arr;
                }
            }
            return new int[size];
        }

        private void PoolReturnIntArray(int[] arr)
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
        private const float SmallFurnace_BaselineWoodPerSecond = 0.5f;
        private const float LargeFurnace_BaselineWoodPerSecond = 1.0f;
        private const float Refinery_BaselineWoodPerSecond = 0.5f;

        private float GetWoodCostScale()
        {
            if (_config == null || !_config.ReducedWoodCostEnabled) return 1f;
            return Mathf.Clamp(_config.WoodCostScale, 0f, 1f);
        }

        private float GetBaselineWoodPerSecond(OvenKind kind)
        {
            switch (kind)
            {
                case OvenKind.SmallFurnace: return SmallFurnace_BaselineWoodPerSecond;
                case OvenKind.LargeFurnace: return LargeFurnace_BaselineWoodPerSecond;
                case OvenKind.SmallRefinery: return Refinery_BaselineWoodPerSecond;
                default: return 0f;
            }
        }

        // How many inputs the plugin can process per cycle for this oven kind. Refineries
        // hold a single input stack so the per-stack cap binds; furnaces split ore across
        // input slots and reach the per-cycle total.
        private int GetPresetThroughputPerCycle(OvenKind kind, out float cycleSeconds)
        {
            RefreshCachedPreset();
            var preset = GetEffectivePresetForKind(_cachedPresetTuning, kind);
            cycleSeconds = Mathf.Max(MinGlobalLoopInterval, preset.CycleSeconds);

            if (preset.MaxTotalConsumedPerCycle == int.MaxValue)
                return int.MaxValue;

            return kind == OvenKind.SmallRefinery
                ? Mathf.Min(preset.MaxConsumedPerStackPerCycle, preset.MaxTotalConsumedPerCycle)
                : preset.MaxTotalConsumedPerCycle;
        }

        // Wood the vanilla burn consumes per input unit while the plugin smelts at full
        // speed. The native burn is not suppressed by this plugin, so the pull math must
        // cover it or the final units strand without fuel.
        private float GetNativeBurnWoodPerUnit(OvenKind kind)
        {
            float burnPerSecond = GetBaselineWoodPerSecond(kind);
            if (burnPerSecond <= 0f) return 0f;

            int perCycle = GetPresetThroughputPerCycle(kind, out float cycleSeconds);
            if (perCycle <= 0 || perCycle == int.MaxValue) return 0f;

            return cycleSeconds * burnPerSecond / perCycle;
        }

        private float EstimateNativeBurnWood(OvenKind kind, int totalInputUnits)
        {
            if (totalInputUnits <= 0) return 0f;

            float burnPerSecond = GetBaselineWoodPerSecond(kind);
            if (burnPerSecond <= 0f) return 0f;

            int perCycle = GetPresetThroughputPerCycle(kind, out float cycleSeconds);
            if (perCycle <= 0 || perCycle == int.MaxValue) return 0f;

            // One extra cycle covers the partially-filled final cycle.
            float estimatedSeconds = (Mathf.Ceil(totalInputUnits / (float)perCycle) + 1f) * cycleSeconds;
            return estimatedSeconds * burnPerSecond;
        }

        private float GetWoodPerInput(OvenKind kind, string inputShortname)
        {
            return GetVanillaWoodPerInput(kind, inputShortname) * GetWoodCostScale();
        }

        private float GetVanillaWoodPerInput(OvenKind kind, string inputShortname)
        {
            if (string.IsNullOrEmpty(inputShortname)) return 0f;

            if (kind == OvenKind.SmallRefinery)
            {
                return inputShortname == ItemCrudeOil ? Refinery_WoodPerCrudeOil : 0f;
            }

            bool large = kind == OvenKind.LargeFurnace;

            switch (inputShortname)
            {
                case ItemMetalOre:
                    return large ? LargeFurnace_WoodPerMetalOre : SmallFurnace_WoodPerMetalOre;
                case ItemSulfurOre:
                    return large ? LargeFurnace_WoodPerSulfurOre : SmallFurnace_WoodPerSulfurOre;
                case ItemHqMetalOre:
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
                if (string.Equals(it.info.shortname, shortname, StringComparison.Ordinal))
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
                int maxCharcoalCapacity = GetAdditionalCapacity(container, ItemCharcoal);
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

            int removed = RemoveFuel(container, ItemWood, desiredRemove);
            tracker.FuelDebt -= removed;

            // The oven ran out of wood: keep only a fractional carry so debt cannot grow
            // unbounded and instantly drain wood the player adds later.
            if (removed < desiredRemove && tracker.FuelDebt > 1f)
                tracker.FuelDebt = 1f;

            if (removed > 0 && _config.ProduceCharcoalFromFuel)
            {
                float ratio = Mathf.Max(0f, _config.CharcoalPerWood);
                float exact = removed * ratio + tracker.CharcoalRemainder;

                int charcoalToGive = Mathf.FloorToInt(exact);
                tracker.CharcoalRemainder = exact - charcoalToGive;

                if (charcoalToGive > 0)
                {
                    if (!TryGiveOutput(container, ItemCharcoal, charcoalToGive))
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

            float now = Time.realtimeSinceStartup;
            float elapsed = tracker.LastBalanceTime > 0f ? Mathf.Max(0.01f, now - tracker.LastBalanceTime) : Mathf.Max(0.01f, preset.CycleSeconds);

            // Cap elapsed at a few cycles so a long stretch without idle burn (e.g. while
            // actively smelting, or while the oven sat without inputs) is not billed retroactively.
            elapsed = Mathf.Min(elapsed, Mathf.Max(1f, preset.CycleSeconds * 4f));
            tracker.LastBalanceTime = now;

            if (isInstantPreset) return;
            if (itemsSmeltedThisTick > 0) return;

            float baselineWoodPerSecond = GetBaselineWoodPerSecond(kind);
            if (baselineWoodPerSecond <= 0f) return;

            // Rust already consumes the native 1x share. SmartSmelt contributes only the
            // additional preset share, preventing (for example) a 10x preset becoming 11x.
            float additionalMultiplier = Mathf.Max(0f, multiplier - 1f);
            float targetThisTick = baselineWoodPerSecond * additionalMultiplier * GetWoodCostScale() * elapsed;
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
                if (!string.Equals(it.info.shortname, shortname, StringComparison.Ordinal)) continue;

                int take = Mathf.Min(it.amount, remaining);
                it.UseItem(take);
                removed += take;
                remaining -= take;
            }

            return removed;
        }

        #endregion

        #region Smelt conversion

        private void FillSmeltableInputs(ItemContainer container, OvenKind kind, List<Item> results)
        {
            results.Clear();
            if (container?.itemList == null) return;

            foreach (var it in container.itemList)
            {
                if (it?.info == null || it.amount <= 0) continue;

                string sn = it.info.shortname;

                if (kind == OvenKind.SmallRefinery)
                {
                    if (sn == ItemCrudeOil)
                        results.Add(it);
                }
                else if (sn == ItemMetalOre || sn == ItemSulfurOre || sn == ItemHqMetalOre)
                {
                    results.Add(it);
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
                if (inSn != ItemCrudeOil) return false;
                outSn = ItemLowGradeFuel;
                outPerIn = 3;
            }
            else
            {
                switch (inSn)
                {
                    case ItemMetalOre:
                        outSn = ItemMetalFragments;
                        outPerIn = 1;
                        break;
                    case ItemSulfurOre:
                        outSn = ItemSulfur;
                        outPerIn = 1;
                        break;
                    case ItemHqMetalOre:
                        outSn = ItemMetalRefined;
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

            var def = GetCachedItemDefinition(shortname);
            if (def == null) return false;

            // All-or-nothing: never partially deliver output, because the caller only
            // consumes input when this returns true. A partial give would mint free items.
            if (GetAdditionalCapacity(container, shortname) < amount) return false;

            var toppedUp = GetPooledItemList();
            var created = GetPooledItemList();
            int[] toppedAmounts = PoolGetIntArray(Mathf.Max(1, container.itemList.Count));
            int remaining = amount;

            try
            {
                foreach (var it in container.itemList)
                {
                    if (remaining <= 0) break;
                    if (it?.info == null) continue;
                    if (it.info.itemid != def.itemid) continue;

                    int maxStack = it.MaxStackable();
                    if (it.amount >= maxStack) continue;

                    int can = Mathf.Min(remaining, maxStack - it.amount);
                    toppedAmounts[toppedUp.Count] = can;
                    toppedUp.Add(it);
                    it.amount += can;
                    it.MarkDirty();
                    remaining -= can;
                }

                while (remaining > 0)
                {
                    int give = Mathf.Min(remaining, def.stackable);

                    var item = ItemManager.Create(def, give);
                    if (item == null || !item.MoveToContainer(container))
                    {
                        if (item != null) item.Remove();
                        RollbackOutputGive(container, toppedUp, toppedAmounts, created);
                        return false;
                    }

                    created.Add(item);
                    remaining -= give;
                }

                return true;
            }
            finally
            {
                PoolReturnIntArray(toppedAmounts);
                ReturnPooledItemList(toppedUp);
                ReturnPooledItemList(created);
            }
        }

        private void RollbackOutputGive(ItemContainer container, List<Item> toppedUp, int[] toppedAmounts, List<Item> created)
        {
            for (int i = 0; i < toppedUp.Count; i++)
            {
                var it = toppedUp[i];
                if (it == null) continue;

                it.amount -= toppedAmounts[i];
                if (it.amount <= 0) it.Remove();
                else it.MarkDirty();
            }

            for (int i = 0; i < created.Count; i++)
            {
                var it = created[i];
                if (it == null) continue;
                if (it.parent == container) it.Remove();
            }
        }
        private int GetAdditionalCapacity(ItemContainer container, string shortname)
        {
            if (container == null) return 0;

            ItemDefinition def = GetCachedItemDefinition(shortname);
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
