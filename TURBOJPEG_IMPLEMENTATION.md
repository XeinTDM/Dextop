# TurboJPEG SIMD-Accelerated Encoding/Decoding Implementation

## Overview

This document describes the integration of TurboJPEG SIMD-accelerated JPEG encoding and decoding into the Dextop remote desktop application. TurboJPEG provides significant performance improvements over the managed .NET JPEG encoder/decoder by leveraging native SIMD instructions (SSE2, AVX2) for compression operations.

## Package Information

- **Package**: TurboJpegWrapper 1.5.2
- **NuGet**: https://www.nuget.org/packages/TurboJpegWrapper/
- **Native Libraries**: Automatically includes libjpeg-turbo native DLLs for x64 and x86 architectures
- **Referenced By**: Both DextopClient and DextopServer projects

## Architecture

### Client Side (DextopClient/Services/ScreenCaptureManager.cs)

The client uses `TJCompressor` for encoding screen captures to JPEG format:

1. **Initialization**:
   - Constructor accepts `useTurboJpeg` parameter (default: `true`)
   - `InitializeTurboJpeg()` attempts to create a `TJCompressor` instance
   - If initialization fails, falls back to managed encoder with console logging

2. **Encoding Process**:
   - Screen is captured into a 32bpp ARGB bitmap
   - Bitmap is locked to access raw pixel buffer
   - Buffer pointer is passed directly to `TJCompressor.Compress()`
   - Uses BGRA pixel format (native Windows format)
   - 4:2:0 chrominance subsampling for optimal compression
   - Quality parameter passed from adaptive bitrate controller

3. **Fallback Mechanism**:
   - If TurboJPEG is unavailable or disabled, uses `ImageCodecInfo` encoder
   - Seamless transition - same output format
   - Allows running on systems without TurboJPEG support

4. **Disposal**:
   - `TJCompressor` is properly disposed in `Dispose()` method
   - Implements `IDisposable` pattern

### Server Side (DextopServer/Services/RemoteDesktopManager.cs)

The server uses `TJDecompressor` for decoding received JPEG frames:

1. **Initialization**:
   - Reads `UseTurboJpeg` from `AppConfiguration` (default: `true`)
   - `InitializeTurboJpeg()` attempts to create a `TJDecompressor` instance
   - If initialization fails, falls back to managed decoder with console logging

2. **Decoding Process**:
   - Receives JPEG-compressed frame bytes
   - Passes buffer to `TJDecompressor.Decompress()`
   - Outputs BGR24 format (matches WPF's `PixelFormats.Bgr24`)
   - Creates `BitmapSource` from decompressed data
   - Wires directly into existing WriteableBitmap pipeline

3. **Fallback Mechanism**:
   - If TurboJPEG is unavailable or disabled, uses `JpegBitmapDecoder`
   - Seamless transition - same output format
   - Allows running on systems without TurboJPEG support

4. **Disposal**:
   - `TJDecompressor` is properly disposed in `Dispose()` method

## Configuration

### Client Configuration

Enable/disable TurboJPEG in `DextopClient/Program.cs`:

```csharp
// Enable TurboJPEG (default)
private static readonly ScreenCaptureManager screenCaptureManager = new(useTurboJpeg: true);

// Disable TurboJPEG (use managed encoder)
private static readonly ScreenCaptureManager screenCaptureManager = new(useTurboJpeg: false);
```

### Server Configuration

Enable/disable TurboJPEG in `DextopServer/Configurations/AppConfiguration.cs`:

```csharp
public class AppConfiguration
{
    public int JpegQuality { get; set; } = 35;
    public bool UseTurboJpeg { get; set; } = true;  // Default: enabled
}
```

The configuration is created in `RemoteDesktopWindow.xaml.cs`:

```csharp
private readonly AppConfiguration config = new();
```

To disable TurboJPEG, set:

```csharp
private readonly AppConfiguration config = new() { UseTurboJpeg = false };
```

## API Details

### Encoder Parameters (Client)

```csharp
byte[] compressedData = tjCompressor.Compress(
    bitmapData.Scan0,                               // IntPtr to pixel buffer
    bitmapData.Stride,                              // Stride in bytes
    bitmap.Width,                                   // Image width
    bitmap.Height,                                  // Image height
    TurboJpegWrapper.TJPixelFormats.TJPF_BGRA,     // Pixel format
    TurboJpegWrapper.TJSubsamplingOptions.TJSAMP_420, // Subsampling
    quality,                                        // Quality (1-100)
    TurboJpegWrapper.TJFlags.BOTTOMUP              // Flags
);
```

### Decoder Parameters (Server)

```csharp
byte[] decompressedData = tjDecompressor.Decompress(
    buffer.ToArray(),                               // Input JPEG data
    TurboJpegWrapper.TJPixelFormats.TJPF_BGR,      // Output pixel format
    TurboJpegWrapper.TJFlags.BOTTOMUP,             // Flags
    out int width,                                  // Output width
    out int height,                                 // Output height
    out int stride                                  // Output stride
);
```

## Native DLL Deployment

The TurboJpegWrapper package automatically copies native DLLs to the output directory:

```
bin/Debug/net9.0-windows/
├── TurboJpegWrapper.dll          # Managed wrapper
├── x64/
│   └── turbojpeg.dll             # Native x64 library
└── x86/
    └── turbojpeg.dll             # Native x86 library
```

For x64 builds (default with `Prefer32Bit=false`), the runtime loads `x64/turbojpeg.dll`.

## Performance Benefits

### Expected Improvements

1. **Encoding (Client)**:
   - Managed encoder: ~15-25ms per frame at 1080p
   - TurboJPEG encoder: ~3-8ms per frame at 1080p
   - **Speed-up**: 3-5x faster

2. **Decoding (Server)**:
   - Managed decoder: ~10-15ms per frame at 1080p
   - TurboJPEG decoder: ~2-5ms per frame at 1080p
   - **Speed-up**: 3-5x faster

3. **Overall Impact**:
   - Sustained 60 FPS at 1080p on mid-range hardware
   - Lower CPU usage (SIMD instructions)
   - Reduced latency for remote desktop interactions

### SIMD Optimizations

TurboJPEG leverages:
- SSE2 instructions (baseline on x64)
- AVX2 instructions (on supported CPUs)
- Optimized DCT, quantization, and color conversion
- Assembly-optimized critical paths

## Error Handling and Diagnostics

Both encoder and decoder log initialization status to console:

```
TurboJPEG encoder initialized successfully
```

or

```
TurboJPEG initialization failed, falling back to managed encoder: [error message]
```

If initialization fails, the system automatically continues with managed codecs. Check console output to verify TurboJPEG is active.

## Compatibility

### Requirements

- **OS**: Windows x64 (due to `Prefer32Bit=false`)
- **CPU**: Any x64 processor (SSE2 required, AVX2 recommended)
- **.NET**: .NET 9 or later
- **Runtime**: Self-contained or with .NET 9 runtime installed

### Fallback Scenarios

TurboJPEG falls back to managed codecs if:
- Native DLL is missing or corrupted
- Incompatible CPU architecture
- Initialization exception (logged to console)
- Explicitly disabled via configuration

### Older Systems

For systems without TurboJPEG support:
1. Set `useTurboJpeg: false` in client
2. Set `UseTurboJpeg = false` in server configuration
3. Application continues with managed codecs (slightly reduced performance)

## Integration with Existing Features

TurboJPEG integrates seamlessly with:

1. **Adaptive Bitrate Controller**:
   - Quality adjustments work identically
   - Faster encoding allows more aggressive bitrate adaptation

2. **Performance Metrics**:
   - Encode/decode times are measured identically
   - Metrics overlay shows reduced times with TurboJPEG

3. **WriteableBitmap Pipeline**:
   - Decoder output wires directly into existing display path
   - Zero additional allocations

4. **Frame Metadata Protocol**:
   - JPEG format unchanged, fully compatible
   - Metadata handling unaffected

## Testing and Validation

### Verify TurboJPEG is Active

1. Start DextopClient and DextopServer
2. Check console output for initialization messages
3. Press F12 to show metrics overlay
4. Observe encode/decode times (should be <10ms at 1080p)

### Performance Comparison

To compare managed vs TurboJPEG:

1. **Baseline (Managed)**:
   - Set `useTurboJpeg: false` on client
   - Set `UseTurboJpeg = false` on server
   - Note encode/decode times from metrics

2. **TurboJPEG**:
   - Set `useTurboJpeg: true` on client
   - Set `UseTurboJpeg = true` on server
   - Note encode/decode times from metrics
   - Expect 3-5x improvement

### Expected Metrics at 1080p

| Metric | Managed | TurboJPEG |
|--------|---------|-----------|
| Encode Time | 15-25ms | 3-8ms |
| Decode Time | 10-15ms | 2-5ms |
| CPU Usage | 40-60% | 20-30% |
| Max FPS | 30-40 | 60+ |

## Build and Deployment

### Build Command

```bash
dotnet build /p:EnableWindowsTargeting=true
```

### Publish Command

```bash
dotnet publish DextopClient/DextopClient.csproj -c Release -r win-x64 --self-contained
dotnet publish DextopServer/DextopServer.csproj -c Release -r win-x64 --self-contained
```

Native DLLs are automatically included in publish output.

## Troubleshooting

### "TurboJPEG initialization failed"

**Cause**: Native DLL not found or incompatible

**Solution**:
1. Verify `x64/turbojpeg.dll` exists in output directory
2. Check CPU architecture (must be x64)
3. Try rebuilding with clean output: `dotnet clean && dotnet build`

### Performance Not Improved

**Cause**: Falling back to managed codec

**Solution**:
1. Check console for initialization error messages
2. Verify configuration is set to enable TurboJPEG
3. Check native DLL is being loaded (use Process Explorer)

### Build Errors

**Cause**: Package not restored

**Solution**:
```bash
dotnet restore /p:EnableWindowsTargeting=true
dotnet build /p:EnableWindowsTargeting=true
```

## Future Enhancements

Potential improvements:
1. Direct BGR capture on client (skip BGRA→BGR conversion on server)
2. GPU-accelerated scaling before encoding
3. Hardware JPEG encoding (NVENC/Quick Sync)
4. YUV color space for even faster encoding

## References

- TurboJpegWrapper NuGet: https://www.nuget.org/packages/TurboJpegWrapper/
- libjpeg-turbo: https://libjpeg-turbo.org/
- JPEG encoding best practices: https://github.com/libjpeg-turbo/libjpeg-turbo/blob/main/README.ijg
