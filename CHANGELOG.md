# Changelog

## v1.1.0 (recommended tag for this release)
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
