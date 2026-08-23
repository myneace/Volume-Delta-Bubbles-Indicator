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
        // ── Audio helpers ─────────────────────────────────────────────────────

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
        /// NAudio contains its own pure-managed MP3 decoder — it does NOT call
        /// into Windows MCI, DirectShow, or any registered codec driver, so it
        /// works reliably even inside host processes (like Quantower) where the
        /// Windows MCI subsystem cannot load its MP3 driver (error 266).
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
                try
                {
                    var audioFile    = new AudioFileReader(filePath);   // handles MP3 + WAV
                    var outputDevice = new WaveOutEvent();              // uses default audio device

                    outputDevice.PlaybackStopped += (s, a) =>
                    {
                        try
                        {
                            outputDevice.Dispose();
                            audioFile.Dispose();
                        }
                        catch { }
                    };

                    outputDevice.Init(audioFile);
                    outputDevice.Play();
                }
                catch (Exception ex)
                {
                    Core.Instance.Loggers.Log(
                        $"[VolΔBubbles] NAudio exception: {ex.Message}");
                }
            });
        }
    }
}
