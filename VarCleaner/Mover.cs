using System.Diagnostics;
using System.Text.Json;

namespace VarCleaner;

public sealed record MoveRecord(string Batch, string Uid, string From, string To, long Size, DateTime When);

public sealed record MoveResult(int Moved, long Bytes, List<string> Errors, string? Batch);

// Moves packages to AddonPackages_Removed (outside VaM's view) and back. Every move is
// logged in AddonPackages_Removed/removed-log.jsonl so it can be undone.
public sealed class Mover
{
    readonly Library lib;
    readonly object sync = new();

    public Mover(Library lib) => this.lib = lib;

    string LogPath => Path.Combine(lib.RemovedDir, "removed-log.jsonl");

    public static bool VamIsRunning() =>
        Process.GetProcessesByName("VaM").Length > 0;

    public MoveResult Remove(List<PlanItem> items)
    {
        lock (sync)
        {
            var errors = new List<string>();
            if (VamIsRunning())
            {
                errors.Add("VaM is running. Close it first: VaM keeps package files open, so they can't be moved.");
                return new MoveResult(0, 0, errors, null);
            }
            string batch = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var records = new List<MoveRecord>();
            foreach (var item in items)
            {
                string from = Path.Combine(lib.AddonDir, item.RelPath);
                string to = Unique(Path.Combine(lib.RemovedDir, item.RelPath));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to);
                    records.Add(new MoveRecord(batch, item.Uid, from, to, item.Size, DateTime.Now));
                    // VaM's "disabled" marker belongs with the package.
                    if (File.Exists(from + ".disabled"))
                    {
                        File.Move(from + ".disabled", to + ".disabled");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(item.RelPath + ": " + ex.Message);
                }
            }
            Append(records);
            return new MoveResult(records.Count, records.Sum(r => r.Size), errors, batch);
        }
    }

    public List<MoveRecord> History()
    {
        lock (sync)
        {
            return Read().Where(r => File.Exists(r.To)).ToList();
        }
    }

    public MoveResult Restore(string batch)
    {
        lock (sync)
        {
            var errors = new List<string>();
            if (VamIsRunning())
            {
                errors.Add("VaM is running. Close it first, then restore.");
                return new MoveResult(0, 0, errors, batch);
            }
            var all = Read();
            var keep = new List<MoveRecord>();
            int moved = 0;
            long bytes = 0;
            foreach (var r in all)
            {
                if (r.Batch != batch || !File.Exists(r.To))
                {
                    if (File.Exists(r.To))
                    {
                        keep.Add(r);
                    }
                    continue;
                }
                try
                {
                    if (File.Exists(r.From))
                    {
                        throw new IOException("a file with that name is already back in AddonPackages");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(r.From)!);
                    File.Move(r.To, r.From);
                    if (File.Exists(r.To + ".disabled"))
                    {
                        File.Move(r.To + ".disabled", r.From + ".disabled");
                    }
                    moved++;
                    bytes += r.Size;
                }
                catch (Exception ex)
                {
                    errors.Add(r.Uid + ": " + ex.Message);
                    keep.Add(r);
                }
            }
            Write(keep);
            return new MoveResult(moved, bytes, errors, batch);
        }
    }

    static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(p))
            {
                return p;
            }
        }
    }

    List<MoveRecord> Read()
    {
        if (!File.Exists(LogPath))
        {
            return new();
        }
        return File.ReadAllLines(LogPath)
            .Where(l => l.Trim().Length > 0)
            .Select(l => JsonSerializer.Deserialize<MoveRecord>(l)!)
            .ToList();
    }

    void Append(List<MoveRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }
        Directory.CreateDirectory(lib.RemovedDir);
        File.AppendAllLines(LogPath, records.Select(r => JsonSerializer.Serialize(r)));
    }

    void Write(List<MoveRecord> records)
    {
        Directory.CreateDirectory(lib.RemovedDir);
        File.WriteAllLines(LogPath, records.Select(r => JsonSerializer.Serialize(r)));
    }
}
