using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("SmartSmelt", "SeesAll", "1.2.16")]
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
            public int ConfigVersion = 4;
            public string Preset = "10x";
            public string PresetOptions = "1x, 2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, instant";

            public bool AutoTuneEnabled = true;
            public int AveragePopulation = 100;
            public string AutoTuneBias = "Balanced";
            public bool AutoTuneWriteToConfig = false;

            public bool EnableOreSplitting = true;
            public bool EnableMixingTableScaling = true;
            public bool EnableConveyorBoosts = true;
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

            // Save the new conveyor setting on upgrade while preserving existing values.
            if (_config.ConfigVersion < 4)
            {
                _config.ConfigVersion = 4;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(_config.PresetOptions) ||
                _config.PresetOptions == "2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, instant")
            {
                _config.PresetOptions = "1x, 2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, instant";
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
        private readonly Dictionary<ulong, ulong> _pendingFuelRecalcPlayers = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, bool> _pendingAutomationToggleStates = new Dictionary<ulong, bool>();
        private void RefreshEffectiveScheduling(bool allowWriteToConfig)
        {
            if (_config == null) return;

            _effAdaptiveScaling = _config.AdaptiveScaling;
            _effAdaptiveMinOvensPerTick = Mathf.Clamp(_config.AdaptiveMinOvensPerTick, 1, 10000);
            _effAdaptiveMaxOvensPerTick = Mathf.Clamp(_config.AdaptiveMaxOvensPerTick, _effAdaptiveMinOvensPerTick, 10000);

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
            if (!SmeltingEnabled) return;
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
                    if (player == null || !player.IsConnected || player.inventory?.loot?.entitySource != oven) return;
                    TryAutoPullFuel(oven, player);
                });
                return;
            }

            ulong playerId = player.userID;
            if (_pendingFuelRecalcPlayers.TryGetValue(id, out var pendingPlayerId))
            {
                // Multiple requests from the same player still collapse into one scan. A
                // different player makes ownership ambiguous, so skip automatic withdrawal.
                if (pendingPlayerId != playerId)
                    _pendingFuelRecalcPlayers[id] = 0ul;
                return;
            }

            _pendingFuelRecalcPlayers[id] = playerId;
            NextTick(() =>
            {
                if (!_pendingFuelRecalcPlayers.TryGetValue(id, out var resolvedPlayerId))
                    return;

                _pendingFuelRecalcPlayers.Remove(id);

                if (resolvedPlayerId == 0ul || resolvedPlayerId != playerId) return;
                if (oven == null || oven.IsDestroyed) return;
                if (player == null || !player.IsConnected || player.inventory?.loot?.entitySource != oven) return;

                TryAutoPullFuel(oven, player);
            });
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
        private const string VanillaPresetKey = "1x";
        private bool SmeltingEnabled => _config != null && _config.Enabled && _config.Preset != VanillaPresetKey;
        private bool BoostsEnabled => SmeltingEnabled && _config.EnableConveyorBoosts;

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
            ["inst"] = InstantPresetKey,
            ["1"] = VanillaPresetKey,
            ["vanilla"] = VanillaPresetKey
        };

        private static readonly Dictionary<string, PresetDefinition> PresetDefinitions = new Dictionary<string, PresetDefinition>(StringComparer.Ordinal)
        {
            [VanillaPresetKey] = MakePreset(0.5f, 0, 0, 1),
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
        private readonly HashSet<(ulong, int, ulong)> _pendingPostMoveRebalances = new HashSet<(ulong, int, ulong)>();
        private readonly HashSet<ulong> _postMoveRebalancesInProgress = new HashSet<ulong>();

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
            _pendingFuelRecalcPlayers.Clear();
            _pendingAutomationToggleStates.Clear();
            _pendingPostMoveRebalances.Clear();
            _postMoveRebalancesInProgress.Clear();
            MarkActiveChanged();
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermDebug, this);
            _globalTimer?.Destroy();
            _globalTimer = null;
            if (SmeltingEnabled)
            {
                EnsureGlobalTimer(ComputeGlobalLoopInterval(0));
                timer.Once(1f, ScanAndTrackRunningOvens);
            }
        }

        private void Unload()
        {
            RemoveMixedWoodPatch();
            _conveyorBoostTimer?.Destroy();
            _conveyorBoostTimer = null;
            _conveyorBoosts.Clear();
            _knownConveyors.Clear();
            _conveyorCandidates.Clear();
            _conveyorBoostOptOuts.Clear();
            _queuedConveyorExtras.Clear();
            _globalTimer?.Destroy();
            _globalTimer = null;
            DisposeStartupScanEnumerator();
            _active.Clear();
            _cachedOvenPrefabNamesLower.Clear();
            _pendingFuelRecalcPlayers.Clear();
            _pendingAutomationToggleStates.Clear();
            _pendingPostMoveRebalances.Clear();
            _postMoveRebalancesInProgress.Clear();
            MarkActiveChanged();
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item, BasePlayer sourcePlayer)
        {
            HandleInputArrival(container, item, sourcePlayer);
        }

        private void OnItemStacked(Item destination, Item source, ItemContainer container, int amount)
        {
            HandleInputArrival(container, destination, source?.GetOwnerPlayer());
        }

        private void HandleInputArrival(ItemContainer container, Item item, BasePlayer sourcePlayer)
        {
            if (!SmeltingEnabled) return;
            if (container == null || item?.info == null) return;

            var oven = container.entityOwner as BaseOven;
            if (oven == null || oven.IsDestroyed || container != oven.inventory) return;
            if (oven.net == null || _postMoveRebalancesInProgress.Contains(oven.net.ID.Value)) return;
            if (!IsWhitelistedSmeltingOven(oven)) return;
            if (!TryGetOvenInputSlotRange(oven, container, out int minSlot, out int maxSlot, out _) ||
                item.position < minSlot || item.position > maxSlot) return;

            var kind = GetKind(oven);
            string shortname = item.info.shortname;
            if (kind == OvenKind.SmallRefinery)
            {
                if (shortname != ItemCrudeOil) return;
            }
            else if (!IsOreShortname(shortname))
            {
                return;
            }

            // Capture the item type that caused the hook. For mixed-ore furnaces this lets
            // the fallback spread only the newly-added type into its own and empty slots,
            // without disturbing ore types the player already arranged.
            QueuePostMoveInputRebalance(
                oven,
                sourcePlayer,
                item.info,
                item.skin,
                kind == OvenKind.SmallRefinery || _config.EnableOreSplitting);
        }
        private void OnEntityKill(BaseNetworkable ent)
        {
            if (ent is IndustrialConveyor conveyor && conveyor.net != null)
            {
                ulong id = conveyor.net.ID.Value;
                _knownConveyors.Remove(id);
                _conveyorBoostOptOuts.Remove(id);
                _queuedConveyorExtras.Remove(id);
                _conveyorBoosts.Remove(id);
            }

            var oven = ent as BaseOven;
            if (oven == null) return;
            StopTracking(oven);
        }

        private void OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (!SmeltingEnabled || oven == null) return;

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
            if (!SmeltingEnabled || !_config.EnableMixingTableScaling)
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
            if (!SmeltingEnabled || entity == null) return;
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
            if (!SmeltingEnabled || oven == null || oven.IsDestroyed) return;
            if (!oven.IsOn()) return;
            if (!IsWhitelistedSmeltingOven(oven)) return;
            StartTracking(oven);
        }
        #region Commands
        private const float ConveyorBoostCheckSeconds = 0.5f;
        private const float MinimumExtraConveyorInterval = 1f;
        private const double OutputPassRateIncrease = 0.175d;
        private const int MinimumOutputTransferBoostPreset = 5;
        private const int ConveyorCandidatesPerCheck = 12;
        private readonly Dictionary<ulong, ConveyorBoost> _conveyorBoosts = new Dictionary<ulong, ConveyorBoost>();
        private readonly Queue<IndustrialConveyor> _conveyorCandidates = new Queue<IndustrialConveyor>();
        private readonly HashSet<ulong> _knownConveyors = new HashSet<ulong>();
        private readonly HashSet<ulong> _conveyorBoostOptOuts = new HashSet<ulong>();
        private readonly Dictionary<ulong, ConveyorBoostMode> _queuedConveyorExtras = new Dictionary<ulong, ConveyorBoostMode>();
        private Timer _conveyorBoostTimer;

        private enum ConveyorBoostMode { None, Output, Wood, MixedWood }
        private const string MixedWoodHarmonyId = "SeesAll.SmartSmelt.MixedWood";
        private static SmartSmelt _mixedWoodPatchOwner;
        private Harmony _mixedWoodHarmony;
        private MethodBase _mixedWoodMethod;
        private MethodBase _nativeConveyorScheduleMethod;
        private bool _conveyorPatchUnavailable;

        private sealed class ConveyorBoost
        {
            public ConveyorBoostMode Mode;
            public float NextCheck;
            public long ExtraPassesQueued;
            public long WoodLimitsRaised;
            public long OutputLimitsRaised;
            public double? PassWoodFactor;
            public double? PassOutputFactor;
            public string Status = "waiting for first check";
        }

        private void OnEntitySpawned(IndustrialConveyor conveyor) => TrackConveyorCandidate(conveyor);

        private void TrackConveyorCandidate(IndustrialConveyor conveyor)
        {
            if (!BoostsEnabled || conveyor == null || conveyor.IsDestroyed ||
                conveyor.net == null || !_knownConveyors.Add(conveyor.net.ID.Value)) return;
            _conveyorCandidates.Enqueue(conveyor);
            if (_conveyorBoostTimer == null)
                _conveyorBoostTimer = timer.Every(ConveyorBoostCheckSeconds, CheckConveyorBoosts);
        }

        [ConsoleCommand("ss.conveyor")]
        private void ConsoleConveyorBoost(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;

            string action = arg.GetString(0, "status");
            if (action == "discover")
            {
                bool includeIneligible = arg.GetString(1, "") == "all";
                var lines = new List<string>();
                foreach (var entity in BaseNetworkable.serverEntities)
                {
                    if (!(entity is IndustrialConveyor candidate) || candidate.IsDestroyed || candidate.net == null) continue;
                    var mode = GetConveyorBoostMode(candidate);
                    if (mode == ConveyorBoostMode.None && !includeIneligible) continue;
                    string details = includeIneligible
                        ? $" [mode {candidate.mode}, filters: {string.Join(", ", candidate.filterItems.ConvertAll(filter =>
                            filter.TargetCategory.HasValue ? $"category:{filter.TargetCategory.Value}" :
                            (filter.TargetItem?.shortname ?? "(unresolved)") + (filter.IsBlueprint ? " (blueprint)" : "")))}]"
                        : "";
                    string state = mode == ConveyorBoostMode.None ? "ineligible filters" :
                        GetConveyorBoostBlockReason(candidate, mode, out _) ?? "eligible";
                    lines.Add($"Conveyor {candidate.net.ID.Value} ({mode}): {state}.{details}");
                    if (lines.Count == 12) break;
                }
                arg.ReplyWith(lines.Count > 0 ? string.Join("\n", lines) :
                    includeIneligible ? "No conveyors found." : "No conveyors with smelted-product or wood-only filters found.");
                return;
            }

            if (action == "status")
            {
                if (!BoostsEnabled)
                {
                    arg.ReplyWith("SmartSmelt automatic conveyor boosts are off in config or vanilla mode.");
                    return;
                }
                var lines = new List<string> { $"Automatic boosts on; {_conveyorBoosts.Count} matching conveyors." };
                foreach (var pair in _conveyorBoosts)
                {
                    var boost = pair.Value;
                    string counter = boost.Mode == ConveyorBoostMode.MixedWood
                        ? $"wood limits raised: {boost.WoodLimitsRaised}"
                        : $"extra passes queued: {boost.ExtraPassesQueued}";
                    if (boost.Mode == ConveyorBoostMode.Wood && _cachedPresetMultiplier >= 10)
                        counter += $"; wood limits raised: {boost.WoodLimitsRaised}";
                    if (boost.Mode == ConveyorBoostMode.Output && _cachedPresetMultiplier >= MinimumOutputTransferBoostPreset)
                        counter += $"; output limits raised: {boost.OutputLimitsRaised}";
                    lines.Add($"Conveyor {pair.Key} ({boost.Mode}): {boost.Status}; {counter}.");
                    if (lines.Count >= 21) break;
                }
                arg.ReplyWith(string.Join("\n", lines));
                return;
            }

            if ((action != "add" && action != "remove") ||
                !ulong.TryParse(arg.GetString(1, ""), out ulong id) || id == 0)
            {
                arg.ReplyWith("Usage: ss.conveyor status | discover [all] | add <conveyor network ID> | remove <conveyor network ID>");
                return;
            }

            if (action == "remove")
            {
                _conveyorBoostOptOuts.Add(id);
                _conveyorBoosts.Remove(id);
                arg.ReplyWith($"Conveyor {id} opted out until SmartSmelt reloads or you add it again.");
                return;
            }

            if (!BoostsEnabled)
            {
                arg.ReplyWith("Conveyor boosts are disabled by configuration or vanilla mode.");
                return;
            }

            var conveyor = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as IndustrialConveyor;
            if (conveyor == null || conveyor.IsDestroyed)
            {
                arg.ReplyWith($"No live Industrial Conveyor has network ID {id}.");
                return;
            }

            var boostMode = GetConveyorBoostMode(conveyor);
            if (boostMode == ConveyorBoostMode.None)
            {
                arg.ReplyWith($"Conveyor {id} needs Any mode and filters for wood only, wood with raw ores, or finished furnace products only.");
                return;
            }
            if (!InstallMixedWoodPatch())
            {
                arg.ReplyWith("Mixed wood boost unavailable on this Rust build. Native conveyors remain active.");
                return;
            }

            _conveyorBoostOptOuts.Remove(id);
            TrackConveyorCandidate(conveyor);
            var added = GetOrCreateConveyorBoost(id, boostMode);
            added.NextCheck = 0f;
            arg.ReplyWith($"Conveyor {id} eligible for {boostMode} when its route is valid.");
        }

        private ConveyorBoost GetOrCreateConveyorBoost(ulong id, ConveyorBoostMode mode)
        {
            if (!_conveyorBoosts.TryGetValue(id, out var boost) || boost.Mode != mode)
            {
                boost = new ConveyorBoost { Mode = mode };
                _conveyorBoosts[id] = boost;
            }
            return boost;
        }

        private static float GetExtraConveyorInterval(float nativeInterval, int multiplier, bool urgent)
        {
            double scale = Math.Sqrt(Math.Max(1d, multiplier / 5d));
            if (urgent) scale *= 2d;
            return Math.Max(MinimumExtraConveyorInterval, (float)(nativeInterval / scale));
        }

        private static float GetOutputExtraInterval(float nativeInterval, float currentExtraInterval)
        {
            // Increase the combined native + extra pass rate by the target percentage.
            double extraRate = (1d + OutputPassRateIncrease) / currentExtraInterval +
                OutputPassRateIncrease / nativeInterval;
            return Math.Max(MinimumExtraConveyorInterval, (float)(1d / extraRate));
        }

        private static float GetDedicatedWoodExtraInterval(float nativeInterval, int multiplier, bool urgent)
        {
            float standard = GetExtraConveyorInterval(nativeInterval, multiplier, urgent);
            return multiplier < 10 ? standard :
                Math.Max(MinimumExtraConveyorInterval, (float)(standard / Math.Sqrt(multiplier / 5d)));
        }

        private static float GetNextFractionalBoostCheck(float now, float previousDue, float interval)
        {
            // Keep fractional intervals across the half-second scheduler ticks without
            // making up more than one tick of delay after congestion.
            float carry = previousDue > 0f ? Math.Min(ConveyorBoostCheckSeconds, Math.Max(0f, now - previousDue)) : 0f;
            return now + Math.Max(MinimumExtraConveyorInterval, interval - carry);
        }

        private void CheckConveyorBoosts()
        {
            if (!BoostsEnabled) return;
            float now = Time.realtimeSinceStartup;
            float nativeInterval = ConVar.Server.conveyorMoveFrequency;
            RefreshCachedPreset();
            int count = Math.Min(ConveyorCandidatesPerCheck, _conveyorCandidates.Count);
            for (int i = 0; i < count; i++)
            {
                var conveyor = _conveyorCandidates.Dequeue();
                if (conveyor == null || conveyor.IsDestroyed || conveyor.net == null) continue;
                _conveyorCandidates.Enqueue(conveyor);
                ulong id = conveyor.net.ID.Value;
                if (_conveyorBoostOptOuts.Contains(id)) continue;
                if (_conveyorBoosts.TryGetValue(id, out var due) && due.NextCheck > now) continue;

                var mode = GetConveyorBoostMode(conveyor);
                if (mode == ConveyorBoostMode.None)
                {
                    _conveyorBoosts.Remove(id);
                    continue;
                }
                if (due == null && (!conveyor.IsOn() || !conveyor.IsPowered())) continue;
                due = GetOrCreateConveyorBoost(id, mode);
                if (!(nativeInterval > 0f) || float.IsInfinity(nativeInterval))
                {
                    due.NextCheck = now + 5f;
                    due.Status = "native conveyors disabled";
                    continue;
                }
                due.Status = GetConveyorBoostBlockReason(conveyor, mode, out bool urgent);
                float interval = GetExtraConveyorInterval(nativeInterval, _cachedPresetMultiplier, urgent);
                if (mode == ConveyorBoostMode.Output)
                    interval = GetOutputExtraInterval(nativeInterval, interval);
                else if (mode == ConveyorBoostMode.Wood)
                    interval = GetDedicatedWoodExtraInterval(nativeInterval, _cachedPresetMultiplier, urgent);
                float previousDue = due.NextCheck;
                due.NextCheck = now + interval;
                if (due.Status != null) continue;
                if (!InstallMixedWoodPatch())
                {
                    due.Status = "native boost patch unavailable";
                    continue;
                }
                if (mode == ConveyorBoostMode.MixedWood)
                {
                    due.Status = _cachedPresetMultiplier >= 10
                        ? "eligible on native passes, demand-based wood limit"
                        : $"eligible on native passes, wood limit up to {GetMixedWoodFactor(_cachedPresetMultiplier, urgent):0.##}x";
                    continue;
                }

                var nativeQueue = IndustrialEntity.Queue;
                if (nativeQueue.Contains(conveyor))
                {
                    due.Status = "native transfer already queued";
                    continue;
                }
                nativeQueue.Add(conveyor);
                if (!nativeQueue.Contains(conveyor))
                {
                    due.Status = "native queue did not accept the extra pass";
                    continue;
                }
                _queuedConveyorExtras[id] = mode;
                due.ExtraPassesQueued++;
                if (mode == ConveyorBoostMode.Output || (mode == ConveyorBoostMode.Wood && _cachedPresetMultiplier >= 10))
                    due.NextCheck = GetNextFractionalBoostCheck(now, previousDue, interval);
                due.Status = $"eligible, extra-pass interval {interval:0.##}s";
                break;
            }
            if (_conveyorCandidates.Count != 0) return;
            _conveyorBoostTimer?.Destroy();
            _conveyorBoostTimer = null;
        }

        private string GetConveyorBoostBlockReason(IndustrialConveyor conveyor, ConveyorBoostMode mode, out bool urgent,
            bool anticipateOutputPressure = false)
        {
            urgent = false;
            if (conveyor == null || conveyor.IsDestroyed || !conveyor.IsOn() || !conveyor.IsPowered())
                return "conveyor off or unpowered";
            if (conveyor.refreshInputOutputs || conveyor.multiFrameTransferInProcess)
                return "waiting for the native transfer queue";
            if (GetConveyorBoostMode(conveyor) != mode)
                return "filters changed; restore the original boost mode or enroll again";

            if (conveyor.splitInputs.Count == 0 || conveyor.splitOutputs.Count == 0)
                return "waiting for connected source and destination";

            if (mode == ConveyorBoostMode.Wood || mode == ConveyorBoostMode.MixedWood)
                return GetWoodBoostBlockReason(conveyor, out urgent);

            bool hasProduct = false;
            foreach (var connection in conveyor.splitInputs)
            {
                var storage = connection.Storage;
                var container = storage?.Container;
                var oven = container?.entityOwner as BaseOven;
                if (oven == null || oven.IsDestroyed || container != oven.inventory)
                    return "every source must be a SmartSmelt furnace output";
                var kind = GetKind(oven);
                if (kind == OvenKind.Unknown || kind == OvenKind.SmallRefinery || !IsWhitelistedSmeltingOven(oven))
                    return "every source must be a SmartSmelt furnace output";

                var range = storage.OutputSlotRange(connection.SlotIndex);
                int end = Math.Min(range.y, container.capacity - 1);
                if (range.x < oven._outputSlotIndex || end >= oven._outputSlotIndex + oven.outputSlots || range.x > end)
                    return "source is not restricted to furnace output slots";

                int emptySlots = 0;
                bool hasProductInOven = false;
                bool hasNearlyFullStack = false;
                for (int i = range.x; i <= end; i++)
                {
                    var item = container.GetSlot(i);
                    if (item == null) emptySlots++;
                    if (item?.info == null || item.amount <= 0 || item.IsBlueprint()) continue;
                    foreach (var filter in conveyor.filterItems)
                    {
                        if (item.info != filter.TargetItem || item.amount <= filter.MinAmountInInput) continue;
                        hasProductInOven = true;
                        if (anticipateOutputPressure)
                            hasNearlyFullStack |= item.amount >= item.MaxStackable() * 0.75d;
                        break;
                    }
                }
                hasProduct |= hasProductInOven;
                urgent |= hasProductInOven && (emptySlots == 0 || (emptySlots == 1 && hasNearlyFullStack));
            }

            return hasProduct ? null : "no matching product waiting";
        }

        private static bool IsBoostedProduct(string shortname) =>
            shortname == ItemSulfur || shortname == ItemMetalFragments || shortname == ItemMetalRefined || shortname == ItemCharcoal;

        private static ConveyorBoostMode GetConveyorBoostMode(IndustrialConveyor conveyor)
        {
            if (conveyor.mode != IndustrialConveyor.ConveyorMode.Any || conveyor.filterItems.Count == 0 || conveyor.filterItems.Count > 4)
                return ConveyorBoostMode.None;

            bool wood = false;
            bool ore = false;
            bool product = false;
            foreach (var filter in conveyor.filterItems)
            {
                if (filter.IsBlueprint || filter.TargetCategory.HasValue)
                    return ConveyorBoostMode.None;
                string name = filter.TargetItem?.shortname;
                if (IsOreShortname(name)) ore = true;
                else if (GetConveyorFilterMode(name) == ConveyorBoostMode.Output) product = true;
                else if (GetConveyorFilterMode(name) == ConveyorBoostMode.Wood && !wood) wood = true;
                else return ConveyorBoostMode.None;
            }
            if (product) return wood || ore ? ConveyorBoostMode.None : ConveyorBoostMode.Output;
            if (!wood) return ConveyorBoostMode.None;
            return ore ? ConveyorBoostMode.MixedWood : ConveyorBoostMode.Wood;
        }

        private static ConveyorBoostMode GetConveyorFilterMode(string shortname)
        {
            if (shortname == ItemWood) return ConveyorBoostMode.Wood;
            return IsBoostedProduct(shortname) ? ConveyorBoostMode.Output : ConveyorBoostMode.None;
        }

        private string GetWoodBoostBlockReason(IndustrialConveyor conveyor, out bool urgent)
        {
            var filter = GetWoodFilter(conveyor);
            string blocked = GetWoodDestinationBlockReason(conveyor, filter.MaxAmountInOutput, out urgent, out _);
            if (blocked != null) return blocked;

            foreach (var connection in conveyor.splitInputs)
            {
                var storage = connection.Storage;
                var container = storage?.Container;
                if (container?.itemList == null) continue;
                var range = storage.OutputSlotRange(connection.SlotIndex);
                foreach (var item in container.itemList)
                {
                    if (item?.info == _woodDefinition && item.amount > filter.MinAmountInInput &&
                        item.position >= range.x && item.position <= range.y && !item.IsBlueprint())
                        return null;
                }
            }
            return "no matching wood waiting";
        }

        private static IndustrialConveyor.ItemFilter GetWoodFilter(IndustrialConveyor conveyor)
        {
            foreach (var filter in conveyor.filterItems)
            {
                if (filter.TargetItem?.shortname == ItemWood) return filter;
            }
            return default(IndustrialConveyor.ItemFilter);
        }

        private string GetWoodDestinationBlockReason(IndustrialConveyor conveyor, int maxWood,
            out bool urgent, out double maxRefill)
        {
            urgent = false;
            maxRefill = 0d;
            bool needsWood = false;
            bool highPreset = _cachedPresetMultiplier >= 10;
            double nativeInterval = ConVar.Server.conveyorMoveFrequency;
            double horizon = nativeInterval > 0d && !double.IsInfinity(nativeInterval)
                ? Math.Min(15d, nativeInterval * 1.5d) : 0d;
            foreach (var connection in conveyor.splitOutputs)
            {
                var storage = connection.Storage;
                var container = storage?.Container;
                var oven = container?.entityOwner as BaseOven;
                if (oven == null || oven.IsDestroyed || container != oven.inventory)
                    return "wood boost requires only wood-burning furnace destinations";
                var kind = GetKind(oven);
                if ((kind != OvenKind.SmallFurnace && kind != OvenKind.LargeFurnace) || !IsWhitelistedSmeltingOven(oven))
                    return "wood boost requires only wood-burning furnace destinations";

                var range = storage.InputSlotRange(connection.SlotIndex);
                int end = Math.Min(Math.Min(range.y, oven.fuelSlots - 1), container.capacity - 1);
                if (range.x < 0 || range.x > end)
                    return "destination does not reach furnace fuel slots";

                if (!TryGetOvenInputSlotRange(oven, container, out int firstInput, out int lastInput, out _))
                    continue;

                var preset = GetEffectivePresetForKind(_cachedPresetTuning, kind);
                float highestWoodCost = 0f;
                double batchWood = 0d;
                double backlogWood = 0d;
                bool hasOre = false;
                for (int i = firstInput; i <= lastInput; i++)
                {
                    var item = container.GetSlot(i);
                    if (item?.info == null || item.amount <= 0 || !IsOreShortname(item.info.shortname)) continue;
                    hasOre = true;
                    float cost = GetWoodPerInput(kind, item.info.shortname);
                    highestWoodCost = Math.Max(highestWoodCost, cost);
                    if (highPreset)
                    {
                        backlogWood += item.amount * (double)cost;
                        batchWood += Math.Min(item.amount, preset.MaxConsumedPerStackPerCycle) * (double)cost;
                    }
                }
                if (!hasOre) continue;

                long currentWood = 0;
                long woodRoom = 0;
                int emptySlotLimit = Math.Max(0, Math.Min(_woodDefinition?.stackable ?? 0,
                    container.maxStackSize > 0 ? container.maxStackSize : int.MaxValue));
                for (int i = range.x; i <= end; i++)
                {
                    var item = container.GetSlot(i);
                    if (item == null)
                        woodRoom += emptySlotLimit;
                    else if (item.info == _woodDefinition)
                    {
                        currentWood += item.amount;
                        woodRoom += Math.Max(0, item.MaxStackable() - item.amount);
                    }
                }
                if (maxWood > 0) woodRoom = Math.Min(woodRoom, maxWood - currentWood);
                if (woodRoom <= 0) continue;

                needsWood = true;
                double cycleLimitWood = preset.MaxTotalConsumedPerCycle * (double)highestWoodCost;
                if (!highPreset)
                {
                    urgent |= currentWood <= Math.Max(1d, cycleLimitWood);
                    continue;
                }

                double reserve = GetAdaptiveWoodReserve(backlogWood, Math.Min(batchWood, cycleLimitWood),
                    Math.Max(MinGlobalLoopInterval, preset.CycleSeconds), horizon,
                    GetBaselineWoodPerSecond(kind), currentWood + woodRoom);
                double refill = Math.Max(0d, reserve - currentWood);
                maxRefill = Math.Max(maxRefill, refill);
                urgent |= refill > 0d;
            }
            return needsWood ? null : "no furnace with ore and space for wood";
        }

        private static double GetAdaptiveWoodReserve(double backlogWood, double batchWood, double cycleSeconds,
            double horizon, double nativeWoodPerSecond, double fuelCapacity)
        {
            // Keep enough fuel for the existing ore through the next native delivery
            // window. Never target more than the fuel slots and the filter can hold.
            double accelerated = Math.Min(backlogWood, batchWood * Math.Ceiling(horizon / cycleSeconds));
            return Math.Min(fuelCapacity, Math.Ceiling(accelerated + horizon * nativeWoodPerSecond));
        }

        private bool InstallMixedWoodPatch()
        {
            if (_mixedWoodHarmony != null) return true;
            if (_conveyorPatchUnavailable) return false;
            var original = AccessTools.Method(typeof(IndustrialConveyor), "RunConveyor", Type.EmptyTypes);
            var nativeSchedule = AccessTools.Method(typeof(IndustrialConveyor), "ScheduleMove", Type.EmptyTypes);
            if (original == null || nativeSchedule == null)
            {
                _conveyorPatchUnavailable = true;
                PrintWarning("Automatic conveyor boosts are unavailable on this Rust build.");
                return false;
            }

            var harmony = new Harmony(MixedWoodHarmonyId);
            try
            {
                harmony.Patch(original,
                    prefix: new HarmonyMethod(typeof(SmartSmelt), nameof(BeforeConveyorPass)),
                    transpiler: new HarmonyMethod(typeof(SmartSmelt), nameof(PatchMixedWoodLimit)));
                harmony.Patch(nativeSchedule, prefix: new HarmonyMethod(typeof(SmartSmelt), nameof(BeforeNativeConveyorSchedule)));
                _mixedWoodMethod = original;
                _nativeConveyorScheduleMethod = nativeSchedule;
                _mixedWoodHarmony = harmony;
                _mixedWoodPatchOwner = this;
                return true;
            }
            catch (Exception ex)
            {
                _conveyorPatchUnavailable = true;
                try
                {
                    harmony.Unpatch(original, HarmonyPatchType.All, MixedWoodHarmonyId);
                    harmony.Unpatch(nativeSchedule, HarmonyPatchType.Prefix, MixedWoodHarmonyId);
                }
                catch (Exception cleanup)
                {
                    PrintWarning($"Could not remove an inactive conveyor patch: {cleanup.GetBaseException().Message}");
                }
                PrintWarning($"Automatic conveyor boosts unavailable: {ex.GetBaseException().Message}");
                return false;
            }
        }

        private void RemoveMixedWoodPatch()
        {
            if (_mixedWoodPatchOwner == this) _mixedWoodPatchOwner = null;
            if (_mixedWoodHarmony == null) return;
            try
            {
                _mixedWoodHarmony.Unpatch(_mixedWoodMethod, HarmonyPatchType.All, MixedWoodHarmonyId);
                _mixedWoodHarmony.Unpatch(_nativeConveyorScheduleMethod, HarmonyPatchType.Prefix, MixedWoodHarmonyId);
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not remove the inactive mixed wood patch: {ex.GetBaseException().Message}");
            }
            _mixedWoodHarmony = null;
            _mixedWoodMethod = null;
            _nativeConveyorScheduleMethod = null;
        }

        private static void BeforeNativeConveyorSchedule(IndustrialConveyor __instance)
        {
            // Rust coalesces duplicate queue entries. A native request makes any
            // already queued extra pass a normal pass.
            if (__instance.net != null)
                _mixedWoodPatchOwner?._queuedConveyorExtras.Remove(__instance.net.ID.Value);
        }

        private static bool BeforeConveyorPass(IndustrialConveyor __instance)
        {
            var owner = _mixedWoodPatchOwner;
            if (owner == null || __instance.net == null) return true;
            ulong id = __instance.net.ID.Value;
            if (owner._conveyorBoosts.TryGetValue(id, out var boost))
            {
                boost.PassWoodFactor = null;
                boost.PassOutputFactor = null;
            }
            if (!owner._queuedConveyorExtras.TryGetValue(id, out var mode)) return true;
            owner._queuedConveyorExtras.Remove(id);
            try
            {
                return owner.BoostsEnabled && !owner._conveyorBoostOptOuts.Contains(id) &&
                    owner.GetConveyorBoostBlockReason(__instance, mode, out _) == null;
            }
            catch (Exception ex)
            {
                if (owner._config.Debug) owner.PrintWarning($"Skipped an extra conveyor pass: {ex.Message}");
                return false;
            }
        }

        private static IEnumerable<CodeInstruction> PatchMixedWoodLimit(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var moveLimit = AccessTools.Field(typeof(IndustrialConveyor), nameof(IndustrialConveyor.MaxStackSizePerMove));
            var itemInfo = AccessTools.Field(typeof(Item), "info");
            var stackable = AccessTools.Field(typeof(ItemDefinition), nameof(ItemDefinition.stackable));
            var minimum = AccessTools.Method(typeof(Mathf), nameof(Mathf.Min), new[] { typeof(int), typeof(int) });
            int match = -1;
            for (int i = 0; i + 5 < code.Count; i++)
            {
                if (code[i].opcode != OpCodes.Ldarg_0 || !code[i + 1].LoadsField(moveLimit) ||
                    !code[i + 2].IsLdloc() || !code[i + 3].LoadsField(itemInfo) ||
                    !code[i + 4].LoadsField(stackable) || !code[i + 5].Calls(minimum)) continue;
                if (match != -1) throw new InvalidOperationException("Found multiple conveyor quantity limits.");
                match = i + 5;
            }
            if (match < 0 || code[match].blocks.Count != 0)
                throw new InvalidOperationException("Could not identify the native conveyor quantity limit.");

            // Replace only Min(move limit, item stack size). The native remainder of the
            // pass still applies split counts, filter minima, buffers and destination limits.
            var loadConveyor = new CodeInstruction(OpCodes.Ldarg_0);
            loadConveyor.labels.AddRange(code[match].labels);
            var loadItem = new CodeInstruction(code[match - 3].opcode, code[match - 3].operand);
            code[match] = new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(SmartSmelt), nameof(GetConveyorTransferLimit)));
            code.Insert(match, loadConveyor);
            code.Insert(match + 1, loadItem);
            return code;
        }

        private static int GetConveyorTransferLimit(int nativeLimit, int stackSize, IndustrialConveyor conveyor, Item item)
        {
            int normal = Math.Min(nativeLimit, stackSize);
            var owner = _mixedWoodPatchOwner;
            if (owner == null || !owner.BoostsEnabled || item?.info == null || conveyor.net == null ||
                !owner._conveyorBoosts.TryGetValue(conveyor.net.ID.Value, out var boost))
                return normal;

            try
            {
                if (boost.Mode == ConveyorBoostMode.Output)
                {
                    if (owner._cachedPresetMultiplier < MinimumOutputTransferBoostPreset ||
                        !IsBoostedProduct(item.info.shortname)) return normal;
                    if (!boost.PassOutputFactor.HasValue)
                    {
                        boost.PassOutputFactor = 1d;
                        if (!owner._conveyorBoostOptOuts.Contains(conveyor.net.ID.Value) &&
                            owner.GetConveyorBoostBlockReason(conveyor, boost.Mode, out bool underPressure,
                                anticipateOutputPressure: true) == null && underPressure)
                            boost.PassOutputFactor = Math.Min(3d, 2d * Math.Sqrt(owner._cachedPresetMultiplier / 10d));
                    }
                    int outputLimit = ScaleMixedWoodLimit(nativeLimit, stackSize, boost.PassOutputFactor.Value);
                    if (outputLimit > normal) boost.OutputLimitsRaised++;
                    return outputLimit;
                }

                if (item.info != owner._woodDefinition || (boost.Mode != ConveyorBoostMode.MixedWood &&
                    (boost.Mode != ConveyorBoostMode.Wood || owner._cachedPresetMultiplier < 10)))
                    return normal;

                if (!boost.PassWoodFactor.HasValue)
                {
                    boost.PassWoodFactor = 1d;
                    if (GetConveyorBoostMode(conveyor) == boost.Mode && conveyor.IsOn() && conveyor.IsPowered() &&
                        owner.GetWoodDestinationBlockReason(conveyor, GetWoodFilter(conveyor).MaxAmountInOutput,
                            out bool urgent, out double maxRefill) == null)
                        boost.PassWoodFactor = GetWoodPassFactor(owner._cachedPresetMultiplier, urgent,
                            boost.Mode == ConveyorBoostMode.MixedWood, nativeLimit, conveyor.splitOutputs.Count, maxRefill);
                }
                int raised = ScaleMixedWoodLimit(nativeLimit, stackSize, boost.PassWoodFactor.Value);
                if (raised > normal) boost.WoodLimitsRaised++;
                return raised;
            }
            catch (Exception ex)
            {
                if (owner._config.Debug)
                    owner.PrintWarning($"Conveyor boost fell back to the native transfer limit: {ex.Message}");
                return normal;
            }
        }

        private static double GetMixedWoodFactor(int multiplier, bool urgent) =>
            Math.Min(8d, (urgent ? 3d : 2d) * Math.Sqrt(Math.Max(1d, multiplier / 5d)));

        private static double GetWoodPassFactor(int multiplier, bool urgent, bool mixed, int nativeLimit,
            int destinations, double maxRefill)
        {
            double existing = mixed ? GetMixedWoodFactor(multiplier, urgent) : 1d;
            if (multiplier < 10 || nativeLimit <= 0 || maxRefill <= 0d) return existing;
            // Rust divides this limit among destinations and still clamps source
            // stack size, filter limits and destination capacity itself.
            double requested = Math.Ceiling(maxRefill) * Math.Max(1, destinations) / nativeLimit;
            double cap = Math.Min(16d, 8d * Math.Sqrt(multiplier / 10d));
            return Math.Min(cap, Math.Max(existing, requested));
        }

        private static int ScaleMixedWoodLimit(int nativeLimit, int stackSize, double factor) =>
            nativeLimit <= 0 ? Math.Min(nativeLimit, stackSize)
                : (int)Math.Min(stackSize, nativeLimit * factor);

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

        private void TryAutoPullFuel(BaseOven oven, BasePlayer player)
        {
            if (!SmeltingEnabled) return;
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

        private static bool IsOreShortname(string shortname)
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
            if (from == null || to == null || from.IsLocked() || to.IsLocked() || to.PlayerItemInputBlocked()) return 0;
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
                    if (it == null || it.amount <= 0 || it.info == null || it.IsLocked()) continue;
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
                        movedToTarget = moving.MoveToContainer(to, preferredPosition, allowStack: true, allowSwap: false) ||
                            moving.MoveToContainer(to, allowSwap: false);
                    else
                        movedToTarget = moving.MoveToContainer(to, allowSwap: false);

                    if (!movedToTarget)
                    {
                        if (moving != it)
                        {
                            it.amount += moving.amount;
                            moving.amount = 0;
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

        private int CountMatchingInputItems(ItemContainer container, ItemDefinition definition, ulong skin, int minSlot, int maxSlot)
        {
            if (container == null || definition == null) return 0;

            long total = 0L;
            for (int i = minSlot; i <= maxSlot; i++)
            {
                var it = container.GetSlot(i);
                if (it != null && it.info == definition && it.skin == skin)
                {
                    total += it.amount;
                    if (total >= int.MaxValue) return int.MaxValue;
                }
            }
            return (int)total;
        }

        private void QueuePostMoveInputRebalance(BaseOven oven, BasePlayer actorPlayer, ItemDefinition addedDefinition, ulong addedSkin, bool rebalanceInputs)
        {
            if (oven == null || oven.IsDestroyed) return;

            ulong id = oven.net != null ? oven.net.ID.Value : 0ul;
            if (actorPlayer != null && actorPlayer.inventory?.loot?.entitySource == oven)
                QueueFuelRecalcNextTick(oven, actorPlayer);
            if (!rebalanceInputs || id == 0ul) return;
            var key = (id, addedDefinition.itemid, addedSkin);
            if (!_pendingPostMoveRebalances.Add(key)) return;

            NextTick(() =>
            {
                if (!_pendingPostMoveRebalances.Remove(key)) return;
                if (oven == null || oven.IsDestroyed) return;

                RebalanceExistingOvenInputs(oven, addedDefinition, addedSkin);
            });
        }

        private void RebalanceExistingOvenInputs(BaseOven oven, ItemDefinition addedDefinition, ulong addedSkin)
        {
            if (oven == null || oven.IsDestroyed) return;

            ulong id = oven.net != null ? oven.net.ID.Value : 0ul;
            if (id != 0ul && !_postMoveRebalancesInProgress.Add(id)) return;

            try
            {
                var container = oven.inventory;
                int minSlot, maxSlot, slots;
                if (!TryGetOvenInputSlotRange(oven, container, out minSlot, out maxSlot, out slots)) return;

                var kind = GetKind(oven);
                ItemDefinition definition = addedDefinition;
                ulong skin = addedSkin;
                long totalLong = 0L;
                var eligibleSlots = new List<int>(slots);
                var emptySlots = new List<int>(slots);

                for (int i = minSlot; i <= maxSlot; i++)
                {
                    var current = container.GetSlot(i);
                    if (current == null)
                    {
                        emptySlots.Add(i);
                        continue;
                    }

                    string shortname = current.info?.shortname;
                    bool validInput = kind == OvenKind.SmallRefinery
                        ? shortname == ItemCrudeOil
                        : IsOreShortname(shortname);
                    if (!validInput) return;

                    if (definition == null && current.info != null)
                    {
                        definition = current.info;
                        skin = current.skin;
                    }

                    if (current.info == definition && current.skin == skin)
                    {
                        eligibleSlots.Add(i);
                        totalLong += current.amount;
                        if (totalLong > int.MaxValue) return;
                    }
                }

                // Keep occupied slots first. If the total is smaller than the number of
                // eligible slots, zero-sized targets then fall only on empty slots; an
                // existing stack never needs to be removed and recreated.
                eligibleSlots.AddRange(emptySlots);

                if (definition == null || totalLong <= 0L || eligibleSlots.Count <= 1) return;

                int totalBefore = (int)totalLong;
                int eligibleCount = eligibleSlots.Count;
                int baseAmount = totalBefore / eligibleCount;
                int remainder = totalBefore - baseAmount * eligibleCount;

                bool needsRebalance = false;
                for (int si = 0; si < eligibleCount; si++)
                {
                    var current = container.GetSlot(eligibleSlots[si]);
                    int currentAmount = current != null && current.info == definition && current.skin == skin
                        ? current.amount
                        : 0;
                    int target = baseAmount + (si < remainder ? 1 : 0);
                    if (currentAmount == target) continue;

                    needsRebalance = true;
                    break;
                }

                if (!needsRebalance) return;

                int heldAmount = 0;
                bool committed = false;
                var reduced = new List<ItemDebit>(eligibleCount);
                var increased = new List<ItemDebit>(eligibleCount);
                var created = new List<ItemDebit>(eligibleCount);

                try
                {
                    for (int si = 0; si < eligibleCount; si++)
                    {
                        int target = baseAmount + (si < remainder ? 1 : 0);
                        var current = container.GetSlot(eligibleSlots[si]);
                        if (current == null) continue;
                        if (!IsRebalanceItemValid(current, container, definition, skin) ||
                            target > current.MaxStackable()) return;
                        if (current.amount <= target) continue;

                        int take = current.amount - target;
                        reduced.Add(new ItemDebit { Item = current, Before = current.amount, Amount = take });
                        current.amount = target;
                        heldAmount += take;
                    }

                    for (int si = 0; si < eligibleCount && heldAmount > 0; si++)
                    {
                        int slotIndex = eligibleSlots[si];
                        int target = baseAmount + (si < remainder ? 1 : 0);
                        var current = container.GetSlot(slotIndex);
                        int give = target - (current?.amount ?? 0);
                        if (give <= 0) continue;
                        if (give > heldAmount) return;

                        if (current == null)
                        {
                            var item = ItemManager.Create(definition, give, skin);
                            if (item == null) return;
                            created.Add(new ItemDebit { Item = item, Amount = give });
                            if (give > item.MaxStackable() ||
                                !item.MoveToContainer(container, slotIndex, allowStack: false, allowSwap: false))
                                return;
                        }
                        else
                        {
                            if (!IsRebalanceItemValid(current, container, definition, skin) ||
                                target > current.MaxStackable()) return;
                            increased.Add(new ItemDebit { Item = current, Before = current.amount, Amount = give });
                            current.amount += give;
                        }
                        heldAmount -= give;
                    }

                    if (heldAmount != 0 || CountMatchingInputItems(container, definition, skin, minSlot, maxSlot) != totalBefore)
                        return;
                    committed = true;
                }
                finally
                {
                    bool clean = true;
                    if (!committed)
                    {
                        foreach (var entry in increased)
                        {
                            if (!IsRebalanceItemValid(entry.Item, container, definition, skin) ||
                                entry.Item.amount != entry.Before + entry.Amount)
                                clean = false;
                            else
                                entry.Item.amount = entry.Before;
                        }
                        foreach (var entry in created)
                            clean &= UndoCreatedItem(container, entry.Item, entry.Amount);
                        if (clean)
                        {
                            foreach (var entry in reduced)
                            {
                                if (!IsRebalanceItemValid(entry.Item, container, definition, skin) ||
                                    entry.Item.amount != entry.Before - entry.Amount)
                                {
                                    clean = false;
                                    continue;
                                }
                                entry.Item.amount = entry.Before;
                            }
                        }
                        if (!clean) PrintWarning("Furnace input rebalance found an uncertain item state during rollback.");
                    }

                    foreach (var entry in reduced) MarkRebalanceItemDirty(entry.Item);
                    foreach (var entry in increased) MarkRebalanceItemDirty(entry.Item);
                    if (committed || !clean) container.MarkDirty();
                }
            }
            catch (Exception ex)
            {
                PrintError($"Post-move furnace rebalance failed: {ex}");
            }
            finally
            {
                if (id != 0ul) _postMoveRebalancesInProgress.Remove(id);

            }
        }

        #endregion
        #region Tracking
        private void ScanAndTrackRunningOvens()
        {
            if (!SmeltingEnabled) return;

            DisposeStartupScanEnumerator();
            _startupScanTracked = 0;
            _startupScanEnumerator = BaseNetworkable.serverEntities.GetEnumerator();
            ContinueStartupOvenScan();
        }

        private void ContinueStartupOvenScan()
        {
            if (!SmeltingEnabled || _startupScanEnumerator == null)
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
                    if (_startupScanEnumerator.Current is IndustrialConveyor conveyor)
                        TrackConveyorCandidate(conveyor);
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
            if (!SmeltingEnabled) return;

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
                FillSmeltableInputs(oven, container, kind, inputs);
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
                float woodAvailableFloat = gateOnWood ? CountItem(container, ItemWood) - tracker.FuelDebt : int.MaxValue;
                tracker.LastBalanceTime = Time.realtimeSinceStartup;

                int totalConsumed = 0;
                int remainingBudget = preset.MaxTotalConsumedPerCycle;
                int safetyPasses = 3;

                int[] stackBudgets = PoolGetIntArray(inputs.Count);
                try
                {
                    for (int i = 0; i < inputs.Count; i++)
                        stackBudgets[i] = preset.MaxConsumedPerStackPerCycle;

                    while (remainingBudget > 0 && safetyPasses-- > 0)
                    {
                        if (!HasActiveInputs(inputs)) break;

                        int budgetBefore = remainingBudget;

                        ProcessSingleItemPass(tracker, kind, container, inputs, stackBudgets, gateOnWood, ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);

                        if (remainingBudget <= 0) break;

                        ProcessAllocatedInputPass(tracker, kind, container, inputs, stackBudgets, gateOnWood, ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);

                        if (remainingBudget == budgetBefore) break;
                    }
                }
                finally
                {
                    PoolReturnIntArray(stackBudgets);
                }

                tracker.Cycles++;

                if (_config.Debug && _config.VerboseCycleLogs && totalConsumed > 0)
                    Puts($"{oven.ShortPrefabName} processed {totalConsumed} items this cycle (cycle #{tracker.Cycles}).");
            }
            catch (Exception ex)
            {
                PrintWarning($"TickOven exception: {ex}");
            }
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
            int[] stackBudgets,
            bool gateOnWood,
            ref float woodAvailableFloat,
            ref int remainingBudget,
            ref int totalConsumed)
        {
            for (int i = 0; i < inputs.Count && remainingBudget > 0; i++)
            {
                var input = inputs[i];
                int canTake = GetAllowedInputTake(kind, input, 1, stackBudgets[i], remainingBudget, gateOnWood, woodAvailableFloat);
                if (canTake <= 0) continue;

                ConvertAndRecordInput(tracker, kind, container, input, canTake, gateOnWood, ref stackBudgets[i], ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);
            }
        }

        private void ProcessAllocatedInputPass(
            OvenTracker tracker,
            OvenKind kind,
            ItemContainer container,
            List<Item> inputs,
            int[] stackBudgets,
            bool gateOnWood,
            ref float woodAvailableFloat,
            ref int remainingBudget,
            ref int totalConsumed)
        {
            long totalCap = 0;
            int[] caps = PoolGetIntArray(inputs.Count);
            try
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    int cap = GetAllowedInputTake(kind, input, input != null ? input.amount : 0, stackBudgets[i], remainingBudget, gateOnWood, woodAvailableFloat);
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
                        int canTake = GetAllowedInputTake(kind, input, alloc, stackBudgets[i], remainingBudget, gateOnWood, woodAvailableFloat);
                        if (canTake <= 0) continue;

                        ConvertAndRecordInput(tracker, kind, container, input, canTake, gateOnWood, ref stackBudgets[i], ref woodAvailableFloat, ref remainingBudget, ref totalConsumed);
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

        private void AllocateInputBudget(int[] caps, int[] allocs, int count, int remainingBudget, long totalCap)
        {
            if (totalCap <= remainingBudget)
            {
                Array.Copy(caps, allocs, count);
                return;
            }

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
                    float maxByFuel = woodAvailableFloat / wpi;
                    if (maxByFuel < canTake)
                        canTake = Mathf.Max(0, Mathf.FloorToInt(maxByFuel));
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
            ref int stackBudget,
            ref float woodAvailableFloat,
            ref int remainingBudget,
            ref int totalConsumed)
        {
            float woodPerInput = gateOnWood ? GetWoodPerInput(kind, input.info.shortname) : 0f;
            if (!TryConvertInput(tracker, kind, container, input, canTake, woodPerInput, out int consumedNow))
                return;

            stackBudget -= consumedNow;
            totalConsumed += consumedNow;
            remainingBudget -= consumedNow;
            woodAvailableFloat -= consumedNow * woodPerInput;
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

            float debt = tracker.FuelDebt + woodNeededFloat;
            int desiredRemove = Mathf.Min(Mathf.FloorToInt(debt), CountItem(container, ItemWood));
            bool pause = MustFitCharcoal();
            if (pause)
                desiredRemove = LimitWoodByCharcoal(tracker, desiredRemove, GetAdditionalCapacity(container, ItemCharcoal));

            int charcoal = GetCharcoalAmount(tracker, desiredRemove);
            if (desiredRemove > 0 && !TryCommitSmelt(tracker.Oven, container, null, 0, desiredRemove,
                ItemCharcoal, pause ? charcoal : 0, 0, out _))
                return;

            tracker.FuelDebt = debt - Mathf.Floor(debt);
            RecordCharcoal(tracker, container, desiredRemove, pause);
        }

        private bool MustFitCharcoal() =>
            _config.ProduceCharcoalFromFuel && _config.CharcoalPerWood > 0f && _config.CharcoalOverflowMode == "Pause";

        private int GetCharcoalAmount(OvenTracker tracker, int wood) =>
            _config.ProduceCharcoalFromFuel
                ? Mathf.FloorToInt(wood * _config.CharcoalPerWood + tracker.CharcoalRemainder)
                : 0;

        private int LimitWoodByCharcoal(OvenTracker tracker, int wood, int capacity)
        {
            int low = 0;
            int high = wood;
            while (low < high)
            {
                int middle = low + (int)(((long)high - low + 1L) / 2L);
                if (GetCharcoalAmount(tracker, middle) <= capacity)
                    low = middle;
                else
                    high = middle - 1;
            }
            return low;
        }

        private void RecordCharcoal(OvenTracker tracker, ItemContainer container, int wood, bool alreadyPlaced)
        {
            if (wood <= 0 || !_config.ProduceCharcoalFromFuel) return;
            float exact = wood * _config.CharcoalPerWood + tracker.CharcoalRemainder;
            int charcoal = Mathf.FloorToInt(exact);
            tracker.CharcoalRemainder = exact - charcoal;
            if (!alreadyPlaced && charcoal > 0)
                TryGiveOutput(container, ItemCharcoal, Mathf.Min(charcoal, GetAdditionalCapacity(container, ItemCharcoal)));
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

        private struct ItemDebit
        {
            public Item Item;
            public int Before;
            public int Amount;
        }

        private readonly Stack<List<ItemDebit>> _debitPool = new Stack<List<ItemDebit>>();

        private bool IsRebalanceItemValid(Item item, ItemContainer container, ItemDefinition definition, ulong skin) =>
            IsDebitSourceValid(item, container, 1) && item.info == definition && item.skin == skin;

        private void MarkRebalanceItemDirty(Item item)
        {
            try
            {
                if (item != null && !item.IsRemoved()) item.MarkDirty();
            }
            catch (Exception ex)
            {
                if (_config.Debug) PrintWarning($"Furnace rebalance notification failed: {ex.Message}");
            }
        }

        private bool IsDebitSourceValid(Item item, ItemContainer container, int amount) =>
            item != null && item.info != null && !item.IsRemoved() && item.parent == container &&
            item.position >= 0 && container.GetSlot(item.position) == item && item.amount >= amount && !item.IsLocked();

        private bool TryReserveDebit(List<ItemDebit> debits, ItemContainer container, Item item, int amount)
        {
            if (!IsDebitSourceValid(item, container, amount)) return false;
            int before = item.amount;
            if (Oxide.Core.Interface.CallHook("OnItemUse", item, amount) is int overridden && overridden != amount)
                return false;
            if (!IsDebitSourceValid(item, container, amount) || item.amount != before) return false;

            debits.Add(new ItemDebit { Item = item, Before = before, Amount = amount });
            item.amount = before - amount;
            return true;
        }

        private bool TryCommitSmelt(BaseOven oven, ItemContainer container, Item input, int inputAmount,
            int wood, string product, int productAmount, int charcoal, out bool mayRetry)
        {
            mayRetry = false;
            if (oven == null || oven.inventory != container) return false;
            var debits = _debitPool.Count == 0 ? new List<ItemDebit>(4) : _debitPool.Pop();
            bool debitCommitted = false;
            bool canRefund = true;
            try
            {
                if (inputAmount > 0 && (input == null || input.position < oven._inputSlotIndex ||
                    input.position >= oven._inputSlotIndex + oven.inputSlots ||
                    !TryReserveDebit(debits, container, input, inputAmount)))
                    return false;

                int remainingWood = wood;
                int fuelSlots = Math.Min(container.capacity, oven.fuelSlots);
                for (int i = 0; i < fuelSlots && remainingWood > 0; i++)
                {
                    var item = container.GetSlot(i);
                    if (item == null || item.info != _woodDefinition || item.amount <= 0) continue;
                    int take = Math.Min(remainingWood, item.amount);
                    if (!TryReserveDebit(debits, container, item, take)) return false;
                    remainingWood -= take;
                }
                if (remainingWood > 0 || oven.IsDestroyed) return false;

                foreach (var debit in debits)
                {
                    if (!IsDebitSourceValid(debit.Item, container, debit.Before - debit.Amount) ||
                        debit.Item.amount != debit.Before - debit.Amount)
                        return false;
                }

                if ((productAmount > 0 || charcoal > 0) &&
                    !TryGiveOutput(container, product, productAmount, charcoal, out canRefund))
                {
                    mayRetry = canRefund;
                    return false;
                }

                debitCommitted = true;
                return true;
            }
            finally
            {
                foreach (var debit in debits)
                {
                    var item = debit.Item;
                    try
                    {
                        if (!debitCommitted && canRefund)
                        {
                            // Restore only a reservation that is still intact.
                            if (item != null && !item.IsRemoved() && item.parent == container &&
                                item.amount == debit.Before - debit.Amount)
                                item.amount = debit.Before;
                            else
                                PrintWarning("A furnace input changed during rollback; skipped the uncertain refund.");
                            continue;
                        }

                        if (item == null || item.IsRemoved()) continue;
                        item.ReduceItemOwnership(debit.Amount);
                        if (item.amount == 0 && item.parent == container)
                        {
                            item.RemoveFromContainer();
                            item.Remove();
                        }
                        else
                        {
                            item.MarkDirty();
                        }
                    }
                    catch (Exception ex)
                    {
                        PrintWarning($"Could not finish furnace debit cleanup: {ex.Message}");
                    }
                }
                debits.Clear();
                if (_debitPool.Count < 8) _debitPool.Push(debits);
            }
        }

        #endregion

        #region Smelt conversion

        private void FillSmeltableInputs(BaseOven oven, ItemContainer container, OvenKind kind, List<Item> results)
        {
            results.Clear();
            if (container?.itemList == null ||
                !TryGetOvenInputSlotRange(oven, container, out int first, out int last, out _)) return;

            foreach (var it in container.itemList)
            {
                if (it?.info == null || it.amount <= 0 || it.position < first || it.position > last ||
                    it.parent != container || it.IsRemoved()) continue;

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

        private bool TryConvertInput(OvenTracker tracker, OvenKind kind, ItemContainer container, Item input, int toConsume, float woodPerInput, out int consumed)
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

            toConsume = Mathf.Min(toConsume, GetAdditionalCapacity(container, outSn) / outPerIn);
            bool pause = woodPerInput > 0f && MustFitCharcoal();
            int charcoalCapacity = pause ? GetAdditionalCapacity(container, ItemCharcoal) : 0;
            int woodAvailable = woodPerInput > 0f ? CountItem(container, ItemWood) : 0;
            if (pause)
                woodAvailable = LimitWoodByCharcoal(tracker, woodAvailable, charcoalCapacity);

            if (woodPerInput > 0f)
            {
                int low = 0;
                int high = toConsume;
                while (low < high)
                {
                    int middle = low + (int)(((long)high - low + 1L) / 2L);
                    if (Mathf.Floor(tracker.FuelDebt + middle * woodPerInput) <= woodAvailable)
                        low = middle;
                    else
                        high = middle - 1;
                }
                toConsume = low;
            }

            while (toConsume > 0)
            {
                float debt = tracker.FuelDebt + toConsume * woodPerInput;
                int wood = woodPerInput > 0f ? Mathf.FloorToInt(debt) : 0;
                int charcoal = pause ? GetCharcoalAmount(tracker, wood) : 0;
                if (TryCommitSmelt(tracker.Oven, container, input, toConsume, wood, outSn, toConsume * outPerIn,
                    charcoal, out bool mayRetry))
                {
                    if (woodPerInput > 0f)
                    {
                        tracker.FuelDebt = debt - wood;
                        RecordCharcoal(tracker, container, wood, pause);
                    }
                    consumed = toConsume;
                    return true;
                }

                // Product and charcoal may be competing for the same empty output slots.
                if (!mayRetry) return false;
                toConsume /= 2;
            }
            return false;
        }

        private bool TryGiveOutput(ItemContainer container, string shortname, int amount) =>
            TryGiveOutput(container, shortname, amount, 0, out _);

        private bool TryGiveOutput(ItemContainer container, string shortname, int amount, int charcoal, out bool canRefund)
        {
            canRefund = true;
            if (amount <= 0 && charcoal <= 0) return true;
            if (container == null) return false;

            var oven = container.entityOwner as BaseOven;
            if (oven == null) return false;

            var toppedUp = GetPooledItemList();
            var created = GetPooledItemList();
            int[] toppedAmounts = PoolGetIntArray(Mathf.Max(1, oven.outputSlots));
            int[] expectedAmounts = PoolGetIntArray(Mathf.Max(1, oven.outputSlots));
            int[] createdAmounts = PoolGetIntArray(Mathf.Max(1, oven.outputSlots));
            bool committed = false;

            try
            {
                committed = (amount <= 0 || PlaceOutput(oven, container, GetCachedItemDefinition(shortname),
                        amount, toppedUp, toppedAmounts, expectedAmounts, created, createdAmounts))
                    && (charcoal <= 0 || PlaceOutput(oven, container, _charcoalDefinition,
                        charcoal, toppedUp, toppedAmounts, expectedAmounts, created, createdAmounts))
                    && OutputEntriesIntact(container, toppedUp, expectedAmounts)
                    && OutputEntriesIntact(container, created, createdAmounts);
                return committed;
            }
            catch (Exception ex)
            {
                if (_config.Debug) PrintWarning($"Furnace output placement failed: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (!committed)
                        canRefund = RollbackOutputGive(container, toppedUp, toppedAmounts, expectedAmounts, created, createdAmounts);
                }
                finally
                {
                    PoolReturnIntArray(toppedAmounts);
                    PoolReturnIntArray(expectedAmounts);
                    PoolReturnIntArray(createdAmounts);
                    ReturnPooledItemList(toppedUp);
                    ReturnPooledItemList(created);
                }
            }
        }

        private bool PlaceOutput(BaseOven oven, ItemContainer container, ItemDefinition definition, int remaining,
            List<Item> toppedUp, int[] toppedAmounts, int[] expectedAmounts, List<Item> created, int[] createdAmounts)
        {
            if (definition == null || definition.stackable <= 0) return false;
            int start = Mathf.Max(0, oven._outputSlotIndex);
            int end = Mathf.Min(container.capacity, start + oven.outputSlots);
            for (int slot = start; slot < end && remaining > 0; slot++)
            {
                var item = container.GetSlot(slot);
                if (item?.info != definition || item.skin != 0) continue;
                int amount = Mathf.Min(remaining, item.MaxStackable() - item.amount);
                if (amount <= 0) continue;
                toppedAmounts[toppedUp.Count] = amount;
                expectedAmounts[toppedUp.Count] = item.amount + amount;
                toppedUp.Add(item);
                item.amount += amount;
                item.MarkDirty();
                remaining -= amount;
            }

            for (int slot = start; slot < end && remaining > 0; slot++)
            {
                if (container.GetSlot(slot) != null) continue;
                int amount = Mathf.Min(remaining, definition.stackable);
                var item = ItemManager.Create(definition, amount);
                if (item == null) return false;
                createdAmounts[created.Count] = amount;
                created.Add(item);
                if (!item.MoveToContainer(container, slot, allowStack: false, allowSwap: false))
                    return false;
                remaining -= amount;
            }
            return remaining == 0;
        }

        private static bool OutputEntriesIntact(ItemContainer container, List<Item> items, int[] amounts)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.IsRemoved() || item.parent != container || item.amount != amounts[i])
                    return false;
            }
            return true;
        }

        private bool RollbackOutputGive(ItemContainer container, List<Item> toppedUp, int[] toppedAmounts,
            int[] expectedAmounts, List<Item> created, int[] createdAmounts)
        {
            bool clean = true;
            for (int i = 0; i < toppedUp.Count; i++)
            {
                var it = toppedUp[i];
                if (it == null || it.IsRemoved() || it.parent != container || it.amount != expectedAmounts[i])
                {
                    clean = false;
                    continue;
                }

                it.amount -= toppedAmounts[i];
                try { it.MarkDirty(); }
                catch (Exception ex)
                {
                    if (_config.Debug) PrintWarning($"Furnace rollback notification failed: {ex.Message}");
                }
            }

            for (int i = 0; i < created.Count; i++)
                clean &= UndoCreatedItem(container, created[i], createdAmounts[i]);
            if (!clean) PrintWarning("A staged furnace output changed during rollback; debits will not be refunded.");
            return clean;
        }

        private bool UndoCreatedItem(ItemContainer container, Item item, int expectedAmount)
        {
            if (item == null || item.IsRemoved() || (item.parent != null && item.parent != container) ||
                item.amount != expectedAmount) return false;

            // A removal veto must not leave a spendable item behind.
            item.amount = 0;
            try
            {
                if (item.parent == container) item.RemoveFromContainer();
                item.Remove();
            }
            catch (Exception ex)
            {
                if (_config.Debug) PrintWarning($"Furnace rollback cleanup failed: {ex.Message}");
            }
            return true;
        }
        private int GetAdditionalCapacity(ItemContainer container, string shortname)
        {
            if (!(container?.entityOwner is BaseOven oven)) return 0;

            ItemDefinition def = GetCachedItemDefinition(shortname);
            if (def == null) return 0;

            int stackSize = def.stackable;
            if (stackSize <= 0) return 0;

            long capacity = 0;
            int start = Mathf.Max(0, oven._outputSlotIndex);
            int end = Mathf.Min(container.capacity, start + oven.outputSlots);
            for (int slot = start; slot < end; slot++)
            {
                var item = container.GetSlot(slot);
                if (item == null)
                    capacity += stackSize;
                else if (item.info == def && item.skin == 0)
                    capacity += Mathf.Max(0, item.MaxStackable() - item.amount);
            }
            return (int)Math.Min(capacity, int.MaxValue);
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
