using System.Windows;

namespace Musterdepot.App.Views;

/// <summary>Kleines Always-on-top-Fenster mit Erlauben/Ablehnen, unten rechts über dem Infobereich.</summary>
public partial class ApprovalWindow : Window
{
    public ApprovalWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PlaceBottomRight();
        SizeChanged += (_, _) => PlaceBottomRight();
    }

    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 16;
        Top = area.Bottom - ActualHeight - 16;
    }
}
