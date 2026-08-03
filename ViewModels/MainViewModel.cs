using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using Warhorse.Models;

namespace Warhorse.ViewModels;

/// <summary>
/// Main window ViewModel. Three text sources per the assignment.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    /// <summary>Loaded text, or null while the start screen is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? _text;

    /// <summary>Name of the current source, shown in the reader header.</summary>
    [ObservableProperty]
    private string? _sourceName;

    /// <summary>True once a source has been loaded.</summary>
    public bool HasText => Text is not null;

    /// <summary>True while the start screen is shown.</summary>
    public bool IsEmpty => Text is null;

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

        await LoadAsync(new FileTextSource(dialog.FileName));
    }

    [RelayCommand]
    private void OpenUrl()
    {
        // TODO: download from URL and create a WebTextSource
        Debug.WriteLine("OpenUrl command executed");
    }

    [RelayCommand]
    private void OpenRandom()
    {
        // TODO: create a RandomTextSource
        Debug.WriteLine("OpenRandom command executed");
    }

    /// <summary>Returns to the start screen.</summary>
    [RelayCommand]
    private void Close()
    {
        Text = null;
        SourceName = null;
    }

    private async Task LoadAsync(ITextSource source)
    {
        try
        {
            Text = await source.LoadAsync();
            SourceName = source.Name;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not open \"{source.Name}\".\n\n{ex.Message}",
                "Text Reader",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
