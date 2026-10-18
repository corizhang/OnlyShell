using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OnlyShell.Presentation.ViewModels;

namespace OnlyShell.App.Controls;

public partial class ServerMetricsView : UserControl
{
    private TerminalTabViewModel? _viewModel;

    public ServerMetricsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        TrendCanvas.SizeChanged += (_, _) => DrawTrend();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.MetricsHistory.CollectionChanged -= OnMetricsHistoryChanged;
        _viewModel = e.NewValue as TerminalTabViewModel;
        if (_viewModel is not null)
            _viewModel.MetricsHistory.CollectionChanged += OnMetricsHistoryChanged;
        DrawTrend();
    }

    private void OnMetricsHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e) => DrawTrend();

    private void DrawTrend()
    {
        if (TrendCanvas is null)
            return;

        TrendCanvas.Children.Clear();
        var width = TrendCanvas.ActualWidth;
        var height = TrendCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        var gridBrush = new SolidColorBrush(Color.FromRgb(43, 42, 49));
        foreach (var fraction in new[] { 0.0, 0.5, 1.0 })
        {
            var y = Math.Round(height * fraction) + 0.5;
            TrendCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            });
        }

        var samples = _viewModel?.MetricsHistory;
        if (samples is null || samples.Count == 0)
            return;

        var networkMax = Math.Max(1, samples.Max(sample => sample.NetworkBytesPerSecond));
        AddSeries(samples.Select(sample => sample.CpuPercent), Color.FromRgb(235, 170, 48), width, height, 100);
        AddSeries(samples.Select(sample => sample.MemoryPercent), Color.FromRgb(85, 207, 229), width, height, 100);
        AddSeries(samples.Select(sample => sample.DiskPercent), Color.FromRgb(91, 220, 144), width, height, 100);
        AddSeries(samples.Select(sample => sample.NetworkBytesPerSecond), Color.FromRgb(181, 140, 255), width, height, networkMax);
    }

    private void AddSeries(IEnumerable<double> values, Color color, double width, double height, double maximum)
    {
        var points = values.ToArray();
        if (points.Length == 0)
            return;

        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.4,
            SnapsToDevicePixels = true
        };
        for (var index = 0; index < points.Length; index++)
        {
            var x = points.Length == 1 ? width : width * index / (points.Length - 1);
            var normalized = Math.Clamp(points[index] / maximum, 0, 1);
            polyline.Points.Add(new System.Windows.Point(x, height - (normalized * (height - 2)) - 1));
        }
        TrendCanvas.Children.Add(polyline);
    }
}
