using System;
using System.Collections.Generic;
using System.Drawing;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles : Indicator, IVolumeAnalysisIndicator
    {
        // ── Sound-alert state ─────────────────────────────────────────────────
        // Key: (bar open-time ticks, price, isBuyDominant)
        // Tracks every bubble we have already alerted on so that chart repaints
        // and scrolls do NOT re-trigger the sound.
        private readonly HashSet<(long, double, bool)> _alertedBubbles
            = new HashSet<(long, double, bool)>();

        // Absolute paths to the two MP3 files, resolved once at construction
        // time from the DLL's own directory so they work wherever Quantower
        // deploys the indicator.
        private readonly string _soundBuy;
        private readonly string _soundSell;

        // Tells Quantower this indicator needs the full per-price (footprint)
        // breakdown, not just the per-bar Total - without this, PriceLevels
        // stays empty even after volume analysis data finishes loading.
        public bool IsRequirePriceLevelsCalculation => true;

        public override string ShortName => "VolΔ Bubbles";

        public HighVolumeDeltaBubbles()
            : base()
        {
            Name = "High Volume Delta Bubbles";
            Description = "Bubbles at the exact price levels, inside each bar, where cluster/footprint data shows a strong buy vs. sell volume imbalance. Bubble size reflects imbalance magnitude; color shows which side was in control.";

            // Draw directly on price, not in a separate oscillator pane -
            // the whole point is aligning bubbles with real price levels.
            SeparateWindow = false;

            // Resolve sound file paths. Assembly.Location is empty when
            // Quantower loads the DLL from memory, so we search multiple
            // candidate roots and log each attempt for easy diagnosis.
            _soundBuy  = ResolveSoundPath("eee.mp3");
            _soundSell = ResolveSoundPath("ohm.mp3");
            Core.Instance.Loggers.Log(
                $"[Vol\u0394Bubbles] Sound paths — buy='{_soundBuy}'  sell='{_soundSell}'");
        }

        // Fires once Quantower finishes crunching trade-by-trade history into
        // per-price buy/sell volume. Force a repaint so bubbles appear as soon
        // as the data is ready instead of waiting for the next chart interaction.
        public void VolumeAnalysisData_Loaded()
        {
            this.CurrentChart?.RedrawBuffer();
        }
    }
}
