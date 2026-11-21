# Image Sorter

A small Windows app for sorting a folder of pictures with the keyboard.

It shows one picture at a time, like a photo viewer. You set up "sorters" (a destination folder with a name and a hotkey), then press a key and the picture goes to that folder. The picture visibly flies into the sorter it was sent to, so you can see where it went.

## Download

From the [latest release](https://github.com/LionHUBYK/ImageSorter/releases/latest), pick one:

- **`ImageSorterSetup.exe`** installs the app for your Windows account (no administrator prompt), adds a Start menu shortcut, and can be removed again from Installed apps.
- **`ImageSorter.exe`** is the portable version: one file, no install, run it from anywhere.

Both include the .NET runtime, so nothing else is needed. Windows 10 or 11, 64-bit.

Neither file is code-signed, so Windows may show a "Windows protected your PC" box the first time. Choose **More info**, then **Run anyway**.

## How to use it

1. Click **Open folder** and pick the folder of pictures to sort.
2. Click **Add sorter** and pick a destination folder. It gets the next number key; you can rename it or change the key.
3. Press a sorter's key to send the current picture there. The next picture appears straight away.

| Key | What it does |
| --- | --- |
| `1` `2` `3` … | Send the picture to that sorter (any number, letter, F-key or punctuation key can be assigned) |
| `←` `→` | Previous / next picture |
| `Space` | Skip this picture |
| `Ctrl+Z` | Undo the last sort |
| `Tab` | Switch between **Move** and **Copy** |
| Mouse wheel | Zoom; drag to pan; double-click for actual size |
| `Esc` | Reset zoom |
| `Ctrl+O` | Open a folder |

You can also click a sorter card instead of pressing its key.

## Good to know

- **Nothing is overwritten.** If the destination already has a file with the same name, the new one is saved as `name (2).jpg`.
- **Undo is real.** Undoing a move puts the file back; undoing a copy sends the copy to the Recycle Bin.
- **Include subfolders** also lists pictures in folders below the one you opened, folder by folder all the way down: the opened folder first, then each subfolder, then its subfolders. The first picture of each subfolder is marked with that folder's name. Folders you sort into are left out.
- Sorters and settings are remembered in `%AppData%\ImageSorter\settings.json`. For portable use, put an empty file called `ImageSorter.settings.json` next to the exe and settings are kept there instead.

## File types

JPG, PNG, WebP, BMP, TIFF and animated GIF are shown directly. Videos (MP4, MOV, AVI and others) play if Windows itself can play them.

HEIC/HEIF/AVIF and camera RAW files are supported in the code, but have not yet been tested with real files. If one shows up blank, please open an issue with a sample.

## Building from source

Requires the .NET 10 SDK on Windows.

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist
```

To build the installer, compile `installer\ImageSorter.iss` with [Inno Setup 6](https://jrsoftware.org/isinfo.php) after publishing; it writes `dist\ImageSorterSetup.exe`.

Built with WPF, [Magick.NET](https://github.com/dlemstra/Magick.NET) and [WpfAnimatedGif](https://github.com/XamlAnimatedGif/WpfAnimatedGif).
