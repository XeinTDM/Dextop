using System.Diagnostics;

namespace DextopServer.Services;

public class RemoteDesktopUIManager : IDisposable
{
    private readonly Stopwatch instantStopwatch;
    private readonly Stopwatch averageStopwatch;
    private int frameCount = 0;
    private int totalFrameCount = 0;
    private double currentAverageFps = 0;
    private readonly double averageUpdateInterval;

    public RemoteDesktopUIManager(double averageIntervalSeconds = 5.0)
    {
        instantStopwatch = new Stopwatch();
        averageStopwatch = new Stopwatch();
        averageUpdateInterval = averageIntervalSeconds;
        instantStopwatch.Start();
        averageStopwatch.Start();
    }

    public string? Update()
    {
        frameCount++;
        totalFrameCount++;

        string? instantFpsText = null;
        if (instantStopwatch.Elapsed.TotalSeconds >= 0.5)
        {
            double fps = frameCount / instantStopwatch.Elapsed.TotalSeconds;
            instantFpsText = $"FPS: {fps:F2}";
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

        if (instantFpsText != null)
        {
            return $"{instantFpsText}  (Avg: {currentAverageFps:F2})";
        }
        return null;
    }

    public void Dispose()
    {
        instantStopwatch.Stop();
        averageStopwatch.Stop();
    }
}