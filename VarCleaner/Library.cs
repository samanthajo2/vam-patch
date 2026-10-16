using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VarCleaner;

public sealed class PackageInfo
{
    public string Uid { get; set; } = "";
    public string Group { get; set; } = "";
    public string Creator { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; }
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public long MTimeTicks { get; set; }
    public List<string> Scenes { get; set; } = new();
    public List<string> Refs { get; set; } = new();
    public Dictionary<string, int> Kinds { get; set; } = new();
    public bool Broken { get; set; }
    public string? Error { get; set; }

    public bool HasScenes => Scenes.Count > 0;
}

// Everything known about one VaM install's packages and what references them.
public sealed class Library
{
    // A package reference inside VaM JSON: "Creator.Name.Version:/path", where Version is
    // a number, "latest" or "minN". Creator and name can't contain dots.
    static readonly Regex RefPattern = new(
        "(?<=\")([^\"./\\\\:\\r\\n]+\\.[^\"./\\\\:\\r\\n]+\\.(?:latest|min\\d+|\\d+)):/",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".vap", ".vaj", ".vam", ".cslist"
    };

    // .vac files are zip archives (exported scenes); their text entries are scanned too.
    const string ArchiveExtension = ".vac";

    const long MaxMetaBytes = 16L * 1024 * 1024;

    // Finds every package reference in a text stream of any size, reading it in chunks.
    // Chunks overlap so a reference split across a boundary is still found.
    static void ScanRefs(TextReader reader, ICollection<string> refs)
    {
        const int ChunkSize = 1 << 20;
        const int Overlap = 1024;
        var buf = new char[ChunkSize + Overlap];
        int carried = 0;
        for (;;)
        {
            int n = reader.ReadBlock(buf, carried, ChunkSize);
            int len = carried + n;
            if (len == 0)
            {
                return;
            }
            foreach (Match m in RefPattern.Matches(new string(buf, 0, len)))
            {
                refs.Add(m.Groups[1].Value);
            }
            if (n < ChunkSize)
            {
                return;
            }
            carried = Math.Min(Overlap, len);
            Array.Copy(buf, len - carried, buf, 0, carried);
        }
    }

    public string VamDir { get; }
    public string AddonDir => Path.Combine(VamDir, "AddonPackages");
    public string RemovedDir => Path.Combine(VamDir, "AddonPackages_Removed");
    public string DataDir { get; }

    public volatile bool Ready;
    public string Phase = "Starting";
    public int Done, Total;
    public string? FatalError;

    public List<PackageInfo> Packages { get; private set; } = new();
    public List<string> SimulatedDirs { get; private set; } = new();
    // Package references found in the user's own files, with one example file for each.
    public Dictionary<string, string> UserRefs { get; private set; } = new();
    // User files that couldn't be read, so their package references are unknown.
    public List<string> UnreadableUserFiles { get; private set; } = new();
    // Package uids with at least one favorite marker.
    public HashSet<string> Favorites { get; private set; } = new();

    Dictionary<string, List<PackageInfo>> byUid = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, List<PackageInfo>> byGroup = new(StringComparer.OrdinalIgnoreCase);

    public Library(string vamDir)
    {
        VamDir = Path.GetFullPath(vamDir);
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VarCleaner");
        Directory.CreateDirectory(DataDir);
    }

    string CachePath => Path.Combine(DataDir, "index-v2-" + StableHash(VamDir) + ".json");

    static string StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s.ToLowerInvariant())
        {
            h = (h ^ c) * 16777619;
        }
        return h.ToString("x8");
    }

    public void Build()
    {
        try
        {
            Ready = false;
            Phase = "Finding packages";
            var files = Directory.EnumerateFiles(AddonDir, "*.var", SearchOption.AllDirectories).ToList();
            var dirs = Directory.EnumerateDirectories(AddonDir, "*.var", SearchOption.AllDirectories).ToList();

            var cache = LoadCache();
            var result = new ConcurrentBag<PackageInfo>();
            Total = files.Count;
            Done = 0;
            Phase = "Reading packages";
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                var rel = Path.GetRelativePath(AddonDir, file);
                var fi = new FileInfo(file);
                if (cache.TryGetValue(rel, out var hit) && hit.Size == fi.Length && hit.MTimeTicks == fi.LastWriteTimeUtc.Ticks)
                {
                    result.Add(hit);
                }
                else
                {
                    result.Add(IndexVar(file, rel, fi));
                }
                Interlocked.Increment(ref Done);
            });

            Phase = "Reading your saved scenes, presets and favorites";
            var userFiles = new List<string>();
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var root in new[] { "Saves", "Custom" }.Select(r => Path.Combine(VamDir, r)).Concat(dirs))
            {
                if (Directory.Exists(root))
                {
                    userFiles.AddRange(Directory.EnumerateFiles(root, "*", opts).Where(IsUserFileToScan));
                }
            }
            userFiles.AddRange(Directory.EnumerateFiles(VamDir, "*.json", SearchOption.TopDirectoryOnly));
            var userCache = LoadUserCache();
            var scanned = new ConcurrentDictionary<string, UserFileInfo>(StringComparer.OrdinalIgnoreCase);
            Total = userFiles.Count;
            Done = 0;
            Parallel.ForEach(userFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, f =>
            {
                string rel = Path.GetRelativePath(VamDir, f);
                var fi = new FileInfo(f);
                if (!userCache.TryGetValue(rel, out var info) || info.Size != fi.Length || info.MTimeTicks != fi.LastWriteTimeUtc.Ticks)
                {
                    info = new UserFileInfo { Size = fi.Length, MTimeTicks = fi.LastWriteTimeUtc.Ticks, Refs = ScanUserFile(f) };
                }
                scanned[rel] = info;
                Interlocked.Increment(ref Done);
            });
            var userRefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unreadable = new List<string>();
            foreach (var kv in scanned.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (kv.Value.Refs == null)
                {
                    unreadable.Add(kv.Key);
                    continue;
                }
                foreach (var r in kv.Value.Refs)
                {
                    userRefs.TryAdd(r, kv.Key);
                }
            }
            UnreadableUserFiles = unreadable;
            SaveUserCache(scanned);

            var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prefsDir = Path.Combine(VamDir, "AddonPackagesFilePrefs");
            if (Directory.Exists(prefsDir))
            {
                foreach (var d in Directory.EnumerateDirectories(prefsDir))
                {
                    if (Directory.EnumerateFiles(d, "*.fav", SearchOption.AllDirectories).Any())
                    {
                        favorites.Add(Path.GetFileName(d));
                    }
                }
            }

            var list = result.OrderBy(p => p.RelPath, StringComparer.OrdinalIgnoreCase).ToList();
            byUid = new(StringComparer.OrdinalIgnoreCase);
            byGroup = new(StringComparer.OrdinalIgnoreCase);
            foreach (var p in list.Where(p => !p.Broken))
            {
                Add(byUid, p.Uid, p);
                Add(byGroup, p.Group, p);
            }
            Packages = list;
            SimulatedDirs = dirs.Select(d => Path.GetRelativePath(AddonDir, d)).ToList();
            UserRefs = userRefs;
            Favorites = favorites;
            SaveCache(list);
            Phase = "Ready";
            Ready = true;
        }
        catch (Exception ex)
        {
            FatalError = ex.Message;
            Phase = "Failed: " + ex.Message;
        }
    }

    static void Add(Dictionary<string, List<PackageInfo>> d, string key, PackageInfo p)
    {
        if (!d.TryGetValue(key, out var l))
        {
            d[key] = l = new List<PackageInfo>();
        }
        l.Add(p);
    }

    public PackageInfo? FindByUid(string uid) =>
        byUid.TryGetValue(uid, out var l) ? l[0] : null;

    // How VaM resolves a reference: the exact version if installed, otherwise the newest
    // installed version of that creator.name ("latest", "minN", or a missing exact version).
    public List<PackageInfo> Resolve(string reference)
    {
        int last = reference.LastIndexOf('.');
        if (last <= 0)
        {
            return new();
        }
        string group = reference.Substring(0, last);
        string version = reference.Substring(last + 1);
        if (int.TryParse(version, out _) && byUid.TryGetValue(reference, out var exact))
        {
            return exact;
        }
        if (!byGroup.TryGetValue(group, out var all))
        {
            return new();
        }
        int newest = all.Max(p => p.Version);
        return all.Where(p => p.Version == newest).ToList();
    }

    PackageInfo IndexVar(string file, string rel, FileInfo fi)
    {
        var p = new PackageInfo
        {
            RelPath = rel,
            Size = fi.Length,
            MTimeTicks = fi.LastWriteTimeUtc.Ticks,
        };
        string stem = Path.GetFileNameWithoutExtension(file);
        var parts = stem.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[2], out int version))
        {
            p.Broken = true;
            p.Error = "File name isn't Creator.Name.Version.var, so VaM ignores it";
            p.Uid = stem;
            return p;
        }
        p.Uid = stem;
        p.Creator = parts[0];
        p.Name = parts[1];
        p.Version = version;
        p.Group = parts[0] + "." + parts[1];

        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = ZipFile.OpenRead(file);
            foreach (var e in zip.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    continue;
                }
                string kind = KindOf(name);
                p.Kinds[kind] = p.Kinds.GetValueOrDefault(kind) + 1;
                if (name.StartsWith("Saves/scene/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    p.Scenes.Add(name);
                }
                bool isMeta = name.Equals("meta.json", StringComparison.OrdinalIgnoreCase);
                if (isMeta && e.Length <= MaxMetaBytes)
                {
                    string text;
                    using (var s = e.Open())
                    using (var r = new StreamReader(s, Encoding.UTF8))
                    {
                        text = r.ReadToEnd();
                    }
                    AddMetaDependencies(text, refs);
                    ScanRefs(new StringReader(text), refs);
                }
                else if (TextExtensions.Contains(Path.GetExtension(name)))
                {
                    using var s = e.Open();
                    using var r = new StreamReader(s, Encoding.UTF8);
                    ScanRefs(r, refs);
                }
            }
        }
        catch (Exception ex)
        {
            p.Broken = true;
            p.Error = "Can't be opened as a zip: " + ex.Message;
            p.Kinds.Clear();
            p.Scenes.Clear();
            return p;
        }
        refs.RemoveWhere(r => r.StartsWith(p.Group + ".", StringComparison.OrdinalIgnoreCase));
        p.Refs = refs.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        p.Scenes.Sort(StringComparer.OrdinalIgnoreCase);
        return p;
    }

    // meta.json lists dependencies as nested objects: { "dependencies": { "A.B.1": { "dependencies": {...} } } }
    static void AddMetaDependencies(string text, HashSet<string> refs)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            Walk(doc.RootElement);
        }
        catch (JsonException)
        {
            // Some packages ship invalid meta.json; their references are still picked up by the regex scan.
        }

        void Walk(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            if (el.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var d in deps.EnumerateObject())
                {
                    refs.Add(d.Name);
                    Walk(d.Value);
                }
            }
        }
    }

    static string KindOf(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.StartsWith("saves/scene/")) return "scenes";
        if (n.StartsWith("custom/clothing/")) return "clothing";
        if (n.StartsWith("custom/hair/")) return "hair";
        if (n.StartsWith("custom/atom/person/morphs/")) return "morphs";
        if (n.StartsWith("custom/atom/person/textures/")) return "textures";
        if (n.StartsWith("custom/atom/person/")) return "presets";
        if (n.StartsWith("custom/assets/")) return "assets";
        if (n.StartsWith("custom/scripts/")) return "plugins";
        if (n.StartsWith("custom/sounds/") || n.StartsWith("custom/music/")) return "audio";
        if (n == "meta.json") return "meta";
        return "other";
    }

    static bool IsUserFileToScan(string path)
    {
        string ext = Path.GetExtension(path);
        return TextExtensions.Contains(ext) || ext.Equals(ArchiveExtension, StringComparison.OrdinalIgnoreCase);
    }

    // Package references in one of the user's own files. Returns null if it can't be read,
    // so the caller can warn instead of treating it as "uses nothing".
    static List<string>? ScanUserFile(string file)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Path.GetExtension(file).Equals(ArchiveExtension, StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(file);
                foreach (var e in zip.Entries)
                {
                    if (TextExtensions.Contains(Path.GetExtension(e.FullName)))
                    {
                        using var r = new StreamReader(e.Open(), Encoding.UTF8);
                        ScanRefs(r, refs);
                    }
                }
            }
            else
            {
                using var r = new StreamReader(file, Encoding.UTF8);
                ScanRefs(r, refs);
            }
        }
        catch (Exception)
        {
            return null;
        }
        return refs.ToList();
    }

    public sealed class UserFileInfo
    {
        public long Size { get; set; }
        public long MTimeTicks { get; set; }
        public List<string>? Refs { get; set; }
    }

    string UserCachePath => Path.Combine(DataDir, "userfiles-v2-" + StableHash(VamDir) + ".json");

    Dictionary<string, UserFileInfo> LoadUserCache()
    {
        try
        {
            if (File.Exists(UserCachePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, UserFileInfo>>(File.ReadAllText(UserCachePath), CacheJson);
                if (d != null)
                {
                    return new Dictionary<string, UserFileInfo>(d, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception)
        {
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    void SaveUserCache(IDictionary<string, UserFileInfo> scanned)
    {
        // Unreadable files (Refs == null) aren't cached, so they're retried next time.
        var ok = scanned.Where(kv => kv.Value.Refs != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        string tmp = UserCachePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(ok, CacheJson));
        File.Move(tmp, UserCachePath, overwrite: true);
    }

    static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    Dictionary<string, PackageInfo> LoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var list = JsonSerializer.Deserialize<List<PackageInfo>>(File.ReadAllText(CachePath), CacheJson);
                if (list != null)
                {
                    return list.GroupBy(p => p.RelPath, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception)
        {
            // A bad cache just means a full rescan.
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    void SaveCache(List<PackageInfo> list)
    {
        string tmp = CachePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, CacheJson));
        File.Move(tmp, CachePath, overwrite: true);
    }
}
