using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Drawing;
using TradingPlatform.BusinessLayer;
using TradingPlatform.BusinessLayer.PowerTrades;
using TradingPlatform.BusinessLayer.History.Aggregations;

namespace VolumeDeltaBubbles
{
    public class SymbolSettingsProfile
    {
        public string Symbol { get; set; } = string.Empty;
        public DateTime LastSavedUtc { get; set; } = DateTime.UtcNow;

        // Volume Delta Filter Parameters
        public int LookbackMinutes { get; set; } = 30;
        public double TotalVolume { get; set; } = 100;
        public double MinTradeVolume { get; set; } = 0;
        public double MaxTradeVolume { get; set; } = 1000000;
        public int TimeInterval { get; set; } = 5;
        public int BasisVolumeInterval { get; set; } = 300;
        public int MinZoneHeight { get; set; } = 0;
        public int MaxZoneHeight { get; set; } = 100000;
        public double DeltaFilter { get; set; } = 50;
        public double BasisRatioFilter { get; set; } = 0;

        // Visual & Sizing Controls
        public BubbleSizingMetric SizingMetric { get; set; } = BubbleSizingMetric.CumulativeVolume;
        public BubbleScalingMethod ScalingMethod { get; set; } = BubbleScalingMethod.LinearDiameter;
        public int MinBubbleSize { get; set; } = 8;
        public int MaxBubbleSize { get; set; } = 50;
        public int Opacity { get; set; } = 75;
        public int BuyColorArgb { get; set; } = Color.FromArgb(255, 0, 245, 245).ToArgb();
        public int SellColorArgb { get; set; } = Color.FromArgb(255, 245, 0, 245).ToArgb();
        public bool ShowLabels { get; set; } = false;
        public bool EnableAlerts { get; set; } = false;
        public bool EnableBubbleAlerts { get; set; } = false;
        public bool EnableTelegramScreenshot { get; set; } = false;
        public TelegramScreenshotMode ScreenshotMode { get; set; } = TelegramScreenshotMode.DirectChartRenderer;
        public string TelegramBotToken { get; set; } = string.Empty;
        public string TelegramChatId { get; set; } = string.Empty;
        public int TelegramCooldownSeconds { get; set; } = 5;

        // Data Persistence & Indexing
        public bool EnablePersistence { get; set; } = true;
        public int MaxCachedDays { get; set; } = 30;
    }

    public partial class HighVolumeDeltaBubbles
    {
        private static string _lastLoadedSymbol;

        private static string StorageFolder
        {
            get
            {
                string asmDir = Path.GetDirectoryName(typeof(HighVolumeDeltaBubbles).Assembly.Location) ?? string.Empty;
                string dir = Path.Combine(asmDir, "SymbolSettings");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private string GetActiveSymbolName()
        {
            if (this.Symbol != null)
            {
                if (!string.IsNullOrWhiteSpace(this.Symbol.Name))
                    return this.Symbol.Name;
                if (!string.IsNullOrWhiteSpace(this.Symbol.Id))
                    return this.Symbol.Id;
                if (!string.IsNullOrWhiteSpace(this.Symbol.Description))
                    return this.Symbol.Description;
                if (!string.IsNullOrWhiteSpace(this.Symbol.Root))
                    return this.Symbol.Root;

                string symStr = this.Symbol.ToString();
                if (!string.IsNullOrWhiteSpace(symStr))
                    return symStr;
            }

            if (_powerTradesData?.Symbol != null)
            {
                if (!string.IsNullOrWhiteSpace(_powerTradesData.Symbol.Name))
                    return _powerTradesData.Symbol.Name;
                if (!string.IsNullOrWhiteSpace(_powerTradesData.Symbol.Id))
                    return _powerTradesData.Symbol.Id;

                string ptStr = _powerTradesData.Symbol.ToString();
                if (!string.IsNullOrWhiteSpace(ptStr))
                    return ptStr;
            }

            if (this.HistoricalData?.Symbol != null)
            {
                if (!string.IsNullOrWhiteSpace(this.HistoricalData.Symbol.Name))
                    return this.HistoricalData.Symbol.Name;
                if (!string.IsNullOrWhiteSpace(this.HistoricalData.Symbol.Id))
                    return this.HistoricalData.Symbol.Id;

                string hdStr = this.HistoricalData.Symbol.ToString();
                if (!string.IsNullOrWhiteSpace(hdStr))
                    return hdStr;
            }

            return "ActiveSymbol";
        }

        private string GetSanitizedSymbolKey()
        {
            string sym = GetActiveSymbolName();
            string key = string.Concat(sym.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
            key = System.Text.RegularExpressions.Regex.Replace(key, @"_+", "_").Trim('_');
            return string.IsNullOrWhiteSpace(key) ? "ActiveSymbol" : key;
        }

        private string GetSettingsFilePath()
        {
            return Path.Combine(StorageFolder, $"{GetSanitizedSymbolKey()}.json");
        }

        public bool SaveSymbolSettings(bool showUiFeedback = true)
        {
            try
            {
                string activeSymbol = GetActiveSymbolName();
                _lastLoadedSymbol = activeSymbol;

                var profile = new SymbolSettingsProfile
                {
                    Symbol               = activeSymbol,
                    LastSavedUtc         = DateTime.UtcNow,
                    LookbackMinutes      = this.LookbackMinutes,
                    TotalVolume          = this.TotalVolume,
                    MinTradeVolume       = this.MinTradeVolume,
                    MaxTradeVolume       = this.MaxTradeVolume,
                    TimeInterval         = this.TimeInterval,
                    BasisVolumeInterval  = this.BasisVolumeInterval,
                    MinZoneHeight        = this.MinZoneHeight,
                    MaxZoneHeight        = this.MaxZoneHeight,
                    DeltaFilter          = this.DeltaFilter,
                    BasisRatioFilter     = this.BasisRatioFilter,
                    SizingMetric         = this.SizingMetric,
                    ScalingMethod        = this.ScalingMethod,
                    MinBubbleSize        = this.MinBubbleSize,
                    MaxBubbleSize        = this.MaxBubbleSize,
                    Opacity              = this.Opacity,
                    BuyColorArgb         = this.BuyColor.ToArgb(),
                    SellColorArgb        = this.SellColor.ToArgb(),
                    ShowLabels               = this.ShowLabels,
                    EnableAlerts             = this.EnableAlerts,
                    EnableBubbleAlerts       = this.EnableBubbleAlerts,
                    EnableTelegramScreenshot = this.EnableTelegramScreenshot,
                    ScreenshotMode           = this.ScreenshotMode,
                    TelegramBotToken         = this.TelegramBotToken,
                    TelegramChatId           = this.TelegramChatId,
                    TelegramCooldownSeconds  = this.TelegramCooldownSeconds,
                    EnablePersistence        = this.EnablePersistence,
                    MaxCachedDays            = this.MaxCachedDays,
                };

                string path = GetSettingsFilePath();
                string json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);

                Core.Instance.Loggers.Log($"[VolΔBubbles] Saved custom symbol parameters for '{activeSymbol}' to {path}");

                if (showUiFeedback)
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"Custom parameters for symbol '{activeSymbol}' saved successfully!\n\nFile:\n{path}",
                        "Volume Delta Bubbles",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
                return true;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error saving symbol parameters: {ex.Message}");
                if (showUiFeedback)
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"Error saving parameters: {ex.Message}",
                        "Volume Delta Bubbles",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                }
                return false;
            }
        }

        public bool LoadSymbolSettings(bool showUiFeedback = false)
        {
            try
            {
                string activeSymbol = GetActiveSymbolName();

                // If auto-loading (no UI feedback) and we already loaded settings for this symbol, skip to avoid overwriting user edits
                if (!showUiFeedback && _lastLoadedSymbol == activeSymbol)
                {
                    return false;
                }

                _lastLoadedSymbol = activeSymbol;
                string path = GetSettingsFilePath();

                if (!File.Exists(path))
                {
                    Core.Instance.Loggers.Log($"[VolΔBubbles] No saved parameters found for symbol '{activeSymbol}' at {path}");
                    if (showUiFeedback)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            $"No saved parameter profile found for symbol '{activeSymbol}'.\n\nConfigure your parameters and click 'Save Symbol Parameters' to create a profile.",
                            "Volume Delta Bubbles",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Information);
                    }
                    return false;
                }

                string json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<SymbolSettingsProfile>(json);
                if (profile == null)
                    return false;

                bool ptFiltersChanged =
                    this.LookbackMinutes != profile.LookbackMinutes ||
                    Math.Abs(this.TotalVolume - profile.TotalVolume) > 0.0001 ||
                    Math.Abs(this.MinTradeVolume - profile.MinTradeVolume) > 0.0001 ||
                    Math.Abs(this.MaxTradeVolume - profile.MaxTradeVolume) > 0.0001 ||
                    this.TimeInterval != profile.TimeInterval ||
                    this.BasisVolumeInterval != profile.BasisVolumeInterval ||
                    this.MinZoneHeight != profile.MinZoneHeight ||
                    this.MaxZoneHeight != profile.MaxZoneHeight ||
                    Math.Abs(this.DeltaFilter - profile.DeltaFilter) > 0.0001 ||
                    Math.Abs(this.BasisRatioFilter - profile.BasisRatioFilter) > 0.0001;
                this.LookbackMinutes     = profile.LookbackMinutes;
                this.TotalVolume         = profile.TotalVolume;
                this.MinTradeVolume      = profile.MinTradeVolume;
                this.MaxTradeVolume      = profile.MaxTradeVolume;
                this.TimeInterval        = profile.TimeInterval;
                this.BasisVolumeInterval = profile.BasisVolumeInterval;
                this.MinZoneHeight       = profile.MinZoneHeight;
                this.MaxZoneHeight       = profile.MaxZoneHeight;
                this.DeltaFilter         = profile.DeltaFilter;
                this.BasisRatioFilter    = profile.BasisRatioFilter;
                this.SizingMetric        = profile.SizingMetric;
                this.ScalingMethod       = profile.ScalingMethod;
                this.MinBubbleSize       = profile.MinBubbleSize;
                this.MaxBubbleSize       = profile.MaxBubbleSize;
                this.Opacity             = profile.Opacity;
                if (profile.BuyColorArgb != 0)
                    this.BuyColor        = Color.FromArgb(profile.BuyColorArgb);
                if (profile.SellColorArgb != 0)
                    this.SellColor       = Color.FromArgb(profile.SellColorArgb);
                this.ShowLabels               = profile.ShowLabels;
                this.EnableAlerts             = profile.EnableAlerts;
                this.EnableBubbleAlerts       = profile.EnableBubbleAlerts;
                this.EnableTelegramScreenshot = profile.EnableTelegramScreenshot;
                this.ScreenshotMode           = profile.ScreenshotMode;
                if (!string.IsNullOrEmpty(profile.TelegramBotToken))
                    this.TelegramBotToken     = profile.TelegramBotToken;
                if (!string.IsNullOrEmpty(profile.TelegramChatId))
                    this.TelegramChatId       = profile.TelegramChatId;
                if (profile.TelegramCooldownSeconds >= 0)
                    this.TelegramCooldownSeconds = profile.TelegramCooldownSeconds;
                this.EnablePersistence = profile.EnablePersistence;
                if (profile.MaxCachedDays > 0)
                    this.MaxCachedDays = profile.MaxCachedDays;

                // Re-subscribe to Volume Delta data feed if aggregate filter settings changed
                if (ptFiltersChanged && _powerTradesData != null)
                {
                    ReSubscribePowerTrades();
                }

                this.CurrentChart?.RedrawBuffer();
                Core.Instance.Loggers.Log($"[VolΔBubbles] Loaded custom symbol parameters for '{activeSymbol}' from disk.");

                if (showUiFeedback)
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"Custom parameters for symbol '{activeSymbol}' loaded successfully!",
                        "Volume Delta Bubbles",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
                return true;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error loading symbol parameters: {ex.Message}");
                if (showUiFeedback)
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"Error loading parameters: {ex.Message}",
                        "Volume Delta Bubbles",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                }
                return false;
            }
        }

        public bool DeleteSymbolSettings(bool showUiFeedback = true)
        {
            try
            {
                _lastLoadedSymbol = null;
                string activeSymbol = GetActiveSymbolName();
                string path = GetSettingsFilePath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Core.Instance.Loggers.Log($"[VolΔBubbles] Deleted saved symbol parameters for '{activeSymbol}'.");
                    if (showUiFeedback)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            $"Saved parameter profile for symbol '{activeSymbol}' has been deleted.",
                            "Volume Delta Bubbles",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Information);
                    }
                    return true;
                }
                else
                {
                    if (showUiFeedback)
                    {
                        System.Windows.Forms.MessageBox.Show(
                            $"No saved parameter profile exists for symbol '{activeSymbol}'.",
                            "Volume Delta Bubbles",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Information);
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error deleting symbol parameters: {ex.Message}");
                if (showUiFeedback)
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"Error deleting parameters: {ex.Message}",
                        "Volume Delta Bubbles",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                }
                return false;
            }
        }

        private void ReSubscribePowerTrades()
        {
            try
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

                // Switch zone cache to the new symbol/filter fingerprint
                _zoneCache = new PtZoneCache();
                if (this.EnablePersistence)
                {
                    string cachePath = GetZoneCacheFilePath();
                    string filterFp = GetFilterFingerprint();
                    if (_zoneCache.Load(cachePath, filterFp, this.MaxCachedDays) && _zoneCache.Count > 0)
                    {
                        _ptZoneSnapshot = _zoneCache.GetSnapshot();
                        _alertedBubbles.Clear();
                        foreach (var z in _ptZoneSnapshot)
                            _alertedBubbles.Add((z.Time.Ticks, z.MidPrice, z.DeltaPercent >= 0));
                    }
                    else
                    {
                        _ptZoneSnapshot = Array.Empty<PtZoneData>();
                        _alertedBubbles.Clear();
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
                        try
                        {
                            fromTime = this.HistoricalData[0, SeekOriginHistory.Begin].TimeLeft;
                        }
                        catch { }
                    }
                }

                DateTime requestFromTime = fromTime;
                if (this.EnablePersistence && _zoneCache != null && _zoneCache.LatestTime.HasValue)
                {
                    DateTime overlapStart = _zoneCache.LatestTime.Value.AddMinutes(-15);
                    if (overlapStart > fromTime)
                        requestFromTime = overlapStart;
                }

                _powerTradesData = this.Symbol.GetHistory(new HistoryRequestParameters
                {
                    Symbol      = this.Symbol,
                    Aggregation = new HistoryAggregationPowerTrades(ptParams),
                    FromTime    = requestFromTime,
                    ToTime      = default,
                });

                _powerTradesData.NewHistoryItem += OnPowerTradesZoneFormed;

                // Rebuild the in-memory paint snapshot and seed alert keys from
                // the newly loaded history buffer.
                RebuildFromBuffer();

                Core.Instance.Loggers.Log($"[VolΔBubbles] Re-subscribed Volume Delta feed with updated filters.");

            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error re-subscribing Volume Delta feed: {ex.Message}");
            }
        }
    }
}
