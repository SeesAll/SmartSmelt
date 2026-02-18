# SmartSmelt

High-performance, preset-based smelting accelerator for **Rust (uMod/Oxide)** with automation-safe behavior, smart fuel pull, and population-aware **AutoTune** scheduling.

> Designed for modded servers (2x–1000x+), heavy conveyor automation, and wipe-day peak usage.

---

## Highlights

- **Preset-based acceleration**: `2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, Instant`
- **Single global loop** (no per-oven timers)
- **Adaptive scaling**: processes more ovens per loop as load increases
- **Dynamic loop interval**: automatically adjusts tick interval based on active ovens
- **Smart fuel auto-pull**: pulls only the **additional** wood needed (delta-based)
- **Mixed-ore fuel top-up fix**: adding a second ore type triggers a NextTick recalc
- **Large furnace fuel balancing**: distributes wood across fuel slots
- **Charcoal overflow control**: `Skip` (automation-friendly) or `Pause` (vanilla-strict)
- **AutoTune**: set average population + bias and let SmartSmelt tune scheduling
- **Ore splitting toggle**: enable/disable input-slot splitting
- **Minimal spam by default**; debug tools when needed

---

## How it Works (Performance Architecture)

SmartSmelt avoids the classic “timer per furnace” approach. Instead it:

1. Tracks active ovens in a lightweight dictionary.
2. Runs a **single global loop** that processes up to a capped number of ovens per pass.
3. Uses a snapshot list rebuilt only when the tracked set changes.
4. Uses a round-robin cursor to prevent starvation.
5. Optionally adjusts global loop interval dynamically based on load.

This keeps CPU predictable even with hundreds of active ovens.

---

## Installation

1. Place `SmartSmelt.cs` into:
   - `oxide/plugins/`
2. Reload:
   - `oxide.reload SmartSmelt`
3. Edit config:
   - `oxide/config/SmartSmelt.json`

---

## Commands

- `/ss.info`
  - Shows plugin status, AutoTune status, and effective scheduling values
- `/ss.debug`
  - Enables additional debug logging (use briefly)

---

## Configuration

### Preset

```json
"Preset": "10x"
```

Supported:
`2x, 3x, 5x, 10x, 25x, 50x, 100x, 1000x, Instant`

### AutoTune (recommended for ease of use)

AutoTune automatically selects scheduling settings based on your **AveragePopulation** and **Preset**.

```json
"AutoTuneEnabled": true,
"AveragePopulation": 300,
"AutoTuneBias": "Balanced",
"AutoTuneWriteToConfig": true
```

Bias modes:

- `Balanced` — default behavior (recommended)
- `Performance` — more conservative (reduces hitch risk)
- `Responsiveness` — more aggressive (snappier updates)

**AutoTuneWriteToConfig**
- `true` (default): writes the chosen scheduling values into the config so you can see them
- `false`: runtime-only tuning; config stays as you wrote it

### Ore splitting

```json
"EnableOreSplitting": true
```

- `true`: distributes moved ore across input slots (when safe)
- `false`: respects vanilla stacking, but fuel auto-pull still works

### Charcoal overflow

```json
"CharcoalOverflowMode": "Skip"
```

- `Skip` (recommended): prevents automation stalls
- `Pause`: vanilla-strict behavior (can stall under heavy charcoal congestion)

---

## Wipe-Day Recommendations

- Use `AutoTuneEnabled = true`
- Set `AveragePopulation` to your real peak (not average daily)
- Prefer `Bias = Balanced` (or `Performance` on weaker hardware)
- Keep `CharcoalOverflowMode = Skip` for conveyor-heavy servers

---

## Troubleshooting

- Smelting feels “bursty” at peak:
  - Lower `DynamicMaxGlobalLoopInterval` slightly **or** increase `AdaptiveMaxOvensPerTick` (only if CPU allows)
- Periodic hitches:
  - Lower `AdaptiveMaxOvensPerTick` first, then raise `DynamicMaxGlobalLoopInterval`
- Want visibility:
  - Run `/ss.info` to view effective scheduling values (especially when AutoTune is enabled)

---

## License

Suggested: MIT (or your preferred license)

---

## Support

If you share a server profile (pop + automation level + CPU), you can dial AutoTune bias and population values for best results.
