# VarCleaner

> A personal tool, shared as-is. It isn't part of the VamScanCache release and
> isn't supported.

**Pick the VaM scenes you don't want any more, and move them, plus anything only
they needed, out of `AddonPackages`.**

VaM has no tool for cleaning up packages. After a few years of downloading, most
of a library can be scenes you'll never open again, plus the clothing, hair and
assets they pulled in. Deleting `.var` files by hand is risky, because you can't
see which packages other scenes still depend on.

VarCleaner reads every package and works out what depends on what. You click the
scenes you don't want, and it shows exactly which packages would go and why. It
also shows which ones it can't remove because something you're keeping still
uses them.

## Running it

Close VaM first if you're going to move anything. Then:

```
dotnet run --project VarCleaner -c Release -- "C:\path\to\your\VaM folder"
```

It opens a page in your browser. The page only works on your own computer; it
isn't reachable from other machines. Leave the console window open while you use
it, and press Ctrl+C there when you're done.

The first run reads every package, which took about 10 minutes for 10,000
packages. Later runs only re-read packages and files that changed, so they're
quick.

## Using the page

- Every package that contains a scene is shown with its thumbnail, creator, size
  and date. Click one to mark it for removal; click again to keep it.
- Search, sort (oldest first is a good place to start) and the "Show" filter
  help with big libraries. "Mark shown for removal" and "Keep shown" apply to
  everything currently visible.
- Your choices are saved as you go, so you can close the page and come back.
- **Preview removal** shows what would happen. Nothing moves until you click
  "Move" and then confirm.

## What it keeps

A package is never removed if any of these need it, directly or through other
packages' dependencies:

- a scene package you didn't mark for removal
- your own scenes, presets and looks in `Saves` and `Custom`, including `.vac`
  archives
- anything you've marked as a favorite in VaM
- unpacked package folders in `AddonPackages`

Content packs (clothing, hair, morphs, assets, plugins) without scenes are kept
by default. The preview has a switch to also remove the ones that nothing you're
keeping uses. Leave it off if you browse those packs directly in VaM.

References follow VaM's own rules: an exact version if it's installed, otherwise
the newest installed version. When an older version of a package isn't used by
anything you keep, it's offered for removal along with the rest.

The preview also lists broken packages VaM can't load (corrupt zips, macOS `._`
files, bad names). They're only removed if you tick them.

## Undo

Removed packages are moved, not deleted, to `AddonPackages_Removed` next to
`AddonPackages`. VaM doesn't look in that folder, so to VaM they're gone. Each
move is logged, and the page has a "Put back in AddonPackages" button for every
batch. Once you're sure, delete `AddonPackages_Removed` yourself to free the
space.

## Where it keeps its data

The package index and your selections are cached in `%LOCALAPPDATA%\VarCleaner`.
Deleting that folder only means the next run reads everything again.
