# Changelog

All notable changes to **Power Trades Bubbles** are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to [Semantic Versioning](https://semver.org/).

---

## [Unreleased]

---

## [1.0.0] — 2026-11-24

### Added
- Initial public release.
- Real-time bubble rendering on the **forming candle** (no bar-close delay).
- Bubble sizing by `Cumulative × |Δ%| / 100` (default) or raw cumulative volume.
- Three scaling modes: `LinearDiameter`, `AreaScaled` (default), `Logarithmic`.
- Buy (cyan) / Sell (magenta) color-coding with configurable colors and opacity.
- Volume labels on sufficiently large bubbles (`ShowLabels`).
- **Sound alerts** via NAudio (`eee.mp3` = buy, `ohm.mp3` = sell) — overlap-safe, non-blocking.
- **Native Quantower alert panel** integration (`EnableBubbleAlerts`).
- **Telegram screenshot alerts** — offscreen 1440×840 chart renderer (occlusion-immune) with screen-capture fallback.
- **Zone cache / persistence** — zones survive restarts; incremental broker queries from last cached timestamp.
- **Per-symbol parameter profiles** — Save, Load, Delete via indicator panel buttons.
- `AutoLoadSymbolSettings` for automatic profile loading on init.
- Deduplication of alerts via `HashSet<(long Ticks, double MidPrice, bool IsBuy)>`.
- `RebuildFromBuffer()` pre-seeds the dedup set at startup and resubscribe to prevent spurious alerts on toggle.
- Lock-free `volatile PtZoneData[]` snapshot for zero-SDK-interop rendering at 60 FPS.
- Cached `Font` objects; reused `SolidBrush` / `Pen` via `.Color` mutation — eliminates GDI handle churn.

### Fixed
- Bubbles on forming candles were hidden until bar close (wrong `rightTime` clipping — now `UtcNow + 10 s`).
- Spurious sound alerts on chart scroll / zoom (alert logic moved from `OnPaintChart` to `OnPowerTradesZoneFormed`).
- Alert burst on first toggle activation (dedup set now pre-populated on init).
- ThreadPool starvation from blocking `while (Playing) Thread.Sleep(100)` in audio playback (replaced with `PlaybackStopped` event disposal).
- Build failure referencing non-existent Quantower `v1.146.13` SDK — updated to `v1.146.18`.

[Unreleased]: https://github.com/<your-username>/PowerTradesBubbles/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/<your-username>/PowerTradesBubbles/releases/tag/v1.0.0
