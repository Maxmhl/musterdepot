using System.Windows.Controls;
using Musterdepot.App.Services;
using Musterdepot.App.ViewModels;

namespace Musterdepot.App.Views;

/// <summary>Depot mit Equity-Kurve (ScottPlot). Das Zeichnen ist reine View-Logik, die Daten kommen aus dem ViewModel.</summary>
public partial class PortfolioView : UserControl
{
    private PortfolioViewModel? _vm;
    private bool _dark;
    private bool _themeHooked;

    public PortfolioView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.EquityCurveChanged -= OnCurveChanged;
            _vm = e.NewValue as PortfolioViewModel;
            if (_vm is not null) _vm.EquityCurveChanged += OnCurveChanged;
            Redraw();
        };
        Loaded += (_, _) =>
        {
            if (!_themeHooked && TryGetTheme() is { } theme)
            {
                _themeHooked = true;
                _dark = theme.IsDark;
                theme.ThemeChanged += (_, dark) => { _dark = dark; Redraw(); };
            }
            Redraw();
        };
    }

    private static ThemeService? TryGetTheme() => AppServiceLocator.Get<ThemeService>();

    private void OnCurveChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        var plot = EquityPlot.Plot;
        plot.Clear();

        var bg = _dark ? ScottPlot.Color.FromHex("#1F2227") : ScottPlot.Colors.White;
        var fg = _dark ? ScottPlot.Color.FromHex("#A0A7B1") : ScottPlot.Color.FromHex("#5B6370");
        var grid = _dark ? ScottPlot.Color.FromHex("#2A2E35") : ScottPlot.Color.FromHex("#EEF0F3");
        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color = bg;
        plot.Axes.Color(fg);
        plot.Grid.MajorLineColor = grid;

        var curve = _vm?.EquityCurve ?? [];
        if (curve.Count >= 1)
        {
            var xs = curve.Select(p => p.TimestampUtc.LocalDateTime.ToOADate()).ToArray();
            var ys = curve.Select(p => (double)p.Equity).ToArray();
            if (xs.Length == 1)
            {
                xs = [xs[0] - 1.0 / 24, xs[0]];
                ys = [ys[0], ys[0]];
            }
            var line = plot.Add.Scatter(xs, ys);
            line.Color = _dark ? ScottPlot.Color.FromHex("#7AA2FF") : ScottPlot.Color.FromHex("#1E40AF");
            line.LineWidth = 2;
            line.MarkerSize = 0;
            plot.Axes.DateTimeTicksBottom();
            plot.Axes.AutoScale();
        }
        else
        {
            var text = plot.Add.Text("Noch keine Equity-Daten", 0, 0);
            text.LabelFontColor = fg;
            text.LabelFontSize = 14;
            text.LabelAlignment = ScottPlot.Alignment.MiddleCenter;
            plot.Axes.SetLimits(-1, 1, -1, 1);
        }
        EquityPlot.Refresh();
    }
}
