using System.Diagnostics;

namespace DextopCommon;

public class PerformanceMetrics
{
    private readonly object lockObject = new();
    private long captureTimeMs;
    private long encodeTimeMs;
    private long decodeTimeMs;
    private int droppedFrames;
    private long bytesSent;
    private long bytesReceived;
    private int queueDepth;
    private double clientFps;
    private double serverFps;
    private long processMemoryMb;
    private double processCpuPercent;
    private int adaptiveQualityLevel;

    public long CaptureTimeMs
    {
        get { lock (lockObject) { return captureTimeMs; } }
        set { lock (lockObject) { captureTimeMs = value; } }
    }

    public long EncodeTimeMs
    {
        get { lock (lockObject) { return encodeTimeMs; } }
        set { lock (lockObject) { encodeTimeMs = value; } }
    }

    public long DecodeTimeMs
    {
        get { lock (lockObject) { return decodeTimeMs; } }
        set { lock (lockObject) { decodeTimeMs = value; } }
    }

    public int DroppedFrames
    {
        get { lock (lockObject) { return droppedFrames; } }
        set { lock (lockObject) { droppedFrames = value; } }
    }

    public long BytesSent
    {
        get { lock (lockObject) { return bytesSent; } }
        set { lock (lockObject) { bytesSent = value; } }
    }

    public long BytesReceived
    {
        get { lock (lockObject) { return bytesReceived; } }
        set { lock (lockObject) { bytesReceived = value; } }
    }

    public int QueueDepth
    {
        get { lock (lockObject) { return queueDepth; } }
        set { lock (lockObject) { queueDepth = value; } }
    }

    public double ClientFps
    {
        get { lock (lockObject) { return clientFps; } }
        set { lock (lockObject) { clientFps = value; } }
    }

    public double ServerFps
    {
        get { lock (lockObject) { return serverFps; } }
        set { lock (lockObject) { serverFps = value; } }
    }

    public long ProcessMemoryMb
    {
        get { lock (lockObject) { return processMemoryMb; } }
        set { lock (lockObject) { processMemoryMb = value; } }
    }

    public double ProcessCpuPercent
    {
        get { lock (lockObject) { return processCpuPercent; } }
        set { lock (lockObject) { processCpuPercent = value; } }
    }

    public int AdaptiveQualityLevel
    {
        get { lock (lockObject) { return adaptiveQualityLevel; } }
        set { lock (lockObject) { adaptiveQualityLevel = value; } }
    }

    public double BandwidthMbps
    {
        get
        {
            lock (lockObject)
            {
                long totalBytes = bytesSent + bytesReceived;
                return totalBytes > 0 ? (totalBytes / (1024.0 * 1024.0)) : 0;
            }
        }
    }

    public long TotalProcessingTimeMs
    {
        get { lock (lockObject) { return captureTimeMs + encodeTimeMs + decodeTimeMs; } }
    }

    public void IncrementBytesReceived(long bytes)
    {
        lock (lockObject) { bytesReceived += bytes; }
    }

    public void IncrementBytesSent(long bytes)
    {
        lock (lockObject) { bytesSent += bytes; }
    }

    public void Reset()
    {
        lock (lockObject)
        {
            captureTimeMs = 0;
            encodeTimeMs = 0;
            decodeTimeMs = 0;
            droppedFrames = 0;
            bytesSent = 0;
            bytesReceived = 0;
            queueDepth = 0;
            clientFps = 0;
            serverFps = 0;
            processMemoryMb = 0;
            processCpuPercent = 0;
            adaptiveQualityLevel = 0;
        }
    }

    public PerformanceMetricsSnapshot GetSnapshot()
    {
        lock (lockObject)
        {
            return new PerformanceMetricsSnapshot
            {
                CaptureTimeMs = captureTimeMs,
                EncodeTimeMs = encodeTimeMs,
                DecodeTimeMs = decodeTimeMs,
                DroppedFrames = droppedFrames,
                BytesSent = bytesSent,
                BytesReceived = bytesReceived,
                QueueDepth = queueDepth,
                ClientFps = clientFps,
                ServerFps = serverFps,
                ProcessMemoryMb = processMemoryMb,
                ProcessCpuPercent = processCpuPercent,
                AdaptiveQualityLevel = adaptiveQualityLevel,
                Timestamp = DateTime.UtcNow
            };
        }
    }
}

public class PerformanceMetricsSnapshot
{
    public long CaptureTimeMs { get; set; }
    public long EncodeTimeMs { get; set; }
    public long DecodeTimeMs { get; set; }
    public int DroppedFrames { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public int QueueDepth { get; set; }
    public double ClientFps { get; set; }
    public double ServerFps { get; set; }
    public long ProcessMemoryMb { get; set; }
    public double ProcessCpuPercent { get; set; }
    public int AdaptiveQualityLevel { get; set; }
    public DateTime Timestamp { get; set; }

    public override string ToString()
    {
        return $"[{Timestamp:HH:mm:ss.fff}] Client FPS: {ClientFps:F1} | Server FPS: {ServerFps:F1} | " +
               $"Bandwidth: {BytesSent + BytesReceived / (1024.0 * 1024.0):F2} MB | Quality: {AdaptiveQualityLevel} | " +
               $"CPU: {ProcessCpuPercent:F1}% | Memory: {ProcessMemoryMb} MB | " +
               $"Capture: {CaptureTimeMs}ms | Encode: {EncodeTimeMs}ms | Decode: {DecodeTimeMs}ms | " +
               $"Dropped: {DroppedFrames} | Queue: {QueueDepth}";
    }
}
