namespace LiveTranscriber.Core.Translation;

/// <summary>
/// Initializes a translation model lazily and owns it for the application lifetime.
/// Session proxies do not dispose the model when recording stops. English ASR
/// can start immediately while translation loads separately on its own worker.
/// </summary>
public sealed class ReusableTranslatorHost : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ITextTranslator>> _factory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task<ITextTranslator>? _load;
    private bool _disposed;

    public ReusableTranslatorHost(Func<CancellationToken, Task<ITextTranslator>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public bool IsReady
    {
        get { lock (_gate) return _load?.IsCompletedSuccessfully ?? false; }
    }

    /// <summary>Returns the existing model process when ready; does not trigger loading.</summary>
    public int? LocalProcessId
    {
        get
        {
            lock (_gate)
            {
                return _load is { IsCompletedSuccessfully: true }
                    && _load.Result is LocalOpusMtTranslator local ? local.WorkerProcessId : null;
            }
        }
    }

    public Task<ITextTranslator> PrepareAsync(CancellationToken cancellationToken = default)
    {
        Task<ITextTranslator> pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_load is null || _load.IsFaulted || _load.IsCanceled)
                _load = _factory(_lifetime.Token);
            pending = _load;
        }
        return pending.WaitAsync(cancellationToken);
    }

    /// <summary>Cheap non-owning proxy, safe to create before the model is initialized.</summary>
    public ITextTranslator CreateSessionTranslator()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        return new SessionProxy(this);
    }

    private sealed class SessionProxy(ReusableTranslatorHost host) : ITextTranslator
    {
        public async Task<string> TranslateToRussianAsync(string englishText, CancellationToken cancellationToken)
        {
            ITextTranslator translator = await host.PrepareAsync(cancellationToken).ConfigureAwait(false);
            return await translator.TranslateToRussianAsync(englishText, cancellationToken).ConfigureAwait(false);
        }

        // The application-level host owns the underlying offline model/process.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task<ITextTranslator>? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            pending = _load;
        }
        try
        {
            if (pending is not null)
            {
                try { await (await pending.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (InvalidOperationException) { /* Startup failed; no model to release. */ }
            }
        }
        finally { _lifetime.Dispose(); }
    }
}
