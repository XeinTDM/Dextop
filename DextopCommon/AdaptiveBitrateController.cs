using System.Diagnostics;

namespace DextopCommon;

public class AdaptiveBitrateController
{
    private readonly object lockObject = new();
    private uint sequenceId = 0;
    private byte[]? lastFrameHash;
    private DateTime lastQualityAdjustment = DateTime.UtcNow;
    private readonly Queue<double> recentEncodeTimes = new();
    private readonly Queue<double> recentSendTimes = new();
    private readonly Queue<int> recentQueueDepths = new();
    private readonly Queue<double> recentFps = new();
    private const int SAMPLE_WINDOW = 10;
    
    // Adaptive bitrate parameters
    private int currentQuality = 35;
    private float currentScaleFactor = 1.0f;
    private const int MIN_QUALITY = 10;
    private const int MAX_QUALITY = 90;
    private const float MIN_SCALE = 0.25f;
    private const float MAX_SCALE = 1.0f;
    
    // Performance targets
    private const double TARGET_FPS = 60.0;
    private const double MAX_ACCEPTABLE_LATENCY = 50.0; // ms
    private const int MAX_QUEUE_DEPTH = 3;
    private const double TARGET_ENCODE_TIME = 20.0; // ms
    
    public int CurrentQuality => currentQuality;
    public float CurrentScaleFactor => currentScaleFactor;
    public uint NextSequenceId { get { lock (lockObject) { return ++sequenceId; } } }
    
    public AdaptiveBitrateController()
    {
        // Initialize with moderate quality
        currentQuality = 35;
        currentScaleFactor = 1.0f;
    }
    
    public bool ShouldDropFrame(byte[] currentFrameHash)
    {
        lock (lockObject)
        {
            // Drop frame if it's identical to the previous one
            if (lastFrameHash != null && currentFrameHash.Length == lastFrameHash.Length)
            {
                bool identical = true;
                for (int i = 0; i < currentFrameHash.Length; i++)
                {
                    if (currentFrameHash[i] != lastFrameHash[i])
                    {
                        identical = false;
                        break;
                    }
                }
                if (identical)
                {
                    return true;
                }
            }
            
            lastFrameHash = new byte[currentFrameHash.Length];
            Buffer.BlockCopy(currentFrameHash, 0, lastFrameHash, 0, currentFrameHash.Length);
            return false;
        }
    }
    
    public void RecordMetrics(double encodeTimeMs, double sendTimeMs, int queueDepth, double fps)
    {
        lock (lockObject)
        {
            // Add to rolling windows
            recentEncodeTimes.Enqueue(encodeTimeMs);
            recentSendTimes.Enqueue(sendTimeMs);
            recentQueueDepths.Enqueue(queueDepth);
            recentFps.Enqueue(fps);
            
            // Keep only recent samples
            if (recentEncodeTimes.Count > SAMPLE_WINDOW) recentEncodeTimes.Dequeue();
            if (recentSendTimes.Count > SAMPLE_WINDOW) recentSendTimes.Dequeue();
            if (recentQueueDepths.Count > SAMPLE_WINDOW) recentQueueDepths.Dequeue();
            if (recentFps.Count > SAMPLE_WINDOW) recentFps.Dequeue();
            
            // Adjust quality every 1 second minimum (for testing)
            if (DateTime.UtcNow - lastQualityAdjustment < TimeSpan.FromSeconds(1))
                return;
                
            // Calculate averages
            double avgEncodeTime = recentEncodeTimes.Count > 0 ? recentEncodeTimes.Average() : 0;
            double avgSendTime = recentSendTimes.Count > 0 ? recentSendTimes.Average() : 0;
            double avgQueueDepth = recentQueueDepths.Count > 0 ? recentQueueDepths.Average() : 0;
            double avgFps = recentFps.Count > 0 ? recentFps.Average() : 0;
            
            bool adjusted = false;
            
            // Priority 1: Reduce quality if queue is backing up
            if (avgQueueDepth > MAX_QUEUE_DEPTH)
            {
                if (currentQuality > MIN_QUALITY)
                {
                    currentQuality = Math.Max(MIN_QUALITY, currentQuality - 10);
                    adjusted = true;
                }
                else if (currentScaleFactor > MIN_SCALE)
                {
                    currentScaleFactor = Math.Max(MIN_SCALE, currentScaleFactor - 0.1f);
                    adjusted = true;
                }
            }
            
            // Priority 2: Reduce quality if encoding is too slow
            else if (avgEncodeTime > TARGET_ENCODE_TIME)
            {
                if (currentQuality > MIN_QUALITY)
                {
                    currentQuality = Math.Max(MIN_QUALITY, currentQuality - 5);
                    adjusted = true;
                }
                else if (currentScaleFactor > MIN_SCALE)
                {
                    currentScaleFactor = Math.Max(MIN_SCALE, currentScaleFactor - 0.05f);
                    adjusted = true;
                }
            }
            
            // Priority 3: Reduce quality if FPS is too low
            else if (avgFps < TARGET_FPS * 0.8) // Less than 80% of target
            {
                if (currentQuality > MIN_QUALITY)
                {
                    currentQuality = Math.Max(MIN_QUALITY, currentQuality - 3);
                    adjusted = true;
                }
            }
            
            // Priority 4: Increase quality if we have headroom
            else if (avgQueueDepth < 1 && avgEncodeTime < TARGET_ENCODE_TIME * 0.6 && avgFps >= TARGET_FPS * 0.95)
            {
                if (currentScaleFactor < MAX_SCALE)
                {
                    currentScaleFactor = Math.Min(MAX_SCALE, currentScaleFactor + 0.05f);
                    adjusted = true;
                }
                else if (currentQuality < MAX_QUALITY)
                {
                    currentQuality = Math.Min(MAX_QUALITY, currentQuality + 2);
                    adjusted = true;
                }
            }
            
            if (adjusted)
            {
                lastQualityAdjustment = DateTime.UtcNow;
                Console.WriteLine($"[AdaptiveBitrate] Adjusted: Quality={currentQuality}, Scale={currentScaleFactor:F2}, " +
                                $"FPS={avgFps:F1}, Queue={avgQueueDepth:F1}, Encode={avgEncodeTime:F1}ms");
            }
        }
    }
    
    public (int width, int height) GetScaledDimensions(int originalWidth, int originalHeight)
    {
        lock (lockObject)
        {
            int scaledWidth = (int)(originalWidth * currentScaleFactor);
            int scaledHeight = (int)(originalHeight * currentScaleFactor);
            
            // Ensure minimum dimensions
            scaledWidth = Math.Max(320, scaledWidth);
            scaledHeight = Math.Max(240, scaledHeight);
            
            // Ensure even dimensions for better JPEG encoding
            scaledWidth = (scaledWidth / 2) * 2;
            scaledHeight = (scaledHeight / 2) * 2;
            
            return (scaledWidth, scaledHeight);
        }
    }
    
    public void Reset()
    {
        lock (lockObject)
        {
            sequenceId = 0;
            lastFrameHash = null;
            lastQualityAdjustment = DateTime.UtcNow;
            recentEncodeTimes.Clear();
            recentSendTimes.Clear();
            recentQueueDepths.Clear();
            recentFps.Clear();
            currentQuality = 35;
            currentScaleFactor = 1.0f;
        }
    }
}