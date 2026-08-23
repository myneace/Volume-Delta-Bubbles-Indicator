using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles
    {
        public override void OnPaintChart(PaintChartEventArgs args)
        {
            try
            {
                if (this.CurrentChart?.MainWindow == null || this.HistoricalData == null || this.Count == 0)
                    return;

                Graphics gr = args.Graphics;

                // ---- Power Trades data readiness check ----
                Font statusFont = _statusFont ?? new Font("Segoe UI", 9, FontStyle.Italic);
                if (_powerTradesData == null)
                {
                    DrawShadowedString(gr,
                        "Power Trades Bubbles: initializing…",
                        statusFont, args.Rectangle.Left + 8, args.Rectangle.Top + 8);
                    return;
                }

                // Read snapshot array atomically via volatile load (zero SDK interop calls during paint)
                var snapshot = _ptZoneSnapshot;
                if (snapshot.Length == 0)
                {
                    DrawShadowedString(gr,
                        "Power Trades Bubbles: no zones found " +
                        "(check feed supports tick data and PT settings)",
                        statusFont, args.Rectangle.Left + 8, args.Rectangle.Top + 8);
                    return;
                }

                var converter  = this.CurrentChart.MainWindow.CoordinatesConverter;
                int barWidthPx = Math.Max(this.CurrentChart.BarsWidth, 1);

                int leftIndex  = Math.Max(args.LeftVisibleBarIndex, 0);
                int rightIndex = Math.Min(args.RightVisibleBarIndex, this.Count - 1);
                if (rightIndex < leftIndex)
                    return;

                gr.SmoothingMode = SmoothingMode.AntiAlias;

                // ---- Determine visible time window from the chart's bar history ----
                DateTime leftTime, rightTime;
                try
                {
                    leftTime  = this.HistoricalData[leftIndex,  SeekOriginHistory.Begin].TimeLeft;
                    rightTime = this.HistoricalData[rightIndex, SeekOriginHistory.Begin].TimeLeft;
                }
                catch { return; }

                // Include forming live bar up to the present
                rightTime = Core.Instance.TimeUtils.DateTimeUtcNow.AddSeconds(10);

                // ---- Pass 1: Find maximum sizing metric among visible zones (Zero allocations) ----
                double maxAbsDeltaVisible = 0;
                bool isCumulativeMetric = (this.SizingMetric == BubbleSizingMetric.CumulativeVolume);

                for (int i = 0; i < snapshot.Length; i++)
                {
                    ref readonly var zone = ref snapshot[i];

                    if (zone.Time < leftTime || zone.Time > rightTime)
                        continue;

                    double absDeltaPct = Math.Abs(zone.DeltaPercent);
                    double metric = isCumulativeMetric
                        ? zone.Cumulative
                        : (zone.Cumulative * absDeltaPct / 100.0);

                    if (metric > maxAbsDeltaVisible)
                        maxAbsDeltaVisible = metric;
                }

                if (maxAbsDeltaVisible <= 0)
                    return;

                double sizeRange = Math.Max(0, this.MaxBubbleSize - this.MinBubbleSize);
                Font labelFont = _labelFont ?? new Font("Segoe UI", 8, FontStyle.Bold);

                // ---- Pass 2: Draw visible bubbles reusing GDI Brush & Pen (Eliminates GDI churn) ----
                using (var fillBrush = new SolidBrush(Color.Empty))
                using (var borderPen = new Pen(Color.Empty, 1f))
                {
                    for (int i = 0; i < snapshot.Length; i++)
                    {
                        ref readonly var zone = ref snapshot[i];

                        if (zone.Time < leftTime || zone.Time > rightTime)
                            continue;

                        double absDeltaPct = Math.Abs(zone.DeltaPercent);
                        double metric = isCumulativeMetric
                            ? zone.Cumulative
                            : (zone.Cumulative * absDeltaPct / 100.0);

                        if (metric <= 0)
                            continue;

                        double ratio = Math.Max(0, Math.Min(1, metric / maxAbsDeltaVisible));
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

                        double y = converter.GetChartY(zone.MidPrice);
                        if (y + diameter < args.Rectangle.Top || y - diameter > args.Rectangle.Bottom)
                            continue;

                        double chartX = converter.GetChartX(zone.Time) + barWidthPx / 2.0;

                        bool  isBuyDominant = zone.DeltaPercent >= 0;
                        Color baseColor     = isBuyDominant ? this.BuyColor : this.SellColor;
                        int   alpha = (int)Math.Round(255 * (this.Opacity / 100.0) * (0.35 + 0.65 * t));
                        alpha = Math.Max(0, Math.Min(255, alpha));

                        float rx = (float)(chartX - diameter / 2);
                        float ry = (float)(y - diameter / 2);
                        float d  = (float)diameter;

                        // Mutate brush and pen colors without recreating GDI object handles
                        fillBrush.Color = Color.FromArgb(alpha, baseColor);
                        borderPen.Color = Color.FromArgb(Math.Min(255, alpha + 60), baseColor);

                        gr.FillEllipse(fillBrush, rx, ry, d, d);
                        gr.DrawEllipse(borderPen, rx, ry, d, d);

                        // Labels
                        if (this.ShowLabels && diameter >= this.MinBubbleSize + 4)
                        {
                            string text = (isBuyDominant ? "+" : "-") + FormatVolume(zone.Cumulative);
                            SizeF textSize = gr.MeasureString(text, labelFont);
                            DrawShadowedString(gr, text, labelFont,
                                (float)(chartX - textSize.Width / 2), ry - textSize.Height - 2);
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
