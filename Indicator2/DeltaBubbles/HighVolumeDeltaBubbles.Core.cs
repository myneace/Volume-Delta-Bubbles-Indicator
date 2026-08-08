using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.PowerTrades;
using TradingPlatform.BusinessLayer.History.Aggregations;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles : Indicator
    {
        // ── Sound-alert state ─────────────────────────────────────────────────
        // Key: (zone formation-time ticks, zone mid-price, isBuyDominant)
        // Tracks every bubble we have already alerted on so that chart repaints
        // and scrolls do NOT re-trigger the sound.
        private readonly HashSet<(long, double, bool)> _alertedBubbles
            = new HashSet<(long, double, bool)>();

        // Absolute paths to the two MP3 files, resolved once at construction
        // time from the DLL's own directory so they work wherever Quantower
        // deploys the indicator.
        private readonly string _soundBuy;
        private readonly string _soundSell;

        // Power Trades subscription — holds the live, streaming history of
        // Power Trades zones for the current symbol.  Allocated in OnInit(),
        // disposed in OnRemove().  All zone parameters are baked in at load
        // time; reload the indicator after changing them.
        private HistoricalData _powerTradesData;

        public override string ShortName => "PT Bubbles";

        public HighVolumeDeltaBubbles()
            : base()
        {
            Name = "Power Trades Bubbles";
            Description =
                "Plots a bubble at every Power Trades zone — Quantower's large-aggressive-order " +
                "detector. Bubble size reflects combined cumulative zone volume and directional " +
                "skew (Cumulative × |Δ%| / 100); color shows buy vs. sell dominance.";

            // Draw directly on the price chart, not a separate pane.
            SeparateWindow = false;

            // Resolve sound file paths once at startup.
            _soundBuy  = ResolveSoundPath("eee.mp3");
            _soundSell = ResolveSoundPath("ohm.mp3");
            Core.Instance.Loggers.Log(
                $"[PTBubbles] Sound paths — buy='{_soundBuy}'  sell='{_soundSell}'");
        }

        protected override void OnInit()
        {
            // Auto-load symbol specific custom parameters if enabled
            if (this.AutoLoadSymbolSettings)
            {
                this.LoadSymbolSettings();
            }

            // Build the Power Trades aggregation parameters from the indicator's
            // input settings and request tick-level history aggregated as PT zones.
            // The subscription streams new zones in real-time via NewHistoryItem.
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

            _powerTradesData = this.Symbol.GetHistory(new HistoryRequestParameters
            {
                Symbol      = this.Symbol,
                Aggregation = new HistoryAggregationPowerTrades(ptParams),
                FromTime    = Core.Instance.TimeUtils.DateTimeUtcNow.AddMinutes(-this.LookbackMinutes),
                ToTime      = default,          // open-ended → streams real-time
            });

            _powerTradesData.NewHistoryItem += OnPowerTradesZoneFormed;

            Core.Instance.Loggers.Log(
                $"[PTBubbles] Subscribed — lookback={this.LookbackMinutes} min, " +
                $"totalVol={this.TotalVolume}, deltaFilter={this.DeltaFilter}");
        }

        /// <summary>
        /// Fires whenever a new Power Trades zone completes in real-time.
        /// Triggers a chart redraw so the new bubble appears immediately.
        /// </summary>
        private void OnPowerTradesZoneFormed(object sender, HistoryEventArgs e)
        {
            this.CurrentChart?.RedrawBuffer();
        }

        public new void Dispose()
        {
            if (_powerTradesData != null)
            {
                _powerTradesData.NewHistoryItem -= OnPowerTradesZoneFormed;
                _powerTradesData.Dispose();
                _powerTradesData = null;
            }
            base.Dispose();
        }
    }
}
