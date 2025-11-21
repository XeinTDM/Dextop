using System.Diagnostics;
using DextopCommon;

namespace DextopServer.Services;

public class RemoteDesktopUIManager : IDisposable
{
    private readonly Stopwatch instantStopwatch;
    private readonly Stopwatch averageStopwatch;
    private int frameCount = 0;
    private int totalFrameCount = 0;
    private double currentAverageFps = 0;
    private readonly double averageUpdateInterval;
    private readonly MetricsCollector metricsCollector;

    public RemoteDesktopUIManager(double averageIntervalSeconds = 5.0, MetricsCollector? metricsCollector = null)
    {
        instantStopwatch = new Stopwatch();
        averageStopwatch = new Stopwatch();
        averageUpdateInterval = averageIntervalSeconds;
        instantStopwatch.Start();
        averageStopwatch.Start();
        this.metricsCollector = metricsCollector ?? new MetricsCollector();
    }

    public MetricsCollector MetricsCollector => metricsCollector;

    public string? Update()
    {
        frameCount++;
        totalFrameCount++;

        string? instantFpsText = null;
        if (instantStopwatch.Elapsed.TotalSeconds >= 0.5)
        {
            double fps = frameCount / instantStopwatch.Elapsed.TotalSeconds;
            instantFpsText = $"FPS: {fps:F2}";
            metricsCollector.RecordServerFps(fps);
            frameCount = 0;
            instantStopwatch.Restart();
        }

        if (averageStopwatch.Elapsed.TotalSeconds >= averageUpdateInterval)
        {
            double avgFps = totalFrameCount / averageStopwatch.Elapsed.TotalSeconds;
            currentAverageFps = avgFps;
            totalFrameCount = 0;
            averageStopwatch.Restart();
        }

        metricsCollector.UpdateCpuAndMemory();

        if (instantFpsText != null)
        {
            return $"{instantFpsText}  (Avg: {currentAverageFps:F2})";
        }
        return null;
    }

    public void RecordBytesReceived(long bytes)
    {
        metricsCollector.RecordBytesReceived(bytes);
    }

    public void RecordBytesSent(long bytes)
    {
        metricsCollector.RecordBytesSent(bytes);
    }

    public void RecordDecodeTime(long milliseconds)
    {
        metricsCollector.RecordDecodeTime(milliseconds);
    }

    public void RecordAdaptiveQualityLevel(int level)
    {
        metricsCollector.RecordAdaptiveQualityLevel(level);
    }

    public void Dispose()
    {
        instantStopwatch.Stop();
        averageStopwatch.Stop();
        metricsCollector.Dispose();
    }
}
