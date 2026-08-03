// ============================================================================
//  High Volume Delta Bubbles  —  Quantower custom indicator
// ----------------------------------------------------------------------------
//  Plots a bubble on the price chart for every price level, inside every bar,
//  where Quantower's Volume Analysis (cluster / footprint) data shows a
//  strong imbalance between buy volume and sell volume ("delta").
//
//  Why this needs cluster data (and not just bar volume):
//  A normal volume indicator only knows one number per bar. Cluster data
//  breaks that number down by the exact price it traded at, and by which
//  side (buyer or seller) was aggressive at that price. That lets this
//  indicator do three things a bar-volume indicator can't:
//
//    - POSITION each bubble at the real price where the imbalance happened
//      (Y = that price, X = the bar's time) instead of approximating with
//      hl2/close, the way a bar-volume overlay has to.
//    - SIZE each bubble by how large the imbalance was, scaled against the
//      strongest imbalance currently on screen, so the real hot-spots are
//      always the biggest circles on the chart.
//    - COLOR each bubble by which side was in control at that specific
//      price (green = buyers, red = sellers), with fill strength also
//      tracking imbalance strength.
//
//  Data requirement: the connected feed must provide trade-side (Bid/Ask)
//  history for the symbol - the same data Quantower's own Cluster Chart and
//  Footprint tools are built from. This works on any ordinary bar chart /
//  timeframe; the chart does not need to be a Footprint/Cluster chart type.
//
//  AUTO MINIMUM VOLUME (enabled by default):
//    When "Auto min volume" is ON the indicator ignores the manual
//    "Minimum volume per price level" value and instead:
//      1. Scans the last N bars (Auto lookback bars) for all per-price volumes.
//      2. Sorts those volumes and picks the configured percentile
//         (Auto percentile %) as the effective minimum threshold.
//    This means the threshold adapts automatically to each pair's typical
//    tick size and liquidity profile — no manual tuning needed.
//    Raise the percentile to see fewer, higher-conviction bubbles;
//    lower it to include more mid-tier activity.
//
//  Settings live in the indicator's "Settings" window once added to a chart:
//    - Auto min volume / Auto lookback bars / Auto percentile %:
//        automatic noise-floor detection — works across all pairs.
//    - Minimum volume per price level (used only when Auto is OFF):
//        manual override for the noise floor.
//    - Minimum delta imbalance %:
//        directional conviction filter.
//    - Minimum/Maximum delta volume: volume-imbalance range filters.
//    - Smallest/Largest bubble (px), Bubble fill opacity %: visual scaling.
//    - Buy/Sell-dominant color, Show delta labels: cosmetics.
// ============================================================================

using System;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles : Indicator, IVolumeAnalysisIndicator
    {
        #region Input parameters

        // ── Auto minimum-volume detection ─────────────────────────────────────
        // When ON, the indicator computes the effective MinVolumeAtPrice from
        // the historical distribution of per-price volumes for this symbol,
        // so you never have to tune it manually.

        [InputParameter("Auto min volume", 5)]
        public bool AutoMinVolume = true;

        /// <summary>
        /// Number of bars scanned backward from the rightmost visible bar to
        /// build the volume distribution used for the percentile calculation.
        /// More bars → smoother/more stable threshold.
        /// Fewer bars → faster adaptation to regime changes.
        /// </summary>
        [InputParameter("Auto lookback bars", 7, 10, 5000, 10, 0)]
        public int AutoLookbackBars = 200;

        /// <summary>
        /// Percentile of the per-price volume distribution that becomes the
        /// minimum volume threshold.
        ///   70 → only the top 30 % of price levels qualify (moderate filter)
        ///   80 → only the top 20 % qualify (stricter)
        ///   90 → only the top 10 % qualify (very strict / high-conviction only)
        /// Raise this if the chart is too busy; lower if you want more bubbles.
        /// </summary>
        [InputParameter("Auto percentile %", 8, 50, 99, 1, 0)]
        public int AutoPercentile = 75;

        // ── Manual override (used only when Auto min volume is OFF) ────────────
        [InputParameter("Minimum volume per price level (manual)", 10, 0, 10000000, 1, 2)]
        public double MinVolumeAtPrice = 20;

        // ── Other filters ─────────────────────────────────────────────────────
        [InputParameter("Minimum delta imbalance %", 20, 0, 100, 1, 0)]
        public double MinDeltaPercent = 50;

        [InputParameter("Minimum delta volume", 30, 0, 10000000, 1, 2)]
        public double MinDeltaVolume = 10;

        [InputParameter("Maximum delta volume", 35, 0, 10000000, 1, 2)]
        public double MaxDeltaVolume = 1000000;

        [InputParameter("Smallest bubble (px)", 40, 1, 200, 1, 0)]
        public int MinBubbleSize = 6;

        [InputParameter("Largest bubble (px)", 50, 1, 400, 1, 0)]
        public int MaxBubbleSize = 34;

        [InputParameter("Bubble fill opacity %", 60, 5, 100, 1, 0)]
        public int Opacity = 75;

        [InputParameter("Buy-dominant color", 70)]
        public Color BuyColor = Color.FromArgb(255, 0, 191, 99);

        [InputParameter("Sell-dominant color", 80)]
        public Color SellColor = Color.FromArgb(255, 226, 65, 82);

        [InputParameter("Show delta labels", 90)]
        public bool ShowLabels = false;

        [InputParameter("Enable sound alerts", 95)]
        public bool EnableAlerts = false;

        #endregion Input parameters
    }
}