using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;

namespace UniversalWebViewFit;

/// <summary>Read-only document measurements; native zoom owns all scaling.</summary>
internal sealed class FitToWindow : IDisposable
{
    private readonly WebView2 view;
    private readonly System.Windows.Forms.Timer debounce = new() { Interval = 150 };
    private bool ready, running, disposed;
    private int revision;
    private bool fitEntirePage;

    internal bool FitEntirePage
    {
        get => fitEntirePage;
        set { if (fitEntirePage == value) return; fitEntirePage = value; Schedule(); }
    }

    private const string Measure = """
        (() => {
            const d = document.documentElement, b = document.body;
            return {
                width: Math.max(d?.scrollWidth || 0, b?.scrollWidth || 0),
                height: Math.max(d?.scrollHeight || 0, b?.scrollHeight || 0),
                viewportWidth: innerWidth,
                viewportHeight: innerHeight,
                pixelRatio: devicePixelRatio
            };
        })()
        """;

    internal FitToWindow(WebView2 view)
    {
        this.view = view;
        view.CoreWebView2.Settings.IsZoomControlEnabled = false;
        view.SizeChanged += OnSizeChanged;
        view.CoreWebView2.NavigationStarting += OnNavigationStarting;
        view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        view.CoreWebView2.ContainsFullScreenElementChanged += OnFullScreenElementChanged;
        debounce.Tick += OnTick;
    }

    private void OnSizeChanged(object? sender, EventArgs e) => Schedule();
    private void OnFullScreenElementChanged(object? sender, object e) => Schedule();

    private void OnNavigationStarting(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        ready = false;
        revision++;
        debounce.Stop();
    }

    private void OnNavigationCompleted(object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        ready = e.IsSuccess;
        Schedule();
    }

    internal void Schedule()
    {
        revision++;
        if (disposed || !ready) return;
        debounce.Stop();
        debounce.Start();
    }

    private bool Current(int ticket) =>
        !disposed && ready && ticket == revision && !view.IsDisposed &&
        view.CoreWebView2 != null && view.ClientSize.Width > 0 && view.ClientSize.Height > 0;

    private async void OnTick(object? sender, EventArgs e)
    {
        debounce.Stop();
        if (running) { debounce.Start(); return; }
        int ticket = revision;
        if (!Current(ticket) || view.Source is not { Scheme: "http" or "https" }) return;
        running = true;
        try
        {
            // Never measure against a previous resize's zoom.
            view.ZoomFactor = 1.0;
            // The page/player owns its fullscreen layout. Do not fit the background document.
            if (view.CoreWebView2.ContainsFullScreenElement) return;
            await Task.Delay(60);
            for (int pass = 0; pass < 5 && Current(ticket); pass++)
            {
                string json = await view.ExecuteScriptAsync(Measure);
                if (!Current(ticket)) return;
                using var document = JsonDocument.Parse(json);
                var dimensions = document.RootElement;
                double width = dimensions.GetProperty("width").GetDouble();
                double height = dimensions.GetProperty("height").GetDouble();
                double viewportWidth = dimensions.GetProperty("viewportWidth").GetDouble();
                double viewportHeight = dimensions.GetProperty("viewportHeight").GetDouble();
                double pixelRatio = dimensions.GetProperty("pixelRatio").GetDouble();
                if (width <= 0 || height <= 0 || pixelRatio <= 0) return;
                // ClientSize is physical pixels; document measurements are CSS pixels.
                double availableWidth = Math.Min(viewportWidth, view.ClientSize.Width / pixelRatio);
                double availableHeight = Math.Min(viewportHeight, view.ClientSize.Height / pixelRatio);
                if (availableWidth <= 0 || availableHeight <= 0) return;
                if (width <= availableWidth + 1 && (!FitEntirePage || height <= availableHeight + 1)) break;
                double scale = Math.Clamp(view.ZoomFactor *
                    (FitEntirePage ? Math.Min(availableWidth / width, availableHeight / height) : availableWidth / width), 0.25, 1.0);
                if (Math.Abs(scale - view.ZoomFactor) < 0.001) break;
                view.ZoomFactor = scale;
                await Task.Delay(60);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.Runtime.InteropServices.COMException or JsonException or
            ObjectDisposedException or KeyNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine($"FitToWindow: {ex.Message}");
        }
        finally { running = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        revision++;
        debounce.Stop();
        debounce.Dispose();
        view.SizeChanged -= OnSizeChanged;
        if (!view.IsDisposed && view.CoreWebView2 != null)
        {
            view.CoreWebView2.NavigationStarting -= OnNavigationStarting;
            view.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            view.CoreWebView2.ContainsFullScreenElementChanged -= OnFullScreenElementChanged;
        }
    }
}