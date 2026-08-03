using System;
using System.Collections.Generic;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles
    {
        /// <summary>
        /// Scans the last <see cref="AutoLookbackBars"/> bars, collects every
        /// per-price <c>Volume</c> value (regardless of delta), sorts the list,
        /// and returns the <see cref="AutoPercentile"/>-th percentile as the
        /// dynamic minimum-volume threshold.
        ///
        /// Algorithm:
        ///   • Gather all Level.Volume values from up to AutoLookbackBars bars
        ///     counting backwards from the most recent bar.
        ///   • Sort ascending.
        ///   • Pick index = floor(percentile/100 × count); that value is the
        ///     threshold — meaning <c>(100-percentile)%</c> of all observed
        ///     price-level volumes will pass the filter.
        ///   • Fallback: if no data is available yet (data still loading),
        ///     returns the manual <see cref="MinVolumeAtPrice"/> so the
        ///     indicator stays silent rather than flooding with noise.
        /// </summary>
        private double ComputePercentileMinVolume()
        {
            if (this.HistoricalData == null || this.Count == 0)
                return this.MinVolumeAtPrice;

            // Walk backward from the newest bar, up to AutoLookbackBars bars.
            int totalBars  = this.Count;
            int startIndex = Math.Max(0, totalBars - this.AutoLookbackBars);
            int endIndex   = totalBars - 1;   // inclusive, newest bar

            var volumes = new List<double>(this.AutoLookbackBars * 10);

            for (int i = startIndex; i <= endIndex; i++)
            {
                IHistoryItem bar;
                try { bar = this.HistoricalData[i, SeekOriginHistory.Begin]; }
                catch { continue; }

                var priceLevels = bar?.VolumeAnalysisData?.PriceLevels;
                if (priceLevels == null || priceLevels.Count == 0)
                    continue;

                foreach (var kvp in priceLevels)
                    volumes.Add(kvp.Value.Volume);
            }

            if (volumes.Count == 0)
                return this.MinVolumeAtPrice;   // data not ready yet

            volumes.Sort();

            // Clamp percentile to [0, 99] so index is always valid.
            int pct   = Math.Max(0, Math.Min(99, this.AutoPercentile));
            int index = (int)Math.Floor(pct / 100.0 * volumes.Count);
            index     = Math.Max(0, Math.Min(volumes.Count - 1, index));

            return volumes[index];
        }
    }
}
