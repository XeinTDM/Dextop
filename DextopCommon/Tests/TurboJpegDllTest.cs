using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DextopCommon.Tests;

/// <summary>
/// Simple test class to verify TurboJPEG DLL loading logic
/// This can be used to validate the DLL search path setup without running the full application
/// </summary>
public static class TurboJpegDllTest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddDllDirectory(string NewDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    /// <summary>
    /// Test method to verify DLL loading setup
    /// </summary>
    public static void TestDllLoading()
    {
        Console.WriteLine("=== TurboJPEG DLL Loading Test ===");
        
        try
        {
            string? assemblyLocation = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(assemblyLocation))
            {
                Console.WriteLine("ERROR: Could not determine assembly location");
                return;
            }

            string assemblyDir = Path.GetDirectoryName(assemblyLocation)!;
            string platformDir = Environment.Is64BitProcess ? "x64" : "x86";
            string dllPath = Path.Combine(assemblyDir, platformDir);
            string turboJpegPath = Path.Combine(dllPath, "turbojpeg.dll");

            Console.WriteLine($"Assembly location: {assemblyLocation}");
            Console.WriteLine($"Assembly directory: {assemblyDir}");
            Console.WriteLine($"Process architecture: {(Environment.Is64BitProcess ? "x64" : "x86")}");
            Console.WriteLine($"TurboJPEG DLL path: {turboJpegPath}");

            if (!Directory.Exists(dllPath))
            {
                Console.WriteLine($"ERROR: Directory {dllPath} does not exist");
                return;
            }

            if (!File.Exists(turboJpegPath))
            {
                Console.WriteLine($"ERROR: TurboJPEG DLL not found at {turboJpegPath}");
                
                // List all files in the directory
                var files = Directory.GetFiles(dllPath);
                Console.WriteLine($"Files in {dllPath}:");
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    Console.WriteLine($"  - {Path.GetFileName(file)} ({info.Length} bytes)");
                }
                return;
            }

            var fileInfo = new FileInfo(turboJpegPath);
            Console.WriteLine($"TurboJPEG DLL found: {fileInfo.Length} bytes, modified: {fileInfo.LastWriteTime}");

            // Test AddDllDirectory
            Console.WriteLine("\nTesting AddDllDirectory...");
            if (AddDllDirectory(dllPath))
            {
                Console.WriteLine("AddDllDirectory succeeded");
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                Console.WriteLine($"AddDllDirectory failed with error code: {error}");
                
                // Try SetDllDirectory as fallback
                Console.WriteLine("Trying SetDllDirectory as fallback...");
                if (SetDllDirectory(dllPath))
                {
                    Console.WriteLine("SetDllDirectory succeeded as fallback");
                }
                else
                {
                    error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"SetDllDirectory also failed with error code: {error}");
                    return;
                }
            }

            // Test loading the DLL
            Console.WriteLine("\nTesting LoadLibrary...");
            IntPtr handle = LoadLibrary(turboJpegPath);
            if (handle != IntPtr.Zero)
            {
                Console.WriteLine($"LoadLibrary succeeded! Handle: 0x{handle:X8}");
                Console.WriteLine("TurboJPEG DLL should be available for use.");
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                Console.WriteLine($"LoadLibrary failed with error code: {error}");
                Console.WriteLine("This may indicate missing dependencies or incompatible architecture.");
            }

            Console.WriteLine("\n=== Test Complete ===");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Test failed with exception: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }
}