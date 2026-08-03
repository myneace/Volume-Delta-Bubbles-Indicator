using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles
    {
        // Lightweight record used while ranking price levels within one bar.
        private struct LevelCandidate
        {
            public double Price;
            public double Delta;
            public double AbsDelta;
            public double ChartX;
            public long   BarTimeTicks;   // bar's TimeLeft.Ticks — used as the alert key
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            try
            {
                if (this.CurrentChart?.MainWindow == null || this.HistoricalData == null || this.Count == 0)
                    return;

                Graphics gr = args.Graphics;
                var progress = this.HistoricalData.VolumeAnalysisCalculationProgress;

                if (progress == null || progress.State != VolumeAnalysisCalculationState.Finished)
                {
                    string msg = progress == null
                        ? "High Volume Delta Bubbles: this symbol/connection has no volume analysis (order flow) data"
                        : string.Format("High Volume Delta Bubbles: loading order flow data... {0:0}%", progress.ProgressPercent);

                    using (Font statusFont = new Font("Segoe UI", 9, FontStyle.Italic))
                        DrawShadowedString(gr, msg, statusFont, args.Rectangle.Left + 8, args.Rectangle.Top + 8);
                    return;
                }

                var converter = this.CurrentChart.MainWindow.CoordinatesConverter;
                int barWidthPx = Math.Max(this.CurrentChart.BarsWidth, 1);

                int leftIndex = Math.Max(args.LeftVisibleBarIndex, 0);
                int rightIndex = Math.Min(args.RightVisibleBarIndex, this.Count - 1);
                if (rightIndex < leftIndex)
                    return;

                gr.SmoothingMode = SmoothingMode.AntiAlias;

                // ---- Compute effective minimum volume threshold ----
                // When AutoMinVolume is ON we scan the last AutoLookbackBars bars,
                // collect every per-price Volume value, sort them, and use the
                // AutoPercentile-th value as the threshold.  This calibrates the
                // noise floor to the instrument's own liquidity profile automatically.
                double effectiveMinVolume = this.AutoMinVolume
                    ? ComputePercentileMinVolume()
                    : this.MinVolumeAtPrice;

                // ---- Pass 1: collect every price level that clears the filters ----
                var candidates = new List<LevelCandidate>();

                for (int barIndex = leftIndex; barIndex <= rightIndex; barIndex++)
                {
                    IHistoryItem bar;
                    try { bar = this.HistoricalData[barIndex, SeekOriginHistory.Begin]; }
                    catch { continue; }

                    var priceLevels = bar?.VolumeAnalysisData?.PriceLevels;
                    if (priceLevels == null || priceLevels.Count == 0)
                        continue;

                    double barCenterX = converter.GetChartX(bar.TimeLeft) + barWidthPx / 2.0;

                    foreach (var kvp in priceLevels)
                    {
                        var item = kvp.Value;
                        if (item.Volume < effectiveMinVolume)
                            continue;
                        if (Math.Abs(item.DeltaPercent) < this.MinDeltaPercent)
                            continue;

                        double absDelta = Math.Abs(item.Delta);
                        if (absDelta < this.MinDeltaVolume || absDelta > this.MaxDeltaVolume)
                            continue;

                        candidates.Add(new LevelCandidate
                        {
                            Price        = kvp.Key,
                            Delta        = item.Delta,
                            AbsDelta     = absDelta,
                            ChartX       = barCenterX,
                            BarTimeTicks = bar.TimeLeft.Ticks
                        });
                    }
                }

                if (candidates.Count == 0)
                    return;

                // ---- Pass 2: size relative to the biggest delta currently visible, then draw ----
                double maxAbsDeltaVisible = 0;
                foreach (var c in candidates)
                    if (c.AbsDelta > maxAbsDeltaVisible)
                        maxAbsDeltaVisible = c.AbsDelta;

                if (maxAbsDeltaVisible <= 0)
                    return;

                double sizeRange = Math.Max(0, this.MaxBubbleSize - this.MinBubbleSize);

                using (Font labelFont = new Font("Segoe UI", 8, FontStyle.Bold))
                {
                    foreach (var c in candidates)
                    {
                        // Perceptual (area-based) scaling: diameter grows with the
                        // square root of the normalized magnitude, so bubble AREA -
                        // what the eye actually reads as "size" - tracks delta,
                        // not just radius.
                        double ratio = Math.Max(0, Math.Min(1, c.AbsDelta / maxAbsDeltaVisible));
                        double t = Math.Sqrt(ratio);
                        double diameter = this.MinBubbleSize + t * sizeRange;

                        double y = converter.GetChartY(c.Price);
                        if (y + diameter < args.Rectangle.Top || y - diameter > args.Rectangle.Bottom)
                            continue;

                        bool isBuyDominant = c.Delta >= 0;
                        Color baseColor = isBuyDominant ? this.BuyColor : this.SellColor;
                        int alpha = (int)Math.Round(255 * (this.Opacity / 100.0) * (0.35 + 0.65 * t));
                        alpha = Math.Max(0, Math.Min(255, alpha));

                        float rx = (float)(c.ChartX - diameter / 2);
                        float ry = (float)(y - diameter / 2);
                        float d = (float)diameter;

                        using (var fillBrush = new SolidBrush(Color.FromArgb(alpha, baseColor)))
                        using (var borderPen = new Pen(Color.FromArgb(Math.Min(255, alpha + 60), baseColor), 1f))
                        {
                            gr.FillEllipse(fillBrush, rx, ry, d, d);
                            gr.DrawEllipse(borderPen, rx, ry, d, d);
                        }

                        // ---- Sound alert on NEW bubbles only ----
                        if (this.EnableAlerts)
                        {
                            var key = (c.BarTimeTicks, c.Price, isBuyDominant);
                            if (_alertedBubbles.Add(key))   // returns true only the first time
                            {
                                string sf = isBuyDominant ? _soundBuy : _soundSell;
                                Core.Instance.Loggers.Log(
                                    $"[VolΔBubbles] Alert triggered — {(isBuyDominant ? "BUY" : "SELL")} " +
                                    $"price={c.Price:F5}  file={sf}  exists={File.Exists(sf)}");
                                PlaySoundAsync(sf);
                            }
                        }

                        if (this.ShowLabels && diameter >= this.MinBubbleSize + 4)
                        {
                            string text = (isBuyDominant ? "+" : "-") + FormatVolume(c.AbsDelta);
                            SizeF textSize = gr.MeasureString(text, labelFont);
                            DrawShadowedString(gr, text, labelFont,
                                (float)(c.ChartX - textSize.Width / 2), ry - textSize.Height - 2);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log("HighVolumeDeltaBubbles paint error: " + ex.Message);
            }
        }

        private static void DrawShadowedString(Graphics gr, string text, Font font, float x, float y)
        {
            gr.DrawString(text, font, Brushes.Black, x + 1, y + 1);
            gr.DrawString(text, font, Brushes.White, x, y);
        }

        private static string FormatVolume(double value)
        {
            double abs = Math.Abs(value);
            if (abs >= 1000000)
                return (abs / 1000000).ToString("0.##") + "M";
            if (abs >= 1000)
                return (abs / 1000).ToString("0.##") + "K";
            return abs.ToString("0.##");
        }
    }
}
