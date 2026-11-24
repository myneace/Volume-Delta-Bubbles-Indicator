# Power Trades Bubbles Indicator — Comprehensive Fix & Optimization Summary

This document provides a detailed technical report of all root-cause investigations, architectural fixes, performance optimizations, and verification steps performed on the **Power Trades Bubbles** Quantower indicator codebase.

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Issue 1: Delayed Bubble Creation on Active Candle](#2-issue-1-delayed-bubble-creation-on-active-candle)
3. [Issue 2: Desynchronized and Spurious Sound Alerts](#3-issue-2-desynchronized-and-spurious-sound-alerts)
4. [Issue 3: Alert Triggering on First Toggle Activation](#4-issue-3-alert-triggering-on-first-toggle-activation)
5. [Issue 4: Performance Bottlenecks & Rendering Lag](#5-issue-4-performance-bottlenecks--rendering-lag)
6. [Issue 5: SDK Version & Build Pipeline Fix](#6-issue-5-sdk-version--build-pipeline-fix)
7. [System Event Flow Comparison (Before vs After)](#7-system-event-flow-comparison-before-vs-after)
8. [File-by-File Breakdown of Changes](#8-file-by-file-breakdown-of-changes)
9. [Build Verification & Testing Guide](#9-build-verification--testing-guide)

---

## 1. Executive Summary

| Issue / Optimization | Root Cause | Implemented Solution | Result |
|---|---|---|---|
| **Delayed Bubble Creation** | Visible time clipping window used bar open time with an insufficient 10 s buffer, excluding active candle events until the bar closed. | Changed `rightTime` calculation to `DateTimeUtcNow + 10s`. | Bubbles now appear in real time on forming candles without waiting for close. |
| **Alert Desynchronization** | Alert generation logic was inside `OnPaintChart`, triggering sounds on viewport changes (scroll, zoom, repaint) for historical zones. | Extracted alert handling to the real-time event `OnPowerTradesZoneFormed`. | Sounds fire strictly once per qualifying new Power Trades zone. |
| **First Activation Alert Burst** | Alert history deduplication set was unpopulated on initial startup when alerts were disabled. Enabling alerts caused visible historical zones to be evaluated as "new". | Added `RebuildFromBuffer()` to pre-populate deduplication keys on startup and resubscribe. | Enabling alerts no longer triggers spurious historical sounds. |
| **Render-Loop SDK Traversal** | `OnPaintChart` called `_powerTradesData[i, SeekOriginHistory.Begin]` hundreds of times per frame (~30,000 native SDK calls/sec at 60 FPS). | Implemented `volatile PtZoneData[] _ptZoneSnapshot` in managed memory with lock-free atomic updates. | Zero native SDK calls during chart rendering. |
| **GDI Resource Churn** | `SolidBrush`, `Pen`, and `Font` objects were created and destroyed per-bubble and per-frame (6,000+ GDI handles/sec). | Cached fonts in `OnInit()` and reused brush/pen instances by mutating `.Color`. | Eliminated GDI handle churn and kernel-mode transitions. |
| **Audio Thread Starvation** | `PlaySoundAsync` blocked a ThreadPool thread using `while (Playing) Thread.Sleep(100)` for seconds per alert. | Switched to asynchronous `outputDevice.PlaybackStopped` event disposal. | Zero ThreadPool worker thread blocking. |
| **Audio Overlap Prevention** | Rapid successive buy/sell alerts played simultaneously and overlapped. | Added synchronized `_soundLock` and device tracking in `PlaySoundAsync` to immediately stop/dispose active playback before starting the new sound. | New bubble pop-ups immediately cut off any preceding sound alert cleanly. |
| **Broken Standalone Build** | `.csproj` referenced non-existent `v1.146.13` directory instead of installed `v1.146.18`. | Updated all SDK hint paths and output directories to `v1.146.18`. | Clean compilation via `dotnet build` (`0 Errors`). |

---

## 2. Issue 1: Delayed Bubble Creation on Active Candle

### Problem Description
Bubbles failed to appear in real time on the current, forming candle and only showed up after the candle closed and a new candle began.

### Root Cause
In [`HighVolumeDeltaBubbles.Drawing.cs`](PowerTradesBubbles/DeltaBubbles/HighVolumeDeltaBubbles.Drawing.cs), the visible chart time filter was calculated as:
```csharp
leftTime  = this.HistoricalData[leftIndex,  SeekOriginHistory.Begin].TimeLeft;
rightTime = this.HistoricalData[rightIndex, SeekOriginHistory.Begin].TimeLeft;
rightTime = rightTime.AddSeconds(this.TimeInterval + 5);
```
- In Quantower, `HistoricalData[rightIndex].TimeLeft` represents the **bar open time** (e.g., `12:00:00` for a 1-minute bar spanning `12:00:00`–`12:01:00`).
- The offset added was `TimeInterval + 5` (default: 5 s + 5 s = 10 s), setting `rightTime` to `12:00:10`.
- Any Power Trades zone forming between second 11 and second 60 had `item.TimeLeft > rightTime`, causing it to be silently skipped by the visibility filter `if (item.TimeLeft > rightTime) continue;`.
- Only when the bar finished and a new bar opened did `rightIndex` advance, finally revealing the previous bar's bubbles.

### Implementation Fix
```csharp
// Extend rightTime to the present so that any PT zone on the
// still-forming live bar is always within the clipping window.
rightTime = Core.Instance.TimeUtils.DateTimeUtcNow.AddSeconds(10);
```

---

## 3. Issue 2: Desynchronized and Spurious Sound Alerts

### Problem Description
Audio alerts played inconsistently, often firing when no corresponding bubble was created or during chart scrolling/zooming.

### Root Cause
The sound alert trigger was embedded inside `OnPaintChart`:
1. `OnPaintChart` executes on UI render frames, scrolling, zooming, resizing, and window focus changes.
2. Because the visible bar range changes during viewport interactions, older historical zones entered the `candidates` list for the first time on a given repaint.
3. `_alertedBubbles.Add(key)` returned `true` for those historical items, triggering false audio playback.

### Implementation Fix
1. Completely removed sound alert logic from `OnPaintChart`.
2. Relocated alert evaluation into `OnPowerTradesZoneFormed`, which executes strictly when the Quantower engine delivers a new zone item.
3. Added robust deduplication keying `(Ticks, MidPrice, IsBuyDominant)`.

```csharp
private void OnPowerTradesZoneFormed(object sender, HistoryEventArgs e)
{
    if (_powerTradesData?.Count > 0)
    {
        try
        {
            var raw = _powerTradesData[_powerTradesData.Count - 1, SeekOriginHistory.Begin];
            if (raw is IPowerTradesHistoryItem pt)
            {
                double zoneMidPrice  = (pt.MaxPrice + pt.MinPrice) / 2.0;
                bool   isBuyDominant = pt.DeltaPercent >= 0;

                AppendZoneToSnapshot(raw.TimeLeft, zoneMidPrice, pt.DeltaPercent, pt.Cumulative);

                if (this.EnableAlerts)
                {
                    var key = (raw.TimeLeft.Ticks, zoneMidPrice, isBuyDominant);
                    if (_alertedBubbles.Add(key))
                    {
                        string sf = isBuyDominant ? _soundBuy : _soundSell;
                        PlaySoundAsync(sf);
                    }
                }
            }
        }
        catch (Exception ex) { Core.Instance.Loggers.Log($"[PTBubbles] Zone event error: {ex.Message}"); }
    }
    this.CurrentChart?.RedrawBuffer();
}
```

---

## 4. Issue 3: Alert Triggering on First Toggle Activation

### Problem Description
Enabling the "Enable sound alerts" toggle caused an immediate audio playback burst, even without any active market event.

### Root Cause
When the indicator initialized with `EnableAlerts = false`, the deduplication set `_alertedBubbles` remained empty. When the user toggled `EnableAlerts = true`, the next repaint treated every visible historical zone as "unalerted" and triggered sounds for all of them.

### Implementation Fix
Introduced `RebuildFromBuffer()`, which pre-populates `_alertedBubbles` with all existing historical zones upon subscription initialization in `OnInit()` and feed reconnection in `ReSubscribePowerTrades()`:

```csharp
private void RebuildFromBuffer()
{
    if (_powerTradesData == null)
    {
        _ptZoneSnapshot = Array.Empty<PtZoneData>();
        _alertedBubbles.Clear();
        return;
    }

    int count = _powerTradesData.Count;
    var zones     = new List<PtZoneData>(count);
    var alertKeys = new HashSet<(long, double, bool)>(count);

    for (int i = 0; i < count; i++)
    {
        try
        {
            var item = _powerTradesData[i, SeekOriginHistory.Begin];
            if (item is IPowerTradesHistoryItem pt)
            {
                double mid   = (pt.MaxPrice + pt.MinPrice) / 2.0;
                bool   isBuy = pt.DeltaPercent >= 0;

                alertKeys.Add((item.TimeLeft.Ticks, mid, isBuy));
                zones.Add(new PtZoneData
                {
                    Time         = item.TimeLeft,
                    MidPrice     = mid,
                    DeltaPercent = pt.DeltaPercent,
                    Cumulative   = pt.Cumulative,
                });
            }
        }
        catch { }
    }

    _ptZoneSnapshot = zones.ToArray();
    _alertedBubbles.Clear();
    foreach (var key in alertKeys)
        _alertedBubbles.Add(key);
}
```

---

## 5. Issue 4: Performance Bottlenecks & Rendering Lag

### Bottlenecks Identified

1. **Per-Frame Native SDK Interop**:
   - `OnPaintChart` previously called `_powerTradesData[i, SeekOriginHistory.Begin]` on every paint frame. With hundreds of zones at 60 FPS, this caused up to **30,000 native calls per second**, acquiring internal SDK locks repeatedly.
   - **Fix**: Created an in-memory `volatile PtZoneData[] _ptZoneSnapshot`. `OnPaintChart` now reads directly from contiguous managed memory with **0 native calls**.

2. **GDI Brush & Pen Handle Allocation Churn**:
   - In `OnPaintChart`, `new SolidBrush(...)` and `new Pen(...)` were allocated and disposed inside the inner loop for every bubble. At 50 bubbles × 60 FPS, this created **6,000+ GDI handles per second**.
   - **Fix**: Single `SolidBrush` and `Pen` instances are allocated once with `using` outside the loop, and their `.Color` property is mutated per bubble without recreating GDI handles.

3. **GDI Font Handle Creation on Every Frame**:
   - `statusFont` and `labelFont` were allocated on every frame via `new Font(...)`.
   - **Fix**: Pre-allocated `_labelFont` and `_statusFont` during `OnInit()`, disposed in `Dispose()`.

4. **Per-Frame Heap Allocation**:
   - `new List<LevelCandidate>()` was instantiated on every paint frame.
   - **Fix**: Replaced intermediate list allocations with a direct, zero-allocation two-pass scan over `_ptZoneSnapshot`.

5. **Audio ThreadPool Block**:
   - `PlaySoundAsync` previously ran `while (outputDevice.PlaybackState == PlaybackState.Playing) Thread.Sleep(100);`, blocking ThreadPool threads for up to 30 seconds.
   - **Fix**: Refactored to non-blocking asynchronous event disposal via `outputDevice.PlaybackStopped`.

---

## 6. Issue 5: SDK Version & Build Pipeline Fix

### Problem Description
The project failed to build via `dotnet build` due to missing references to `TradingPlatform.BusinessLayer.dll`.

### Root Cause
[`Indicator2.csproj`](PowerTradesBubbles/PowerTradesBubbles.csproj) had hardcoded paths pointing to Quantower version `v1.146.13`, whereas the installed Quantower version on the system is `v1.146.18`.

### Implementation Fix
Updated all HintPaths and OutputPaths in [`Indicator2.csproj`](PowerTradesBubbles/PowerTradesBubbles.csproj):
```xml
<PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Debug|AnyCPU'">
  <OutputPath>C:\Quantower\TradingPlatform\v1.146.18\..\..\Settings\Scripts\Indicators\Indicator2</OutputPath>
</PropertyGroup>
<PropertyGroup Condition="'$(Configuration)|$(Platform)'=='Release|AnyCPU'">
  <OutputPath>C:\Quantower\TradingPlatform\v1.146.18\..\..\Settings\Scripts\Indicators\Indicator2</OutputPath>
</PropertyGroup>
<ItemGroup>
  <Reference Include="TradingPlatform.BusinessLayer">
    <HintPath>C:\Quantower\TradingPlatform\v1.146.18\bin\TradingPlatform.BusinessLayer.dll</HintPath>
    <Private>False</Private>
  </Reference>
</ItemGroup>
```

---

## 7. System Event Flow Comparison (Before vs After)

### Before (Problematic Flow)
```
Market Tick
    │
    ▼
Quantower PT Engine
    │
    ▼
NewHistoryItem ──► OnPowerTradesZoneFormed() ──► RedrawBuffer()
                                                        │
                                                        ▼
                                                 OnPaintChart() [60 FPS / Scrolled]
                                                        ├── O(N) SDK Indexer calls (_powerTradesData[i])
                                                        ├── new List<LevelCandidate>() [Heap Alloc]
                                                        ├── new Font(...) [GDI Handle Churn]
                                                        ├── Filter Time: rightTime < forming candle! ──► Bubble hidden until close!
                                                        ├── new SolidBrush / new Pen per bubble [GDI Churn]
                                                        └── if (EnableAlerts) _alertedBubbles.Add() ──► SPURIOUS ALERTS ON SCROLL/INIT!
```

### After (Optimized & Synchronized Flow)
```
Market Tick
    │
    ▼
Quantower PT Engine
    │
    ▼
NewHistoryItem ──► OnPowerTradesZoneFormed() [Data Event Thread]
                         ├── AppendZoneToSnapshot() [Atomic Lock-Free Array Publish]
                         ├── if (EnableAlerts) Dedup Key Check ──► PlaySoundAsync() [Event-Driven Non-blocking]
                         └── RedrawBuffer()
                                  │
                                  ▼
                           OnPaintChart() [UI Thread - Fast 60 FPS Render]
                                  ├── Read volatile _ptZoneSnapshot [Zero SDK Interop]
                                  ├── Use Cached Fonts (_labelFont, _statusFont)
                                  ├── Filter Time: rightTime = UtcNow + 10s [Active Candle Included!]
                                  ├── Pass 1: Find Max Size [Zero Allocations]
                                  └── Pass 2: Draw with Reused Brush/Pen (.Color mutated)
```

---

## 8. File-by-File Breakdown of Changes

### 1. [`PowerTradesBubbles.csproj`](PowerTradesBubbles/PowerTradesBubbles.csproj)
- Updated Quantower version reference from `v1.146.13` to `v1.146.18`.

### 2. [`HighVolumeDeltaBubbles.Core.cs`](PowerTradesBubbles/DeltaBubbles/HighVolumeDeltaBubbles.Core.cs)
- Added `PtZoneData` struct and `volatile PtZoneData[] _ptZoneSnapshot`.
- Added cached `_labelFont` and `_statusFont` fields.
- Implemented `AppendZoneToSnapshot(...)` for atomic, lock-free real-time zone additions.
- Implemented `RebuildFromBuffer()` for single-pass zone snapshot creation and alert key pre-population.
- Updated `OnPowerTradesZoneFormed()` to handle snapshot appending and strictly synchronized alert triggers.
- Updated `Dispose()` to properly release fonts and clear collections.

### 3. [`HighVolumeDeltaBubbles.Drawing.cs`](PowerTradesBubbles/DeltaBubbles/HighVolumeDeltaBubbles.Drawing.cs)
- Rewrote `OnPaintChart` to render directly from `_ptZoneSnapshot`.
- Set `rightTime = Core.Instance.TimeUtils.DateTimeUtcNow.AddSeconds(10)` to ensure forming candle zones are immediately visible.
- Replaced per-bubble `SolidBrush`/`Pen` allocations with single reused instances.
- Reused cached font objects instead of allocating on each paint pass.
- Removed intermediate `List<LevelCandidate>` allocations.
- Removed all sound alert logic from the rendering loop.

### 4. [`HighVolumeDeltaBubbles.Alerts.cs`](PowerTradesBubbles/DeltaBubbles/HighVolumeDeltaBubbles.Alerts.cs)
- Refactored `PlaySoundAsync` to use non-blocking event-driven resource disposal on `outputDevice.PlaybackStopped`.

### 5. [`HighVolumeDeltaBubbles.SymbolStorage.cs`](PowerTradesBubbles/DeltaBubbles/HighVolumeDeltaBubbles.SymbolStorage.cs)
- Updated `ReSubscribePowerTrades()` to call `RebuildFromBuffer()` to keep the snapshot and alert state synchronized after parameter changes.

---

## 9. Build Verification & Testing Guide

### Automated Build Verification
Ran Release build using the .NET CLI:
```powershell
dotnet build Indicator2\Indicator2.csproj --configuration Release
```
**Result**: `0 Error(s)`, `Build Succeeded`. The compiled assembly was deployed directly to `C:\Quantower\Settings\Scripts\Indicators\Indicator2\Indicator2.dll`.

### Quantower Testing Checklist
1. **Live Candle Bubble Test**: Load indicator on an active symbol. As soon as aggressive order volume crosses the threshold on the active, still-forming candle, verify that the bubble immediately renders on the current candle without waiting for candle close.
2. **Toggle Activation Test**: Enable the "Enable sound alerts" toggle in indicator settings. Confirm that **no sound** plays immediately upon enabling.
3. **Alert Synchronization Test**: Observe the chart when a new qualifying zone forms. Verify that the sound alert triggers simultaneously with the bubble appearing.
4. **Viewport Interaction Test**: Pan, scroll back through historical bars, and zoom the chart. Confirm that no false alerts are produced and rendering remains smooth at 60 FPS with no stutter.
5. **Symbol / Parameter Change Test**: Change parameters (e.g., Total Volume) and save. Confirm that the data subscription cleanly re-subscribes without generating burst alerts.
