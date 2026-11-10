using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace Musterdepot.App.Services;

/// <summary>Dialoge, damit ViewModels ohne direkte WPF-Abhängigkeit testbar bleiben.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message, bool warning = false);
    void Info(string title, string message);
    void Error(string title, string message);
    /// <summary>Speichert Text über einen Datei-Dialog. Liefert den Pfad oder <c>null</c> bei Abbruch.</summary>
    string? SaveText(string suggestedName, string filter, string content, Encoding encoding);
    void CopyToClipboard(string text);
}

public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public bool Confirm(string title, string message, bool warning = false)
    {
        var icon = warning ? MessageBoxImage.Warning : MessageBoxImage.Question;
        var r = Owner is { } o
            ? MessageBox.Show(o, message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        return r == MessageBoxResult.Yes;
    }

    public void Info(string title, string message) => Show(title, message, MessageBoxImage.Information);

    public void Error(string title, string message) => Show(title, message, MessageBoxImage.Error);

    private static void Show(string title, string message, MessageBoxImage icon)
    {
        if (Owner is { } o) MessageBox.Show(o, message, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }

    public string? SaveText(string suggestedName, string filter, string content, Encoding encoding)
    {
        var dlg = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        var ok = Owner is { } o ? dlg.ShowDialog(o) : dlg.ShowDialog();
        if (ok != true) return null;
        File.WriteAllText(dlg.FileName, content, encoding);
        return dlg.FileName;
    }

    public void CopyToClipboard(string text)
    {
        // Die Zwischenablage ist gelegentlich von anderen Programmen gesperrt – kurz wiederholen.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
