using System.Net.Sockets;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DextopCommon;

public static class ScreenshotProtocol
{
    public const byte LEGACY_PROTOCOL_VERSION = 1;
    public const byte CURRENT_PROTOCOL_VERSION = 2;
    public const int LEGACY_METADATA_SIZE = 34; // version(1) + sequenceId(4) + timestamp(8) + width(2) + height(2) + quality(1) + hash(16)
    public const int CURRENT_METADATA_SIZE = 46; // version(1) + sequenceId(4) + timestamp(8) + baseWidth(2) + baseHeight(2) + regionX(4) + regionY(4) + regionWidth(2) + regionHeight(2) + quality(1) + hash(16)

    public struct FrameMetadata
    {
        public byte Version;
        public uint SequenceId;
        public long Timestamp;
        public ushort BaseWidth;
        public ushort BaseHeight;
        public int RegionX;
        public int RegionY;
        public ushort RegionWidth;
        public ushort RegionHeight;
        public byte Quality;
        public byte[] Hash; // MD5 hash of frame content for duplicate detection

        public FrameMetadata(uint sequenceId, long timestamp, ushort baseWidth, ushort baseHeight,
            int regionX, int regionY, ushort regionWidth, ushort regionHeight, byte quality, byte[]? hash = null,
            byte version = CURRENT_PROTOCOL_VERSION)
        {
            Version = version;
            SequenceId = sequenceId;
            Timestamp = timestamp;
            BaseWidth = baseWidth;
            BaseHeight = baseHeight;
            RegionX = regionX;
            RegionY = regionY;
            RegionWidth = regionWidth;
            RegionHeight = regionHeight;
            Quality = quality;
            Hash = hash ?? new byte[16];
        }

        public static FrameMetadata CreateLegacy(uint sequenceId, long timestamp, ushort width, ushort height, byte quality, byte[]? hash = null)
        {
            return new FrameMetadata
            {
                Version = LEGACY_PROTOCOL_VERSION,
                SequenceId = sequenceId,
                Timestamp = timestamp,
                BaseWidth = width,
                BaseHeight = height,
                RegionX = 0,
                RegionY = 0,
                RegionWidth = width,
                RegionHeight = height,
                Quality = quality,
                Hash = hash ?? new byte[16]
            };
        }
    }

    private static int GetMetadataSize(byte version) =>
        version == LEGACY_PROTOCOL_VERSION ? LEGACY_METADATA_SIZE : CURRENT_METADATA_SIZE;

    public static async Task WriteFrameWithMetadataAsync(NetworkStream stream, FrameMetadata metadata, ReadOnlyMemory<byte> frameData)
    {
        int metadataSize = GetMetadataSize(metadata.Version);

        // Write total size (metadata + frame data)
        int totalSize = metadataSize + frameData.Length;
        await WriteInt32Async(stream, totalSize).ConfigureAwait(false);

        // Write metadata
        byte[] metadataBytes = ArrayPool<byte>.Shared.Rent(metadataSize);
        try
        {
            int offset = 0;
            metadataBytes[offset++] = metadata.Version;
            BinaryPrimitives.WriteUInt32LittleEndian(metadataBytes.AsSpan(offset, 4), metadata.SequenceId);
            offset += 4;
            BinaryPrimitives.WriteInt64LittleEndian(metadataBytes.AsSpan(offset, 8), metadata.Timestamp);
            offset += 8;

            if (metadata.Version == LEGACY_PROTOCOL_VERSION)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.RegionWidth);
                offset += 2;
                BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.RegionHeight);
                offset += 2;
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.BaseWidth);
                offset += 2;
                BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.BaseHeight);
                offset += 2;
                BinaryPrimitives.WriteInt32LittleEndian(metadataBytes.AsSpan(offset, 4), metadata.RegionX);
                offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(metadataBytes.AsSpan(offset, 4), metadata.RegionY);
                offset += 4;
                BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.RegionWidth);
                offset += 2;
                BinaryPrimitives.WriteUInt16LittleEndian(metadataBytes.AsSpan(offset, 2), metadata.RegionHeight);
                offset += 2;
            }

            metadataBytes[offset++] = metadata.Quality;
            Buffer.BlockCopy(metadata.Hash, 0, metadataBytes, offset, 16);

            await stream.WriteAsync(metadataBytes.AsMemory(0, metadataSize)).ConfigureAwait(false);
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

            FrameMetadata metadata;
            if (version == LEGACY_PROTOCOL_VERSION)
            {
                ushort width = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
                offset += 2;
                ushort height = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
                offset += 2;
                byte quality = memory.Span[offset++];
                byte[] hash = new byte[16];
                memory.Span.Slice(offset, 16).CopyTo(hash);

                metadata = FrameMetadata.CreateLegacy(sequenceId, timestamp, width, height, quality, hash);
            }
            else
            {
                ushort baseWidth = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
                offset += 2;
                ushort baseHeight = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
                offset += 2;
                int regionX = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(offset, 4));
                offset += 4;
                int regionY = BinaryPrimitives.ReadInt32LittleEndian(memory.Span.Slice(offset, 4));
                offset += 4;
                ushort regionWidth = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
                offset += 2;
                ushort regionHeight = BinaryPrimitives.ReadUInt16LittleEndian(memory.Span.Slice(offset, 2));
                offset += 2;
                byte quality = memory.Span[offset++];
                byte[] hash = new byte[16];
                memory.Span.Slice(offset, 16).CopyTo(hash);

                metadata = new FrameMetadata(
                    sequenceId,
                    timestamp,
                    baseWidth,
                    baseHeight,
                    regionX,
                    regionY,
                    regionWidth,
                    regionHeight,
                    quality,
                    hash,
                    version);
            }
            
            // Create pooled buffer for frame data (everything after metadata)
            int metadataSize = GetMetadataSize(version);
            int frameDataSize = Math.Max(0, totalSize - metadataSize);
            IMemoryOwner<byte> frameOwner = MemoryPool<byte>.Shared.Rent(frameDataSize);
            owner.Memory.Slice(metadataSize, frameDataSize).CopyTo(frameOwner.Memory);
            
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
            // For now, we assume frames larger than the legacy metadata block are framed
            return totalSize > LEGACY_METADATA_SIZE && totalSize < 50 * 1024 * 1024; // Reasonable upper bound
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
