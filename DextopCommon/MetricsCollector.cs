using System.Diagnostics;

namespace DextopCommon;

public class MetricsCollector : IDisposable
{
    private readonly PerformanceMetrics metrics;
    private Process? currentProcess;
    private DateTime lastCpuCheck = DateTime.UtcNow;
    private TimeSpan lastTotalProcessorTime = TimeSpan.Zero;
    private readonly int processorCount;
    private string? metricsLogPath;
    private bool isRecording;
    private readonly object recordingLock = new();
    private CancellationTokenSource? recordingCts;
    private Task? recordingTask;

    public PerformanceMetrics Metrics => metrics;

    public MetricsCollector()
    {
        metrics = new PerformanceMetrics();
        processorCount = Environment.ProcessorCount;
        try
        {
            currentProcess = Process.GetCurrentProcess();
            lastTotalProcessorTime = currentProcess.TotalProcessorTime;
        }
        catch { }
    }

    public void RecordCaptureTime(long milliseconds)
    {
        metrics.CaptureTimeMs = milliseconds;
    }

    public void RecordEncodeTime(long milliseconds)
    {
        metrics.EncodeTimeMs = milliseconds;
    }

    public void RecordDecodeTime(long milliseconds)
    {
        metrics.DecodeTimeMs = milliseconds;
    }

    public void RecordBytesSent(long bytes)
    {
        metrics.IncrementBytesSent(bytes);
    }

    public void RecordBytesReceived(long bytes)
    {
        metrics.IncrementBytesReceived(bytes);
    }

    public void RecordClientFps(double fps)
    {
        metrics.ClientFps = fps;
    }

    public void RecordServerFps(double fps)
    {
        metrics.ServerFps = fps;
    }

    public void RecordQueueDepth(int depth)
    {
        metrics.QueueDepth = depth;
    }

    public void RecordDroppedFrame()
    {
        metrics.DroppedFrames++;
    }

    public void RecordAdaptiveQualityLevel(int level)
    {
        metrics.AdaptiveQualityLevel = level;
    }

    public void UpdateCpuAndMemory()
    {
        if (currentProcess == null)
            return;

        try
        {
            // Update memory (can be called frequently, it's fast)
            long memoryBytes = currentProcess.WorkingSet64;
            metrics.ProcessMemoryMb = memoryBytes / (1024 * 1024);

            // Update CPU (throttle to once per second to avoid overhead)
            DateTime now = DateTime.UtcNow;
            if ((now - lastCpuCheck).TotalSeconds >= 1.0)
            {
                currentProcess.Refresh();
                TimeSpan currentTotalProcessorTime = currentProcess.TotalProcessorTime;
                double cpuUsedMs = (currentTotalProcessorTime - lastTotalProcessorTime).TotalMilliseconds;
                double totalMsPassed = (now - lastCpuCheck).TotalMilliseconds;
                double cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
                metrics.ProcessCpuPercent = cpuUsageTotal * 100;

                lastCpuCheck = now;
                lastTotalProcessorTime = currentTotalProcessorTime;
            }
        }
        catch { }
    }

    public void StartRecordingMetrics(string? outputDirectory = null)
    {
        lock (recordingLock)
        {
            if (isRecording)
                return;

            outputDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "DextopMetrics");
            Directory.CreateDirectory(outputDirectory);

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            metricsLogPath = Path.Combine(outputDirectory, $"metrics_{timestamp}.log");

            isRecording = true;
            recordingCts = new CancellationTokenSource();
            recordingTask = RecordMetricsLoop(recordingCts.Token);
        }
    }

    public void StopRecordingMetrics()
    {
        lock (recordingLock)
        {
            if (!isRecording)
                return;

            isRecording = false;
            recordingCts?.Cancel();
        }

        if (recordingTask != null)
        {
            try
            {
                recordingTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
    }

    private async Task RecordMetricsLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = metrics.GetSnapshot();
                try
                {
                    await File.AppendAllTextAsync(metricsLogPath!, snapshot.ToString() + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                }
                catch { }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public bool IsRecording
    {
        get { lock (recordingLock) { return isRecording; } }
    }

    public string? GetMetricsLogPath()
    {
        lock (recordingLock) { return metricsLogPath; }
    }

    public void Dispose()
    {
        StopRecordingMetrics();
        recordingCts?.Dispose();
    }
}
