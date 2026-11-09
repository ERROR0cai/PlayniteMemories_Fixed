using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SharpMemories
{
    // File events only enqueue complete-format candidates; all disk IO runs off the watcher callback.
    public sealed class FolderMonitorManager
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly SharpMemoriesSettingsViewModel settings;
        private readonly object gate = new object();
        private FileSystemWatcher watcher;
        private CancellationTokenSource cancellation;
        private Task worker;
        private string currentGameTitle;
        private string monitoredFolder;
        private DateTime startedUtc;
        private readonly ConcurrentDictionary<string, Candidate> pending =
            new ConcurrentDictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> completed =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private sealed class Candidate
        {
            public long Size = -1;
            public DateTime Modified;
            public int StableChecks;
            public DateTime RetryAfter;
            public int Failures;
        }

        public FolderMonitorManager(SharpMemoriesSettingsViewModel settings)
        {
            this.settings = settings;
        }

        private static bool IsScreenshot(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        public void EnsureMonitoring(string gameTitle)
        {
            string path = settings?.Settings?.MonitorFolder;
            if (string.IsNullOrWhiteSpace(path)) { StopMonitoring(); return; }
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { logger.Error(ex, "Invalid monitor folder"); return; }
            lock (gate)
            {
                if (watcher != null && string.Equals(monitoredFolder, full, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(currentGameTitle, gameTitle, StringComparison.Ordinal)) return;
            }
            StartMonitoring(gameTitle);
        }

        public void StartMonitoring(string gameTitle)
        {
            StopMonitoring();
            var path = settings?.Settings?.MonitorFolder;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                path = Path.GetFullPath(path);
                if (!Directory.Exists(path)) { logger.Warn($"Monitor folder missing: {path}"); return; }
                lock (gate)
                {
                    currentGameTitle = gameTitle;
                    monitoredFolder = path;
                    startedUtc = DateTime.UtcNow;
                    pending.Clear();
                    completed.Clear();
                    cancellation = new CancellationTokenSource();
                    watcher = new FileSystemWatcher(path)
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                        Filter = "*.*",
                        IncludeSubdirectories = false,
                        InternalBufferSize = 32768
                    };
                    watcher.Created += OnCreated;
                    watcher.Renamed += OnRenamed;
                    watcher.Error += OnError;
                    watcher.EnableRaisingEvents = true;
                    var token = cancellation.Token;
                    worker = Task.Run(() => WorkerLoop(path, gameTitle, startedUtc, token));
                }
                logger.Info($"Screenshot monitor started: {path}");
            }
            catch (Exception ex) { logger.Error(ex, "Failed to start screenshot monitor"); StopMonitoring(); }
        }

        public void StopMonitoring()
        {
            FileSystemWatcher oldWatcher;
            CancellationTokenSource oldToken;
            Task oldWorker;
            lock (gate)
            {
                oldWatcher = watcher;
                watcher = null;
                oldToken = cancellation;
                cancellation = null;
                oldWorker = worker;
                worker = null;
                currentGameTitle = null;
                monitoredFolder = null;
                if (oldWatcher != null) oldWatcher.EnableRaisingEvents = false;
                if (oldToken != null) oldToken.Cancel();
            }
            if (oldWatcher != null)
            {
                oldWatcher.Created -= OnCreated;
                oldWatcher.Renamed -= OnRenamed;
                oldWatcher.Error -= OnError;
                oldWatcher.Dispose();
            }
            if (oldToken != null)
            {
                if (oldWorker != null)
                    oldWorker.ContinueWith(_ => oldToken.Dispose(), TaskScheduler.Default);
                else oldToken.Dispose();
            }
        }

        private void OnCreated(object sender, FileSystemEventArgs e) { Enqueue(e.FullPath); }
        private void OnRenamed(object sender, RenamedEventArgs e) { Enqueue(e.FullPath); }
        private void OnError(object sender, ErrorEventArgs e)
        {
            logger.Error(e.GetException(), "Folder monitor watcher error (polling remains active)");
        }

        private void Enqueue(string path)
        {
            if (!IsScreenshot(path) || completed.ContainsKey(path)) return;
            pending.TryAdd(path, new Candidate());
        }

        private async Task WorkerLoop(string folder, string gameTitle, DateTime start, CancellationToken token)
        {
            try
            {
                DateTime nextScan = DateTime.MinValue;
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(700, token);
                    if (DateTime.UtcNow >= nextScan)
                    {
                        nextScan = DateTime.UtcNow.AddSeconds(15);
                        try
                        {
                            foreach (string file in Directory.EnumerateFiles(folder))
                            {
                                if (token.IsCancellationRequested) break;
                                if (!IsScreenshot(file) || completed.ContainsKey(file)) continue;
                                // Do not automatically move screenshots that were already present
                                // before monitoring began. Newly renamed files will be enqueued.
                                if (File.GetLastWriteTimeUtc(file) >= start.AddSeconds(-1)) Enqueue(file);
                            }
                        }
                        catch (Exception ex) { logger.Warn(ex, "Monitor fallback scan failed"); }
                    }
                    foreach (var entry in pending.ToArray())
                    {
                        if (token.IsCancellationRequested) break;
                        var file = entry.Key;
                        var state = entry.Value;
                        if (DateTime.UtcNow < state.RetryAfter) continue;
                        if (!File.Exists(file)) { Candidate removed; pending.TryRemove(file, out removed); continue; }
                        try
                        {
                            var info = new FileInfo(file);
                            info.Refresh();
                            if (info.Length <= 0) continue;
                            if (state.Size != info.Length || state.Modified != info.LastWriteTimeUtc)
                            {
                                state.Size = info.Length;
                                state.Modified = info.LastWriteTimeUtc;
                                state.StableChecks = 0;
                                continue;
                            }
                            if (++state.StableChecks < 2) continue;
                            if (token.IsCancellationRequested) break;
                            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                            string output = settings?.Settings?.OutputFolder;
                            if (string.IsNullOrWhiteSpace(output))
                                output = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "Playnite", "Plugins", "SharpMemories", "Screenshots");
                            string destination = Path.Combine(output, FileHelpers.MakeSafeFilename(gameTitle));
                            // Avoid moving a file onto itself when input/output folders coincide.
                            if (string.Equals(Path.GetFullPath(destination).TrimEnd('\\'),
                                Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            {
                                logger.Warn("Monitor source equals destination; skipping file move");
                                completed[file] = DateTime.UtcNow;
                                Candidate unused; pending.TryRemove(file, out unused);
                                continue;
                            }
                            Directory.CreateDirectory(destination);
                            var baseName = Path.GetFileNameWithoutExtension(file);
                            var ext = Path.GetExtension(file);
                            var target = FileHelpers.GetAvailablePath(destination, baseName, ext);
                            File.Move(file, target);
                            logger.Info($"Monitored screenshot moved: {file} -> {target}");
                            completed[file] = DateTime.UtcNow;
                            Candidate removed; pending.TryRemove(file, out removed);
                        }
                        catch (IOException ex)
                        {
                            state.Failures++;
                            state.RetryAfter = DateTime.UtcNow.AddSeconds(Math.Min(60, 2 * state.Failures));
                            if (state.Failures == 3 || state.Failures % 15 == 0) logger.Warn(ex, $"Screenshot move retry: {file}");
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            state.Failures++;
                            state.RetryAfter = DateTime.UtcNow.AddSeconds(60);
                            if (state.Failures == 1 || state.Failures % 15 == 0) logger.Warn(ex, $"Screenshot move access denied: {file}");
                        }
                        catch (Exception ex)
                        {
                            state.RetryAfter = DateTime.UtcNow.AddMinutes(1);
                            logger.Error(ex, $"Screenshot monitor processing failed: {file}");
                        }
                    }
                    foreach (var done in completed.ToArray())
                        if (DateTime.UtcNow - done.Value > TimeSpan.FromMinutes(10))
                        { DateTime unused; completed.TryRemove(done.Key, out unused); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.Error(ex, "Screenshot monitor worker stopped unexpectedly"); }
        }
    }
}
