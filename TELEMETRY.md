# Performance Telemetry Overlay

This document describes the real-time performance telemetry system integrated into Dextop.

## Overview

The telemetry system provides runtime performance instrumentation to validate that the 60 FPS target is being achieved. It captures metrics from both the client (capture agent) and server (viewer) sides, with minimal overhead (<1 ms per frame).

## Metrics Collected

### Timing Metrics
- **Capture Time**: Time to capture the screen via GDI BitBlt (milliseconds)
- **Encode Time**: Time to JPEG encode the captured frame (milliseconds)
- **Decode Time**: Time to decode the JPEG on the server side (milliseconds)
- **Total Processing Time**: Sum of capture + encode + decode times

### Throughput Metrics
- **Client FPS**: Frames per second being captured and sent by the client
- **Server FPS**: Frames per second being decoded and displayed by the server
- **Bandwidth**: Total bytes sent + received (in MB), updated per frame
- **Queue Depth**: Number of frames pending in the queue

### Resource Metrics
- **CPU Usage**: Percent of CPU used by the current process (sampled every 1 second)
- **Memory Usage**: Working set memory in MB (sampled every 1 second)

### Quality Metrics
- **Adaptive Quality Level**: Current JPEG quality setting (0-100)
- **Dropped Frames**: Count of frames that were dropped during transmission

## Using the Telemetry Overlay

### Keyboard Shortcuts

**F12**: Toggle the metrics overlay display on/off
- Displays real-time performance metrics in a panel in the top-left corner of the viewer window
- Overlay updates every 500ms

**F10**: Toggle metrics recording to disk
- Starts/stops recording all metrics to a timestamped log file
- Records are saved to: `Desktop\DextopMetrics\metrics_YYYY-MM-DD_HH-mm-ss.log`
- Records are written every 500ms

**F11**: Toggle full-screen mode (existing functionality)

### Overlay Display Format

When enabled with F12, the overlay shows:

```
Performance Metrics
Client FPS: 60.0
Server FPS: 60.0
Bandwidth: 45.25 MB
Quality: 75
CPU: 12.5%
Memory: 256 MB
Capture: 2ms
Encode: 8ms
Decode: 3ms
Queue: 0
Dropped: 0
```

## Metrics Architecture

### Client Side (DextopClient)

- `ScreenCaptureManager`: Instrumented to measure capture and encode times
- `Program.cs`: Tracks client-side FPS by counting frames per second
- `MetricsCollector`: Centralized collection and optional disk recording

### Server Side (DextopServer)

- `RemoteDesktopManager`: Measures JPEG decode time
- `RemoteDesktopUIManager`: Tracks server-side FPS and updates metrics display
- `RemoteDesktopWindow`: Displays metrics overlay with F12/F10 shortcuts
- `MetricsCollector`: Shared with client for synchronized recording

### Shared Components (DextopCommon)

- `PerformanceMetrics`: Thread-safe metrics container with all metric properties
- `PerformanceMetricsSnapshot`: Point-in-time snapshot for logging
- `MetricsCollector`: Collects metrics and handles recording to disk

## Performance Overhead

The telemetry system is optimized for minimal overhead:

- **Stopwatch measurements**: <0.1ms per frame (hardware counters)
- **Memory sampling**: <0.01ms per frame (single property read)
- **CPU sampling**: <0.5ms every 1 second (only once per second)
- **Thread-safe operations**: Lock-free where possible, minimal lock contention
- **Recording**: Async file I/O on background thread

**Total overhead**: <1 ms per frame (well below 16.7ms frame budget at 60 FPS)

## Recording Metrics to Disk

When metrics recording is enabled:

1. Press F10 to start recording
2. A confirmation dialog appears
3. Metrics are recorded to `Desktop\DextopMetrics\metrics_YYYY-MM-DD_HH-mm-ss.log`
4. Each line contains a complete metrics snapshot with timestamp
5. Press F10 again to stop recording

### Log File Format

Each line contains:
```
[HH:mm:ss.fff] Client FPS: XX.X | Server FPS: XX.X | Bandwidth: XX.XX MB | Quality: XX | CPU: X.X% | Memory: XXX MB | Capture: Xms | Encode: Xms | Decode: Xms | Dropped: X | Queue: X
```

## Validating 60 FPS Performance

To validate that the 60 FPS target is being met:

1. Start the Dextop server and client
2. Press F12 to enable the metrics overlay
3. Observe the Client FPS and Server FPS values
4. Both should consistently show 60.0 (or very close)
5. For validation runs, press F10 to record metrics
6. Run typical usage scenarios
7. Analyze the recorded log file to calculate statistics

### Performance Goals

- **Client FPS**: 60.0 (consistent)
- **Server FPS**: 60.0 (consistent)
- **Total Processing Time**: <16.7ms (frame budget)
- **CPU Usage**: <25% (4-core system)
- **Memory Usage**: <500 MB
- **Dropped Frames**: 0

## Implementation Details

### Thread Safety

All metrics access is protected by locks to ensure consistency across multiple threads:
- Client capture thread updates capture/encode metrics
- Server decode thread updates decode metrics
- UI thread reads metrics for display
- Recording thread reads metrics for logging

### Memory Efficiency

- Single `PerformanceMetrics` instance per application
- Snapshots are created on-demand for logging
- No persistent history kept in memory (streaming to disk)
- Minimal allocation overhead during capture loop

### CPU Efficiency

- CPU usage is sampled every 1 second (not every frame)
- Uses Process.TotalProcessorTime for accurate measurement
- Normalized to percentage across available cores
- Lock contention minimized with coarse-grained updates

## Future Enhancements

Potential improvements for future versions:

- GC pause tracking and display
- Network latency measurement
- Frame rate variance/jitter metrics
- Per-connection bandwidth limits
- Adaptive quality adjustment based on metrics
- Historical trending and graphs
- Export metrics to CSV or JSON

## Troubleshooting

### Metrics show 0 or very low values

- Ensure both client and server are running
- Check that client is connected to server
- Verify network connectivity

### Recording doesn't start

- Ensure the Desktop\DextopMetrics directory exists or is writable
- Check application has permissions to write to Desktop
- Review console output for any error messages

### High CPU usage during recording

- Recording uses async I/O, overhead should be minimal
- If high CPU is observed, check system load
- Consider recording smaller intervals

### Overlay doesn't appear

- Press F12 to toggle overlay visibility
- Check that the server window is active
- Verify the metrics overlay control rendered properly
