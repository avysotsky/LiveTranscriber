using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveTranscriber.Core;
using LiveTranscriber.Desktop.Audio;
using LiveTranscriber.Desktop.Engines;

namespace LiveTranscriber.Desktop;

public partial class MainWindow : Window
{
    private TranscriptionSession? _session;
    private readonly StringBuilder _confirmed = new();
    private readonly SystemResourceSampler _resourceSampler = new();
    private readonly DispatcherTimer _resourceTimer;
    private string _hypothesis = string.Empty;
    private bool _busy;

    private sealed record ProcessChoice(int Id, string Description);

    public MainWindow()
    {
        InitializeComponent();
        RefreshProcesses();
        _resourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _resourceTimer.Tick += (_, _) => UpdateResources();
    }

    private void CaptureSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessSelect is null || RefreshButton is null) return;
        bool app = CaptureSelect.SelectedIndex == 1;
        ProcessSelect.IsEnabled = app && !_busy && _session is null;
        RefreshButton.IsEnabled = app && !_busy && _session is null;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private void RefreshProcesses()
    {
        var choices = new List<ProcessChoice>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == Environment.ProcessId || p.HasExited || p.MainWindowHandle == IntPtr.Zero)
                        continue;
                    string name = p.ProcessName;
                    string title = p.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    if (title.Length > 65) title = title[..65] + "…";
                    choices.Add(new ProcessChoice(p.Id, $"{name} (PID {p.Id}) — {title}"));
                }
                catch (Exception) { /* Some system processes deny access or exit during enumeration. */ }
            }
        }
        ProcessSelect.ItemsSource = choices.OrderBy(p => p.Description).ToList();
    }

    private CaptureSourceSelection GetSelection()
    {
        if (CaptureSelect.SelectedIndex == 0)
            return new CaptureSourceSelection(CaptureSourceMode.DeviceLoopback);

        int? pid = ProcessSelect.SelectedItem is ProcessChoice selected
            ? selected.Id
            : int.TryParse(ProcessSelect.Text.Trim(), out int parsed) ? parsed : null;
        var choice = new CaptureSourceSelection(CaptureSourceMode.ProcessLoopback, pid);
        choice.Validate();
        return choice;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session is not null) return;
        _busy = true;
        SetCaptureControls(false);
        StartButton.IsEnabled = false;
        StatusText.Text = "Initializing recognition and capture...";
        TranscriptionSession? candidate = null;
        try
        {
            CaptureSourceSelection selection = GetSelection();
            IAudioSource audio = selection.Mode == CaptureSourceMode.DeviceLoopback
                ? new WasapiSpeakerSource()
                : await ProcessLoopbackSource.CreateAsync(selection.ProcessId!.Value);
            ISpeechEngine engine = EngineSelect.SelectedIndex == 0
                ? new SherpaLocalSpeechEngine()
                : new AzureCloudSpeechEngine();
            candidate = new TranscriptionSession(audio, engine);
            candidate.TextAvailable += UpdateText;
            candidate.Failed += ShowError;
            await candidate.StartAsync();
            _session = candidate;
            StopButton.IsEnabled = true;
            StatusText.Text = selection.Mode == CaptureSourceMode.DeviceLoopback
                ? "Listening to all default speaker playback. No microphone opened."
                : $"Listening to application PID {selection.ProcessId} and its child processes. No microphone opened.";
            _resourceSampler.Sample();
            _resourceTimer.Start();
        }
        catch (Exception ex)
        {
            if (candidate is not null) await candidate.DisposeAsync();
            MessageBox.Show(this, ex.Message, "Cannot start transcription", MessageBoxButton.OK, MessageBoxImage.Warning);
            StartButton.IsEnabled = true;
            SetCaptureControls(true);
            StatusText.Text = "Stopped.";
        }
        finally { _busy = false; }
    }

    private void SetCaptureControls(bool enabled)
    {
        EngineSelect.IsEnabled = enabled;
        CaptureSelect.IsEnabled = enabled;
        bool app = enabled && CaptureSelect.SelectedIndex == 1;
        ProcessSelect.IsEnabled = app;
        RefreshButton.IsEnabled = app;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopCurrentAsync();

    private async Task StopCurrentAsync()
    {
        if (_busy || _session is null) return;
        _busy = true;
        _resourceTimer.Stop();
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping...";
        var session = _session;
        try { await session.DisposeAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Stop error"); }
        finally
        {
            _session = null;
            _busy = false;
            StartButton.IsEnabled = true;
            SetCaptureControls(true);
            _hypothesis = string.Empty;
            RenderTranscript();
            StatusText.Text = $"Stopped. Dropped audio chunks: {session.DroppedChunks}.";
        }
    }

    private void UpdateResources()
    {
        if (_session is null) return;
        (double cpu, double memory) = _resourceSampler.Sample();
        string ratioName = EngineSelect.SelectedIndex == 0 ? "Local processing RTF" : "Cloud upload time/audio";
        ResourceText.Text = $"CPU {cpu:0.0}%  |  RAM {memory:0} MiB  |  " +
            $"Dropped chunks {_session.DroppedChunks}  |  {ratioName} {_session.ProcessingRatio:0.00}";
    }

    private void UpdateText(TranscriptUpdate update)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (update.IsFinal)
            {
                if (!string.IsNullOrWhiteSpace(update.Text))
                    _confirmed.AppendLine(update.Text.Trim());
                _hypothesis = string.Empty;
            }
            else _hypothesis = update.Text.Trim();
            RenderTranscript();
        });
    }

    private void ShowError(Exception error)
    {
        _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"Audio or recognition error: {error.Message}");
    }

    private void RenderTranscript()
    {
        TranscriptBox.Text = _confirmed.ToString() +
            (string.IsNullOrWhiteSpace(_hypothesis) ? "" : _hypothesis + " …");
        TranscriptBox.ScrollToEnd();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (TranscriptBox.Text.Length > 0) Clipboard.SetText(TranscriptBox.Text);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _confirmed.Clear();
        _hypothesis = string.Empty;
        RenderTranscript();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _resourceTimer.Stop();
        if (_session is not null)
        {
            try { await _session.DisposeAsync(); }
            catch { /* Shutdown must not log or persist sensitive audio. */ }
            _session = null;
        }
        base.OnClosed(e);
    }
}
