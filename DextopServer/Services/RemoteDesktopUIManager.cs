using System.Diagnostics;
using DextopCommon;

namespace DextopServer.Services;

public class RemoteDesktopUIManager : IDisposable
{
    private readonly Stopwatch instantStopwatch;
    private readonly Stopwatch averageStopwatch;
    private int frameCount = 0;
    private int totalFrameCount = 0;
    private double currentInstantFps = 0;
    private double currentAverageFps = 0;
    private readonly double averageUpdateInterval;
    private readonly MetricsCollector metricsCollector;
    private bool hasPendingFpsUpdate;

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

    public void RecordFrame()
    {
        frameCount++;
        totalFrameCount++;

        if (instantStopwatch.Elapsed.TotalSeconds >= 0.5)
        {
            currentInstantFps = frameCount / instantStopwatch.Elapsed.TotalSeconds;
            metricsCollector.RecordServerFps(currentInstantFps);
            frameCount = 0;
            instantStopwatch.Restart();
            hasPendingFpsUpdate = true;
        }

        if (averageStopwatch.Elapsed.TotalSeconds >= averageUpdateInterval)
        {
            double avgFps = totalFrameCount / averageStopwatch.Elapsed.TotalSeconds;
            currentAverageFps = avgFps;
            totalFrameCount = 0;
            averageStopwatch.Restart();
        }
    }

    public string? Update()
    {
        metricsCollector.UpdateCpuAndMemory();

        if (!hasPendingFpsUpdate)
            return null;

        hasPendingFpsUpdate = false;
        return $"FPS: {currentInstantFps:F2}  (Avg: {currentAverageFps:F2})";
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

    public void UpdateCurrentQuality(byte quality)
    {
        metricsCollector.RecordAdaptiveQualityLevel(quality);
    }

    public void UpdateCurrentResolution(int width, int height)
    {
        // Store current resolution for UI display if needed
        // For now, just log it
        Console.WriteLine($"[Resolution] Current: {width}x{height}");
    }

    public void Dispose()
    {
        instantStopwatch.Stop();
        averageStopwatch.Stop();
        metricsCollector.Dispose();
    }
}
