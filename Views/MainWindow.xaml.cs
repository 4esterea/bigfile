using System.Windows;
using Wpf.Ui.Controls;

namespace Warhorse.Views;

/// <summary>
/// Window code-behind. Kept minimal in MVVM — only focus handling lives here.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Puts the caret in the URL box as soon as the prompt appears.</summary>
    private void UrlTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            UrlTextBox.Focus();
        }
    }
}
