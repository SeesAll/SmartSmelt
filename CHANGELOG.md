# Changelog

##Version 1.1.6
### Changes
-   Replaced flat refinery buffer with dynamic per-crude scaling.
-   Added refinery-specific completion model (~0.02 wood per crude).
-   Ensured clean completion for all crude batch sizes.
-   Finalized refinery behavior for modded environments.

##Version 1.1.5
### Changes
-   Removed generic idle fuel consumption path from small refinery.
-   Ensured refinery relies only on crude-processing fuel ratios.

##Version 1.1.4
### Changes
-   Implemented refinery-specific insertion-time logic.
-   Prevented overfilling crude that cannot be fully processed by
    available fuel.
-   Improved alignment between crude batches and fuel availability.

## Version 1.1.3
### Changes
-   Added electric-furnace-specific preset scaling system.
-   Introduced:
    -   EnableElectricFurnaceNativeScaling
    -   ElectricFurnaceThroughputScale
    -   ElectricFurnaceCycleSpeedScale
-   Improved perceived speed of electric furnaces relative to presets.

## Version 1.1.2 
### Changes
-   Optimized TryAutoPullFuel() to reduce multiple inventory scans.
-   Added upper clamp to CharcoalPerWood.
-   Hardened GetKind() detection logic.
-   Removed unnecessary locking from array pool.
-   Minor config and readability improvements.

## v1.1.1
### Changes
-   Fixed critical GlobalTick brace bug causing excessive
    MarkActiveChanged() calls.
-   Removed redundant second pass in RemoveFuel().
-   Added debug logging to CanMoveItem() exception paths.
-   Cached whitelist fragment comparisons.
-   Replaced FindObjectsOfType<BaseOven() with more efficient server
    entity scanning.
-   Removed redundant TickOven() off-state branch.
-   Cached StartCooking access.
-   Cached charcoal definition lookup.
-   Improved config comment clarity for AutoPullFuelBufferPercent.

## v1.1.0
### Added
- AutoTune scheduling (AveragePopulation + AutoTuneBias)
- AutoTuneWriteToConfig option (default: true)
- EnableOreSplitting toggle

### Fixed
- Mixed-ore fuel top-up: NextTick fuel recalculation now triggers when mixed ores are present or ore splitting is disabled
- Network ID handling for NextTick fuel recalc dedupe uses `ulong` for compatibility with newer Rust builds

### Performance
- Removed iterator allocations from smeltable-input scanning (hot-path optimization)
- Cached ItemDefinition lookups (reduces repeated ItemManager calls)

## v1.0.6
- Production-ready scheduling + fuel pull logic baseline
