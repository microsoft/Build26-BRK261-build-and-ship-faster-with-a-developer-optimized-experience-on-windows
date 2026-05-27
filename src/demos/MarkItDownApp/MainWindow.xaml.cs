using System.Diagnostics;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using MarkItDownApp.Services;
using WinRT.Interop;

namespace MarkItDownApp;

public enum StatusState { Dim, Pending, Ready, Warning, Error }

public sealed partial class MainWindow : Window
{
    private ContainerService? _containerService;
    private MarkItDownClient? _apiClient;
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherTimer? _healthCheckTimer;

    // Log display
    private readonly StringBuilder _logBuffer = new();
    private readonly object _logLock = new();
    private readonly StringBuilder _pendingOutput = new();
    private const int MaxLogChars = 50_000;

    // Current conversion result
    private string? _currentMarkdown;
    private string? _currentFileName;
    private bool _previewReady;

    private bool _closed;
    private bool _convertActive = true;
    private bool _containerReady;

    public MainWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine("[MainWindow] OnLoaded entered");
        try
        {
            Title = "MarkItDown";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _apiClient = new MarkItDownClient();

            NavView.SelectedItem = ConvertNavItem;

            StartContainerAsync();
            Debug.WriteLine("[MainWindow] OnLoaded done");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] OnLoaded EXCEPTION: 0x{ex.HResult:X8} {ex.Message}");
        }
    }

    // ========== Navigation ==========

    private void NavView_SelectionChanged(NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_closed) return;

        try
        {
            if (args.IsSettingsSelected) return;

            var selectedItem = args.SelectedItemContainer;
            if (selectedItem is not NavigationViewItem navItem) return;

            var tag = navItem.Tag?.ToString();
            ConvertPanel.Visibility = tag == "Convert" ? Visibility.Visible : Visibility.Collapsed;
            LogPanel.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;

            _convertActive = tag == "Convert";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] NavView_SelectionChanged exception: {ex.Message}");
        }
    }

    // ========== Drag & Drop ==========

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride != null)
        {
            e.DragUIOverride.Caption = "Convert to Markdown";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_closed) return;

        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count == 0) return;

            var file = items[0] as StorageFile;
            if (file == null) return;

            await ConvertFileAsync(file.Path, file.Name);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] OnDrop exception: {ex.Message}");
            ShowError($"Failed to process dropped file: {ex.Message}");
        }
    }

    // ========== File Picker ==========

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_closed) return;

        try
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.List;
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await ConvertFileAsync(file.Path, file.Name);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] OnBrowseClick exception: {ex.Message}");
            ShowError($"Failed to open file picker: {ex.Message}");
        }
    }

    // ========== Conversion ==========

    private async Task ConvertFileAsync(string filePath, string fileName)
    {
        if (_closed || _apiClient == null) return;

        if (!_containerReady)
        {
            ShowError("Container is not ready yet. Please wait for it to start.");
            return;
        }

        // Show progress
        DropZonePanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = $"Converting {fileName}...";
        ConvertHeaderText.Text = $"Converting — {fileName}";

        try
        {
            var result = await _apiClient.ConvertFileAsync(filePath);

            if (_closed) return;

            _currentMarkdown = result.Markdown;
            _currentFileName = fileName;

            // Show result
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
            CopyButton.Visibility = Visibility.Visible;
            SaveButton.Visibility = Visibility.Visible;

            MarkdownOutput.Text = result.Markdown;
            ResultInfoText.Text = $"{result.Filename} — {FormatBytes(result.Bytes)} — {result.DurationMs}ms";
            ConvertHeaderText.Text = $"Converted — {result.Filename}";

            // Render preview
            await RenderPreviewAsync(result.Markdown);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] ConvertFileAsync exception: {ex.Message}");

            ProgressPanel.Visibility = Visibility.Collapsed;
            DropZonePanel.Visibility = Visibility.Visible;
            ConvertHeaderText.Text = "Document Converter";

            ShowError($"Conversion failed: {ex.Message}");
        }
    }

    // ========== Copy / Save ==========

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentMarkdown)) return;

        try
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(_currentMarkdown);
            Clipboard.SetContent(dataPackage);

            // Brief feedback
            ConvertHeaderText.Text = "Copied to clipboard ✅";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_closed)
                    ConvertHeaderText.Text = $"Converted — {_currentFileName}";
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] OnCopyClick exception: {ex.Message}");
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentMarkdown)) return;

        try
        {
            var picker = new FileSavePicker();
            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
            picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });

            var baseName = Path.GetFileNameWithoutExtension(_currentFileName ?? "converted");
            picker.SuggestedFileName = baseName;

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await FileIO.WriteTextAsync(file, _currentMarkdown);

                ConvertHeaderText.Text = $"Saved to {file.Name} ✅";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (!_closed)
                        ConvertHeaderText.Text = $"Converted — {_currentFileName}";
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] OnSaveClick exception: {ex.Message}");
            ShowError($"Failed to save file: {ex.Message}");
        }
    }

    // ========== Markdown Preview ==========

    private async Task RenderPreviewAsync(string markdown)
    {
        try
        {
            if (!_previewReady)
            {
                await PreviewWebView.EnsureCoreWebView2Async();
                _previewReady = true;
            }

            // Escape the markdown for embedding in a JS string
            var escaped = markdown
                .Replace("\\", "\\\\")
                .Replace("`", "\\`")
                .Replace("$", "\\$");

            var html = $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
                body {
                    font-family: 'Segoe UI', system-ui, sans-serif;
                    font-size: 14px;
                    line-height: 1.6;
                    color: #000;
                    background: transparent;
                    padding: 16px;
                    margin: 0;
                }
                h1, h2, h3, h4, h5, h6 { color: #ffffff; margin-top: 1.2em; margin-bottom: 0.4em; }
                h1 { font-size: 1.8em; border-bottom: 1px solid #444; padding-bottom: 0.3em; }
                h2 { font-size: 1.4em; border-bottom: 1px solid #333; padding-bottom: 0.2em; }
                code {
                    background: #2d2d2d;
                    padding: 2px 6px;
                    border-radius: 4px;
                    font-family: 'Cascadia Code', 'Consolas', monospace;
                    font-size: 0.9em;
                }
                pre {
                    background: #1e1e1e;
                    border: 1px solid #333;
                    border-radius: 6px;
                    padding: 12px;
                    overflow-x: auto;
                }
                pre code { background: none; padding: 0; }
                blockquote {
                    border-left: 3px solid #0078d4;
                    margin: 0.8em 0;
                    padding: 0.4em 1em;
                    color: #b0b0b0;
                }
                table { border-collapse: collapse; width: 100%; margin: 1em 0; }
                th, td { border: 1px solid #444; padding: 8px 12px; text-align: left; }
                th { background: #2a2a2a; }
                a { color: #4da6ff; }
                img { max-width: 100%; }
                hr { border: none; border-top: 1px solid #444; margin: 1.5em 0; }
                ul, ol { padding-left: 1.5em; }
            </style>
            </head>
            <body><div id="content"></div>
            <script>
                // Simple markdown-to-HTML renderer
                function renderMarkdown(md) {
                    let html = md;
                    // Code blocks (fenced)
                    html = html.replace(/```(\w*)\n([\s\S]*?)```/g, '<pre><code>$2</code></pre>');
                    // Inline code
                    html = html.replace(/`([^`]+)`/g, '<code>$1</code>');
                    // Headers
                    html = html.replace(/^######\s+(.+)$/gm, '<h6>$1</h6>');
                    html = html.replace(/^#####\s+(.+)$/gm, '<h5>$1</h5>');
                    html = html.replace(/^####\s+(.+)$/gm, '<h4>$1</h4>');
                    html = html.replace(/^###\s+(.+)$/gm, '<h3>$1</h3>');
                    html = html.replace(/^##\s+(.+)$/gm, '<h2>$1</h2>');
                    html = html.replace(/^#\s+(.+)$/gm, '<h1>$1</h1>');
                    // Bold + italic
                    html = html.replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>');
                    html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
                    html = html.replace(/\*(.+?)\*/g, '<em>$1</em>');
                    // Links + images
                    html = html.replace(/!\[([^\]]*)\]\(([^)]+)\)/g, '<img alt="$1" src="$2">');
                    html = html.replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2">$1</a>');
                    // Horizontal rule
                    html = html.replace(/^---+$/gm, '<hr>');
                    // Blockquotes
                    html = html.replace(/^>\s+(.+)$/gm, '<blockquote>$1</blockquote>');
                    // Unordered lists
                    html = html.replace(/^[\-\*]\s+(.+)$/gm, '<li>$1</li>');
                    html = html.replace(/(<li>.*<\/li>)/s, '<ul>$1</ul>');
                    // Tables
                    html = html.replace(/^(\|.+\|)\n\|[\s\-:|]+\|\n((?:\|.+\|\n?)*)/gm, function(m, header, body) {
                        let ths = header.split('|').filter(c => c.trim()).map(c => '<th>' + c.trim() + '</th>').join('');
                        let rows = body.trim().split('\n').map(r => {
                            let tds = r.split('|').filter(c => c.trim()).map(c => '<td>' + c.trim() + '</td>').join('');
                            return '<tr>' + tds + '</tr>';
                        }).join('');
                        return '<table><thead><tr>' + ths + '</tr></thead><tbody>' + rows + '</tbody></table>';
                    });
                    // Paragraphs (double newline)
                    html = html.replace(/\n\n+/g, '</p><p>');
                    html = '<p>' + html + '</p>';
                    // Clean up empty paragraphs around block elements
                    html = html.replace(/<p>\s*(<h[1-6]|<pre|<ul|<ol|<table|<blockquote|<hr)/g, '$1');
                    html = html.replace(/(<\/h[1-6]>|<\/pre>|<\/ul>|<\/ol>|<\/table>|<\/blockquote>|<hr>)\s*<\/p>/g, '$1');
                    // Line breaks
                    html = html.replace(/\n/g, '<br>');
                    return html;
                }
                document.getElementById('content').innerHTML = renderMarkdown(`{{escaped}}`);
            </script>
            </body>
            </html>
            """;

            PreviewWebView.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] RenderPreviewAsync error: {ex.Message}");
        }
    }

    // ========== Container Management ==========

    private void StartContainerAsync()
    {
        _containerService = new ContainerService();

        _containerService.OutputReceived += text => OnContainerOutput(text);
        _containerService.StatusChanged += text => OnContainerStatus(text);

        var svc = _containerService;
        Task.Run(async () =>
        {
            try
            {
                svc.StartContainer();

                _dispatcherQueue?.TryEnqueue(() =>
                {
                    if (_closed) return;
                    StatusText.Text = "Container running";
                    SetStatusIcon(StatusState.Pending);
                    AppendToLog("Container started. Waiting for service to be ready...\n");
                });

                // Wait for the HTTP service to become ready
                await WaitForServiceReady();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MainWindow] Container start failed: {ex.Message}");
                _dispatcherQueue?.TryEnqueue(() =>
                {
                    if (!_closed)
                    {
                        StatusText.Text = "Container failed";
                        SetStatusIcon(StatusState.Error);
                        AppendToLog($"\n❌ Container failed to start: {ex.Message}\n");
                    }
                });
            }
        });
    }

    private async Task WaitForServiceReady()
    {
        if (_apiClient == null) return;

        for (int i = 0; i < 60; i++)
        {
            if (_closed) return;

            try
            {
                var healthy = await _apiClient.CheckHealthAsync();
                if (healthy)
                {
                    _containerReady = true;
                    _dispatcherQueue?.TryEnqueue(() =>
                    {
                        if (_closed) return;
                        StatusText.Text = "Service ready";
                        SetStatusIcon(StatusState.Ready);
                        AppendToLog("✅ MarkItDown service is ready!\n");
                        ConvertHeaderText.Text = "Document Converter — Ready";
                    });

                    // Start periodic health check
                    _dispatcherQueue?.TryEnqueue(StartHealthCheckTimer);
                    return;
                }
            }
            catch { /* service not ready yet */ }

            await Task.Delay(2000);
        }

        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (!_closed)
            {
                StatusText.Text = "Service timeout";
                SetStatusIcon(StatusState.Warning);
                AppendToLog("⚠️ Service did not become ready within 2 minutes.\n");
            }
        });
    }

    private void StartHealthCheckTimer()
    {
        if (_closed) return;

        _healthCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _healthCheckTimer.Tick += async (_, _) =>
        {
            if (_closed || _apiClient == null) return;

            try
            {
                var healthy = await _apiClient.CheckHealthAsync();
                _containerReady = healthy;
                StatusText.Text = healthy ? "Service ready" : "Service unhealthy";
                SetStatusIcon(healthy ? StatusState.Ready : StatusState.Warning);
            }
            catch
            {
                _containerReady = false;
                StatusText.Text = "Service unreachable";
                SetStatusIcon(StatusState.Warning);
            }
        };
        _healthCheckTimer.Start();
    }

    private void OnContainerOutput(string text)
    {
        lock (_logLock)
        {
            _pendingOutput.Append(text);
        }

        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_closed) return;

            string pending;
            lock (_logLock)
            {
                pending = _pendingOutput.ToString();
                _pendingOutput.Clear();
            }

            if (string.IsNullOrEmpty(pending)) return;
            AppendToLog(pending);
        });
    }

    private void OnContainerStatus(string text)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            if (_closed) return;
            StatusText.Text = text;
            LogHeaderText.Text = $"Container Log — {text}";
        });
    }

    // ========== UI Helpers ==========

    private void SetStatusIcon(StatusState state)
    {
        switch (state)
        {
            case StatusState.Dim:
                StatusIcon.Glyph = "\uE945"; // circle
                StatusIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorDisabledBrush"];
                break;
            case StatusState.Pending:
                StatusIcon.Glyph = "\uE916"; // sync
                StatusIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                break;
            case StatusState.Ready:
                StatusIcon.Glyph = "\uE73E"; // checkmark
                StatusIcon.Foreground = new SolidColorBrush(Colors.LimeGreen);
                break;
            case StatusState.Warning:
                StatusIcon.Glyph = "\uE7BA"; // warning
                StatusIcon.Foreground = new SolidColorBrush(Colors.Orange);
                break;
            case StatusState.Error:
                StatusIcon.Glyph = "\uE711"; // cancel
                StatusIcon.Foreground = new SolidColorBrush(Colors.Tomato);
                break;
        }
    }

    private void AppendToLog(string text)
    {
        _logBuffer.Append(text);

        if (_logBuffer.Length > MaxLogChars)
        {
            var trimmed = _logBuffer.ToString(
                _logBuffer.Length - (MaxLogChars - 10_000),
                MaxLogChars - 10_000);
            _logBuffer.Clear();
            _logBuffer.Append(trimmed);
        }

        LogTextBlock.Text = _logBuffer.ToString();

        LogScrollViewer.UpdateLayout();
        LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null);
    }

    private async void ShowError(string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainWindow] ShowError dialog exception: {ex.Message}");
        }
    }

    private static string FormatBytes(int bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
        };
    }

    // ========== Window Close ==========

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;

        _healthCheckTimer?.Stop();
        _apiClient?.Dispose();
        _apiClient = null;

        if (_containerService != null)
        {
            var svc = _containerService;
            _containerService = null;
            Task.Run(() => svc.Shutdown());
        }
    }
}
