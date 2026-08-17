using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections;
using System.IO;
using System.Windows;
using Bigfile.Models;

namespace Bigfile.ViewModels;

/// <summary>
/// Main window ViewModel. Three text sources per the assignment.
/// The text itself is never held here — only a virtual list over the
/// document that reads lines from disk on demand.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>Matching lines the filter collects at most; four bytes each.</summary>
    private const int MaxFilterMatches = 1_000_000;

    /// <summary>Lines a generated document starts out with.</summary>
    private const int DefaultRandomLines = 500_000;

    /// <summary>Bounds offered for a generated document.</summary>
    private const int MinRandomLines = 1;
    private const int MaxRandomLines = 100_000_000;

    private ITextDocument? _document;

    /// <summary>
    /// Cancels the operation the busy overlay is showing — loading, saving or
    /// filtering. Indexing tens of gigabytes takes minutes, so every one of them
    /// has to be abandonable from the overlay.
    /// </summary>
    private CancellationTokenSource? _busyCts;

    /// <summary>Cancels a background find, which the next find supersedes.</summary>
    private CancellationTokenSource? _searchCts;

    /// <summary>The filtered view while it is shown, for mapping rows back.</summary>
    private VirtualLineList? _filtered;

    /// <summary>Virtual list of lines, or null while the start screen is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(ShowStartButtons))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenSearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFilterCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindPreviousCommand))]
    private IList? _lines;

    /// <summary>Name of the current source, shown in the reader header.</summary>
    [ObservableProperty]
    private string? _sourceName;

    /// <summary>Number of lines in the open document.</summary>
    [ObservableProperty]
    private int _lineCount;

    /// <summary>
    /// True while a source is being loaded, saved or filtered. Everything the
    /// overlay covers is disabled while it is up, so two sweeps of the same
    /// document can never overlap.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenUrlCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRandomCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFilterCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(FindPreviousCommand))]
    private bool _isBusy;

    /// <summary>Load progress from 0 to 100, for the progress bar.</summary>
    [ObservableProperty]
    private double _progressPercent;

    /// <summary>What the progress overlay is waiting for.</summary>
    [ObservableProperty]
    private string _busyMessage = string.Empty;

    /// <summary>True while the inline URL prompt is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartButtons))]
    private bool _isUrlPromptOpen;

    /// <summary>Address typed into the URL prompt.</summary>
    [ObservableProperty]
    private string _urlInput = string.Empty;

    /// <summary>True when the typed address failed validation.</summary>
    [ObservableProperty]
    private bool _hasUrlError;

    /// <summary>True while the generated-text prompt is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartButtons))]
    private bool _isRandomPromptOpen;

    /// <summary>Lines to generate, as typed into the prompt.</summary>
    [ObservableProperty]
    private double? _randomLineInput = DefaultRandomLines;

    /// <summary>True when the typed line count is out of range.</summary>
    [ObservableProperty]
    private bool _hasRandomError;

    /// <summary>
    /// Range hint shown under the line count box. An instance property because
    /// a plain binding path cannot reach a static one.
    /// </summary>
    public string RandomLineRange =>
        $"Enter a number between {MinRandomLines:N0} and {MaxRandomLines:N0}.";

    /// <summary>True while the search bar is shown.</summary>
    [ObservableProperty]
    private bool _isSearchOpen;

    /// <summary>Text typed into the search box.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleFilterCommand))]
    private string _searchQuery = string.Empty;

    /// <summary>True while only the matching lines are shown.</summary>
    [ObservableProperty]
    private bool _isFilterActive;

    /// <summary>
    /// Offset of the current match inside its line, or -1 when there is none.
    /// It marks the occurrence on screen and is where the next step starts, so a
    /// line with several matches is walked one at a time.
    /// </summary>
    [ObservableProperty]
    private int _matchColumn = -1;

    /// <summary>True when the filter stopped at <see cref="MaxFilterMatches"/>.</summary>
    private bool _filterCapped;

    /// <summary>
    /// Line selected in the reader, or -1. It doubles as the search cursor:
    /// clicking a line or landing on a match is where the next search starts.
    /// </summary>
    [ObservableProperty]
    private int _selectedLine = -1;

    /// <summary>Outcome of the last search, shown beside the search box.</summary>
    [ObservableProperty]
    private string _searchStatus = string.Empty;

    /// <summary>
    /// What the status leaves unsaid, as its tooltip — currently only that the
    /// filter stopped at its limit, which the trailing plus alone would not
    /// explain.
    /// </summary>
    [ObservableProperty]
    private string? _searchHint;

    /// <summary>
    /// Asks the view to bring a match into view, by line and column. Scrolling is
    /// a view concern and cannot be expressed as state — the same match can be
    /// found twice.
    /// </summary>
    public event Action<int, int>? MatchFound;

    /// <summary>Asks the view to put the caret in the search box.</summary>
    public event Action? SearchFocusRequested;

    /// <summary>
    /// Rows the reader currently shows, supplied by the view. The selection is
    /// the search cursor, but once the user has scrolled away from it, carrying
    /// on from a line they can no longer see would be surprising — so searching
    /// falls back to the viewport.
    /// </summary>
    public Func<(int First, int Last)?>? VisibleRows { get; set; }

    /// <summary>True once a source has been loaded.</summary>
    public bool HasText => Lines is not null;

    /// <summary>
    /// The source buttons are hidden behind the URL prompt so they do not
    /// show through its translucent card. The progress overlay is opaque, so
    /// they stay put under it — dimmed, but where the user left them.
    /// </summary>
    public bool ShowStartButtons =>
        Lines is null && !IsUrlPromptOpen && !IsRandomPromptOpen;

    /// <summary>A new source can only be opened while nothing else is running.</summary>
    private bool CanLoad => !IsBusy;

    /// <summary>
    /// Working on the open document needs one to be open and no sweep of it in
    /// flight, so saving and searching cannot race the overlay.
    /// </summary>
    private bool CanUseDocument => HasText && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open text file",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FileName;
        await LoadAsync(
            Path.GetFileName(path),
            (p, token) => FileTextDocument.OpenAsync(path, p, token));
    }

    /// <summary>Opens the inline URL prompt on the start screen.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private void OpenUrl()
    {
        UrlInput = string.Empty;
        HasUrlError = false;
        IsUrlPromptOpen = true;
    }

    [RelayCommand]
    private void CancelUrlPrompt()
    {
        IsUrlPromptOpen = false;
        HasUrlError = false;
    }

    /// <summary>Validates the typed address and loads it.</summary>
    [RelayCommand]
    private async Task SubmitUrlAsync()
    {
        if (!WebTextLoader.TryParse(UrlInput, out var uri))
        {
            HasUrlError = true;
            return;
        }

        IsUrlPromptOpen = false;
        HasUrlError = false;

        await LoadAsync(
            WebTextLoader.NameOf(uri),
            (p, token) => WebTextLoader.OpenAsync(uri, p, token));
    }

    /// <summary>Opens the inline prompt asking how many lines to generate.</summary>
    [RelayCommand(CanExecute = nameof(CanLoad))]
    private void OpenRandom()
    {
        RandomLineInput = DefaultRandomLines;
        HasRandomError = false;
        IsRandomPromptOpen = true;
    }

    [RelayCommand]
    private void CancelRandomPrompt()
    {
        IsRandomPromptOpen = false;
        HasRandomError = false;
    }

    /// <summary>
    /// Generates a document of the requested size. Nothing is written or
    /// indexed, so it goes through the shared path only for the plumbing.
    /// </summary>
    [RelayCommand]
    private async Task SubmitRandomAsync()
    {
        if (RandomLineInput is not { } value ||
            value < MinRandomLines ||
            value > MaxRandomLines)
        {
            HasRandomError = true;
            return;
        }

        var lineCount = (int)Math.Round(value);

        IsRandomPromptOpen = false;
        HasRandomError = false;

        await LoadAsync(
            "Random text",
            (_, _) => Task.FromResult<ITextDocument>(new RandomTextDocument(lineCount)));
    }

    /// <summary>
    /// Writes the reader's contents to a file. While the filter is on that is
    /// the matching lines only, which is the useful half of filtering.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private async Task SaveAsync()
    {
        if (_document is not { } document)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save text",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = IsFilterActive ? "filtered.txt" : "text.txt",
            DefaultExt = ".txt"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FileName;
        var lines = _filtered?.SourceLines;

        var cts = BeginBusy("Saving");

        try
        {
            await Task.Run(
                () => TextExport.Save(document, path, lines, BusyProgress(), cts.Token),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A half-written file is worse than none, so the cancelled output goes.
            TryDelete(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not save to \"{Path.GetFileName(path)}\".\n\n{ex.Message}",
                "Text Reader",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            EndBusy(cts);
        }
    }

    /// <summary>Removes an output file that was abandoned midway.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Nothing useful to do if the partial file cannot be removed.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Raises the busy overlay and returns the source its Cancel button cancels.
    /// </summary>
    private CancellationTokenSource BeginBusy(string message)
    {
        var cts = new CancellationTokenSource();

        _busyCts = cts;
        BusyMessage = message;
        ProgressPercent = 0;
        IsBusy = true;

        return cts;
    }

    /// <summary>Drops the overlay and retires the source raised with it.</summary>
    private void EndBusy(CancellationTokenSource cts)
    {
        if (_busyCts == cts)
        {
            _busyCts = null;
        }

        cts.Dispose();
        IsBusy = false;
    }

    /// <summary>Channel the overlay's ring follows, in percent.</summary>
    private IProgress<double> BusyProgress() =>
        new Progress<double>(value => ProgressPercent = value * 100);

    /// <summary>Abandons whatever the overlay is showing.</summary>
    [RelayCommand]
    private void CancelBusy() => _busyCts?.Cancel();

    /// <summary>Shows the search bar and puts the caret in it.</summary>
    [RelayCommand(CanExecute = nameof(HasText))]
    private void OpenSearch()
    {
        IsSearchOpen = true;
        SearchFocusRequested?.Invoke();
    }

    /// <summary>
    /// Hides the search bar. The query goes with it so the highlighting does
    /// not outlive the box that explains it.
    /// </summary>
    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        SearchQuery = string.Empty;
        SearchStatus = string.Empty;
        SearchHint = null;
        MatchColumn = -1;
        CancelSearch();
    }

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private Task FindNextAsync() => FindAsync(forward: true);

    [RelayCommand(CanExecute = nameof(CanUseDocument))]
    private Task FindPreviousAsync() => FindAsync(forward: false);

    /// <summary>
    /// Searches from the current match outwards, occurrence by occurrence and
    /// wrapping around the document. The sweep runs on the thread pool because it
    /// can take minutes on a large file; only the code after the await touches
    /// bound properties, and that is back on the UI thread.
    /// </summary>
    private async Task FindAsync(bool forward)
    {
        if (_document is not { } document || string.IsNullOrEmpty(SearchQuery))
        {
            return;
        }

        // Every filtered row is a match already, so stepping needs no sweep.
        if (IsFilterActive)
        {
            StepFilter(forward);
            return;
        }

        // A new search supersedes one that is still running.
        CancelSearch();

        var cts = new CancellationTokenSource();
        _searchCts = cts;

        var query = SearchQuery;
        var (line, column) = SearchOrigin(forward);

        IsSearchOpen = true;
        SearchStatus = "Searching…";

        try
        {
            var hit = await Task.Run(
                () => forward
                    ? TextSearch.FindNext(document, query, line, column, cts.Token)
                    : TextSearch.FindPrevious(document, query, line, column, cts.Token),
                cts.Token);

            if (!hit.Found)
            {
                SearchStatus = "No matches";
                return;
            }

            // Outside the filter there is no honest total to show: counting the
            // matches of a 50 GB document would mean sweeping all of it.
            SearchStatus = $"Line {hit.Line + 1:N0}";

            // The selection is assigned first: it resets the match column, so
            // that a plain click on a line starts a fresh walk of that line.
            SelectedLine = hit.Line;
            MatchColumn = hit.Column;
            MatchFound?.Invoke(hit.Line, hit.Column);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search, or the document was closed.
        }
        finally
        {
            if (_searchCts == cts)
            {
                _searchCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Position a step starts from: just past the current match while it is on
    /// screen, otherwise the edge of the viewport the step moves away from.
    /// </summary>
    private (int Line, int Column) SearchOrigin(bool forward)
    {
        var rows = VisibleRows?.Invoke();
        var onScreen = rows is not { } visible
            ? SelectedLine >= 0
            : SelectedLine >= visible.First && SelectedLine <= visible.Last;

        if (onScreen)
        {
            return MatchColumn >= 0
                ? (SelectedLine, forward ? MatchColumn + 1 : MatchColumn - 1)
                : (SelectedLine, forward ? 0 : int.MaxValue);
        }

        if (rows is not { } row)
        {
            // No viewport to fall back on: start at the end the step comes from.
            return forward ? (0, 0) : (LineCount - 1, int.MaxValue);
        }

        return forward ? (row.First, 0) : (row.Last, int.MaxValue);
    }

    private void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
    }

    /// <summary>
    /// A click on a line ends the walk of the previous match, so the next step
    /// searches that line from its start rather than from a stale offset.
    /// </summary>
    partial void OnSelectedLineChanged(int value) => MatchColumn = -1;

    /// <summary>
    /// Steps to the next or previous occurrence of the filtered view, wrapping
    /// around. Occurrences within the current row come first, and only when the
    /// row runs out does the step move to another one. No sweep is needed: every
    /// row is a match, and the reader can read a single row cheaply.
    /// </summary>
    private void StepFilter(bool forward)
    {
        if (_filtered is not { Count: > 0 } filtered)
        {
            return;
        }

        var (line, column) = SearchOrigin(forward);
        var row = line >= 0 && line < filtered.Count
            ? line
            : forward ? 0 : filtered.Count - 1;

        var found = forward
            ? TextSearch.IndexIn(filtered[row], SearchQuery, column)
            : TextSearch.LastIndexIn(filtered[row], SearchQuery, column);

        if (found == TextSearch.NotFound)
        {
            row += forward ? 1 : -1;

            if (row < 0)
            {
                row = filtered.Count - 1;
            }
            else if (row >= filtered.Count)
            {
                row = 0;
            }

            found = forward
                ? TextSearch.IndexIn(filtered[row], SearchQuery, 0)
                : TextSearch.LastIndexIn(filtered[row], SearchQuery, int.MaxValue);
        }

        SelectedLine = row;
        MatchColumn = found;
        SearchStatus = FilterStatus(row);
        MatchFound?.Invoke(row, Math.Max(found, 0));
    }

    /// <summary>
    /// Which match of how many the reader is on. The total is only known while
    /// the filter is on — counting matches otherwise would mean sweeping the
    /// whole document for a number nobody asked for. A trailing plus means the
    /// filter stopped at its limit and more matches exist.
    /// </summary>
    private string FilterStatus(int row) =>
        $"{row + 1:N0} of {LineCount:N0}{(_filterCapped ? "+" : string.Empty)}";

    private bool CanToggleFilter => CanUseDocument && !string.IsNullOrEmpty(SearchQuery);

    /// <summary>
    /// Replaces the reader with the matching lines only, or restores the whole
    /// document when the filter is already on.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleFilter))]
    private async Task ToggleFilterAsync()
    {
        if (_document is not { } document)
        {
            return;
        }

        if (IsFilterActive)
        {
            ClearFilter();
            return;
        }

        CancelSearch();

        var query = SearchQuery;
        var cts = BeginBusy("Filtering");

        try
        {
            // Unlike a jump to the next match, this one has to read the whole
            // document, so it reports progress and can be cancelled.
            var matches = await Task.Run(
                () => TextSearch.FindAll(
                    document, query, MaxFilterMatches, BusyProgress(), cts.Token),
                cts.Token);

            if (matches.Length == 0)
            {
                SearchStatus = "No matches";
                return;
            }

            _filtered = new VirtualLineList(document, matches);
            _filterCapped = matches.Length == MaxFilterMatches;

            Lines = _filtered;
            LineCount = matches.Length;
            SelectedLine = 0;
            MatchColumn = TextSearch.IndexIn(_filtered[0], query, 0);
            IsFilterActive = true;

            SearchStatus = FilterStatus(0);
            SearchHint = _filterCapped
                ? $"The filter stopped at its limit of {MaxFilterMatches:N0} matching lines; "
                  + "the document holds more."
                : null;

            MatchFound?.Invoke(0, Math.Max(MatchColumn, 0));
        }
        catch (OperationCanceledException)
        {
            SearchStatus = string.Empty;
        }
        finally
        {
            EndBusy(cts);

            // Clicking the toggle checks it on its own. When the filter did not
            // end up on — no matches, or cancelled — IsFilterActive never
            // changed, so the binding has to be pushed back to unstick it.
            OnPropertyChanged(nameof(IsFilterActive));
        }
    }

    /// <summary>
    /// Restores the full document, leaving the reader on the line the selected
    /// row came from rather than back at the top.
    /// </summary>
    private void ClearFilter()
    {
        if (_document is not { } document || !IsFilterActive)
        {
            return;
        }

        var line = _filtered is { } filtered && SelectedLine >= 0 && SelectedLine < filtered.Count
            ? filtered.SourceLine(SelectedLine)
            : -1;

        // The offset within the line survives the switch, so the match stays
        // marked where the filtered row left off.
        var column = MatchColumn;

        _filtered = null;
        _filterCapped = false;
        IsFilterActive = false;

        // Swapping the list resets the selection, so it is restored after.
        Lines = new VirtualLineList(document);
        LineCount = document.LineCount;
        SelectedLine = line;
        SearchStatus = string.Empty;
        SearchHint = null;

        if (line >= 0)
        {
            MatchColumn = column;
            MatchFound?.Invoke(line, Math.Max(column, 0));
        }
    }

    /// <summary>
    /// The filtered view is a snapshot of one query, so a new query drops it
    /// rather than leaving stale rows on screen.
    /// </summary>
    partial void OnSearchQueryChanged(string value)
    {
        if (IsFilterActive)
        {
            ClearFilter();
        }
    }

    /// <summary>Closes the document and returns to the start screen.</summary>
    [RelayCommand]
    private void Close()
    {
        CancelSearch();
        _busyCts?.Cancel();

        IsSearchOpen = false;
        SearchStatus = string.Empty;
        SelectedLine = -1;
        MatchColumn = -1;
        IsFilterActive = false;
        _filtered = null;
        _filterCapped = false;

        Lines = null;
        SourceName = null;
        LineCount = 0;

        _document?.Dispose();
        _document = null;
    }

    /// <summary>
    /// Shared plumbing for every source: progress reporting, cancellation,
    /// swapping in the new document and reporting failures.
    /// </summary>
    private async Task LoadAsync(
        string name,
        Func<IProgress<double>, CancellationToken, Task<ITextDocument>> open)
    {
        var cts = BeginBusy("Indexing");

        try
        {
            var document = await open(BusyProgress(), cts.Token);

            // Only once the new document is in hand is the old one let go, so a
            // failed or cancelled load leaves the reader as it was.
            Close();
            _document = document;
            Lines = new VirtualLineList(document);
            LineCount = document.LineCount;
            SourceName = name;
        }
        catch (OperationCanceledException)
        {
            // Cancelled from the overlay; the partial index is simply dropped.
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open \"{name}\".\n\n{ex.Message}",
                "Text Reader",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            EndBusy(cts);
        }
    }
}
