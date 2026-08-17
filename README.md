# Bigfile

A Windows reader for text files that are too large to open in an editor: tens of gigabytes on a machine with 16 GB of RAM and no pagefile.

Bigfile never loads the document. It scans the file once to record where every 512th line starts, then reads lines off disk as you scroll. Memory use follows the size of that index, not the size of the file.

Written in WPF on .NET 8. No text-editor component is used, so the virtualization and the search are all part of the project.

![C#](https://img.shields.io/badge/C%23-12-239120)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![WPF](https://img.shields.io/badge/WPF-desktop-0078D4)
![WPF-UI](https://img.shields.io/badge/WPF--UI-4.3-2496ED)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4)
![Tests](https://img.shields.io/badge/tests-49%20passing-brightgreen)


## Overview

- Opens files of any size. The limit is `int.MaxValue` lines, not the amount of RAM.
- Reads from a file on disk, a web address, or a generated document of up to 100,000,000 lines.
- Full-text search that steps through individual occurrences, plus a filter that hides everything else.
- Saves the whole document, or only the filtered lines.
- Indexing, downloading, filtering and saving all report progress and can be cancelled.


## Features

**Reading**
- Only the visible lines become UI elements, and only the visible lines are read from disk, so a file with millions of lines scrolls like a small one
- Scrollbars sit in the layout instead of floating over the text, and the thumb stays big enough to grab in a huge file
- Wheel, thumb and keyboard scrolling are animated at the display's refresh rate instead of the 60 Hz WPF uses by default
- The encoding is detected from the start of the file: UTF-8, ASCII and single-byte Windows code pages, with LF, CRLF or CR line endings

**Search**
- `Ctrl+F` opens the search bar, `F3` and `Shift+F3` step through matches
- A line with several matches is walked one occurrence at a time, and the current occurrence is highlighted differently from the rest
- The view scrolls sideways to the match itself, not just down to its line
- After you scroll away, stepping continues from what is on screen
- Filter mode hides everything except matching lines, up to 1,000,000 of them

**Sources**
- **Open File** for any plain-text file
- **From URL** downloads to a temporary file and reads from there, so the download is not held in memory
- **Random Text** generates a document from a seed without storing it anywhere

**Saving**
- `Ctrl+S` writes the whole text, or the filtered lines while the filter is on
- Output is UTF-8 without a byte order mark, whatever the source encoding was


## Tech stack

| Layer | Technology |
|---|---|
| UI | WPF (.NET 8), XAML, WPF-UI 4.3 Fluent controls |
| Pattern | MVVM with CommunityToolkit.Mvvm source generators |
| Concurrency | `async`/`await`, `Task.Run`, `IProgress<T>`, `CancellationToken` |
| Tests | xUnit, 49 tests over the model layer |
| Packaging | `dotnet publish`, single-file self-contained win-x64 |


## Architecture

`FileTextDocument` scans the file once and stores the byte offset of every 512th line. For a 50 GB file with 500M lines that index is about 8 MB. To fetch a line it seeks to the start of the line's block and decodes from there, and the 64 most recently used blocks stay decoded in memory, so scrolling back over lines just read costs nothing.

On the UI side a `ListBox` over a `VirtualizingStackPanel` builds elements only for the visible rows. Its item source is `VirtualLineList`, an `IList` whose indexer reads a single line from the document. Nothing enumerates the list, so the text is never pulled into memory as a whole.

Search, filter and save go through `ITextDocument.ReadFrom`, a lazy sweep that holds one line at a time. Sweeping 50 GB takes the same memory as sweeping a small file and only costs more time, which is why all of them can be cancelled. `RandomTextDocument` keeps nothing at all: every line is computed from the seed and the line index with SplitMix64, so the same line always comes back the same.


## Project structure

```
├── Models/                     # Plain C#, no WPF dependency. This is what the tests cover
│   ├── ITextDocument.cs        # Line-addressable document contract
│   ├── FileTextDocument.cs     # File on disk: sparse index + LRU block cache
│   ├── RandomTextDocument.cs   # Generated from a seed, stored nowhere
│   ├── WebTextLoader.cs        # URL to temporary file to document
│   ├── VirtualLineList.cs      # IList bridge that reads a line on demand
│   ├── TextSearch.cs           # Occurrence-level search and filtering
│   ├── TextExport.cs           # Streaming save
│   └── ProgressPacing.cs       # Throttles progress reports to the UI thread
│
├── ViewModels/                 # Screen state and commands (CommunityToolkit.Mvvm)
├── Views/                      # MainWindow.xaml and view-only code-behind
├── Behaviors/                  # Attached properties: smooth scrolling, highlighting,
│                               #   thumb sizing, display refresh rate
├── Styles/                     # Buttons and the in-layout scrollbars
│
├── Bigfile.Tests/              # xUnit tests for the model layer
└── Bigfile.slnx                # Solution: app + tests
```


## Getting started

### Prerequisites
- Windows 10 or 11
- .NET 8 SDK to build. The self-contained build needs nothing installed to run

### 1. Build and run

```powershell
dotnet run --project Bigfile.csproj
```

### 2. Run the tests

```powershell
dotnet test Bigfile.slnx -c Release
```

### 3. Publish a single executable

```powershell
dotnet publish Bigfile.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o artifacts\selfcontained
```

That produces one `Bigfile.exe` of about 65 MB that runs on a machine without .NET. Passing `--self-contained false` instead gives a 6.6 MB exe that needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).


## Keyboard

| Shortcut | Action |
|---|---|
| `Ctrl+F` | Open the search bar |
| `F3` / `Shift+F3` | Next / previous match |
| `Enter` / `Shift+Enter` | The same, from inside the search box |
| `Esc` | Close the search bar |
| `Ctrl+S` | Save |
| `Home` / `End` | Start / end of the document |
| `PgUp` / `PgDn` / arrows | Scroll |


## Limitations, and why

- **UTF-16 files are rejected** with a message instead of being misread. The index scan looks for a one-byte line separator, which two-byte encodings break. Files that are not valid UTF-8 are decoded with the system ANSI code page, so they show real characters instead of replacement marks.
- **Display lines are cut at 8 KB** and marked with a trailing `…`. Search and save read a line whole up to 64 MB, so nothing is lost from the file itself: a minified HTML page that is one long line is saved intact.
- **Filtering stops after 1,000,000 matching lines.** The count is then shown with a trailing `+`.
- **The counter shows "N of M" only in filter mode.** Outside it the total is unknown until the whole document has been swept.
