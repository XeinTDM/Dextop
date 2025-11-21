using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice;

namespace DextopClient.Services;

public sealed class DesktopDuplicator : IDisposable
{
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0006);
    private ID3D11Device device;
    private ID3D11DeviceContext context;
    private IDXGIOutputDuplication duplication;
    private ID3D11Texture2D stagingTexture;
    private readonly int adapterIndex;
    private readonly int outputIndex;
    private readonly object captureLock = new();
    private bool disposed;
    private int invalidArgCount;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public DesktopDuplicator(int adapterIndex = 0, int outputIndex = 0)
    {
        this.adapterIndex = adapterIndex;
        this.outputIndex = outputIndex;
        (device, context, duplication, stagingTexture, Width, Height) = CreateDuplication(adapterIndex, outputIndex);
    }

    private static (ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication, ID3D11Texture2D staging, int width, int height) CreateDuplication(int adapterIndex, int outputIndex)
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_12_1,
            FeatureLevel.Level_12_0,
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0
        };

        Result creationResult = D3D11.D3D11CreateDevice(
            adapter: null,
            driverType: DriverType.Hardware,
            flags: DeviceCreationFlags.BgraSupport | DeviceCreationFlags.Singlethreaded,
            featureLevels: featureLevels,
            device: out ID3D11Device device,
            featureLevel: out FeatureLevel _,
            immediateContext: out ID3D11DeviceContext context);

        creationResult.CheckError();
        

        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        Result adapterResult = factory.EnumAdapters1((uint)adapterIndex, out IDXGIAdapter1 adapter);
        adapterResult.CheckError();
        using IDXGIAdapter1 adapter1 = adapter;

        Result outputResult = adapter1.EnumOutputs((uint)outputIndex, out IDXGIOutput output);
        outputResult.CheckError();
        RawRect desktopBounds = output.Description.DesktopCoordinates;
        int width = desktopBounds.Right - desktopBounds.Left;
        int height = desktopBounds.Bottom - desktopBounds.Top;

        using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
        IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);

        var textureDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        ID3D11Texture2D stagingTexture = device.CreateTexture2D(textureDesc) ?? throw new InvalidOperationException("Failed to create staging texture.");
        return (device, context, duplication, stagingTexture, width, height);
    }

    public bool TryCaptureFrame(out Bitmap? bitmap, out Rectangle[] dirtyRectangles, out bool timedOut, int timeoutMs = 33)
    {
        bitmap = null;
        dirtyRectangles = Array.Empty<Rectangle>();
        timedOut = false;

        lock (captureLock)
        {
            bool frameAcquired = false;
            IDXGIResource? desktopResource = null;

            try
            {
                Result result = duplication.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo _, out desktopResource);
                if (result.Code == DxgiErrorWaitTimeout)
                {
                    timedOut = true;
                    return false;
                }

                result.CheckError();
                frameAcquired = true;

                using ID3D11Texture2D frameTexture = desktopResource!.QueryInterface<ID3D11Texture2D>();
                context.CopyResource(stagingTexture, frameTexture);

                context.Map(stagingTexture, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped);
                try
                {
                    bitmap = CopyMappedResourceToBitmap(mapped);
                }
                finally
                {
                    context.Unmap(stagingTexture, 0);
                }

                dirtyRectangles = GetDirtyRectangles();
                return true;
            }
            catch (Exception ex)
            {
                if (IsInvalidArg(ex))
                {
                    int hits = Interlocked.Increment(ref invalidArgCount);
                    if (hits <= 3)
                    {
                        RecreateDuplicationLocked();
                        return false;
                    }

                    Console.WriteLine("DesktopDuplication invalid-argument persisted after retries; disabling duplication.");
                    throw;
                }

                Console.WriteLine($"DesktopDuplication error: {ex.Message}");
                return false;
            }
            finally
            {
                desktopResource?.Dispose();
                if (frameAcquired)
                {
                    duplication.ReleaseFrame();
                }
            }
        }
    }

    private Rectangle[] GetDirtyRectangles()
    {
        uint rectSize = (uint)Unsafe.SizeOf<RawRect>();
        if (rectSize == 0)
        {
            return Array.Empty<Rectangle>();
        }

        Result infoResult = duplication.GetFrameDirtyRects(0, null, out uint requiredBytes);
        if (requiredBytes == 0)
        {
            if (infoResult.Failure)
            {
                infoResult.CheckError();
            }

            return Array.Empty<Rectangle>();
        }

        int rectCount = (int)Math.Min(int.MaxValue, (requiredBytes + rectSize - 1) / rectSize);
        rectCount = Math.Max(1, rectCount);
        var rawRects = new RawRect[rectCount];
        ulong bufferSize = (ulong)rectCount * rectSize;
        uint bufferSizeUInt = bufferSize > uint.MaxValue ? uint.MaxValue : (uint)bufferSize;
        duplication.GetFrameDirtyRects(bufferSizeUInt, rawRects, out uint filledBytes).CheckError();

        int actualCount = (int)(filledBytes / rectSize);
        actualCount = Math.Min(actualCount, rawRects.Length);
        if (actualCount <= 0)
        {
            return Array.Empty<Rectangle>();
        }

        var rectangles = new Rectangle[actualCount];
        for (int i = 0; i < actualCount; i++)
        {
            var raw = rawRects[i];
            rectangles[i] = Rectangle.FromLTRB(raw.Left, raw.Top, raw.Right, raw.Bottom);
        }

        return rectangles;
    }

    private unsafe Bitmap CopyMappedResourceToBitmap(MappedSubresource mapped)
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            byte* dest = (byte*)data.Scan0;
            byte* src = (byte*)mapped.DataPointer;
            int destStride = data.Stride;
            int srcStride = (int)mapped.RowPitch;
            int bytesToCopy = Math.Min(destStride, srcStride);

            for (int row = 0; row < Height; row++)
            {
                Buffer.MemoryCopy(src + (long)row * srcStride, dest + (long)row * destStride, destStride, bytesToCopy);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            stagingTexture.Dispose();
            duplication.Dispose();
            context.Dispose();
            device.Dispose();
            disposed = true;
        }
    }

    private static bool IsInvalidArg(Exception ex)
    {
        const int E_INVALIDARG = unchecked((int)0x80070057);
        return ex.HResult == E_INVALIDARG;
    }

    private void RecreateDuplicationLocked()
    {
        try
        {
            stagingTexture?.Dispose();
            duplication?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
        catch
        {
            // ignore cleanup errors
        }

        (device, context, duplication, stagingTexture, int width, int height) = CreateDuplication(adapterIndex, outputIndex);
        Width = width;
        Height = height;
        invalidArgCount = 0;
    }
}
