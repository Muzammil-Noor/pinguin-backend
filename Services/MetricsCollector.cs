using System.Collections.Concurrent;

namespace Pinguin.Services;

// PRD 13.3: implicit metrics only -- aggregate counters and a connections-over-time series.
// Nothing here is keyed by username, IP or connection id, and it all dies with the process.
public class MetricsCollector
{
    private long _currentConnections;
    private long _totalConnections;
    private long _peakConnections;
    private long _messages;
    private long _voiceSignals;
    private long _whiteboardEvents;
    private long _aiPrompts;

    // 30-second samples, capped to an hour of history.
    private const int MaxSamples = 120;
    private readonly ConcurrentQueue<(DateTime At, long Count)> _samples = new();

    public long CurrentConnections => Interlocked.Read(ref _currentConnections);
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long PeakConnections => Interlocked.Read(ref _peakConnections);
    public long Messages => Interlocked.Read(ref _messages);
    public long VoiceSignals => Interlocked.Read(ref _voiceSignals);
    public long WhiteboardEvents => Interlocked.Read(ref _whiteboardEvents);
    public long AiPrompts => Interlocked.Read(ref _aiPrompts);

    public void ConnectionOpened()
    {
        var current = Interlocked.Increment(ref _currentConnections);
        Interlocked.Increment(ref _totalConnections);

        // CAS loop: two connections racing must not lose a peak update.
        long peak;
        while (current > (peak = Interlocked.Read(ref _peakConnections)))
        {
            if (Interlocked.CompareExchange(ref _peakConnections, current, peak) == peak) break;
        }
    }

    public void ConnectionClosed() => Interlocked.Decrement(ref _currentConnections);

    public void CountMessage() => Interlocked.Increment(ref _messages);
    public void CountVoiceSignal() => Interlocked.Increment(ref _voiceSignals);
    public void CountWhiteboardEvent() => Interlocked.Increment(ref _whiteboardEvents);
    public void CountAiPrompt() => Interlocked.Increment(ref _aiPrompts);

    public void Sample()
    {
        _samples.Enqueue((DateTime.UtcNow, CurrentConnections));
        while (_samples.Count > MaxSamples) _samples.TryDequeue(out _);
    }

    public IReadOnlyList<(DateTime At, long Count)> GetSamples() => _samples.ToList();
}

public class MetricsSamplingService : BackgroundService
{
    private readonly MetricsCollector _metrics;

    public MetricsSamplingService(MetricsCollector metrics) => _metrics = metrics;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _metrics.Sample();
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
