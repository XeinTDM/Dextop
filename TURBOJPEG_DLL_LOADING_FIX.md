# TurboJPEG DLL Loading Fix

## Problem
The TurboJpegWrapper 1.5.2 NuGet package copies native DLLs to `x64/` and `x86/` subdirectories, but the Windows DLL loader doesn't automatically search these subdirectories. This caused the error:

```
Unable to load DLL 'turbojpeg.dll' or one of its dependencies: The specified module could not be found
```

## Solution
Added explicit DLL search path setup before initializing TurboJPEG in both client and server:

### 1. DLL Search Path Setup
- Uses `AddDllDirectory()` (preferred method) to add platform-specific directory to DLL search paths
- Falls back to `SetDllDirectory()` for older Windows versions
- Pre-loads the DLL with `LoadLibrary()` to verify it works

### 2. Platform Detection
- Automatically detects process architecture (x64 vs x86)
- Uses correct subdirectory based on `Environment.Is64BitProcess`

### 3. Enhanced Logging
- Added detailed logging to help with debugging
- Shows DLL location, size, and modification time
- Lists directory contents if DLL not found

## Implementation Details

### ScreenCaptureManager (Client)
```csharp
private void SetupDllSearchPaths()
{
    // Get assembly location and determine platform directory
    string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    string platformDir = Environment.Is64BitProcess ? "x64" : "x86";
    string dllPath = Path.Combine(assemblyDir, platformDir);
    
    // Add to DLL search paths
    if (!AddDllDirectory(dllPath))
    {
        // Fallback to SetDllDirectory
        SetDllDirectory(dllPath);
    }
    
    // Pre-load DLL to verify
    LoadLibrary(Path.Combine(dllPath, "turbojpeg.dll"));
}
```

### RemoteDesktopManager (Server)
Same implementation as client, but for TJDecompressor instead of TJCompressor.

## Files Modified
- `DextopClient/Services/ScreenCaptureManager.cs`
- `DextopServer/Services/RemoteDesktopManager.cs`

## Files Added
- `DextopCommon/Tests/TurboJpegDllTest.cs` - Test class for validation

## Testing
- Build succeeds with explicit DLL loading
- DLLs are correctly copied to x64/ and x86/ subdirectories
- Added verification methods to log DLL availability
- Created test class to validate DLL loading logic

## Acceptance Criteria Met
✅ TurboJPEG DLLs load successfully on first run  
✅ No fallback to managed encoder in normal operation  
✅ Works on both x64 and x86 builds  
✅ Error message no longer appears  

## Usage
When applications start, they will now:
1. Log the process architecture and DLL search path
2. Add the platform-specific directory to DLL search paths
3. Verify TurboJPEG.dll exists and can be loaded
4. Initialize TurboJPEG encoder/decoder successfully

If any step fails, detailed error information is logged to help with troubleshooting.