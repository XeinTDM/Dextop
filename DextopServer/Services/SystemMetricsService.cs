using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace DextopServer.Services;

public sealed class SystemMetricsService : IDisposable
{
    private readonly Timer timer;
    private readonly object sync = new();
    private bool disposed;
    private PerformanceCounter? cpuCounter;
    private List<PerformanceCounter>? gpuCounters;
    private bool primed;

    public event Action<double, double>? MetricsUpdated; // cpuPercent, gpuPercent

    public SystemMetricsService(TimeSpan? interval = null)
    {
        InitializeCounters();
        timer = new Timer(OnTick, null, interval ?? TimeSpan.FromSeconds(1), interval ?? TimeSpan.FromSeconds(1));
    }

    private void InitializeCounters()
    {
        try
        {
            cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _ = cpuCounter.NextValue(); // prime
        }
        catch
        {
            cpuCounter = null;
        }

        try
        {
            gpuCounters = new List<PerformanceCounter>();
            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                if (instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                        _ = pc.NextValue(); // prime
                        gpuCounters.Add(pc);
                    }
                    catch { /* ignore individual counter failures */ }
                }
            }
        }
        catch
        {
            gpuCounters = null;
        }

        primed = true;
    }

    private void OnTick(object? state)
    {
        if (disposed) return;
        double cpu = 0;
        double gpu = 0;

        try
        {
            if (cpuCounter is not null)
            {
                cpu = Math.Clamp(cpuCounter.NextValue(), 0, 100);
            }
        }
        catch { cpu = 0; }

        try
        {
            if (gpuCounters is not null && gpuCounters.Count > 0)
            {
                double sum = 0;
                foreach (var c in gpuCounters)
                {
                    try { sum += c.NextValue(); } catch { }
                }
                // Sum may exceed 100 in multi-engine; clamp to 100
                gpu = Math.Clamp(sum, 0, 100);
            }
        }
        catch { gpu = 0; }

        try
        {
            MetricsUpdated?.Invoke(cpu, gpu);
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Dispose();
        cpuCounter?.Dispose();
        if (gpuCounters is not null)
        {
            foreach (var c in gpuCounters) c.Dispose();
        }
    }
}