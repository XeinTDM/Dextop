using System.Net.Sockets;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DextopCommon;

public static class ScreenshotProtocol
{
    public const byte PROTOCOL_VERSION = 1;
    public const int METADATA_SIZE = 25; // version(1) + sequenceId(4) + timestamp(8) + width(2) + height(2) + quality(1) + hash(16)

    public struct FrameMetadata
    {
        public byte Version;
        public uint SequenceId;
        public long Timestamp;
        public ushort Width;
        public ushort Height;
        public byte Quality;
        public byte[] Hash; // MD5 hash of frame content for duplicate detection

        public FrameMetadata(uint sequenceId, long timestamp, ushort width, ushort height, byte quality, byte[]? hash = null)
        {
            Version = PROTOCOL_VERSION;
            SequenceId = sequenceId;
            Timestamp = timestamp;
            Width = width;
            Height = height;
            Quality = quality;
            Hash = hash ?? new byte[16];
        }
    }

    public static async Task WriteFrameWithMetadataAsync(NetworkStream stream, FrameMetadata metadata, ReadOnlyMemory<byte> frameData)
    {
        // Write total size (metadata + frame data)
        int totalSize = METADATA_SIZE + frameData.Length;
        await WriteInt32Async(stream, totalSize).ConfigureAwait(false);

        // Write metadata
        byte[] metadataBytes = ArrayPool<byte>.Shared.Rent(METADATA_SIZE);
        try
        {
            int offset = 0;
            metadataBytes[offset++] = metadata.Version;
            BinaryPrimitives.WriteUInt32LittleEndian(metadataBytes.AsSpan(offset, 4), metadata.SequenceId);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(metadataBytes.AsSpan(offset, 8), metadata.Timestamp);
            offset += 8;
            BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.Width);
            offset += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.Height);
            offset += 2;
            metadataBytes[offset++] = metadata.Quality;
            Buffer.BlockCopy(metadata.Hash, 0, metadataBytes, offset, 16);

            await stream.WriteAsync(metadataBytes.AsMemory(0, METADATA_SIZE)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(metadataBytes);
        }

        // Write frame data
        await stream.WriteAsync(frameData).ConfigureAwait(false);
    }

    public static async Task<(FrameMetadata Metadata, PooledBuffer FrameData)> ReadFrameWithMetadataAsync(NetworkStream stream)
    {
        int totalSize = await ReadInt32Async(stream).ConfigureAwait(false);
        
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(totalSize);
        int totalRead = 0;
        try
        {
            while (totalRead < totalSize)
            {
                int read = await stream.ReadAsync(owner.Memory.Slice(totalRead, totalSize - totalRead)).ConfigureAwait(false);
                if (read == 0)
                {
                    owner.Dispose();
                    throw new IOException("Disconnected");
                }
                totalRead += read;
            }

            // Parse metadata
            var memory = owner.Memory;
            int offset = 0;
            byte version = memory.Span[offset++];
            uint sequenceId = BinaryPrimitives.ReadUInt32LittleEndian(memory.Span.Slice(offset, 4));
            offset += 4;
            long timestamp = BinaryPrimitives.ReadInt64LittleEndian(memory.Span.Slice(offset, 8));
            offset += 8;
            ushort width = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
            offset += 2;
            ushort height = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
            offset += 2;
            byte quality = memory.Span[offset++];
            byte[] hash = new byte[16];
            memory.Span.Slice(offset, 16).CopyTo(hash);

            var metadata = new FrameMetadata(sequenceId, timestamp, width, height, quality, hash);
            
            // Create pooled buffer for frame data (everything after metadata)
            int frameDataSize = totalSize - METADATA_SIZE;
            IMemoryOwner<byte> frameOwner = MemoryPool<byte>.Shared.Rent(frameDataSize);
            owner.Memory.Slice(METADATA_SIZE, frameDataSize).CopyTo(frameOwner.Memory);
            
            owner.Dispose(); // Return the original buffer
            return (metadata, new PooledBuffer(frameOwner, frameDataSize));
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public static byte[] ComputeFrameHash(ReadOnlyMemory<byte> frameData)
    {
        using var md5 = MD5.Create();
        return md5.ComputeHash(frameData.ToArray());
    }

    public static async Task WriteInt32Async(NetworkStream stream, int value)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            await stream.WriteAsync(bytes.AsMemory(0, 4)).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static async Task<int> ReadInt32Async(NetworkStream stream)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            int totalRead = 0;
            while (totalRead < 4)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(totalRead, 4 - totalRead)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("Disconnected");
                }
                totalRead += read;
            }
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    public static async Task WriteBytesAsync(NetworkStream stream, byte[] data)
    {
        await WriteInt32Async(stream, data.Length).ConfigureAwait(false);
        await stream.WriteAsync(data.AsMemory()).ConfigureAwait(false);
    }

    public static async Task WriteBytesAsync(NetworkStream stream, ReadOnlyMemory<byte> data)
    {
        await WriteInt32Async(stream, data.Length).ConfigureAwait(false);
        await stream.WriteAsync(data).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadBytesAsync(NetworkStream stream)
    {
        int length = await ReadInt32Async(stream).ConfigureAwait(false);
        byte[] data = new byte[length];
        int totalRead = 0;
        while (totalRead < length)
        {
            int read = await stream.ReadAsync(data.AsMemory(totalRead, length - totalRead)).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Disconnected");
            }
            totalRead += read;
        }
        return data;
    }

    public static async Task<PooledBuffer> ReadBytesPooledAsync(NetworkStream stream)
    {
        int length = await ReadInt32Async(stream).ConfigureAwait(false);
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(length);
        int totalRead = 0;
        try
        {
            while (totalRead < length)
            {
                int read = await stream.ReadAsync(owner.Memory.Slice(totalRead, length - totalRead)).ConfigureAwait(false);
                if (read == 0)
                {
                    owner.Dispose();
                    throw new IOException("Disconnected");
                }
                totalRead += read;
            }
            return new PooledBuffer(owner, length);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    // Backward compatibility methods - detect if frame has metadata
    public static async Task<bool> IsFrameWithMetadataAsync(NetworkStream stream)
    {
        // Peek at the first 4 bytes to get the total size
        byte[] sizeBytes = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            // Read the size without consuming it from the stream
            int totalRead = 0;
            while (totalRead < 4)
            {
                int read = await stream.ReadAsync(sizeBytes.AsMemory(totalRead, 4 - totalRead)).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("Disconnected");
                }
                totalRead += read;
            }
            
            int totalSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
            
            // If total size is reasonable for a frame with metadata, assume it's new format
            // This is a heuristic - in practice, we'd need version negotiation
            // For now, we assume frames > METADATA_SIZE are new format
            return totalSize > METADATA_SIZE && totalSize < 50 * 1024 * 1024; // Reasonable upper bound
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sizeBytes);
        }
    }
}

public readonly struct PooledBuffer : IDisposable
{
    private readonly IMemoryOwner<byte> owner;
    public readonly int Length;

    public PooledBuffer(IMemoryOwner<byte> owner, int length)
    {
        this.owner = owner;
        Length = length;
    }

    public Memory<byte> Memory => owner.Memory.Slice(0, Length);

    public void Dispose()
    {
        owner.Dispose();
    }
}
