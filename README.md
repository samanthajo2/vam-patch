# VamScanCache

**Makes Virt-A-Mate start in seconds instead of minutes, and load heavy scenes
about 35% faster, when you have a large package library.**

If you have thousands of `.var` packages installed, you've probably noticed VaM
getting slow. Launching it after a break can take 3–5 minutes, and a scene with
a Person in it can take over two minutes to load. Most of that time has nothing
to do with what you're loading. VaM is doing work for every package you own,
every time.

This is a BepInEx plugin that fixes the biggest causes while VaM runs, without
modifying any game files. It also includes the profiling tools that were used to
find them, in case you want to dig further.

Measured with 10,010 packages (435 GB) on an NVMe SSD:

| Test                                   | Stock VaM    | With this plugin |
| -------------------------------------- | ------------ | ---------------- |
| Launching after a few hours away       | 3–5 min (\*) | **28 s**         |
| Launching again right after closing    | 26.9 s       | **11.4 s**       |
| `.var` files opened at startup         | 10,005       | **0**            |
| Loading a heavy scene (Autumn Reverie) | ~130 s       | **~84 s**        |

(\*) This is how long stock VaM usually takes on this machine after a break. It
wasn't timed during this investigation, because a test like this can only be
done once every few hours, and that chance was used to measure the plugin.

Why does the time after a break matter so much? When you launch VaM twice in a
row, Windows still has the package files in memory from the first launch, so
reading them is quick. After a few hours that memory has been reused, and every
file has to come off the disk again. Stock VaM reads from all 10,000 packages on
every launch, so it pays that cost in full. The plugin doesn't read them at all
at startup, so a launch after a break is about as fast as an immediate relaunch.

The test scene is `Zero Lambda.Autumn_Reverie.1`. It has 169 atoms (one Person,
67 props, about 20 plugin scripts) and depends on 111 other packages. Tests were
run on Windows 11 in desktop mode.

## Supported VaM version

This was written and tested against **VaM 1.22.0.13** (Unity 2018.1.9). That
build's `VaM_Data\Managed\Assembly-CSharp.dll` has SHA-256
`b368d7a6bf3c2a51861741d26c13a0e92e687db55c99c6285320717d7e4978db`.

The plugin checks this when VaM starts. On any other build it still runs, but it
writes a warning to `BepInEx\LogOutput.log`, because the parts of VaM it hooks
into may have changed.

## Install

1. Download `VamScanCache-<version>.zip` from the releases page.
2. Close VaM, then unzip everything in it into your VaM folder (the one that
   contains `VaM.exe`). It includes
   [BepInEx 5](https://github.com/BepInEx/BepInEx), which is what loads the
   plugin.
3. Launch VaM as usual. The first launch builds the cache and is no faster than
   normal. Every launch after that uses it.

If you already use BepInEx for other mods, use
`VamScanCache-<version>-plugin-only.zip` instead. It only adds
`BepInEx\plugins\VamScanCache.dll`.

To turn it off, delete `BepInEx/plugins/VamScanCache.dll`, or delete
`winhttp.dll` to disable BepInEx entirely. To remove everything, also delete
`doorstop_config.ini` and the `BepInEx/` folder.

You don't need to do anything when you add, remove or update packages. On the
next launch, any `.var` whose size or modified date has changed gets scanned the
normal way and its cache entry is updated.

A VaM update could change the parts of VaM this plugin hooks into. If something
looks wrong after an update, check `BepInEx/LogOutput.log` for errors and
disable the plugin.

## Settings

Settings are in `BepInEx/config/VamScanCache.cfg`. Each optimization can be
switched off on its own. That's also how every number on this page was measured:
the same test, with one setting changed.

| Setting              | Default | What it does                  |
| -------------------- | ------- | ----------------------------- |
| `CacheEnabled`       | true    | Package scan cache (below)    |
| `FastNames`          | true    | Fast file entry names (below) |
| `BatchAnatomy`       | true    | Batched `SyncAnatomy` (below) |
| `PluginCompileCache` | false   | Plugin compile cache (below)  |

`PluginCompileCache` changes how plugins behave, so it's off.

## The optimizations

### 1. Package scan cache

This is the fix for slow launches after a break.

**What VaM does.** Every time it starts, VaM opens every `.var` file (each is a
zip), reads the list of files inside it, and builds an in-memory index of every
file in every package. That's what lets it find things like "all the clothing"
or "all the morphs" later. It also unzips each package's `meta.json`. None of
this is saved, so the next launch does it all again for all 10,000 packages.

In code terms: `FileManager.Refresh()` creates a `VarPackage` for every `.var`.
Its constructor calls `VarPackage.Scan()`, which opens the zip with SharpZipLib,
reads the central directory, and creates a `VarFileEntry` for every file and
`VarDirectoryEntry` objects for every folder. `LoadMetaData()` then decompresses
`meta.json` from each zip.

VaM does have a cache, `Cache/PackageJSON`, but it doesn't help with this:

- It only stores the contents of each package's `.vmi` (morph) and `.vam`
  (clothing/hair) files. It doesn't store the file list or `meta.json`, which
  are what startup needs.
- It's only checked after the zip has already been opened and listed, so it can
  never save the open.
- During the startup scan it isn't checked at all. `CacheManager.CachingEnabled`
  is still false at that point and only gets switched on later.
- It decides whether an entry is still valid by file size only, not by date.

VaM also keeps every zip open for the rest of the session, which means 10,000
open file handles and all of their zip directory data held in memory.

**What the plugin does.** The first time it sees a package, it saves what VaM
learned from it:

- the list of files inside (name, size and date of each)
- the `meta.json` contents
- VaM's own `.vmi`/`.vam` JSON cache for that package

Everything goes into one file, `BepInEx/cache/varscan.bin` (90 MB for this
library; it loads in about a third of a second). Each package's entry is checked
against the `.var`'s size and modified date, so a changed package is always
rescanned.

On later launches:

- **Unchanged package:** the plugin builds the same index entries from the cache
  instead of opening the zip. A Harmony prefix on `VarPackage.Scan` mirrors the
  original code step by step.
- **Zips are opened only when needed:** the first time VaM actually reads a file
  from a package. All of VaM's reads from a package go through one place, the
  `VarFileEntryStream` constructor, so that's where the plugin opens the zip.
- **`meta.json`** is handed to VaM from the cache through that same spot, so
  reading it never opens the zip.
- **New or changed package:** VaM's normal scan runs, and the result is saved
  for next time.
- **Broken packages** (corrupt zips) are never cached. VaM scans them the normal
  way every launch and logs the same errors as before.
- **Deleted packages** are dropped from the cache file.

**Why it works.** Everything VaM collects at startup only changes when the
`.var` itself changes. Checking the size and date is enough to know the saved
copy is still right. Startup becomes one read of one file, instead of 10,000
files opened and parsed.

When the files are already in memory (an immediate relaunch), opening the zips
is fairly quick. It was only 1.3 s of the 19.7 s scan; most of the rest is the
CPU work fixed in #2. The difference shows up after a break, when all those
files have to come off disk. That's where 20 seconds turns into several minutes.
With the cache, a launch after a break took 12.8 s to scan packages, nearly the
same as the 11.3 s of an immediate relaunch. The plugin removes the slowdown
from launching after a break almost entirely.

**Result.**

- **Launching after a break:** from 3–5 minutes to 28 s.
- **Relaunching immediately:** this fix alone saves about 7 s (VaM's own
  `Scanned 10010 packages` timing went from 26.9 s to 19.5 s).
- **Zips opened at startup:** zero instead of 10,005. Even after loading Autumn
  Reverie, only 69 packages had needed to be opened.

**Checked.** VaM logged exactly the same errors and warnings with and without
the cache. The `SelfTest` setting read 25 random files out of cache-built
packages, and every one came back correct.

### 2. Fast file entry names

This saves about 8 seconds on every launch, from a one-line change.

**What VaM does.** For each of the roughly 720,000 files and folders in the
index (584,926 files and 136,965 folders here), VaM works out the name with
`Regex.Replace(path, ".*/", "")`: "delete everything up to the last slash".
That's in the constructors of `VarFileEntry`, `VarDirectoryEntry`, `FileEntry`
and `DirectoryEntry`.

**Why it's a problem.** Using a regular expression to get a file name from a
path is a well-known anti-pattern. Every call starts up the regex engine and
creates temporary objects, to do something a single string search can do. In C#
the usual way is:

```csharp
string name = path.Substring(path.LastIndexOf('/') + 1);
```

or `Path.GetFileName(path)`, which also understands `\`. VaM uses the same regex
in other places too, for example when it works out package names.

Technically the regex behaves differently from the substring in one case: `.`
doesn't match a newline, so on a path containing a newline it would strip text
up to the last `/` on each line. That was almost certainly never intended. The
developers just meant "the part after the last slash", and package paths never
contain newlines. Replacing the regex with `LastIndexOf` is the right fix in VaM
itself.

**What the plugin does.** It rewrites the regex call in those four constructors
to return the text after the last `/`. Because the plugin patches VaM from
outside, it errs on the side of caution and keeps the regex's exact behavior.
Any other pattern, or a path that contains a newline, still goes to
`Regex.Replace`, so results are identical in every case. VaM's own fix wouldn't
need that fallback.

**Result.** The regex was 60% of the time spent building the index: that part
went from 13.0 s to 5.2 s. Combined with #1, VaM's `Scanned 10010 packages` time
goes from 26.9 s to 11.4 s.

### 3. Batched `SyncAnatomy`

This is the biggest single fix: about a third of the scene's load time, from one
piece of redundant work.

**What VaM does.** When a Person is created or loaded, VaM resets their clothing
and hair. It does that by going through every clothing and hair item installed,
not just the ones the character wears, and setting each one on or off
(`ResetClothing`/`ResetHair` calling
`SetActiveClothingItem`/`SetActiveHairItem`).

After each item, it calls `SyncAnatomy()`, which goes through every clothing and
hair item again, checking whether any item that's being worn hides the anatomy.
Each of those checks is a Unity object comparison, which calls into Unity's
native code and isn't cheap.

So the work grows with the square of how many items you own (O(n²)). With about
9,700 clothing items and 5,400 hair items installed, one scene load called
`SyncAnatomy` 41,609 times, at about 1 ms each.

**What the plugin does.** While a clothing or hair reset is running, the plugin
skips `SyncAnatomy` and remembers which character needs it. When the reset
finishes, it runs `SyncAnatomy` once. (A counter tracks nested resets, and it's
reset in a finalizer, so an exception can't leave it stuck.)

**Why the result is the same.** `SyncAnatomy` only looks at the final state ("is
anything worn that hides the anatomy?") and sets some flags from that. Nothing
during the reset loop reads those flags. Running it once at the end with the
final state gives the same answer as running it after every item.

**Result.** `SyncAnatomy` was taking 42.8 s of the ~130 s load. Now it takes
0.16 s. Restoring the Person dropped from 22 s to under a second, and the whole
scene now loads in about 84 s instead of about 130 s.

| Autumn Reverie load | Individual runs          | Average |
| ------------------- | ------------------------ | ------- |
| Stock               | 132.5, 127.2, 129.7 s    | ~130 s  |
| With this plugin    | 82.6, 83.6, 85.7, 83.5 s | ~84 s   |

### 4. Plugin compile cache (off by default)

This works, but isn't worth turning on. It's included to show the problem.

**What VaM does.** Every time an atom loads a plugin script, VaM compiles that
script's C# source code, even if another atom just compiled the exact same file.
Each compile also loads a new copy of the plugin into memory that's never freed.
Autumn Reverie compiled scripts 91 times, and 59 of those were repeats.

**What the plugin does.** It remembers each compiled result, keyed on the source
code (VaM already computes an MD5 of it), and hands back the saved result
instead of compiling again.

**Why it's off.** Sharing one compiled copy means every atom using that plugin
also shares its static variables. Today each atom gets its own. A plugin that
relies on that could behave differently. And the payoff is small: the 91
compiles only took 5.9 s in total, and the cache saves about 4 s of that. If VaM
fixed this itself, the safe version would save the compiled bytes but still load
a separate copy for each atom.

## Where the remaining time goes

These were measured but not fixed, because fixing them properly needs changes
inside VaM.

- **Creating a Person loads things from every package you own, not just the ones
  the scene uses (~25 s here).**
  - Each of the six morph banks goes through every package that contains morphs
    (`RefreshPackageMorphs`): 67,954 morphs from 4,816 folders, about 7 s.
  - VaM sets up every installed clothing and hair item (`RefreshDynamicClothes`,
    `RefreshDynamicHair`), about 8 s.
  - There are 157,158 checks for whether a file exists
    (`FileManager.FileExists`), about 4 s.
- **Garbage collection (~15 s).**
  - Each entry in VaM's file index stores its path about 14 times, in slightly
    different forms. That's about 1.7 GB of memory just for paths, out of 3.8 GB
    used right after startup.
  - By the time the scene has loaded, memory use is 9.8 GB, and each garbage
    collection pass takes 1.9 s. Loading this scene triggers 8–9 of them.
  - The garbage collector in Unity 2018 checks all of that memory every time, so
    the bigger the index, the slower every collection.
- **Asset bundles (~35 s):** waiting for Unity to load the scene's 67 props.

## Profiling tools

All of these are in the `Debug` section of the settings file and are off unless
you turn them on.

- **`ProfileLoads`**: writes `BepInEx/LoadProfile.txt` after each scene load. It
  contains:

  - a timestamped timeline of VaM's loading steps and log messages
  - every frame that took over 250 ms
  - how long each atom took to restore
  - how many `.var` files had to be opened
  - slow plugin and storable callbacks
  - how long each C# compile took
  - an estimate of the time spent in garbage collection

  It also logs the size of the file index and memory use after startup. It adds
  a pause of about 2 s after each load while it measures garbage collection.

- **`AutoLoadScene` / `AutoQuitAfterLoad`**: load a scene (for example
  `Creator.Package.1:/Saves/scene/Scene.json`) once VaM is idle, and quit when
  it's done, so you can repeat a timing without clicking through the UI.
- **`TimeMethods`**: a comma-separated `Type.Method` list. Logs how often each
  is called and how long it takes.
- **`DeepProfileTypes`**: type name prefixes (for example
  `DAZ,Atom,SuperController`). Times every non-trivial method in matching types
  and reports where the time goes. This is what found the `SyncAnatomy` problem.
  It's heavy: it patches about 2,400 methods, adds about 8 s to startup, and
  crashed VaM once.
- **`SelfTest`**: after startup, reads random files from cache-built packages
  and checks they come back correct.

## Building from source

Building needs the .NET SDK and an installed copy of VaM, because the plugin
compiles against VaM's own assemblies. None of VaM's files are copied into the
build or the release zips.

- Plugin only: `dotnet build VamScanCache -c Release -p:VamDir=<VaM folder>`,
  then copy `VamScanCache/bin/Release/net35/VamScanCache.dll` to
  `BepInEx/plugins/`.
- Release zips: `.\release\build-release.ps1 -VamDir <VaM folder>` builds both
  zips into `dist\`. It downloads a pinned BepInEx release and checks its
  SHA-256 before bundling it.

The release zips can't be built by GitHub Actions without VaM's assemblies, and
those aren't published in this repo. They're built locally and attached to a
GitHub release.
