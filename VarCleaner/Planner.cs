namespace VarCleaner;

public sealed class Selections
{
    // Scene packages the user wants gone.
    public List<string> RemoveUids { get; set; } = new();
    // Also remove content packs (no scenes) that nothing being kept uses.
    public bool RemoveUnusedContent { get; set; }
    // Broken packages (RelPath) the user wants moved out.
    public List<string> RemoveBroken { get; set; } = new();
}

public sealed record PlanItem(string Uid, string RelPath, long Size, string Reason, bool HasScenes);

public sealed record BlockedItem(string Uid, string KeptBecause);

public sealed record Plan(List<PlanItem> Remove, List<BlockedItem> Blocked, long TotalBytes, int KeptCount, List<string> MissingRefs);

public static class Planner
{
    public static Plan Make(Library lib, Selections sel)
    {
        var removeScenes = new HashSet<string>(sel.RemoveUids, StringComparer.OrdinalIgnoreCase);
        // Why each kept package is kept; the first reason found wins.
        var kept = new Dictionary<PackageInfo, string>();
        var queue = new Queue<PackageInfo>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Keep(PackageInfo p, string why)
        {
            if (kept.TryAdd(p, why))
            {
                queue.Enqueue(p);
            }
        }

        void KeepRef(string reference, string why)
        {
            var found = lib.Resolve(reference);
            if (found.Count == 0)
            {
                missing.Add(reference);
            }
            foreach (var p in found)
            {
                Keep(p, why);
            }
        }

        foreach (var p in lib.Packages.Where(p => !p.Broken))
        {
            if (p.HasScenes && !removeScenes.Contains(p.Uid))
            {
                Keep(p, "It has scenes you're keeping");
            }
            else if (!p.HasScenes && !sel.RemoveUnusedContent)
            {
                Keep(p, "Content packs without scenes are kept (setting)");
            }
        }
        foreach (var kv in lib.UserRefs)
        {
            KeepRef(kv.Key, "Used by your file " + kv.Value);
        }
        foreach (var uid in lib.Favorites)
        {
            KeepRef(uid, "You marked something in it as a favorite");
        }

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var r in p.Refs)
            {
                KeepRef(r, "Needed by " + p.Uid);
            }
        }

        var remove = new List<PlanItem>();
        foreach (var p in lib.Packages)
        {
            if (p.Broken)
            {
                if (sel.RemoveBroken.Contains(p.RelPath, StringComparer.OrdinalIgnoreCase))
                {
                    remove.Add(new PlanItem(p.Uid, p.RelPath, p.Size, "Broken: " + p.Error, false));
                }
                continue;
            }
            if (kept.ContainsKey(p))
            {
                continue;
            }
            string reason = p.HasScenes
                ? "Scene package you marked for removal"
                : "Content pack that nothing you're keeping uses";
            remove.Add(new PlanItem(p.Uid, p.RelPath, p.Size, reason, p.HasScenes));
        }

        var blocked = new List<BlockedItem>();
        foreach (var uid in removeScenes)
        {
            foreach (var p in lib.Packages.Where(p => !p.Broken && string.Equals(p.Uid, uid, StringComparison.OrdinalIgnoreCase)))
            {
                if (kept.TryGetValue(p, out var why))
                {
                    blocked.Add(new BlockedItem(p.Uid, why));
                }
            }
        }

        return new Plan(
            remove.OrderByDescending(r => r.HasScenes).ThenBy(r => r.Uid, StringComparer.OrdinalIgnoreCase).ToList(),
            blocked.OrderBy(b => b.Uid, StringComparer.OrdinalIgnoreCase).ToList(),
            remove.Sum(r => r.Size),
            kept.Count,
            missing.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList());
    }
}
