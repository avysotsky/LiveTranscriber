using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveTranscriber.Core;
using LiveTranscriber.Core.Translation;
using LiveTranscriber.Desktop.Audio;
using LiveTranscriber.Desktop.Engines;

namespace LiveTranscriber.Desktop;

public partial class MainWindow : Window
{
    private TranscriptionSession? _session;
    private TranslationPipeline? _translations;
    // Cached model belongs to the WPF window, NOT each recording. Model creation
    // is deferred to a separate translation worker and never delays Start/ASR.
    private readonly ReusableTranslatorHost _offlineTranslator = new(
        async token => await LocalOpusMtTranslator.StartAsync(cancellationToken: token)
            .ConfigureAwait(false));
    private readonly StringBuilder _confirmed = new();
    private readonly StringBuilder _russian = new();
    private string _previewRussian = string.Empty;
    private string _lastPreviewEnglish = string.Empty;
    private DateTimeOffset _lastPreviewSubmitted;
    private TranslationMetrics? _lastTranslationMetrics;
    private bool _translationWasEnabled;
    private bool _translationUsesCloud;
    private bool _translationUsesChatGpt;
    private readonly ChatGptPlanConnection _chatGptConnection = new();
    private string TranslationProviderName => _translationUsesChatGpt
        ? "ChatGPT plan" : _translationUsesCloud ? "Groq" : "Local OPUS-MT";
    private string SelectedChatGptModel =>
        (ChatGptModelSelect.SelectedItem as ChatGptModel)?.Slug ??
        throw new InvalidOperationException("Sign in with ChatGPT and select a model first.");
    private string _lastTranslationError = string.Empty;
    private SystemResourceSampler _resourceSampler = new();
    private PipelineHealthMonitor _healthMonitor = new();
    private DateTimeOffset _sessionStartedAt;
    private double _peakCpuPercent;
    private double _peakMemoryMiB;
    private double _lastDroppedAudioSeconds;
    private long _lastNearCapacityEvents;
    private long _telemetrySamples;
    private string _previousDiagnosticReport = "No diagnostic session has been completed.";
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

    private void TranslationProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChatGptSignInButton is null || ChatGptModelSelect is null ||
            ChatGptDisconnectButton is null) return;
        bool chatgpt = TranslationProviderSelect.SelectedIndex == 2;
        ChatGptSignInButton.IsEnabled = chatgpt && !_busy && _session is null;
        ChatGptModelSelect.IsEnabled = chatgpt && _chatGptConnection.IsConnected &&
            _session is null && !_busy;
        ChatGptDisconnectButton.IsEnabled = _chatGptConnection.IsConnected &&
            _session is null && !_busy;
    }

    private async void ChatGptSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _busy) return;
        _busy = true;
        SetCaptureControls(false);
        StartButton.IsEnabled = false;
        ChatGptAccountStatus.Text = "Opening secure ChatGPT sign-in in your browser...";
        try
        {
            await _chatGptConnection.SignInAsync();
            ChatGptAccountStatus.Text = "Signed in; checking available plan models...";
            IReadOnlyList<ChatGptModel> models = await _chatGptConnection.ListModelsAsync();
            if (models.Count == 0)
                throw new InvalidOperationException(
                    "Sign-in succeeded but no plan models are available for this account.");
            ChatGptModelSelect.ItemsSource = models;
            ChatGptModelSelect.SelectedIndex = 0;
            ChatGptAccountStatus.Text = "Authorized (temporary session)";
        }
        catch (Exception ex)
        {
            ChatGptAccountStatus.Text = ex is InvalidOperationException
                ? ex.Message : "ChatGPT sign-in failed. Check browser, network and permissions.";
        }
        finally
        {
            _busy = false;
            SetCaptureControls(true);
            StartButton.IsEnabled = true;
        }
    }

    private void ChatGptDisconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _busy) return;
        _chatGptConnection.ForgetRegistration();
        ChatGptModelSelect.ItemsSource = null;
        ChatGptAccountStatus.Text = "Disconnected (tokens discarded from memory)";
        SetCaptureControls(true);
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
        TranslationPipeline? translationCandidate = null;
        try
        {
            _translationWasEnabled = TranslateToggle.IsChecked == true;
            _translationUsesCloud = TranslationProviderSelect.SelectedIndex == 1;
            _translationUsesChatGpt = TranslationProviderSelect.SelectedIndex == 2;
            _lastTranslationMetrics = null;
            _lastTranslationError = string.Empty;
            _previewRussian = string.Empty;
            _lastPreviewEnglish = string.Empty;
            _lastPreviewSubmitted = DateTimeOffset.MinValue;
            if (_translationWasEnabled)
            {
                // Groq text upload requires *both* selecting Groq and enabling translation.
                // Local worker never sends HTTP requests or downloads a model.
                ITextTranslator translator = _translationUsesChatGpt
                    ? new ChatGptPlanTranslator(_chatGptConnection, SelectedChatGptModel)
                    : _translationUsesCloud
                        ? new GroqRussianTranslator(Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "")
                        : _offlineTranslator.CreateSessionTranslator();
                translationCandidate = new TranslationPipeline(translator,
                    _translationUsesCloud || _translationUsesChatGpt
                        ? TimeSpan.FromMilliseconds(2500) : TimeSpan.Zero);
                TranslationPipeline active = translationCandidate;
                active.Translated += (russianText, generation) =>
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!ReferenceEquals(_translations, active) ||
                            generation != active.Generation) return;
                        _russian.AppendLine(russianText.Trim());
                        _previewRussian = string.Empty;
                        _lastTranslationError = string.Empty;
                        RenderRussian();
                        RefreshTranslationStatus();
                    });
                active.PreviewTranslated += (russianText, generation) =>
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (!ReferenceEquals(_translations, active) ||
                            generation != active.Generation) return;
                        _previewRussian = russianText.Trim();
                        RenderRussian();
                        RefreshTranslationStatus();
                    });
                active.Error += message =>
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (ReferenceEquals(_translations, active))
                        {
                            _lastTranslationError = message;
                            RefreshTranslationStatus();
                        }
                    });
                _translations = active;
                TranslationStatus.Text = _translationUsesChatGpt
                    ? "RU enabled (ChatGPT plan): interim and final English text sent to OpenAI."
                    : _translationUsesCloud
                        ? "RU enabled (Groq cloud): interim and final English text sent to Groq."
                        : "RU enabled (local OPUS-MT): English text stays on this machine.";
            }
            else TranslationStatus.Text = "RU disabled — select a provider and check Auto-translate.";

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
            _resourceSampler = new SystemResourceSampler(
                _translationWasEnabled && !_translationUsesCloud && !_translationUsesChatGpt
                    ? () => _offlineTranslator.LocalProcessId : null);
            _resourceSampler.Sample();
            _healthMonitor = new PipelineHealthMonitor();
            _sessionStartedAt = DateTimeOffset.UtcNow;
            _peakCpuPercent = 0;
            _peakMemoryMiB = 0;
            _lastDroppedAudioSeconds = 0;
            _lastNearCapacityEvents = 0;
            _telemetrySamples = 0;
            HealthText.Text = "Health: warming up";
            ResourceText.Text = "Gathering CPU, RAM, queue and ASR processing metrics...";
            _resourceTimer.Start();
        }
        catch (Exception ex)
        {
            if (candidate is not null) await candidate.DisposeAsync();
            if (translationCandidate is not null) await translationCandidate.DisposeAsync();
            _translations = null;
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
        TranslateToggle.IsEnabled = enabled;
        TranslationProviderSelect.IsEnabled = enabled;
        TestGroqButton.IsEnabled = enabled;
        ChatGptSignInButton.IsEnabled = enabled && TranslationProviderSelect.SelectedIndex == 2;
        ChatGptDisconnectButton.IsEnabled = enabled && _chatGptConnection.IsConnected;
        ChatGptModelSelect.IsEnabled = enabled && _chatGptConnection.IsConnected &&
            TranslationProviderSelect.SelectedIndex == 2;
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
        var translations = _translations;
        try
        {
            await session.DisposeAsync();
            if (translations is not null)
            {
                TranslationStatus.Text = "RU: completing remaining translations...";
                await translations.CompleteAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Stop error"); }
        finally
        {
            if (translations is not null)
            {
                _lastTranslationMetrics = translations.GetMetrics();
                await translations.DisposeAsync();
                _translations = null;
                TranslationStatus.Text = BuildTranslationStatus(_lastTranslationMetrics) + " (stopped)";
            }
            _previousDiagnosticReport = BuildDiagnostics(session.GetMetrics());
            ResourceText.Text = _previousDiagnosticReport.Replace(Environment.NewLine, "  |  ");
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
        _telemetrySamples++;
        _peakCpuPercent = Math.Max(_peakCpuPercent, cpu);
        _peakMemoryMiB = Math.Max(_peakMemoryMiB, memory);

        PipelineMetrics metrics = _session.GetMetrics();
        double newLoss = Math.Max(0, metrics.DroppedAudioSeconds - _lastDroppedAudioSeconds);
        _lastDroppedAudioSeconds = metrics.DroppedAudioSeconds;
        long newNearFull = Math.Max(0, metrics.NearCapacityEvents - _lastNearCapacityEvents);
        _lastNearCapacityEvents = metrics.NearCapacityEvents;
        bool cloud = EngineSelect.SelectedIndex == 1;
        PipelineHealth health = _healthMonitor.Observe(new PipelineHealthReading(
            cpu, metrics.QueuedAudioSeconds, metrics.ProcessingRatio,
            newLoss, cloud, metrics.ProcessedAudioSeconds,
            metrics.QueuedChunks, metrics.QueueCapacityChunks, newNearFull));
        string ratioName = cloud ? "Client upload/audio" : "Local processing RTF";

        ResourceText.Text = $"CPU {cpu:0.0}% (peak {_peakCpuPercent:0.0}%)  |  " +
            $"RAM {memory:0} MiB (peak {_peakMemoryMiB:0})  |  " +
            $"Queue {metrics.QueuedAudioSeconds:0.00}s ({metrics.QueuedChunks}/{metrics.QueueCapacityChunks})  |  " +
            $"Near-full events {metrics.NearCapacityEvents}  |  " +
            $"Lost {metrics.DroppedAudioSeconds:0.00}s  |  " +
            $"Queue wait avg/peak {metrics.AverageQueueWaitMilliseconds:0.0}/{metrics.PeakQueueWaitMilliseconds:0.0}ms  |  " +
            $"{ratioName} {metrics.ProcessingRatio:0.00}";
        HealthText.Text = health switch
        {
            PipelineHealth.Healthy => "Health: healthy",
            PipelineHealth.UnderPressure => "Health: under pressure — reduce load or choose Cloud manually",
            _ => "Health: warming up"
        };
        TrySubmitInterimTranslation();
        RefreshTranslationStatus();
    }

    private string BuildDiagnostics(PipelineMetrics metrics)
    {
        var elapsed = _sessionStartedAt == default
            ? TimeSpan.Zero : DateTimeOffset.UtcNow - _sessionStartedAt;
        string cpuPeak = _telemetrySamples == 0 ? "not sampled" : $"{_peakCpuPercent:0.0}%";
        string ramPeak = _telemetrySamples == 0 ? "not sampled" : $"{_peakMemoryMiB:0} MiB";
        return string.Join(Environment.NewLine, new[]
        {
            "LiveTranscriber session diagnostics (no transcript or audio)",
            "Process resource figures include the offline Python translation worker when ready.",
            $"Duration: {elapsed:hh\\:mm\\:ss}",
            $"Engine: {(EngineSelect.SelectedIndex == 0 ? "Local" : "Cloud")}",
            $"Capture: {(CaptureSelect.SelectedIndex == 0 ? "All output" : "Selected application")}",
            $"Peak process CPU: {cpuPeak}",
            $"Peak process working set: {ramPeak}",
            $"Peak queued audio: {metrics.PeakQueuedAudioSeconds:0.00}s ({metrics.PeakQueuedChunks}/{metrics.QueueCapacityChunks} chunks)",
            $"Near-capacity queue events: {metrics.NearCapacityEvents}",
            $"Remaining queued audio: {metrics.QueuedAudioSeconds:0.00}s",
            $"Dropped audio: {metrics.DroppedAudioSeconds:0.00}s ({metrics.DroppedChunks} chunks)",
            $"Processed audio: {metrics.ProcessedAudioSeconds:0.00}s ({metrics.ProcessedChunks} chunks)",
            $"Average capture callback-to-ASR-start wait: {metrics.AverageQueueWaitMilliseconds:0.00} ms",
            $"Peak capture callback-to-ASR-start wait: {metrics.PeakQueueWaitMilliseconds:0.00} ms",
            $"Longest individual recognizer call: {metrics.PeakRecognizerCallMilliseconds:0.00} ms",
            $"Client processing/audio ratio: {metrics.ProcessingRatio:0.00}" +
                (EngineSelect.SelectedIndex == 1 ? " (cloud upload, not speech latency)" : " (local ASR)"),
            $"Health: {_healthMonitor.State}",
            "Queue wait starts at the capture callback, not at speech onset. End-to-end transcript delay is NOT measured.",
            BuildTranslationStatus(_translations?.GetMetrics() ?? _lastTranslationMetrics)
        });
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        string report = _session is null
            ? _previousDiagnosticReport
            : BuildDiagnostics(_session.GetMetrics());
        Clipboard.SetText(report);
    }

    private void TrySubmitInterimTranslation()
    {
        TranslationPipeline? pipeline = _translations;
        if (pipeline is null || _session is null) return;

        // At most one changed interim hypothesis every four seconds; do not
        // turn each Sherpa token into an API request.
        string english = _hypothesis.Trim();
        if (!_translationUsesCloud && !_translationUsesChatGpt)
        {
            // Audio recognition is strictly more important than interim RU.
            // Skip previews whenever ASR audio is backing up or the translator
            // already has an active/pending job. Final text is never dropped here.
            TranslationMetrics translation = pipeline.GetMetrics();
            if (_session.GetMetrics().QueuedChunks >= 2 ||
                translation.PendingPhrases > 0 ||
                translation.ApiRequestsStarted >
                    translation.ApiRequestsSucceeded + translation.ApiRequestsFailed)
                return;

            // Preview is replaceable: cap repeated translations of an endlessly
            // growing interim hypothesis to a recent speech window.
            if (english.Length > 280)
            {
                int split = english.IndexOf(' ', english.Length - 280);
                english = split >= 0 ? english[(split + 1)..] : english[^280..];
            }
        }
        if (english.Length < 12 || english == _lastPreviewEnglish ||
            DateTimeOffset.UtcNow - _lastPreviewSubmitted < TimeSpan.FromSeconds(4))
            return;

        if (pipeline.TryEnqueuePreview(english))
        {
            _lastPreviewEnglish = english;
            _lastPreviewSubmitted = DateTimeOffset.UtcNow;
        }
    }

    private void RenderRussian()
    {
        RussianBox.Text = _russian.ToString() +
            (string.IsNullOrWhiteSpace(_previewRussian)
                ? string.Empty : _previewRussian + " …");
        RussianBox.ScrollToEnd();
    }

    private string BuildTranslationStatus(TranslationMetrics? metrics)
    {
        if (!_translationWasEnabled) return "RU: off (translation disabled)";
        if (metrics is null) return $"RU ({TranslationProviderName}): enabled; no phrases processed yet";
        string state = $"RU ({TranslationProviderName}): final {metrics.FinalPhrasesQueued}, interim {metrics.PreviewPhrasesQueued}, " +
            $"requests {metrics.ApiRequestsStarted}, OK {metrics.ApiRequestsSucceeded}, " +
            $"failed {metrics.ApiRequestsFailed}, pending {metrics.PendingPhrases}";
        return string.IsNullOrEmpty(_lastTranslationError)
            ? state : state + " | Error: " + _lastTranslationError;
    }

    private void RefreshTranslationStatus()
    {
        TranslationStatus.Text = BuildTranslationStatus(
            _translations?.GetMetrics() ?? _lastTranslationMetrics);
    }

    private async void TestGroq_Click(object sender, RoutedEventArgs e)
    {
        if (TranslateToggle.IsChecked != true)
        {
            TranslationStatus.Text = "RU TEST: check Auto-translate to enable the selected provider.";
            return;
        }

        bool cloud = TranslationProviderSelect.SelectedIndex == 1;
        bool chatgpt = TranslationProviderSelect.SelectedIndex == 2;
        TestGroqButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        TranslationProviderSelect.IsEnabled = false;
        TranslationStatus.Text = chatgpt
            ? "RU TEST: using authorized ChatGPT plan with a fixed sample..."
            : cloud ? "RU TEST: contacting Groq with a fixed example sentence..."
                : "RU TEST: starting the offline OPUS-MT worker...";
        try
        {
            ITextTranslator translator = chatgpt
                ? new ChatGptPlanTranslator(_chatGptConnection, SelectedChatGptModel)
                : cloud
                    ? new GroqRussianTranslator(Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? "")
                    : await _offlineTranslator.PrepareAsync();
            string result;
            try
            {
                result = await translator.TranslateToRussianAsync(
                    "Can you explain dependency injection in ASP.NET Core?", CancellationToken.None);
            }
            finally
            {
                // Cloud clients are ephemeral; the local model stays warm between
                // Test translator, Start and Stop until this window is closed.
                if (cloud || chatgpt) await translator.DisposeAsync();
            }
            TranslationStatus.Text = chatgpt
                ? "RU TEST: ChatGPT plan translation OK."
                : cloud ? "RU TEST: Groq connection OK."
                    : "RU TEST: local translation OK; no network used.";
            MessageBox.Show(this, result, chatgpt ? "ChatGPT plan translation test"
                : cloud ? "Groq translation test" : "Offline translation test",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            string safeMessage = ex is InvalidOperationException
                ? ex.Message : "Translator failed. Check the local model or network configuration.";
            TranslationStatus.Text = "RU TEST FAILED: " + safeMessage;
        }
        finally
        {
            TestGroqButton.IsEnabled = _session is null && !_busy;
            StartButton.IsEnabled = _session is null && !_busy;
            TranslationProviderSelect.IsEnabled = _session is null && !_busy;
        }
    }

    private void UpdateText(TranscriptUpdate update)
    {
        if (update.IsFinal && !string.IsNullOrWhiteSpace(update.Text))
        {
            _translations?.TryEnqueueFinal(update.Text);
            _lastPreviewEnglish = string.Empty;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (update.IsFinal)
            {
                if (!string.IsNullOrWhiteSpace(update.Text))
                    _confirmed.AppendLine(update.Text.Trim());
                _hypothesis = string.Empty;
                _previewRussian = string.Empty;
                RenderRussian();
                RefreshTranslationStatus();
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
        if (TranscriptBox.Text.Length > 0 || RussianBox.Text.Length > 0)
            Clipboard.SetText("English:\n" + TranscriptBox.Text +
                "\n\nРусский перевод:\n" + RussianBox.Text);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _translations?.ClearPending();
        _confirmed.Clear();
        _russian.Clear();
        _previewRussian = string.Empty;
        _lastPreviewEnglish = string.Empty;
        _hypothesis = string.Empty;
        RenderRussian();
        RefreshTranslationStatus();
        RenderTranscript();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _resourceTimer.Stop();
        if (_translations is not null)
        {
            try { await _translations.DisposeAsync(); }
            catch { /* Closing: never log submitted text or API keys. */ }
            _translations = null;
        }
        if (_session is not null)
        {
            try { await _session.DisposeAsync(); }
            catch { /* Shutdown must not log or persist sensitive audio. */ }
            _session = null;
        }
        try { await _offlineTranslator.DisposeAsync(); }
        catch { /* Stop the locally loaded model when the window closes. */ }
        await _chatGptConnection.DisposeAsync();
        base.OnClosed(e);
    }
}
