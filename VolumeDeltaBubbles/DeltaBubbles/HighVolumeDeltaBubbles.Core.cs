using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.PowerTrades;
using TradingPlatform.BusinessLayer.History.Aggregations;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles : Indicator
    {
        // ── Paint-data snapshot ───────────────────────────────────────────────
        // A plain managed-memory cache of Volume Delta zone data extracted from
        // _powerTradesData.  Updated atomically via a volatile reference swap so
        // OnPaintChart can read it lock-free without touching the SDK buffer.
        //
        // Performance rationale:
        //   _powerTradesData[i, SeekOriginHistory.Begin] crosses the managed→native
        //   SDK boundary on every access and may acquire an internal lock.  With
        //   hundreds of zones and ~60 fps rendering this previously produced tens of
        //   thousands of SDK interop calls per second.  Reading from a plain array in
        //   managed memory eliminates all of that cost during painting.
        internal struct PtZoneData
        {
            internal DateTime Time;         // zone formation time (for X mapping and clipping)
            internal double   MidPrice;     // (MaxPrice + MinPrice) / 2  (for Y mapping)
            internal double   DeltaPercent; // signed delta % (sign → color, magnitude → size)
            internal double   Cumulative;   // cumulative volume (size metric + labels)
        }

        // Volatile reference → atomic read/write without a lock.
        // Writers always publish a fully-constructed new array; readers see
        // either the old or new array, never a partially-written one.
        private volatile PtZoneData[] _ptZoneSnapshot = Array.Empty<PtZoneData>();

        // ── Sound-alert dedup state ───────────────────────────────────────────
        // Key: (zone formation-time ticks, zone mid-price, isBuyDominant)
        // Ensures each zone triggers at most one sound, regardless of repaints
        // or scrolls.
        private readonly HashSet<(long, double, bool)> _alertedBubbles
            = new HashSet<(long, double, bool)>();

        // ── GDI resource cache ────────────────────────────────────────────────
        // Allocated once in OnInit(), disposed in Dispose().
        // Avoids two GDI kernel-mode font-creation calls per paint frame.
        private Font _labelFont;
        private Font _statusFont;

        // Absolute paths to the two MP3 files, resolved once at startup.
        private readonly string _soundBuy;
        private readonly string _soundSell;

        // Volume Delta SDK subscription (open-ended → streams real-time zones).
        private HistoricalData _powerTradesData;

        public override string ShortName => "VolΔ Bubbles";

        public HighVolumeDeltaBubbles()
            : base()
        {
            Name = "Volume Delta Bubbles";
            Description =
                "Plots a bubble at every Volume Delta zone — Quantower's large-aggressive-order " +
                "detector. Bubble size reflects combined cumulative zone volume and directional " +
                "skew (Cumulative × |Δ%| / 100); color shows buy vs. sell dominance.";

            SeparateWindow = false;

            _soundBuy  = ResolveSoundPath("eee.mp3");
            _soundSell = ResolveSoundPath("ohm.mp3");
            Core.Instance.Loggers.Log(
                $"[VolΔBubbles] Sound paths — buy='{_soundBuy}'  sell='{_soundSell}'");
        }

        protected override void OnInit()
        {
            // Auto-load symbol-specific parameters if enabled.
            if (this.AutoLoadSymbolSettings)
                this.LoadSymbolSettings();

            // Cache GDI font objects once.  OnPaintChart reads these fields instead
            // of calling new Font(...) on every frame (fixes HIGH GDI perf issue).
            _labelFont?.Dispose();
            _statusFont?.Dispose();
            _labelFont  = new Font("Segoe UI", 8, FontStyle.Bold);
            _statusFont = new Font("Segoe UI", 9, FontStyle.Italic);

            // Initialize zone cache & attempt warm start
            _zoneCache = new PtZoneCache();
            if (this.EnablePersistence)
            {
                string cachePath = GetZoneCacheFilePath();
                string filterFp = GetFilterFingerprint();
                if (_zoneCache.Load(cachePath, filterFp, this.MaxCachedDays) && _zoneCache.Count > 0)
                {
                    // Instant paint ready before SDK connection!
                    _ptZoneSnapshot = _zoneCache.GetSnapshot();

                    // Seed alert tracking so historical zones don't fire sound/alerts
                    foreach (var z in _ptZoneSnapshot)
                        _alertedBubbles.Add((z.Time.Ticks, z.MidPrice, z.DeltaPercent >= 0));

                    Core.Instance.Loggers.Log(
                        $"[VolΔBubbles] Warm start: loaded {_zoneCache.Count} cached zones from '{cachePath}'. Instant paint active.");
                }
            }

            var ptParams = new HistoryAggregationPowerTradesParameters
            {
                TotalVolume         = this.TotalVolume,
                MinTradeVolume      = this.MinTradeVolume,
                MaxTradeVolume      = this.MaxTradeVolume,
                TimeInterval        = this.TimeInterval,
                BasisVolumeInterval = this.BasisVolumeInterval,
                MinZoneHeight       = this.MinZoneHeight,
                MaxZoneHeight       = this.MaxZoneHeight,
                DeltaFilter         = this.DeltaFilter,
                BasisRatioFilter    = this.BasisRatioFilter,
            };

            DateTime fromTime;
            if (this.LookbackMinutes > 0)
            {
                fromTime = Core.Instance.TimeUtils.DateTimeUtcNow.AddMinutes(-this.LookbackMinutes);
            }
            else
            {
                fromTime = Core.Instance.TimeUtils.DateTimeUtcNow.AddDays(-7);
                if (this.HistoricalData != null && this.Count > 0)
                {
                    try { fromTime = this.HistoricalData[0, SeekOriginHistory.Begin].TimeLeft; }
                    catch { }
                }
            }

            // Incremental index optimization:
            // If we already have cached zones, query history from (latestCachedTime - 15min)
            // to drastically shorten broker tick-history download and aggregation time.
            DateTime requestFromTime = fromTime;
            if (this.EnablePersistence && _zoneCache != null && _zoneCache.LatestTime.HasValue)
            {
                DateTime overlapStart = _zoneCache.LatestTime.Value.AddMinutes(-15);
                if (overlapStart > fromTime)
                {
                    requestFromTime = overlapStart;
                    Core.Instance.Loggers.Log(
                        $"[VolΔBubbles] Incremental query: requesting history from {requestFromTime:u} (latest cached={_zoneCache.LatestTime.Value:u}) instead of {fromTime:u}.");
                }
            }

            _powerTradesData = this.Symbol.GetHistory(new HistoryRequestParameters
            {
                Symbol      = this.Symbol,
                Aggregation = new HistoryAggregationPowerTrades(ptParams),
                FromTime    = requestFromTime,
                ToTime      = default,   // open-ended → real-time streaming
            });

            // Subscribe before rebuilding so no real-time zone event is missed.
            _powerTradesData.NewHistoryItem += OnPowerTradesZoneFormed;

            // Build the managed-memory snapshot from all zones already in the
            // buffer, and seed _alertedBubbles so enabling alerts later does not
            // play sounds for historical zones.
            RebuildFromBuffer();

            Core.Instance.Loggers.Log(
                $"[VolΔBubbles] Subscribed — lookback={this.LookbackMinutes} min (queryStart={requestFromTime:u}), " +
                $"totalVol={this.TotalVolume}, deltaFilter={this.DeltaFilter}");
        }

        /// <summary>
        /// Fires whenever a new Volume Delta zone completes in real-time.
        /// Appends the zone to the paint snapshot and triggers a sound alert
        /// (if enabled), then schedules a chart redraw.
        /// </summary>
        private void OnPowerTradesZoneFormed(object sender, HistoryEventArgs e)
        {
            // HistoryEventArgs carries no HistoryItem property in this SDK version.
            // Read the newest zone directly from the buffer.
            if (_powerTradesData?.Count > 0)
            {
                try
                {
                    var raw = _powerTradesData[_powerTradesData.Count - 1, SeekOriginHistory.Begin];

                    if (raw is IPowerTradesHistoryItem pt)
                    {
                        double zoneMidPrice  = (pt.MaxPrice + pt.MinPrice) / 2.0;
                        bool   isBuyDominant = pt.DeltaPercent >= 0;

                        // ── 1. Append to paint snapshot (lock-free) ────────────
                        // OnPaintChart reads _ptZoneSnapshot via volatile load so
                        // it always sees either the old or the fully-written new
                        // array — never a partially-updated state.
                        AppendZoneToSnapshot(raw.TimeLeft, zoneMidPrice, pt.DeltaPercent, pt.Cumulative);

                        if (this.EnablePersistence && _zoneCache != null)
                        {
                            _zoneCache.Add(new PtZoneData
                            {
                                Time         = raw.TimeLeft,
                                MidPrice     = zoneMidPrice,
                                DeltaPercent = pt.DeltaPercent,
                                Cumulative   = pt.Cumulative,
                            });
                        }

                        // ── 2. Alerts (Native Quantower + Sound) ───────────────
                        var key = (raw.TimeLeft.Ticks, zoneMidPrice, isBuyDominant);
                        if (_alertedBubbles.Add(key))
                        {
                            // ── Native Quantower alert event ───────────────────
                            if (this.EnableBubbleAlerts)
                            {
                                TriggerBubbleAlert(raw.TimeLeft, zoneMidPrice, isBuyDominant, pt.Cumulative, pt.DeltaPercent);
                            }

                            // ── Telegram screenshot alert ──────────────────────
                            if (this.EnableTelegramScreenshot)
                            {
                                string directionStr = isBuyDominant ? "Positive" : "Negative";
                                string eventTypeStr = isBuyDominant ? "Created" : "Triggered";
                                SendTelegramBubbleAlertAsync(directionStr, eventTypeStr, zoneMidPrice, pt.Cumulative, pt.DeltaPercent, raw.TimeLeft);
                            }

                            // ── Sound alert ────────────────────────────────────
                            if (this.EnableAlerts)
                            {
                                string sf = isBuyDominant ? _soundBuy : _soundSell;
                                Core.Instance.Loggers.Log(
                                    $"[VolΔBubbles] Sound Alert — {(isBuyDominant ? "BUY" : "SELL")} " +
                                    $"price={zoneMidPrice:F5}  cumVol={pt.Cumulative:F0}  file={sf}");
                                PlaySoundAsync(sf);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(
                        $"[VolΔBubbles] Zone event error: {ex.Message}");
                }
            }

            this.CurrentChart?.RedrawBuffer();
        }

        /// <summary>
        /// Atomically appends one zone to <see cref="_ptZoneSnapshot"/> by
        /// publishing a new array via a volatile reference swap.
        /// O(N) in the number of existing zones but called only when a real-time
        /// zone arrives (at most once every few seconds), never during painting.
        /// </summary>
        private void AppendZoneToSnapshot(DateTime time, double midPrice, double deltaPercent, double cumulative)
        {
            var newZone = new PtZoneData
            {
                Time         = time,
                MidPrice     = midPrice,
                DeltaPercent = deltaPercent,
                Cumulative   = cumulative,
            };

            var oldSnap = _ptZoneSnapshot;
            var newSnap = new PtZoneData[oldSnap.Length + 1];
            Array.Copy(oldSnap, newSnap, oldSnap.Length);
            newSnap[oldSnap.Length] = newZone;
            _ptZoneSnapshot = newSnap;   // volatile write — atomic reference publish
        }

        /// <summary>
        /// Reads every item currently in <see cref="_powerTradesData"/> in a
        /// single O(N) pass to rebuild both the paint snapshot and the alert
        /// dedup set from scratch.  Called at init and after a resubscribe —
        /// never during steady-state painting or zone events.
        /// </summary>
        private void RebuildFromBuffer()
        {
            if (_powerTradesData == null)
            {
                if (!this.EnablePersistence || _zoneCache == null || _zoneCache.Count == 0)
                {
                    _ptZoneSnapshot = Array.Empty<PtZoneData>();
                    _alertedBubbles.Clear();
                }
                return;
            }

            int count = _powerTradesData.Count;
            var incomingZones = new List<PtZoneData>(count);
            var alertKeys     = new HashSet<(long, double, bool)>(count);

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
                        incomingZones.Add(new PtZoneData
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

            if (this.EnablePersistence && _zoneCache != null)
            {
                bool anyAdded = _zoneCache.Merge(incomingZones);
                _ptZoneSnapshot = _zoneCache.GetSnapshot();

                foreach (var z in _ptZoneSnapshot)
                    _alertedBubbles.Add((z.Time.Ticks, z.MidPrice, z.DeltaPercent >= 0));

                if (anyAdded)
                {
                    _zoneCache.Save(GetZoneCacheFilePath(), GetActiveSymbolName(), GetFilterFingerprint());
                }
            }
            else
            {
                // Publish the new snapshot atomically.
                _ptZoneSnapshot = incomingZones.ToArray();

                // Replace alert dedup state.
                _alertedBubbles.Clear();
                foreach (var key in alertKeys)
                    _alertedBubbles.Add(key);
            }

            Core.Instance.Loggers.Log(
                $"[VolΔBubbles] Rebuilt zone cache: {_ptZoneSnapshot.Length} zones, {_alertedBubbles.Count} alert keys.");
        }

        public new void Dispose()
        {
            if (this.EnablePersistence && _zoneCache != null && _zoneCache.IsDirty)
            {
                _zoneCache.Save(GetZoneCacheFilePath(), GetActiveSymbolName(), GetFilterFingerprint());
            }

            if (_powerTradesData != null)
            {
                _powerTradesData.NewHistoryItem -= OnPowerTradesZoneFormed;
                _powerTradesData.Dispose();
                _powerTradesData = null;
            }

            _ptZoneSnapshot = Array.Empty<PtZoneData>();
            _alertedBubbles.Clear();

            _labelFont?.Dispose();
            _statusFont?.Dispose();
            _labelFont  = null;
            _statusFont = null;

            base.Dispose();
        }
    }
}
