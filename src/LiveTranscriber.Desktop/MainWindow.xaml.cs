using System.Text;
using System.Windows;
using LiveTranscriber.Core;
using LiveTranscriber.Desktop.Audio;
using LiveTranscriber.Desktop.Engines;

namespace LiveTranscriber.Desktop;

public partial class MainWindow : Window
{
    private TranscriptionSession? _session;
    private readonly StringBuilder _confirmed = new();
    private string _hypothesis = string.Empty;
    private bool _busy;

    public MainWindow() => InitializeComponent();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _session is not null) return;
        _busy = true;
        StartButton.IsEnabled = false;
        EngineSelect.IsEnabled = false;
        StatusText.Text = "Initializing recognition...";
        TranscriptionSession? candidate = null;
        try
        {
            ISpeechEngine engine = EngineSelect.SelectedIndex == 0
                ? new SherpaLocalSpeechEngine()
                : new AzureCloudSpeechEngine();
            candidate = new TranscriptionSession(new WasapiSpeakerSource(), engine);
            candidate.TextAvailable += UpdateText;
            candidate.Failed += ShowError;
            await candidate.StartAsync();
            _session = candidate;
            StopButton.IsEnabled = true;
            StatusText.Text = "Listening to default Windows speaker output. Microphone is not opened.";
        }
        catch (Exception ex)
        {
            if (candidate is not null) await candidate.DisposeAsync();
            MessageBox.Show(this, ex.Message, "Cannot start transcription", MessageBoxButton.OK, MessageBoxImage.Warning);
            EngineSelect.IsEnabled = true;
            StartButton.IsEnabled = true;
            StatusText.Text = "Stopped.";
        }
        finally { _busy = false; }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopCurrentAsync();

    private async Task StopCurrentAsync()
    {
        if (_busy || _session is null) return;
        _busy = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "Stopping...";
        var session = _session;
        try { await session.DisposeAsync(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Stop error"); }
        finally
        {
            _session = null;
            _busy = false;
            EngineSelect.IsEnabled = true;
            StartButton.IsEnabled = true;
            _hypothesis = string.Empty;
            RenderTranscript();
            StatusText.Text = $"Stopped. Dropped audio chunks: {session.DroppedChunks}.";
        }
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
        if (_session is not null)
        {
            try { await _session.DisposeAsync(); }
            catch { /* window is closing; no secrets or raw audio are logged */ }
            _session = null;
        }
        base.OnClosed(e);
    }
}
