using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Playnite.SDK;

namespace SharpMemories
{
    // Persistent data deliberately excludes PIDs and HWNDs: neither survives a restart.
    public sealed class GameWindowObservation
    {
        public string ExecutablePath { get; set; }
        public string WindowTitle { get; set; }
        public string WindowClass { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int WindowStyle { get; set; }
        public int WindowExStyle { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int SeenCount { get; set; }
        public override string ToString() => string.Format("{0} | {1} | {2} | {3}x{4}",
            WindowTitle, Path.GetFileName(ExecutablePath ?? ""), WindowClass, Width, Height);
    }

    public sealed class GameWindowFingerprint
    {
        public string GameId { get; set; }
        public string GameName { get; set; }
        // 0 = None, 1 = Preferred, 2 = Forced. Previously saved ForceMatch=true remains readable.
        public int MatchMode { get; set; }
        [System.Xml.Serialization.XmlElement("ForceMatch")]
        public bool ForceMatch
        {
            get => MatchMode == 2;
            set { if (value) MatchMode = 2; }
        }
        // New files only store MatchMode; old ForceMatch elements are still accepted.
        public bool ShouldSerializeForceMatch() => false;
        public string PreferredExecutablePath { get; set; }
        public string PreferredTitleContains { get; set; }
        public string ExcludedTitleContains { get; set; }
        public string PreferredWindowClass { get; set; }
        public List<GameWindowObservation> Observations { get; set; } = new List<GameWindowObservation>();
        public override string ToString() => (GameName ?? GameId) + (MatchMode == 2 ? " [FORCE]" : MatchMode == 1 ? " [PREFERRED]" : "");
    }

    [XmlRoot("GameWindowFingerprints")]
    public sealed class GameWindowFingerprintDatabase
    {
        public List<GameWindowFingerprint> Games { get; set; } = new List<GameWindowFingerprint>();
    }

    public sealed class GameFingerprintStore
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static GameFingerprintStore Current { get; private set; }
        private readonly object gate = new object();
        private readonly string filepath;
        private GameWindowFingerprintDatabase data = new GameWindowFingerprintDatabase();
        private static readonly XmlSerializer serializer = new XmlSerializer(typeof(GameWindowFingerprintDatabase));

        public static void Initialize(string directory)
        {
            Current = new GameFingerprintStore(Path.Combine(directory, "GameWindowFingerprints.xml"));
        }
        private GameFingerprintStore(string path)
        {
            filepath = path;
            try
            {
                if (File.Exists(filepath))
                    using (var stream = File.OpenRead(filepath))
                        data = (GameWindowFingerprintDatabase)serializer.Deserialize(stream);
                if (data == null || data.Games == null) data = new GameWindowFingerprintDatabase();
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "Unable to read game window fingerprints; keeping existing file unchanged");
                data = new GameWindowFingerprintDatabase();
            }
        }
        public string FilePath => filepath;
        private static GameWindowFingerprint Clone(GameWindowFingerprint r)
        {
            if (r == null) return null;
            return new GameWindowFingerprint
            {
                GameId = r.GameId, GameName = r.GameName, MatchMode = r.MatchMode,
                PreferredExecutablePath = r.PreferredExecutablePath,
                PreferredTitleContains = r.PreferredTitleContains,
                ExcludedTitleContains = r.ExcludedTitleContains,
                PreferredWindowClass = r.PreferredWindowClass,
                Observations = (r.Observations ?? new List<GameWindowObservation>()).Select(o => new GameWindowObservation
                {
                    ExecutablePath=o.ExecutablePath, WindowTitle=o.WindowTitle, WindowClass=o.WindowClass,
                    Width=o.Width, Height=o.Height, WindowStyle=o.WindowStyle, WindowExStyle=o.WindowExStyle,
                    FirstSeenUtc=o.FirstSeenUtc, LastSeenUtc=o.LastSeenUtc,
                    SeenCount=o.SeenCount
                }).ToList()
            };
        }
        public List<GameWindowFingerprint> GetAll()
        {
            lock (gate) return data.Games.Select(Clone).OrderBy(x => x.GameName).ToList();
        }
        public GameWindowFingerprint Get(string id)
        {
            lock (gate) return Clone(data.Games.FirstOrDefault(x => x.GameId == id));
        }
        public void Ensure(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            lock (gate)
            {
                var r = data.Games.FirstOrDefault(x => x.GameId == id);
                if (r == null)
                {
                    data.Games.Add(new GameWindowFingerprint { GameId = id, GameName = name });
                    SaveInternal();
                }
                else if (r.GameName != name) { r.GameName = name; SaveInternal(); }
            }
        }
        public bool UpdateRules(GameWindowFingerprint updated)
        {
            if (updated == null || string.IsNullOrWhiteSpace(updated.GameId)) return false;
            lock (gate)
            {
                var r = data.Games.FirstOrDefault(x => x.GameId == updated.GameId);
                if (r == null) return false;
                r.MatchMode=updated.MatchMode >= 0 && updated.MatchMode <= 2 ? updated.MatchMode : 0;
                r.PreferredExecutablePath=updated.PreferredExecutablePath?.Trim();
                r.PreferredTitleContains=updated.PreferredTitleContains?.Trim();
                r.ExcludedTitleContains=updated.ExcludedTitleContains?.Trim();
                r.PreferredWindowClass=updated.PreferredWindowClass?.Trim();
                return SaveInternal();
            }
        }
        public void Observe(string id, string path, string title, string className, int width, int height, int style, int exStyle)
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(title)) return;
            lock (gate)
            {
                var r = data.Games.FirstOrDefault(x => x.GameId == id);
                if (r == null) return;
                // Same Java process may own a launcher AND a game window. Record each separately.
                var obs = r.Observations.FirstOrDefault(o =>
                    string.Equals(o.ExecutablePath, path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.WindowTitle, title, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(o.WindowClass, className, StringComparison.OrdinalIgnoreCase));
                DateTime now = DateTime.UtcNow;
                if (obs == null)
                {
                    r.Observations.Add(new GameWindowObservation
                    {
                        ExecutablePath=path, WindowTitle=title, WindowClass=className,
                        Width=width, Height=height, WindowStyle=style, WindowExStyle=exStyle,
                        FirstSeenUtc=now, LastSeenUtc=now, SeenCount=1
                    });
                    // Avoid unbounded growth from title strings that constantly change.
                    if (r.Observations.Count > 100) r.Observations.RemoveAt(0);
                    SaveInternal();
                }
                else if ((now - obs.LastSeenUtc).TotalMinutes >= 5)
                {
                    obs.LastSeenUtc=now; obs.SeenCount++; obs.Width=width; obs.Height=height; obs.WindowStyle=style; obs.WindowExStyle=exStyle;
                    SaveInternal();
                }
            }
        }
        public static bool IsForceRuleUsable(GameWindowFingerprint r) =>
            r != null && (!string.IsNullOrWhiteSpace(r.PreferredTitleContains) ||
                          !string.IsNullOrWhiteSpace(r.PreferredWindowClass));

        public static bool MatchesPreferredRule(GameWindowFingerprint r, string path, string title, string className)
        {
            if (!IsForceRuleUsable(r)) return false;
            if (!string.IsNullOrWhiteSpace(r.PreferredExecutablePath) &&
                !string.Equals(r.PreferredExecutablePath, path, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrWhiteSpace(r.PreferredTitleContains) &&
                (title ?? "").IndexOf(r.PreferredTitleContains, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (!string.IsNullOrWhiteSpace(r.ExcludedTitleContains) &&
                (title ?? "").IndexOf(r.ExcludedTitleContains, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (!string.IsNullOrWhiteSpace(r.PreferredWindowClass) &&
                !string.Equals(r.PreferredWindowClass, className, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        public bool MatchesForced(string id, string path, string title, string className)
        {
            var r = Get(id);
            return r == null || r.MatchMode != 2 || MatchesPreferredRule(r, path, title, className);
        }
        public int GetMatchMode(string id) => Get(id)?.MatchMode ?? 0;
        public bool MatchesPreferred(string id, string path, string title, string className)
        {
            var r = Get(id);
            return r != null && r.MatchMode != 0 && MatchesPreferredRule(r, path, title, className);
        }
        public int GetHintScore(string id, string path, string title, string className, int width, int height, int style, int exStyle)
        {
            var r = Get(id);
            if (r == null) return 0;
            int score = 0;
            if (!string.IsNullOrWhiteSpace(r.ExcludedTitleContains) &&
                (title ?? "").IndexOf(r.ExcludedTitleContains, StringComparison.OrdinalIgnoreCase) >= 0) score -= 100;
            if (!string.IsNullOrWhiteSpace(r.PreferredTitleContains) &&
                (title ?? "").IndexOf(r.PreferredTitleContains, StringComparison.OrdinalIgnoreCase) >= 0) score += 120;
            if (!string.IsNullOrWhiteSpace(r.PreferredExecutablePath) &&
                string.Equals(r.PreferredExecutablePath, path, StringComparison.OrdinalIgnoreCase)) score += 20;
            if (!string.IsNullOrWhiteSpace(r.PreferredWindowClass) &&
                string.Equals(r.PreferredWindowClass, className, StringComparison.OrdinalIgnoreCase)) score += 15;
            var known = (r.Observations ?? new List<GameWindowObservation>()).FirstOrDefault(o =>
                string.Equals(o.ExecutablePath, path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.WindowTitle, title, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.WindowClass, className, StringComparison.OrdinalIgnoreCase));
            if (known != null) score += 5 + (known.Width == width && known.Height == height ? 2 : 0) +
                (known.WindowStyle == style && known.WindowExStyle == exStyle ? 2 : 0);
            return score;
        }
        private bool SaveInternal()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filepath));
                string temp = filepath + ".tmp";
                using (var stream = File.Create(temp)) serializer.Serialize(stream, data);
                if (File.Exists(filepath))
                {
                    string backup = filepath + ".bak";
                    File.Replace(temp, filepath, backup);
                }
                else File.Move(temp, filepath);
                return true;
            }
            catch (Exception ex) { logger.Warn(ex, "Unable to save game window fingerprints"); return false; }
        }
    }
}
