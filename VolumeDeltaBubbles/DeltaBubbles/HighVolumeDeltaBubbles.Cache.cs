using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    // ── Cache DTOs ────────────────────────────────────────────────────────────

    public class CachedPtZone
    {
        public DateTime Time { get; set; }
        public double MidPrice { get; set; }
        public double DeltaPercent { get; set; }
        public double Cumulative { get; set; }
    }

    public class ZoneCacheFile
    {
        public int SchemaVersion { get; set; } = 1;
        public string Symbol { get; set; } = string.Empty;
        public string FilterFingerprint { get; set; } = string.Empty;
        public DateTime LastSavedUtc { get; set; } = DateTime.UtcNow;
        public List<CachedPtZone> Zones { get; set; } = new List<CachedPtZone>();
    }

    // ── PtZoneCache ───────────────────────────────────────────────────────────

    internal class PtZoneCache
    {
        // Unique key for deduplication: (Time.Ticks, quantized mid-price)
        // Quantizing mid-price to 5 decimal places avoids floating-point rounding quirks
        // across serialization cycles while remaining far below any real instrument tick size.
        private readonly Dictionary<(long Ticks, long PriceQuantized), HighVolumeDeltaBubbles.PtZoneData> _zonesByKey
            = new Dictionary<(long, long), HighVolumeDeltaBubbles.PtZoneData>();

        private bool _isDirty;
        private DateTime? _latestTime;

        internal int Count => _zonesByKey.Count;
        internal DateTime? LatestTime => _latestTime;
        internal bool IsDirty => _isDirty;

        private static long QuantizePrice(double price) => (long)Math.Round(price * 100000.0);

        internal bool Load(string filePath, string expectedFingerprint, int maxDays)
        {
            try
            {
                if (!File.Exists(filePath))
                    return false;

                string json = File.ReadAllText(filePath);
                var file = JsonSerializer.Deserialize<ZoneCacheFile>(json);
                if (file == null || file.SchemaVersion != 1 || file.FilterFingerprint != expectedFingerprint)
                    return false;

                DateTime cutoff = maxDays > 0 ? DateTime.UtcNow.AddDays(-maxDays) : DateTime.MinValue;

                _zonesByKey.Clear();
                _latestTime = null;

                foreach (var z in file.Zones)
                {
                    if (z.Time < cutoff)
                        continue;

                    var zoneData = new HighVolumeDeltaBubbles.PtZoneData
                    {
                        Time         = z.Time,
                        MidPrice     = z.MidPrice,
                        DeltaPercent = z.DeltaPercent,
                        Cumulative   = z.Cumulative,
                    };

                    var key = (z.Time.Ticks, QuantizePrice(z.MidPrice));
                    _zonesByKey[key] = zoneData;

                    if (!_latestTime.HasValue || z.Time > _latestTime.Value)
                        _latestTime = z.Time;
                }

                _isDirty = false;
                return true;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error loading zone cache from {filePath}: {ex.Message}");
                return false;
            }
        }

        internal bool Save(string filePath, string symbol, string filterFingerprint)
        {
            if (!_isDirty)
                return false;

            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var file = new ZoneCacheFile
                {
                    SchemaVersion     = 1,
                    Symbol            = symbol,
                    FilterFingerprint = filterFingerprint,
                    LastSavedUtc      = DateTime.UtcNow,
                    Zones             = _zonesByKey.Values
                                            .OrderBy(z => z.Time)
                                            .Select(z => new CachedPtZone
                                            {
                                                Time         = z.Time,
                                                MidPrice     = z.MidPrice,
                                                DeltaPercent = z.DeltaPercent,
                                                Cumulative   = z.Cumulative,
                                            }).ToList()
                };

                string json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = false });
                
                // Write atomically via temporary file
                string tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);

                _isDirty = false;
                Core.Instance.Loggers.Log($"[VolΔBubbles] Saved {_zonesByKey.Count} zones to cache: {filePath}");
                return true;
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error saving zone cache to {filePath}: {ex.Message}");
                return false;
            }
        }

        internal bool Add(HighVolumeDeltaBubbles.PtZoneData zone)
        {
            var key = (zone.Time.Ticks, QuantizePrice(zone.MidPrice));
            if (!_zonesByKey.ContainsKey(key))
            {
                _zonesByKey[key] = zone;
                if (!_latestTime.HasValue || zone.Time > _latestTime.Value)
                    _latestTime = zone.Time;
                _isDirty = true;
                return true;
            }
            return false;
        }

        internal bool Merge(IEnumerable<HighVolumeDeltaBubbles.PtZoneData> incomingZones)
        {
            bool anyAdded = false;
            foreach (var zone in incomingZones)
            {
                if (Add(zone))
                    anyAdded = true;
            }
            return anyAdded;
        }

        internal HighVolumeDeltaBubbles.PtZoneData[] GetSnapshot()
        {
            return _zonesByKey.Values.OrderBy(z => z.Time).ToArray();
        }

        internal void Clear(string filePath)
        {
            _zonesByKey.Clear();
            _latestTime = null;
            _isDirty = false;

            try
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch { }
        }
    }

    // ── HighVolumeDeltaBubbles Cache Helpers ───────────────────────────────────

    public partial class HighVolumeDeltaBubbles
    {
        private PtZoneCache _zoneCache;

        private static string CacheDirectory
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Quantower", "VolumeDeltaBubbles", "ZoneCache");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private string GetFilterFingerprint()
        {
            // Fingerprint includes all parameters that affect Volume Delta zone formation
            string raw = $"{this.TotalVolume:F2}_{this.MinTradeVolume:F2}_{this.MaxTradeVolume:F2}_" +
                         $"{this.TimeInterval}_{this.BasisVolumeInterval}_" +
                         $"{this.MinZoneHeight}_{this.MaxZoneHeight}_" +
                         $"{this.DeltaFilter:F2}_{this.BasisRatioFilter:F2}";

            using (var md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(raw));
                return BitConverter.ToString(hash).Replace("-", "").Substring(0, 12);
            }
        }

        private string GetZoneCacheFilePath()
        {
            string symbolKey = GetSanitizedSymbolKey();
            string filterFp = GetFilterFingerprint();
            return Path.Combine(CacheDirectory, $"{symbolKey}_{filterFp}.json");
        }

        public void ClearCurrentZoneCache(bool showUiFeedback = true)
        {
            try
            {
                string path = GetZoneCacheFilePath();
                string sym = GetActiveSymbolName();

                _zoneCache?.Clear(path);
                _ptZoneSnapshot = Array.Empty<PtZoneData>();
                _alertedBubbles.Clear();

                Core.Instance.Loggers.Log($"[VolΔBubbles] Zone cache cleared for '{sym}' ({path}).");

                // Re-subscribe to force fresh data acquisition from broker
                ReSubscribePowerTrades();

                if (showUiFeedback)
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"Zone cache for '{sym}' has been cleared.\n\nRe-fetching fresh history from broker.",
                        "Volume Delta Bubbles",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error clearing zone cache: {ex.Message}");
            }
        }
    }
}
