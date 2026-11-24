using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.Wave;
using TradingPlatform.BusinessLayer;

namespace VolumeDeltaBubbles
{
    public partial class HighVolumeDeltaBubbles
    {
        // ── Quantower Native Alerts ──────────────────────────────────────────

        /// <summary>
        /// Emits a native Quantower alert event when a positive bubble is created
        /// or a negative bubble is triggered in real-time.
        /// </summary>
        private void TriggerBubbleAlert(DateTime time, double midPrice, bool isBuyDominant, double cumulativeVolume, double deltaPercent)
        {
            try
            {
                string directionStr = isBuyDominant ? "Positive" : "Negative";
                string eventTypeStr = isBuyDominant ? "Created" : "Triggered";
                string symName = GetActiveSymbolName();
                string title = $"{this.Name}: {directionStr} Bubble {eventTypeStr}";
                
                string message = $"[{this.Name}] {directionStr} bubble {eventTypeStr.ToLowerInvariant()} | " +
                                 $"Symbol: {symName} | Price: {midPrice:F2} | " +
                                 $"Vol: {FormatVolume(cumulativeVolume)} | " +
                                 $"Δ: {deltaPercent:+0.0;-0.0;0.0}% | Time: {time:HH:mm:ss}";

                Core.Instance.Loggers.Log($"[VolΔBubbles] Native Alert — {title} — {message}");

                Core.Instance.Alert(message, title, symName);
            }
            catch (Exception ex)
            {
                Core.Instance.Loggers.Log($"[VolΔBubbles] Error triggering native alert: {ex.Message}");
            }
        }

        // Lock and tracking references for currently playing audio to ensure
        // only one sound plays at a time (cutting off any previous sound).
        private static readonly object _soundLock = new object();
        private static WaveOutEvent _currentOutputDevice;
        private static AudioFileReader _currentAudioFile;

        /// <summary>
        /// Searches for <paramref name="filename"/> inside a "sound" subfolder
        /// by walking up from several anchor directories (AppDomain base,
        /// Quantower exe location) until the file is found.
        /// Returns the full absolute path, or empty string if not found.
        /// </summary>
        private static string ResolveSoundPath(string filename)
        {
            var candidates = new List<string>();

            // Candidate A: next to the DLL (only works if Assembly.Location is set).
            string loc = typeof(HighVolumeDeltaBubbles).Assembly.Location;
            if (!string.IsNullOrEmpty(loc))
                candidates.Add(Path.Combine(Path.GetDirectoryName(loc)!, "sound", filename));

            // Candidates B: walk up from AppDomain.BaseDirectory.
            string cur = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            for (int i = 0; i <= 6 && !string.IsNullOrEmpty(cur); i++)
            {
                candidates.Add(Path.Combine(cur, "Settings", "Scripts", "Indicators", "Indicator2", "sound", filename));
                string parent = Path.GetDirectoryName(cur);
                if (parent == null || parent == cur) break;
                cur = parent;
            }

            // Candidates C: walk up from the host exe (Quantower.exe).
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                string up = Path.GetDirectoryName(exePath) ?? string.Empty;
                for (int i = 0; i <= 6 && !string.IsNullOrEmpty(up); i++)
                {
                    candidates.Add(Path.Combine(up, "Settings", "Scripts", "Indicators", "Indicator2", "sound", filename));
                    string parent = Path.GetDirectoryName(up);
                    if (parent == null || parent == up) break;
                    up = parent;
                }
            }
            catch { }

            foreach (string c in candidates)
            {
                try
                {
                    if (File.Exists(c))
                    {
                        Core.Instance.Loggers.Log($"[Vol\u0394Bubbles] Found sound: '{c}'");
                        return c;
                    }
                }
                catch { }
            }

            // Log all tried paths so the user can see what went wrong.
            Core.Instance.Loggers.Log(
                $"[Vol\u0394Bubbles] Sound file '{filename}' not found. Tried:\n" +
                string.Join("\n", candidates));
            return string.Empty;
        }

        /// <summary>
        /// Plays an MP3 or WAV file asynchronously using NAudio.
        /// If a previous sound is currently playing (e.g. A buy alert followed
        /// immediately by a sell alert), it cuts off the previous sound immediately
        /// so the new sound plays cleanly without overlap.
        /// </summary>
        private static void PlaySoundAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                Core.Instance.Loggers.Log(
                    $"[VolΔBubbles] Sound SKIPPED — file not found: '{filePath}'");
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_soundLock)
                {
                    try
                    {
                        // ── Cut off and dispose any currently active sound ───
                        if (_currentOutputDevice != null)
                        {
                            try
                            {
                                _currentOutputDevice.Stop();
                                _currentOutputDevice.Dispose();
                            }
                            catch { }
                            _currentOutputDevice = null;
                        }

                        if (_currentAudioFile != null)
                        {
                            try { _currentAudioFile.Dispose(); }
                            catch { }
                            _currentAudioFile = null;
                        }

                        // ── Start playback for the new sound ─────────────────
                        var audioFile    = new AudioFileReader(filePath);   // handles MP3 + WAV
                        var outputDevice = new WaveOutEvent();              // uses default audio device

                        _currentAudioFile    = audioFile;
                        _currentOutputDevice = outputDevice;

                        outputDevice.PlaybackStopped += (s, a) =>
                        {
                            lock (_soundLock)
                            {
                                try
                                {
                                    // Only clean up if this device is still the active one
                                    if (_currentOutputDevice == outputDevice)
                                    {
                                        _currentOutputDevice.Dispose();
                                        _currentOutputDevice = null;
                                        _currentAudioFile?.Dispose();
                                        _currentAudioFile = null;
                                    }
                                }
                                catch { }
                            }
                        };

                        outputDevice.Init(audioFile);
                        outputDevice.Play();
                    }
                    catch (Exception ex)
                    {
                        Core.Instance.Loggers.Log(
                            $"[VolΔBubbles] NAudio exception: {ex.Message}");
                    }
                }
            });
        }
    }
}
