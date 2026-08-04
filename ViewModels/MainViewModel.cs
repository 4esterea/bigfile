using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Warhorse.Models;

namespace Warhorse.ViewModels;

/// <summary>
/// Main window ViewModel. Three text sources per the assignment.
/// The text itself is never held here — only a virtual list over the
/// document that reads lines from disk on demand.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private ITextDocument? _document;

    /// <summary>Virtual list of lines, or null while the start screen is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(ShowStartButtons))]
    private IList? _lines;

    /// <summary>Name of the current source, shown in the reader header.</summary>
    [ObservableProperty]
    private string? _sourceName;

    /// <summary>Number of lines in the open document.</summary>
    [ObservableProperty]
    private int _lineCount;

    /// <summary>True while a source is being loaded.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Load progress from 0 to 100, for the progress bar.</summary>
    [ObservableProperty]
    private double _progressPercent;

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

    /// <summary>True once a source has been loaded.</summary>
    public bool HasText => Lines is not null;

    /// <summary>
    /// The source buttons are hidden behind the URL prompt so they do not
    /// show through its translucent card.
    /// </summary>
    public bool ShowStartButtons => Lines is null && !IsUrlPromptOpen;

    [RelayCommand]
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
        await LoadAsync(Path.GetFileName(path), p => FileTextDocument.OpenAsync(path, p));
    }

    /// <summary>Opens the inline URL prompt on the start screen.</summary>
    [RelayCommand]
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

        await LoadAsync(WebTextLoader.NameOf(uri), p => WebTextLoader.OpenAsync(uri, p));
    }

    [RelayCommand]
    private void OpenRandom()
    {
        // TODO: create a RandomTextDocument
        Debug.WriteLine("OpenRandom command executed");
    }

    /// <summary>Closes the document and returns to the start screen.</summary>
    [RelayCommand]
    private void Close()
    {
        Lines = null;
        SourceName = null;
        LineCount = 0;

        _document?.Dispose();
        _document = null;
    }

    /// <summary>
    /// Shared plumbing for every source: progress reporting, swapping in the
    /// new document and reporting failures.
    /// </summary>
    private async Task LoadAsync(string name, Func<IProgress<double>, Task<ITextDocument>> open)
    {
        IsBusy = true;
        ProgressPercent = 0;

        var progress = new Progress<double>(value => ProgressPercent = value * 100);

        try
        {
            var document = await open(progress);

            Close();
            _document = document;
            Lines = new VirtualLineList(document);
            LineCount = document.LineCount;
            SourceName = name;
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
            IsBusy = false;
        }
    }
}
