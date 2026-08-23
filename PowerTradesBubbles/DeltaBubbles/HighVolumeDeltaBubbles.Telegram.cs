using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using TradingPlatform.BusinessLayer;
using Color = System.Drawing.Color;
using FontFamily = System.Drawing.FontFamily;
using Matrix = System.Drawing.Drawing2D.Matrix;
using Pen = System.Drawing.Pen;
using Point = System.Drawing.Point;
using GdiBrushes = System.Drawing.Brushes;
using GdiDashStyle = System.Drawing.Drawing2D.DashStyle;
using GdiLinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles
    {
        #region Telegram Input Parameters

        [InputParameter("Enable Telegram Screenshot", 97)]
        public bool EnableTelegramScreenshot = false;

        [InputParameter("Screenshot Mode", 98)]
        public TelegramScreenshotMode ScreenshotMode = TelegramScreenshotMode.DirectChartRenderer;

        [InputParameter("Telegram Bot Token", 99)]
        public string TelegramBotToken = "";

        [InputParameter("Telegram Chat ID", 100)]
        public string TelegramChatId = "";

        [InputParameter("Telegram Alert Cooldown (sec)", 101, 0, 3600, 1, 0)]
        public int TelegramCooldownSeconds = 5;

        #endregion Telegram Input Parameters

        #region Win32 Interop

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        #endregion Win32 Interop

        private static readonly HttpClient _httpClient = new HttpClient();
        private DateTime _lastTelegramSentTime = DateTime.MinValue;

        /// <summary>
        /// Asynchronously captures the chart screenshot and sends it via Telegram photo API.
        /// Executed in background task to avoid blocking calculation and paint threads.
        /// </summary>
        public void SendTelegramBubbleAlertAsync(string direction, string eventType, double price, double cumulativeVolume, double deltaPercent, DateTime time)
        {
            if (!this.EnableTelegramScreenshot)
                return;

            if (string.IsNullOrWhiteSpace(this.TelegramBotToken) || string.IsNullOrWhiteSpace(this.TelegramChatId))
            {
                Core.Instance.Loggers.Log("[PTBubbles] Telegram screenshot skipped: Bot Token or Chat ID is not configured.");
                return;
            }

            // Check cooldown
            if (this.TelegramCooldownSeconds > 0)
            {
                var elapsed = DateTime.UtcNow - _lastTelegramSentTime;
                if (elapsed.TotalSeconds < this.TelegramCooldownSeconds)
                {
                    Core.Instance.Loggers.Log($"[PTBubbles] Telegram alert throttled by cooldown ({elapsed.TotalSeconds:F1}s / {this.TelegramCooldownSeconds}s).");
                    return;
                }
            }

            _lastTelegramSentTime = DateTime.UtcNow;

            string symName = GetActiveSymbolName();
            string caption = $"[{this.Name}] {direction} Bubble {eventType}\n" +
                             $"Symbol: {symName}\n" +
                             $"Price: {price:F2}\n" +
                             $"Volume: {FormatVolume(cumulativeVolume)}\n" +
                             $"Delta: {deltaPercent:+0.0;-0.0;0.0}%\n" +
                             $"Time: {time:HH:mm:ss} UTC";

            Task.Run(async () =>
            {
                string tempPngPath = null;
                try
                {
                    if (this.ScreenshotMode == TelegramScreenshotMode.DirectChartRenderer)
                    {
                        tempPngPath = RenderOffscreenChart(symName, direction, eventType, price, cumulativeVolume, deltaPercent, time);
                    }
                    else
                    {
                        tempPngPath = CaptureChartScreenshot(symName);
                    }

                    // Fallback to direct renderer if screen capture failed
                    if ((string.IsNullOrEmpty(tempPngPath) || !File.Exists(tempPngPath)) && this.ScreenshotMode != TelegramScreenshotMode.DirectChartRenderer)
                    {
                        tempPngPath = RenderOffscreenChart(symName, direction, eventType, price, cumulativeVolume, deltaPercent, time);
                    }

                    if (string.IsNullOrEmpty(tempPngPath) || !File.Exists(tempPngPath))
                    {
                        Core.Instance.Loggers.Log("[PTBubbles] Failed to generate chart screenshot.");
                        return;
                    }

                    await PostTelegramPhotoAsync(tempPngPath, caption);
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log($"[PTBubbles] Telegram send error: {ex.Message}");
                }
                finally
                {
                    // Clean up temporary image file
                    if (!string.IsNullOrEmpty(tempPngPath) && File.Exists(tempPngPath))
                    {
                        try { File.Delete(tempPngPath); } catch { }
                    }
                }
            });
        }

        #region Direct Offscreen Chart Generator (100% Occlusion Immune)

        /// <summary>
        /// Generates a pristine, high-definition standalone trading chart image (1440x840) directly in memory.
        /// Renders real candles, price/time grids, Power Trades bubbles with exact sizes/colors, volume histogram,
        /// and alert event badges. 100% immune to window occlusion, minimization, or desktop switching.
        /// </summary>
        public string RenderOffscreenChart(string symbol, string alertDirection, string alertEventType, double alertPrice, double alertVolume, double alertDelta, DateTime alertTime)
        {
            try
            {
                int totalBars = this.HistoricalData?.Count ?? 0;
                if (totalBars == 0)
                {
                    Core.Instance.Loggers.Log("[PTBubbles] Direct chart render skipped: no historical bars available.");
                    return null;
                }

                int imgWidth = 1440;
                int imgHeight = 840;

                int headerHeight = 65;
                int timeAxisHeight = 45;
                int priceAxisWidth = 95;

                int chartLeft = 20;
                int chartTop = headerHeight + 10;
                int chartRight = imgWidth - priceAxisWidth;
                int chartBottom = imgHeight - timeAxisHeight;
                int chartWidth = chartRight - chartLeft;
                int chartHeight = chartBottom - chartTop;

                int barsToRender = Math.Min(totalBars, 85);
                int startIndex = Math.Max(0, totalBars - barsToRender);
                int count = totalBars - startIndex;

                // ── 1. Read Historical Bars ──────────────────────────────────────────
                var barTimes = new DateTime[count];
                var opens = new double[count];
                var highs = new double[count];
                var lows = new double[count];
                var closes = new double[count];
                var volumes = new double[count];

                double minPrice = double.MaxValue;
                double maxPrice = double.MinValue;
                double maxVolume = 0;

                for (int i = 0; i < count; i++)
                {
                    var item = this.HistoricalData[startIndex + i, SeekOriginHistory.Begin];
                    barTimes[i] = item.TimeLeft;
                    opens[i] = item[PriceType.Open];
                    highs[i] = item[PriceType.High];
                    lows[i] = item[PriceType.Low];
                    closes[i] = item[PriceType.Close];
                    volumes[i] = item[PriceType.Volume];

                    if (highs[i] > maxPrice) maxPrice = highs[i];
                    if (lows[i] < minPrice) minPrice = lows[i];
                    if (volumes[i] > maxVolume) maxVolume = volumes[i];
                }

                if (minPrice >= maxPrice || minPrice == double.MaxValue)
                {
                    minPrice = opens[0] * 0.99;
                    maxPrice = opens[0] * 1.01;
                }

                // Add 6% padding on top and bottom for price scale
                double priceRange = maxPrice - minPrice;
                minPrice -= priceRange * 0.06;
                maxPrice += priceRange * 0.08;
                priceRange = maxPrice - minPrice;

                DateTime startTime = barTimes[0];
                DateTime endTime = barTimes[count - 1];

                // Coordinate Mapping Helpers
                double PriceToY(double price) => chartTop + (maxPrice - price) / priceRange * (chartHeight - 75);
                double BarIndexToX(int idx) => chartLeft + (idx + 0.5) * ((double)chartWidth / count);
                double TimeToX(DateTime t)
                {
                    if (t <= startTime) return chartLeft;
                    if (t >= endTime) return chartRight;
                    double ratio = (t - startTime).TotalSeconds / Math.Max(1, (endTime - startTime).TotalSeconds);
                    return chartLeft + ratio * chartWidth;
                }

                string fileName = $"PTBubbles_Direct_{symbol}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.png";
                string fullPath = Path.Combine(Path.GetTempPath(), fileName);

                using (var bmp = new Bitmap(imgWidth, imgHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (var gr = Graphics.FromImage(bmp))
                {
                    gr.SmoothingMode = SmoothingMode.AntiAlias;
                    gr.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    gr.InterpolationMode = InterpolationMode.HighQualityBicubic;

                    // ── 2. Background Gradient ───────────────────────────────────────
                    using (var bgBrush = new GdiLinearGradientBrush(new Rectangle(0, 0, imgWidth, imgHeight),
                        Color.FromArgb(255, 19, 23, 34), Color.FromArgb(255, 14, 17, 25), LinearGradientMode.Vertical))
                    {
                        gr.FillRectangle(bgBrush, 0, 0, imgWidth, imgHeight);
                    }

                    // ── 3. Grid Lines & Axis Markings ─────────────────────────────────
                    using (var gridPen = new Pen(Color.FromArgb(255, 33, 38, 52), 1f) { DashStyle = GdiDashStyle.Dash })
                    using (var axisFont = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Regular))
                    using (var axisTextBrush = new SolidBrush(Color.FromArgb(255, 130, 138, 155)))
                    {
                        // Horizontal Price Grid (7 divisions)
                        int priceDivisions = 7;
                        for (int d = 0; d <= priceDivisions; d++)
                        {
                            double p = minPrice + (d / (double)priceDivisions) * priceRange;
                            float y = (float)PriceToY(p);

                            gr.DrawLine(gridPen, chartLeft, y, chartRight, y);
                            string pStr = p.ToString("F2");
                            gr.DrawString(pStr, axisFont, axisTextBrush, chartRight + 8, y - 7);
                        }

                        // Vertical Time Grid (6 divisions)
                        int timeDivisions = 6;
                        for (int d = 0; d <= timeDivisions; d++)
                        {
                            int barIdx = (int)Math.Round(d * (count - 1) / (double)timeDivisions);
                            float x = (float)BarIndexToX(barIdx);

                            gr.DrawLine(gridPen, x, chartTop, x, chartBottom);
                            string tStr = barTimes[barIdx].ToString("HH:mm");
                            gr.DrawString(tStr, axisFont, axisTextBrush, x - 18, chartBottom + 8);
                        }
                    }

                    // ── 4. Volume Histogram (Bottom 60px) ─────────────────────────────
                    float volBottom = chartBottom;
                    float volMaxHeight = 60f;
                    double barWidth = Math.Max(2, (chartWidth / (double)count) * 0.72);

                    for (int i = 0; i < count; i++)
                    {
                        float x = (float)(BarIndexToX(i) - barWidth / 2);
                        float h = maxVolume > 0 ? (float)((volumes[i] / maxVolume) * volMaxHeight) : 0;
                        float y = volBottom - h;

                        bool isUp = closes[i] >= opens[i];
                        Color vColor = isUp ? Color.FromArgb(80, 38, 166, 154) : Color.FromArgb(80, 239, 83, 80);
                        using (var vBrush = new SolidBrush(vColor))
                        {
                            gr.FillRectangle(vBrush, x, y, (float)barWidth, h);
                        }
                    }

                    // ── 5. Candlesticks ───────────────────────────────────────────────
                    Color upColor = Color.FromArgb(255, 38, 166, 154);
                    Color downColor = Color.FromArgb(255, 239, 83, 80);

                    using (var upBrush = new SolidBrush(upColor))
                    using (var downBrush = new SolidBrush(downColor))
                    using (var upPen = new Pen(upColor, 1.2f))
                    using (var downPen = new Pen(downColor, 1.2f))
                    {
                        for (int i = 0; i < count; i++)
                        {
                            float cx = (float)BarIndexToX(i);
                            float oY = (float)PriceToY(opens[i]);
                            float cY = (float)PriceToY(closes[i]);
                            float hY = (float)PriceToY(highs[i]);
                            float lY = (float)PriceToY(lows[i]);

                            bool isBull = closes[i] >= opens[i];
                            Pen currentPen = isBull ? upPen : downPen;
                            SolidBrush currentBrush = isBull ? upBrush : downBrush;

                            // Draw Wick
                            gr.DrawLine(currentPen, cx, hY, cx, lY);

                            // Draw Candle Body
                            float bodyTop = Math.Min(oY, cY);
                            float bodyHeight = Math.Max(1.5f, Math.Abs(oY - cY));
                            float bodyLeft = (float)(cx - barWidth / 2);

                            gr.FillRectangle(currentBrush, bodyLeft, bodyTop, (float)barWidth, bodyHeight);
                        }
                    }

                    // ── 6. Power Trades Bubbles ───────────────────────────────────────
                    var snapshot = _ptZoneSnapshot;
                    double maxMetric = 0;
                    bool isCum = (this.SizingMetric == BubbleSizingMetric.CumulativeVolume);

                    for (int i = 0; i < snapshot.Length; i++)
                    {
                        ref readonly var z = ref snapshot[i];
                        if (z.Time < startTime || z.Time > endTime.AddMinutes(5)) continue;
                        double m = isCum ? z.Cumulative : (z.Cumulative * Math.Abs(z.DeltaPercent) / 100.0);
                        if (m > maxMetric) maxMetric = m;
                    }
                    if (alertVolume > maxMetric) maxMetric = alertVolume;
                    if (maxMetric <= 0) maxMetric = 100;

                    double sizeRange = Math.Max(0, this.MaxBubbleSize - this.MinBubbleSize);
                    using (var labelFont = new System.Drawing.Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold))
                    {
                        for (int i = 0; i < snapshot.Length; i++)
                        {
                            ref readonly var zone = ref snapshot[i];
                            if (zone.Time < startTime || zone.Time > endTime.AddMinutes(5)) continue;

                            double metric = isCum ? zone.Cumulative : (zone.Cumulative * Math.Abs(zone.DeltaPercent) / 100.0);
                            double ratio = Math.Max(0, Math.Min(1, metric / maxMetric));
                            double t = Math.Sqrt(ratio);
                            double diameter = this.MinBubbleSize + t * sizeRange;

                            float bx = (float)TimeToX(zone.Time);
                            float by = (float)PriceToY(zone.MidPrice);

                            bool isBuy = zone.DeltaPercent >= 0;
                            Color bColor = isBuy ? this.BuyColor : this.SellColor;
                            int alpha = (int)Math.Round(255 * (this.Opacity / 100.0) * (0.4 + 0.6 * t));
                            alpha = Math.Max(20, Math.Min(255, alpha));

                            float rx = (float)(bx - diameter / 2);
                            float ry = (float)(by - diameter / 2);
                            float d = (float)diameter;

                            using (var fillBrush = new SolidBrush(Color.FromArgb(alpha, bColor)))
                            using (var borderPen = new Pen(Color.FromArgb(Math.Min(255, alpha + 60), bColor), 1.5f))
                            {
                                gr.FillEllipse(fillBrush, rx, ry, d, d);
                                gr.DrawEllipse(borderPen, rx, ry, d, d);
                            }

                            // Bubble Volume Label
                            if (this.ShowLabels && diameter >= this.MinBubbleSize + 4)
                            {
                                string text = (isBuy ? "+" : "-") + FormatVolume(zone.Cumulative);
                                SizeF ts = gr.MeasureString(text, labelFont);
                                gr.DrawString(text, labelFont, GdiBrushes.Black, (float)(bx - ts.Width / 2 + 1), ry - ts.Height - 1);
                                gr.DrawString(text, labelFont, GdiBrushes.White, (float)(bx - ts.Width / 2), ry - ts.Height - 2);
                            }
                        }
                    }

                    // ── 7. Highlight the Triggered Alert Event Bubble with Callout ────
                    if (alertPrice > 0)
                    {
                        float alertX = (float)TimeToX(alertTime > startTime ? alertTime : endTime);
                        float alertY = (float)PriceToY(alertPrice);
                        bool isBuyAlert = alertDirection.IndexOf("Positive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                          alertDirection.IndexOf("BUY", StringComparison.OrdinalIgnoreCase) >= 0;

                        Color alertColor = isBuyAlert ? Color.FromArgb(255, 0, 245, 245) : Color.FromArgb(255, 245, 0, 245);

                        // Pulsing outer halo ring
                        using (var haloPen = new Pen(Color.FromArgb(180, alertColor), 2.5f) { DashStyle = GdiDashStyle.Dot })
                        {
                            gr.DrawEllipse(haloPen, alertX - 28, alertY - 28, 56, 56);
                        }

                        // Callout pin
                        using (var calloutBrush = new SolidBrush(alertColor))
                        using (var calloutFont = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Bold))
                        {
                            string badge = isBuyAlert ? "▲ TRIGGER" : "▼ TRIGGER";
                            gr.FillPolygon(calloutBrush, new PointF[] {
                                new PointF(alertX, alertY - 32),
                                new PointF(alertX - 6, alertY - 44),
                                new PointF(alertX + 6, alertY - 44)
                            });
                        }
                    }

                    // ── 8. Header Bar (Top 65px) ──────────────────────────────────────
                    using (var headerBg = new SolidBrush(Color.FromArgb(240, 23, 27, 40)))
                    using (var headerBorder = new Pen(Color.FromArgb(255, 38, 44, 60), 1.5f))
                    {
                        gr.FillRectangle(headerBg, 0, 0, imgWidth, headerHeight);
                        gr.DrawLine(headerBorder, 0, headerHeight, imgWidth, headerHeight);
                    }

                    // Symbol & Timeframe Text
                    using (var titleFont = new System.Drawing.Font("Segoe UI", 16f, System.Drawing.FontStyle.Bold))
                    using (var subFont = new System.Drawing.Font("Segoe UI", 10f, System.Drawing.FontStyle.Regular))
                    {
                        gr.DrawString(symbol, titleFont, GdiBrushes.White, 20, 10);
                        string subText = $"Power Trades Bubbles • {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC • Close: {closes[count - 1]:F2}";
                        gr.DrawString(subText, subFont, new SolidBrush(Color.FromArgb(255, 140, 148, 165)), 20, 38);
                    }

                    // Prominent Event Badge on Top Right
                    bool isPositive = alertDirection.IndexOf("Positive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      alertDirection.IndexOf("BUY", StringComparison.OrdinalIgnoreCase) >= 0;

                    Color badgeBg = isPositive ? Color.FromArgb(255, 0, 180, 216) : Color.FromArgb(255, 224, 0, 112);
                    string badgeText = isPositive
                        ? $"▲ POSITIVE BUBBLE {alertEventType.ToUpperInvariant()} | Price: {alertPrice:F2} | Vol: {FormatVolume(alertVolume)} | Δ: {alertDelta:+0.0;-0.0;0.0}%"
                        : $"▼ NEGATIVE BUBBLE {alertEventType.ToUpperInvariant()} | Price: {alertPrice:F2} | Vol: {FormatVolume(alertVolume)} | Δ: {alertDelta:+0.0;-0.0;0.0}%";

                    using (var badgeFont = new System.Drawing.Font("Segoe UI", 10.5f, System.Drawing.FontStyle.Bold))
                    {
                        SizeF bSize = gr.MeasureString(badgeText, badgeFont);
                        float bWidth = bSize.Width + 24;
                        float bHeight = 36;
                        float bX = imgWidth - bWidth - 20;
                        float bY = 14;

                        using (var path = new GraphicsPath())
                        {
                            float radius = 8f;
                            path.AddArc(bX, bY, radius, radius, 180, 90);
                            path.AddArc(bX + bWidth - radius, bY, radius, radius, 270, 90);
                            path.AddArc(bX + bWidth - radius, bY + bHeight - radius, radius, radius, 0, 90);
                            path.AddArc(bX, bY + bHeight - radius, radius, radius, 90, 90);
                            path.CloseFigure();

                            using (var bBrush = new SolidBrush(badgeBg))
                            {
                                gr.FillPath(bBrush, path);
                            }
                        }

                        gr.DrawString(badgeText, badgeFont, GdiBrushes.White, bX + 12, bY + 8);
                    }

                    // ── 9. Watermark ──────────────────────────────────────────────────
                    using (var wmFont = new System.Drawing.Font("Segoe UI", 9f, System.Drawing.FontStyle.Italic))
                    using (var wmBrush = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                    {
                        gr.DrawString("Power Trades Bubbles • Quantower Native Engine", wmFont, wmBrush, chartLeft + 8, chartBottom - 18);
                    }

                    bmp.Save(fullPath, ImageFormat.Png);
                }

                Core.Instance.Loggers.Log($"[PTBubbles] Rendered offscreen chart image (1440x840): {fullPath}");
                return fullPath;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[PTBubbles] Direct chart render exception: {ex.Message}");
                return null;
            }
        }

        #endregion Direct Offscreen Chart Generator

        #region Screen Area Capture (Alternative Mode)

        /// <summary>
        /// Captures the exact chart window or screen area to a temporary PNG file.
        /// </summary>
        public string CaptureChartScreenshot(string symbol)
        {
            try
            {
                Rectangle bounds = ResolveChartScreenBounds();
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Core.Instance.Loggers.Log("[PTBubbles] Screenshot bounds are empty or invalid.");
                    return null;
                }

                string fileName = $"PTBubbles_Screen_{symbol}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}.png";
                string fullPath = Path.Combine(Path.GetTempPath(), fileName);

                using (var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                    }

                    bmp.Save(fullPath, ImageFormat.Png);
                }

                Core.Instance.Loggers.Log($"[PTBubbles] Chart screen capture: {bounds.Width}x{bounds.Height} at ({bounds.X},{bounds.Y}) -> {fullPath}");
                return fullPath;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[PTBubbles] Screenshot capture exception: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves the absolute screen coordinate rectangle of the active chart window.
        /// </summary>
        private Rectangle ResolveChartScreenBounds()
        {
            string resolutionMethod = "Unknown";
            Rectangle result = Rectangle.Empty;

            try
            {
                result = ResolveViaWpfDispatcher(out resolutionMethod);
                if (IsValidBounds(result)) return result;

                result = ResolveViaChartReflection(out resolutionMethod);
                if (IsValidBounds(result)) return result;

                result = ResolveViaWin32ProcessWindows(out resolutionMethod);
                if (IsValidBounds(result)) return result;

                return ResolveViaActiveScreen(out resolutionMethod);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[PTBubbles] Error in ResolveChartScreenBounds: {ex.Message}");
                return Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            }
        }

        private static bool IsValidBounds(Rectangle r) => r.Width > 100 && r.Height > 100;

        private Rectangle ResolveViaWpfDispatcher(out string method)
        {
            method = "WPF Visual Tree";
            var app = System.Windows.Application.Current;
            if (app == null) { method = "WPF App Null"; return Rectangle.Empty; }

            Rectangle resolved = Rectangle.Empty;
            Action searchAction = () =>
            {
                try
                {
                    string activeSym = GetActiveSymbolName();
                    object chartObj = this.CurrentChart;
                    object mainWinObj = this.CurrentChart?.MainWindow;

                    foreach (System.Windows.Window win in app.Windows)
                    {
                        if (win == null || !win.IsVisible) continue;
                        var match = FindChartElementRecursive(win, chartObj, mainWinObj, activeSym);
                        if (match != null && match.IsLoaded && match.ActualWidth > 100 && match.ActualHeight > 100)
                        {
                            System.Windows.Point screenPoint = match.PointToScreen(new System.Windows.Point(0, 0));
                            PresentationSource source = PresentationSource.FromVisual(match);
                            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

                            resolved = new Rectangle((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y),
                                (int)Math.Round(match.ActualWidth * dpiX), (int)Math.Round(match.ActualHeight * dpiY));
                            return;
                        }
                    }
                }
                catch { }
            };

            if (app.Dispatcher.CheckAccess()) searchAction();
            else { try { app.Dispatcher.Invoke(searchAction, System.Windows.Threading.DispatcherPriority.Send); } catch { } }

            return resolved;
        }

        private static FrameworkElement FindChartElementRecursive(DependencyObject parent, object chartObj, object mainWinObj, string symbol)
        {
            if (parent == null) return null;
            FrameworkElement bestMatch = null;
            int bestScore = 0;

            void Traverse(DependencyObject current)
            {
                if (current == null) return;
                int count = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(current, i);
                    if (child is FrameworkElement fe && fe.IsLoaded && fe.ActualWidth > 100 && fe.ActualHeight > 100)
                    {
                        int score = 0;
                        if (fe.DataContext != null)
                        {
                            if (fe.DataContext == chartObj || fe.DataContext == mainWinObj) score += 100;
                            string dcStr = fe.DataContext.ToString();
                            if (!string.IsNullOrEmpty(symbol) && dcStr.IndexOf(symbol, StringComparison.OrdinalIgnoreCase) >= 0) score += 40;
                        }
                        string typeName = fe.GetType().Name;
                        if (typeName.Contains("ChartControl") || typeName.Contains("ChartView") || typeName.Contains("ChartPanel")) score += 60;
                        else if (typeName.Contains("Chart")) score += 30;

                        if (score > bestScore) { bestScore = score; bestMatch = fe; }
                    }
                    Traverse(child);
                }
            }

            Traverse(parent);
            return bestMatch;
        }

        private Rectangle ResolveViaChartReflection(out string method)
        {
            method = "Chart Reflection";
            try
            {
                if (this.CurrentChart?.MainWindow != null)
                {
                    Control c = FindWinFormsControlRecursive(this.CurrentChart.MainWindow, 0);
                    if (c != null && !c.IsDisposed && c.IsHandleCreated && c.Width > 100)
                    {
                        Point p = c.PointToScreen(Point.Empty);
                        return new Rectangle(p.X, p.Y, c.Width, c.Height);
                    }
                }
            }
            catch { }
            return Rectangle.Empty;
        }

        private static Control FindWinFormsControlRecursive(object obj, int depth)
        {
            if (obj == null || depth > 3) return null;
            if (obj is Control c) return c;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var f in obj.GetType().GetFields(flags))
            {
                try
                {
                    if (typeof(Control).IsAssignableFrom(f.FieldType) && f.GetValue(obj) is Control ctrl) return ctrl;
                }
                catch { }
            }
            return null;
        }

        private Rectangle ResolveViaWin32ProcessWindows(out string method)
        {
            method = "Win32 Process Windows";
            uint currentProcId = (uint)Process.GetCurrentProcess().Id;
            string activeSymbol = GetActiveSymbolName();
            IntPtr bestHwnd = IntPtr.Zero;
            RECT bestRect = default;
            int bestScore = 0;

            EnumWindowsProc callback = (hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint procId);
                if (procId != currentProcId) return true;

                var sbTitle = new StringBuilder(256);
                GetWindowText(hWnd, sbTitle, 256);
                string title = sbTitle.ToString();

                if (GetWindowRect(hWnd, out RECT wr) && wr.Width > 150 && wr.Height > 150)
                {
                    int score = 0;
                    if (!string.IsNullOrEmpty(activeSymbol) && title.IndexOf(activeSymbol, StringComparison.OrdinalIgnoreCase) >= 0) score += 50;
                    if (title.IndexOf("Chart", StringComparison.OrdinalIgnoreCase) >= 0) score += 30;

                    if (score > bestScore) { bestScore = score; bestHwnd = hWnd; bestRect = wr; }
                }
                return true;
            };

            try { EnumChildWindows(IntPtr.Zero, callback, IntPtr.Zero); } catch { }

            if (bestScore > 0 && bestRect.Width > 100)
            {
                return new Rectangle(bestRect.Left, bestRect.Top, bestRect.Width, bestRect.Height);
            }
            return Rectangle.Empty;
        }

        private Rectangle ResolveViaActiveScreen(out string method)
        {
            method = "Monitor Bounds";
            Point cursorPos = Cursor.Position;
            Screen screen = Screen.FromPoint(cursorPos) ?? Screen.PrimaryScreen;
            return screen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        }

        #endregion Screen Area Capture

        /// <summary>
        /// Sends a photo with caption to Telegram Bot API using multipart/form-data.
        /// </summary>
        private async Task PostTelegramPhotoAsync(string imagePath, string caption)
        {
            string botToken = this.TelegramBotToken.Trim();
            string chatId = this.TelegramChatId.Trim();
            string url = $"https://api.telegram.org/bot{botToken}/sendPhoto";

            using (var form = new MultipartFormDataContent())
            using (var fileStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");

                form.Add(new StringContent(chatId), "chat_id");
                form.Add(new StringContent(caption), "caption");
                form.Add(fileContent, "photo", Path.GetFileName(imagePath));

                var response = await _httpClient.PostAsync(url, form);
                if (response.IsSuccessStatusCode)
                {
                    Core.Instance.Loggers.Log("[PTBubbles] Telegram screenshot delivered successfully.");
                }
                else
                {
                    string respBody = await response.Content.ReadAsStringAsync();
                    Core.Instance.Loggers.Log($"[PTBubbles] Telegram sendPhoto HTTP {response.StatusCode}: {respBody}");
                }
            }
        }
    }
}
