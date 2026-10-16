using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using VarCleaner;

bool openBrowser = !args.Contains("--no-browser");
// With no folder given, look where the user is and where VarCleaner.exe sits, so
// dropping the exe into the VaM folder and double-clicking it just works.
string vamDir = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
        .FirstOrDefault(d => File.Exists(Path.Combine(d, "VaM.exe")))
    ?? Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(vamDir, "VaM.exe")) || !Directory.Exists(Path.Combine(vamDir, "AddonPackages")))
{
    Console.Error.WriteLine("Couldn't find VaM.exe and AddonPackages in " + vamDir);
    Console.Error.WriteLine("Put VarCleaner.exe in your VaM folder (next to VaM.exe) and run it again,");
    Console.Error.WriteLine("or run: VarCleaner \"<path to your VaM folder>\"");
    if (args.Length == 0)
    {
        // Started by double-clicking: keep the window open so the message can be read.
        Console.Error.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }
    return 1;
}

var lib = new Library(vamDir);
var mover = new Mover(lib);
_ = Task.Run(lib.Build);

string selectionsPath = Path.Combine(lib.DataDir, "selections-" + Path.GetFileName(lib.VamDir.TrimEnd('\\')) + ".json");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

Selections LoadSelections()
{
    try
    {
        if (File.Exists(selectionsPath))
        {
            return JsonSerializer.Deserialize<Selections>(File.ReadAllText(selectionsPath), json) ?? new();
        }
    }
    catch (Exception)
    {
    }
    return new();
}

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// Local only: the page can move files, so it must not be reachable from other machines.
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app = builder.Build();

string html;
using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")!)
using (var r = new StreamReader(s))
{
    html = r.ReadToEnd();
}

app.MapGet("/", () => Results.Content(html, "text/html; charset=utf-8"));

app.MapGet("/api/status", () => Results.Json(new
{
    ready = lib.Ready,
    phase = lib.Phase,
    done = lib.Done,
    total = lib.Total,
    error = lib.FatalError,
    vamDir = lib.VamDir,
    vamRunning = Mover.VamIsRunning(),
}, json));

app.MapGet("/api/library", () =>
{
    if (!lib.Ready)
    {
        return Results.StatusCode(503);
    }
    var valid = lib.Packages.Where(p => !p.Broken).ToList();
    return Results.Json(new
    {
        stats = new
        {
            packages = valid.Count,
            bytes = valid.Sum(p => p.Size),
            scenePackages = valid.Count(p => p.HasScenes),
            sceneBytes = valid.Where(p => p.HasScenes).Sum(p => p.Size),
            contentPackages = valid.Count(p => !p.HasScenes),
            broken = lib.Packages.Count(p => p.Broken),
            unpackedFolders = lib.SimulatedDirs.Count,
            userRefs = lib.UserRefs.Count,
            favorites = lib.Favorites.Count,
            unreadableUserFiles = lib.UnreadableUserFiles,
        },
        scenePackages = valid.Where(p => p.HasScenes).Select(p => new
        {
            uid = p.Uid,
            creator = p.Creator,
            name = p.Name,
            version = p.Version,
            size = p.Size,
            modified = new DateTime(p.MTimeTicks, DateTimeKind.Utc),
            scenes = p.Scenes.Select(Path.GetFileNameWithoutExtension).ToList(),
            kinds = p.Kinds,
        }),
        broken = lib.Packages.Where(p => p.Broken).Select(p => new { relPath = p.RelPath, size = p.Size, error = p.Error }),
    }, json);
});

app.MapGet("/api/thumb", (string uid, int? scene) =>
{
    var p = lib.FindByUid(uid);
    if (p == null || p.Scenes.Count == 0)
    {
        return Results.NotFound();
    }
    string scenePath = p.Scenes[Math.Clamp(scene ?? 0, 0, p.Scenes.Count - 1)];
    string jpg = scenePath.Substring(0, scenePath.Length - ".json".Length) + ".jpg";
    try
    {
        using var zip = ZipFile.OpenRead(Path.Combine(lib.AddonDir, p.RelPath));
        var e = zip.Entries.FirstOrDefault(x => string.Equals(x.FullName.Replace('\\', '/'), jpg, StringComparison.OrdinalIgnoreCase));
        if (e == null)
        {
            return Results.NotFound();
        }
        using var s = e.Open();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return Results.File(ms.ToArray(), "image/jpeg");
    }
    catch (Exception)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/selections", () => Results.Json(LoadSelections(), json));

app.MapPut("/api/selections", (Selections sel) =>
{
    File.WriteAllText(selectionsPath, JsonSerializer.Serialize(sel, json));
    return Results.NoContent();
});

app.MapPost("/api/plan", (Selections sel) =>
    lib.Ready ? Results.Json(Planner.Make(lib, sel), json) : Results.StatusCode(503));

app.MapPost("/api/apply", (Selections sel) =>
{
    if (!lib.Ready)
    {
        return Results.StatusCode(503);
    }
    // Recompute the plan here rather than trusting a list from the page.
    var plan = Planner.Make(lib, sel);
    var result = mover.Remove(plan.Remove);
    if (result.Moved > 0)
    {
        lib.Build();
    }
    return Results.Json(result, json);
});

app.MapGet("/api/history", () => Results.Json(
    mover.History()
        .GroupBy(r => r.Batch)
        .OrderByDescending(g => g.Key)
        .Select(g => new { batch = g.Key, count = g.Count(), bytes = g.Sum(r => r.Size) }),
    json));

app.MapPost("/api/restore", (string batch) =>
{
    var result = mover.Restore(batch);
    if (result.Moved > 0)
    {
        lib.Build();
    }
    return Results.Json(result, json);
});

await app.StartAsync();
string url = app.Urls.First();
Console.WriteLine("VarCleaner is running at " + url);
Console.WriteLine("VaM folder: " + lib.VamDir);
Console.WriteLine("Leave this window open while you use the page. Press Ctrl+C to quit.");
try
{
    if (openBrowser)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
catch (Exception)
{
    Console.WriteLine("Open the address above in your browser.");
}
await app.WaitForShutdownAsync();
return 0;
