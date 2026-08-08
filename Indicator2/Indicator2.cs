// ============================================================================
//  Power Trades Bubbles  —  Quantower custom indicator
// ----------------------------------------------------------------------------
//  Plots a bubble on the price chart for every Power Trades zone detected by
//  Quantower's built-in order-flow engine. Each zone represents a period where
//  a large volume of aggressive orders accumulated within a short time window,
//  creating a meaningful directional imbalance.
//
//  Data source — Power Trades API:
//    The indicator subscribes to Quantower's HistoryAggregationPowerTrades,
//    the same engine that drives the native Power Trades panel. Zones are
//    pre-filtered by the platform using the parameters set below. No Volume
//    Analysis / Footprint data is required.
//
//  Bubble sizing:
//    Each bubble's diameter is area-scaled by:
//        Cumulative zone volume × |Delta%| / 100
//    This rewards zones that are both large in total volume AND strongly
//    directional — the same intuition as the old delta metric, but computed
//    at the zone level rather than per price-tick.
//
//  Bubble position:
//    X  = zone formation time mapped to chart coordinates.
//    Y  = zone mid-price  ((zone High + zone Low) / 2).
//
//  Real-time streaming:
//    New zones fire NewHistoryItem, which immediately triggers a redraw —
//    no manual refresh needed.
//
//  Settings live in the indicator's "Settings" window once added to a chart:
//    Power Trades filters (applied at load time — reload to change):
//      Lookback window, Min cumulative volume, Min/Max single-trade volume,
//      Time interval, Basis volume interval, Min delta %, Min basis ratio %,
//      Min/Max zone height.
//    Visual controls (live-update on repaint):
//      Smallest/Largest bubble (px), Bubble fill opacity %,
//      Buy/Sell color, Show volume labels, Enable sound alerts.
// ============================================================================

using System;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    public enum BubbleSizingMetric
    {
        CumulativeVolume,
        DeltaVolume
    }

    public enum BubbleScalingMethod
    {
        LinearDiameter,
        AreaScaled,
        Logarithmic
    }

    public partial class HighVolumeDeltaBubbles : Indicator
    {
        #region Input parameters

        // ── Symbol Profile Storage ─────────────────────────────────────────────
        [InputParameter("Auto-load symbol settings", 2)]
        public bool AutoLoadSymbolSettings = true;

        // ── Power Trades history window ────────────────────────────────────────
        // How many minutes of tick history to load on startup.  A wider window
        // shows more historical zones but takes longer on first load.
        [InputParameter("Lookback window (minutes)", 5, 1, 1440, 1, 0)]
        public int LookbackMinutes = 30;

        // ── Power Trades zone filters ──────────────────────────────────────────
        // These mirror the parameters in Quantower's own Power Trades panel.
        // IMPORTANT: They are baked into the data subscription at load time.
        // Reload (remove + re-add) the indicator after changing any of them.

        /// <summary>
        /// Minimum total cumulative volume that must accumulate within the
        /// TimeInterval window for the event to qualify as a zone.
        /// Raise this to suppress low-volume noise.
        /// </summary>
        [InputParameter("Total Volume", 10, 0, 10000000, 1, 2)]
        public double TotalVolume = 100;

        [InputParameter("Min. Trade Volume", 11, 0, 10000000, 1, 2)]
        public double MinTradeVolume = 0;

        [InputParameter("Max. Trade Volume", 12, 0, 10000000, 1, 2)]
        public double MaxTradeVolume = 1000000;

        /// <summary>
        /// Rolling time window (seconds) within which trades must cluster to
        /// be counted as a single Power Trades zone event.
        /// Matches "Time interval" in the PT panel UI.
        /// </summary>
        [InputParameter("Time Interval (sec)", 13, 1, 3600, 1, 0)]
        public int TimeInterval = 5;

        /// <summary>
        /// Reference window (seconds) used to compute the basis (baseline)
        /// volume against which zone volume is compared.
        /// Matches "Basis volume interval" in the PT panel UI.
        /// </summary>
        [InputParameter("Basis Volume Interval (sec)", 14, 1, 86400, 1, 0)]
        public int BasisVolumeInterval = 300;

        [InputParameter("Min. Zone Height (ticks)", 15, 0, 100000, 1, 0)]
        public int MinZoneHeight = 0;

        [InputParameter("Max. Zone Height (ticks)", 16, 0, 100000, 1, 0)]
        public int MaxZoneHeight = 100000;

        /// <summary>
        /// Minimum absolute delta percent for a zone to qualify.
        /// 50 = buy and sell volumes must differ by at least 50 % of the
        /// zone's total cumulative volume (directional conviction filter).
        /// Matches "Delta filter" in the PT panel UI.
        /// </summary>
        [InputParameter("Delta Filter (%)", 20, 0, 100, 1, 0)]
        public double DeltaFilter = 50;

        /// <summary>
        /// Minimum basis ratio percent — filters out zones whose cumulative
        /// volume is not significantly above the baseline.
        /// Matches "Basis ratio filter" in the PT panel UI.
        /// </summary>
        [InputParameter("Basis Ratio Filter (%)", 25, 0, 100, 1, 0)]
        public double BasisRatioFilter = 0;

        // ── Visual controls ────────────────────────────────────────────────────
        [InputParameter("Sizing metric", 30)]
        public BubbleSizingMetric SizingMetric = BubbleSizingMetric.CumulativeVolume;

        [InputParameter("Scaling method", 35)]
        public BubbleScalingMethod ScalingMethod = BubbleScalingMethod.LinearDiameter;

        [InputParameter("Smallest bubble (px)", 40, 1, 200, 1, 0)]
        public int MinBubbleSize = 8;

        [InputParameter("Largest bubble (px)", 50, 1, 400, 1, 0)]
        public int MaxBubbleSize = 50;

        [InputParameter("Bubble fill opacity %", 60, 5, 100, 1, 0)]
        public int Opacity = 75;

        [InputParameter("Buy-dominant color", 70)]
        public Color BuyColor = Color.FromArgb(255, 0, 191, 99);

        [InputParameter("Sell-dominant color", 80)]
        public Color SellColor = Color.FromArgb(255, 226, 65, 82);

        [InputParameter("Show volume labels", 90)]
        public bool ShowLabels = false;

        [InputParameter("Enable sound alerts", 95)]
        public bool EnableAlerts = false;

        #endregion Input parameters

        #region Custom Settings UI Override

        public override System.Collections.Generic.IList<SettingItem> Settings
        {
            get
            {
                var settings = base.Settings;

                settings.Add(new SettingItemAction("Save Symbol Parameters", (args) =>
                {
                    this.SaveSymbolSettings(true);
                    return null;
                }, 200));

                settings.Add(new SettingItemAction("Load Symbol Parameters", (args) =>
                {
                    this.LoadSymbolSettings(true);
                    return null;
                }, 201));

                settings.Add(new SettingItemAction("Reset Symbol Parameters", (args) =>
                {
                    this.DeleteSymbolSettings(true);
                    return null;
                }, 202));

                return settings;
            }
            set
            {
                base.Settings = value;
            }
        }

        #endregion Custom Settings UI Override
    }
}