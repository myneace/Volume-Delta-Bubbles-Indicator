using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.PowerTrades;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles
    {
        // Lightweight record used while ranking Power Trades zones within the
        // visible chart window before drawing.
        private struct LevelCandidate
        {
            public double Price;           // zone mid-price  ((High + Low) / 2)
            public double Delta;           // signed DeltaPercent  (+ = buy dom.)
            public double AbsDelta;        // sizing metric: Cumulative × |Δ%| / 100
            public double CumulativeVol;   // raw cumulative zone volume (for labels)
            public double ChartX;
            public long   BarTimeTicks;    // zone TimeLeft.Ticks — used as alert key
        }

        public override void OnPaintChart(PaintChartEventArgs args)
        {
            try
            {
                if (this.CurrentChart?.MainWindow == null || this.HistoricalData == null || this.Count == 0)
                    return;

                Graphics gr = args.Graphics;

                // ---- Power Trades data readiness check ----
                using (Font statusFont = new Font("Segoe UI", 9, FontStyle.Italic))
                {
                    if (_powerTradesData == null)
                    {
                        DrawShadowedString(gr,
                            "Power Trades Bubbles: initializing…",
                            statusFont, args.Rectangle.Left + 8, args.Rectangle.Top + 8);
                        return;
                    }
                    if (_powerTradesData.Count == 0)
                    {
                        DrawShadowedString(gr,
                            "Power Trades Bubbles: no zones found " +
                            "(check feed supports tick data and PT settings)",
                            statusFont, args.Rectangle.Left + 8, args.Rectangle.Top + 8);
                        return;
                    }
                }

                var converter  = this.CurrentChart.MainWindow.CoordinatesConverter;
                int barWidthPx = Math.Max(this.CurrentChart.BarsWidth, 1);

                int leftIndex  = Math.Max(args.LeftVisibleBarIndex, 0);
                int rightIndex = Math.Min(args.RightVisibleBarIndex, this.Count - 1);
                if (rightIndex < leftIndex)
                    return;

                gr.SmoothingMode = SmoothingMode.AntiAlias;

                // ---- Determine visible time window from the chart's bar history ----
                // We still use the chart's own HistoricalData only to find the
                // wall-clock time of the leftmost and rightmost visible bars so we
                // can clip Power Trades zones to the visible area.
                DateTime leftTime, rightTime;
                try
                {
                    leftTime  = this.HistoricalData[leftIndex,  SeekOriginHistory.Begin].TimeLeft;
                    rightTime = this.HistoricalData[rightIndex, SeekOriginHistory.Begin].TimeLeft;
                }
                catch { return; }

                // Add a small buffer on the right so a zone that is still forming
                // on the live bar is included.
                rightTime = rightTime.AddSeconds(this.TimeInterval + 5);

                // ---- Pass 1: collect every PT zone whose formation time falls
                //              within the visible chart window ----
                var candidates = new List<LevelCandidate>();

                for (int i = 0; i < _powerTradesData.Count; i++)
                {
                    IHistoryItem item;
                    try { item = _powerTradesData[i, SeekOriginHistory.Begin]; }
                    catch { continue; }

                    if (item.TimeLeft < leftTime || item.TimeLeft > rightTime)
                        continue;

                    if (!(item is IPowerTradesHistoryItem pt))
                        continue;

                    double absDeltaPct = Math.Abs(pt.DeltaPercent);

                    // Calculate sizing metric based on selected user preference.
                    // CumulativeVolume (default) scales directly by zone contract volume so BIG trades are immediately obvious.
                    double sizingMetric = (this.SizingMetric == BubbleSizingMetric.CumulativeVolume)
                        ? pt.Cumulative
                        : (pt.Cumulative * absDeltaPct / 100.0);

                    if (sizingMetric <= 0)
                        continue;

                    // Y = zone mid-price (MaxPrice and MinPrice are the zone's price bounds)
                    double zoneMidPrice = (pt.MaxPrice + pt.MinPrice) / 2.0;

                    // X = zone formation time, offset by half a bar-width to centre
                    // the bubble on the bar the zone formed in (matches old behaviour).
                    double chartX = converter.GetChartX(item.TimeLeft) + barWidthPx / 2.0;

                    candidates.Add(new LevelCandidate
                    {
                        Price         = zoneMidPrice,
                        Delta         = pt.DeltaPercent,    // signed
                        AbsDelta      = sizingMetric,
                        CumulativeVol = pt.Cumulative,
                        ChartX        = chartX,
                        BarTimeTicks  = item.TimeLeft.Ticks,
                    });
                }

                if (candidates.Count == 0)
                    return;

                // ---- Pass 2: size relative to the biggest zone visible, then draw ----
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
                        double ratio = Math.Max(0, Math.Min(1, c.AbsDelta / maxAbsDeltaVisible));
                        double t;

                        switch (this.ScalingMethod)
                        {
                            case BubbleScalingMethod.LinearDiameter:
                                t = ratio;
                                break;
                            case BubbleScalingMethod.Logarithmic:
                                t = Math.Log(1.0 + ratio * 9.0) / Math.Log(10.0);
                                break;
                            case BubbleScalingMethod.AreaScaled:
                            default:
                                t = Math.Sqrt(ratio);
                                break;
                        }

                        double diameter = this.MinBubbleSize + t * sizeRange;

                        double y = converter.GetChartY(c.Price);
                        if (y + diameter < args.Rectangle.Top || y - diameter > args.Rectangle.Bottom)
                            continue;

                        bool  isBuyDominant = c.Delta >= 0;
                        Color baseColor     = isBuyDominant ? this.BuyColor : this.SellColor;
                        int   alpha = (int)Math.Round(255 * (this.Opacity / 100.0) * (0.35 + 0.65 * t));
                        alpha = Math.Max(0, Math.Min(255, alpha));

                        float rx = (float)(c.ChartX - diameter / 2);
                        float ry = (float)(y  - diameter / 2);
                        float d  = (float)diameter;

                        using (var fillBrush = new SolidBrush(Color.FromArgb(alpha, baseColor)))
                        using (var borderPen = new Pen(Color.FromArgb(Math.Min(255, alpha + 60), baseColor), 1f))
                        {
                            gr.FillEllipse(fillBrush, rx, ry, d, d);
                            gr.DrawEllipse(borderPen, rx, ry, d, d);
                        }

                        // ---- Sound alert on NEW zones only ----
                        if (this.EnableAlerts)
                        {
                            var key = (c.BarTimeTicks, c.Price, isBuyDominant);
                            if (_alertedBubbles.Add(key))   // returns true only the first time
                            {
                                string sf = isBuyDominant ? _soundBuy : _soundSell;
                                Core.Instance.Loggers.Log(
                                    $"[PTBubbles] Alert — {(isBuyDominant ? "BUY" : "SELL")} " +
                                    $"price={c.Price:F5}  cumVol={c.CumulativeVol:F0}  " +
                                    $"file={sf}  exists={File.Exists(sf)}");
                                PlaySoundAsync(sf);
                            }
                        }

                        // Show the raw cumulative zone volume as the label text.
                        if (this.ShowLabels && diameter >= this.MinBubbleSize + 4)
                        {
                            string text = (isBuyDominant ? "+" : "-") + FormatVolume(c.CumulativeVol);
                            SizeF textSize = gr.MeasureString(text, labelFont);
                            DrawShadowedString(gr, text, labelFont,
                                (float)(c.ChartX - textSize.Width / 2), ry - textSize.Height - 2);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log("PowerTradesBubbles paint error: " + ex.Message);
            }
        }

        private static void DrawShadowedString(Graphics gr, string text, Font font, float x, float y)
        {
            gr.DrawString(text, font, Brushes.Black, x + 1, y + 1);
            gr.DrawString(text, font, Brushes.White, x,     y);
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
