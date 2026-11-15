using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MeiyouToolbox.Models;

namespace MeiyouToolbox.Services;

/// <summary>
/// payload.bin（Android A/B 全量 OTA）的读取与解包。
/// <para>
/// 只解析本工具需要的字段，不引用 protobuf 运行时 —— manifest 的结构是固定的，
/// 手写变长整数读取比引一整套 protobuf 工具链更轻、也不会因为 protoc 版本变化而失效。
/// 未知字段按 wire type 跳过，所以厂商加字段不会让我们崩。
/// </para>
/// </summary>
public interface IPayloadService
{
    /// <summary>只读 header 与 manifest，不落盘。</summary>
    Task<PayloadDescriptor> InspectAsync(string payloadPath, CancellationToken cancellationToken = default);

    /// <summary>把选中分区解成 &lt;目录&gt;/&lt;分区名&gt;.img。</summary>
    Task<ExtractResult> ExtractAsync(
        string payloadPath,
        ExtractRequest request,
        IProgress<ExtractProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default);
}

/// <summary><see cref="IPayloadService"/> 的实现。</summary>
public sealed class PayloadService : IPayloadService
{
    private const uint PayloadMagic = 0x43724155; // "CrAU"

    /// <summary>版本 1 的头部是 20 字节，版本 2 多一个 4 字节的 metadata_signature_size。</summary>
    private const int HeaderSizeV1 = 20;
    private readonly IAppLogger _logger;

    public PayloadService(IAppLogger logger) => _logger = logger;

    /// <inheritdoc />
    public async Task<PayloadDescriptor> InspectAsync(
        string payloadPath,
        CancellationToken cancellationToken = default)
    {
        await using var source = await OpenPayloadAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var (descriptor, manifest, _) = await ReadManifestAsync(source, cancellationToken).ConfigureAwait(false);
        return descriptor with
        {
            Partitions = manifest.Partitions
                .Select(p => new PayloadPartition(p.Name, p.UncompressedSize, p.Operations.Count))
                .ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<ExtractResult> ExtractAsync(
        string payloadPath,
        ExtractRequest request,
        IProgress<ExtractProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);

        var started = DateTime.UtcNow;
        Directory.CreateDirectory(request.OutputDirectory);

        await using var source = await OpenPayloadAsync(payloadPath, cancellationToken).ConfigureAwait(false);
        var (descriptor, manifest, dataOffset) = await ReadManifestAsync(source, cancellationToken)
            .ConfigureAwait(false);

        if (descriptor.IsIncremental)
        {
            throw new NotSupportedException(
                "这是差分（增量）OTA 包。差分需要设备上的原始镜像才能还原，" +
                "请换完整包（full OTA）。");
        }

        var selected = request.Partitions is { Count: > 0 }
            ? manifest.Partitions.Where(p => request.Partitions.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            : manifest.Partitions.ToList();

        // 用户点名的分区如果包里没有，单独汇报而不是静默跳过。
        var missing = request.Partitions is { Count: > 0 }
            ? request.Partitions
                .Where(n => !manifest.Partitions.Any(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : [];

        var totalBytes = selected.Sum(p => p.UncompressedSize);
        long doneBytes = 0;
        var extracted = new List<string>(selected.Count);
        var blockSize = descriptor.BlockSize;

        foreach (var partition in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke($"解包 {partition.Name}（{PayloadPartition.FormatSize(partition.UncompressedSize)}）");

            var targetPath = Path.Combine(request.OutputDirectory, partition.Name + ".img");
            var partialPath = targetPath + ".partial";
            if (File.Exists(partialPath))
            {
                File.Delete(partialPath);
            }

            await using (var output = new FileStream(
                partialPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                // 先按目标大小占位，这样末尾的空洞也是确定的长度。
                if (partition.UncompressedSize > 0)
                {
                    output.SetLength(partition.UncompressedSize);
                }

                foreach (var operation in partition.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var written = await ApplyOperationAsync(
                        source, output, operation, dataOffset, blockSize, cancellationToken).ConfigureAwait(false);

                    doneBytes += written;
                    progress?.Report(new ExtractProgress(doneBytes, totalBytes, partition.Name));
                }

                if (request.VerifySize)
                {
                    var actual = output.Length;
                    if (partition.UncompressedSize > 0 && actual < partition.UncompressedSize)
                    {
                        throw new InvalidOperationException(
                            $"{partition.Name} 解出的长度不足：期望 {partition.UncompressedSize} 字节，" +
                            $"实际 {actual} 字节。");
                    }
                }
            }

            // 原子落盘：先写 .partial，成功后再改名，失败时不会留下半截镜像。
            File.Move(partialPath, targetPath, overwrite: true);
            extracted.Add(partition.Name);
            log?.Invoke($"{partition.Name}.img 完成");
        }

        var duration = DateTime.UtcNow - started;
        _logger.Info($"payload 解包结束：{extracted.Count} 个分区，{doneBytes} 字节，{duration.TotalSeconds:0.0} 秒");

        return new ExtractResult(extracted, missing, doneBytes, duration);
    }

    /// <summary>把一个操作的数据写到输出的目标区间里，返回写出的字节数。</summary>
    private static async Task<long> ApplyOperationAsync(
        Stream source,
        FileStream output,
        PayloadOperation operation,
        long dataOffset,
        int blockSize,
        CancellationToken cancellationToken)
    {
        if (operation.Type is PayloadOperationType.Zero or PayloadOperationType.Discard)
        {
            var zeros = new byte[blockSize];
            foreach (var extent in operation.DstExtents)
            {
                output.Position = extent.StartBlock * blockSize;
                var remaining = extent.NumBlocks * blockSize;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(remaining, zeros.Length);
                    await output.WriteAsync(zeros.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                    remaining -= chunk;
                }
            }

            return 0;
        }

        if (operation.Type is not (PayloadOperationType.Replace
            or PayloadOperationType.ReplaceBz
            or PayloadOperationType.ReplaceXz
            or PayloadOperationType.Zstd))
        {
            throw new NotSupportedException(
                $"不支持的写入方式 {operation.Type}（增量补丁类操作）。请使用完整包。");
        }

        if (operation.DataLength <= 0)
        {
            return 0;
        }

        var raw = new byte[operation.DataLength];
        source.Position = dataOffset + operation.DataOffset;
        await source.ReadExactlyAsync(raw, cancellationToken).ConfigureAwait(false);

        byte[] data = operation.Type switch
        {
            PayloadOperationType.Replace => raw,
            PayloadOperationType.ReplaceBz => DecompressBZip2(raw),
            PayloadOperationType.ReplaceXz => DecompressXz(raw),
            PayloadOperationType.Zstd => DecompressZstd(raw),
            _ => raw,
        };

        var offset = 0;
        var totalBlocks = operation.DstExtents.Sum(e => (long)e.NumBlocks);
        if (totalBlocks * blockSize < data.Length)
        {
            throw new InvalidOperationException(
                $"操作数据 {data.Length} 字节大于目标区间 {totalBlocks * blockSize} 字节，manifest 可能已损坏。");
        }

        foreach (var extent in operation.DstExtents)
        {
            output.Position = extent.StartBlock * blockSize;
            var remaining = (int)Math.Min((long)extent.NumBlocks * blockSize, data.Length - offset);
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, 1 << 20);
                await output.WriteAsync(data.AsMemory(offset, chunk), cancellationToken).ConfigureAwait(false);
                offset += chunk;
                remaining -= chunk;
            }

            if (offset >= data.Length)
            {
                break;
            }
        }

        return data.Length;
    }

    private static byte[] DecompressBZip2(byte[] raw)
    {
        using var input = new MemoryStream(raw, writable: false);
        using var bz = new SharpCompress.Compressors.BZip2.BZip2Stream(
            input, SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: true);
        using var output = new MemoryStream();
        bz.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecompressXz(byte[] raw)
    {
        using var input = new MemoryStream(raw, writable: false);
        using var xz = new SharpCompress.Compressors.Xz.XZStream(input);
        using var output = new MemoryStream();
        xz.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecompressZstd(byte[] raw)
    {
        using var decompressor = new ZstdSharp.Decompressor();
        return decompressor.Unwrap(raw).ToArray();
    }

    /// <summary>
    /// 打开 payload 数据源。支持直接是 payload.bin，也支持整个 OTA 压缩包
    /// （此时把里面的 payload.bin 解到临时文件，因为随机访问需要可寻址的流）。
    /// </summary>
    private static async Task<Stream> OpenPayloadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到文件：{path}", path);
        }

        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        }

        using var archive = ZipFile.OpenRead(path);
        var entry = archive.Entries.FirstOrDefault(e =>
                        string.Equals(e.Name, "payload.bin", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("压缩包里没有 payload.bin，这不是 OTA 包。");

        var tempPath = Path.Combine(Path.GetTempPath(), $"meiyou-payload-{Guid.NewGuid():N}.bin");
        await using (var output = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            await entry.Open().CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        // DeleteOnClose：解包结束后临时文件自动消失。
        return new FileStream(
            tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    /// <summary>读 header 与 manifest。</summary>
    private static async Task<(PayloadDescriptor Descriptor, PayloadManifest Manifest, long DataOffset)>
        ReadManifestAsync(Stream source, CancellationToken cancellationToken)
    {
        // 头部布局：magic(4) + version(8) + manifest_size(8) = 20 字节；
        // 版本 2 再补一个 metadata_signature_size(4)。数据段紧跟 manifest 之后的
        // 空白区，所以 data_offset 一律取读完 manifest 后的实际流位置。
        var header = new byte[HeaderSizeV1];
        await source.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic != PayloadMagic)
        {
            throw new InvalidOperationException(
                "不是 payload 文件（文件头不是 CrAU）。请确认选择的是 OTA 包里的 payload.bin。");
        }

        var version = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(4));
        if (version is < 1 or > 2)
        {
            throw new InvalidOperationException($"payload 版本 {version} 不受支持（只支持 1 与 2）。");
        }

        var manifestSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12));
        if (manifestSize is <= 0 or > 512L * 1024 * 1024)
        {
            throw new InvalidOperationException($"manifest 长度异常：{manifestSize} 字节。");
        }

        // 版本 2 的头部比版本 1 多一个 metadata_signature_size 字段。
        uint metadataSignatureSize = 0;
        if (version >= 2)
        {
            var extra = new byte[4];
            await source.ReadExactlyAsync(extra, cancellationToken).ConfigureAwait(false);
            metadataSignatureSize = BinaryPrimitives.ReadUInt32BigEndian(extra);
        }

        var manifest = new byte[manifestSize];
        await source.ReadExactlyAsync(manifest, cancellationToken).ConfigureAwait(false);

        // 紧接 manifest 之后是 metadata 签名块，它的长度就是头里那个字段。
        // 数据段在这个签名之后才开始 —— 漏跳这一段会让所有 data_offset 整体偏移，
        // 解出来的镜像全是垃圾数据。
        if (metadataSignatureSize > 0)
        {
            source.Seek(metadataSignatureSize, SeekOrigin.Current);
        }

        var parsed = PayloadManifest.Parse(manifest);
        var dataOffset = source.Position;

        var descriptor = new PayloadDescriptor(
            source.Length,
            dataOffset,
            parsed.BlockSize,
            version,
            [])
        {
            IsIncremental = parsed.Partitions.Any(p => p.Operations.Any(o => o.IsIncremental)),
        };

        return (descriptor, parsed, dataOffset);
    }
}

/// <summary>payload manifest 里的一次安装操作。</summary>
internal sealed class PayloadOperation
{
    public PayloadOperationType Type { get; set; }

    public long DataOffset { get; set; }

    public int DataLength { get; set; }

    public List<PayloadExtent> DstExtents { get; } = [];

    /// <summary>带 src_sha256_hash 或补丁类操作的就是增量包。</summary>
    public bool HasSrcHash { get; set; }

    public bool IsIncremental =>
        HasSrcHash ||
        Type is PayloadOperationType.Move
            or PayloadOperationType.BsDiff
            or PayloadOperationType.SourceCopy
            or PayloadOperationType.SourceBsDiff
            or PayloadOperationType.PuffDiff
            or PayloadOperationType.BrotliBsDiff
            or PayloadOperationType.Zucchini
            or PayloadOperationType.Lz4DiffBsDiff
            or PayloadOperationType.Lz4DiffPuffDiff;
}

internal enum PayloadOperationType
{
    Replace = 0,
    ReplaceBz = 1,
    Move = 2,
    BsDiff = 3,
    SourceCopy = 4,
    SourceBsDiff = 5,
    Zero = 6,
    Discard = 7,
    ReplaceXz = 8,
    PuffDiff = 9,
    BrotliBsDiff = 10,
    Zucchini = 11,
    Lz4DiffBsDiff = 12,
    Lz4DiffPuffDiff = 13,
    Zstd = 14,
}

/// <summary>目标区间，以块为单位。</summary>
internal readonly record struct PayloadExtent(long StartBlock, long NumBlocks);

/// <summary>一个分区的更新信息。</summary>
internal sealed class PayloadPartitionUpdate
{
    public string Name { get; set; } = string.Empty;

    public List<PayloadOperation> Operations { get; } = [];

    /// <summary>所有目标区间里最靠后的结束块（不含块大小换算）。</summary>
    public long EndBlock { get; set; }

    /// <summary>解包后的字节数 = 结束块 × 块大小，由 manifest 解析完后再算。</summary>
    public long UncompressedSize { get; set; }
}

/// <summary>manifest 的解析结果。</summary>
internal sealed class PayloadManifest
{
    public int BlockSize { get; private set; } = 4096;

    public List<PayloadPartitionUpdate> Partitions { get; } = [];

    /// <summary>按 update_metadata.proto 的字段号手工解析。</summary>
    public static PayloadManifest Parse(ReadOnlySpan<byte> buffer)
    {
        var manifest = new PayloadManifest();
        var reader = new ProtoReader(buffer);

        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field, wire)
            {
                case (3, 0): // block_size
                    manifest.BlockSize = (int)reader.ReadVarint();
                    break;

                case (13, 2): // partitions
                    var partitionSlice = reader.ReadBytes();
                    var partition = new PayloadPartitionUpdate();
                    var partReader = new ProtoReader(partitionSlice);
                    while (partReader.HasMore)
                    {
                        var (pField, pWire) = partReader.ReadTag();
                        switch (pField, pWire)
                        {
                            case (1, 2):
                                partition.Name = Encoding.UTF8.GetString(partReader.ReadBytes());
                                break;
                            case (8, 2):
                                ParseOperation(partReader.ReadBytes(), partition);
                                break;
                            default:
                                partReader.Skip(pWire);
                                break;
                        }
                    }

                    if (partition.Name.Length > 0)
                    {
                        partition.EndBlock = ComputeEndBlock(partition);
                        manifest.Partitions.Add(partition);
                    }

                    break;

                default:
                    reader.Skip(wire);
                    break;
            }
        }

        if (manifest.BlockSize <= 0)
        {
            manifest.BlockSize = 4096;
        }

        // 块大小解析完才知道分区字节数，所以统一在这里换算。
        foreach (var partition in manifest.Partitions)
        {
            partition.UncompressedSize = partition.EndBlock * manifest.BlockSize;
        }

        return manifest;
    }

    private static void ParseOperation(ReadOnlySpan<byte> slice, PayloadPartitionUpdate partition)
    {
        var operation = new PayloadOperation();
        var reader = new ProtoReader(slice);

        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field, wire)
            {
                case (1, 0):
                    operation.Type = (PayloadOperationType)reader.ReadVarint();
                    break;
                case (2, 0):
                    operation.DataOffset = (long)reader.ReadVarint();
                    break;
                case (3, 0):
                    operation.DataLength = (int)reader.ReadVarint();
                    break;
                case (6, 2):
                    operation.DstExtents.Add(ParseExtent(reader.ReadBytes()));
                    break;
                case (9, _):
                    // 有 src_sha256_hash 就说明要拿旧数据做补丁 —— 增量包。
                    operation.HasSrcHash = true;
                    reader.Skip(wire);
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        partition.Operations.Add(operation);
    }

    private static PayloadExtent ParseExtent(ReadOnlySpan<byte> slice)
    {
        long start = 0;
        long count = 0;
        var reader = new ProtoReader(slice);
        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field, wire)
            {
                case (1, 0):
                    start = (long)reader.ReadVarint();
                    break;
                case (2, 0):
                    count = (long)reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new PayloadExtent(start, count);
    }

    /// <summary>分区结束块取所有目标区间里最靠后的结束位置。</summary>
    private static long ComputeEndBlock(PayloadPartitionUpdate partition)
    {
        long endBlock = 0;
        foreach (var operation in partition.Operations)
        {
            foreach (var extent in operation.DstExtents)
            {
                var end = extent.StartBlock + extent.NumBlocks;
                if (end > endBlock)
                {
                    endBlock = end;
                }
            }
        }

        return endBlock;
    }
}

/// <summary>
/// 极简 protobuf 读取器。只实现 varint 与长度分隔两种 wire type，
/// 其余按类型跳过 —— manifest 的 schema 固定，不需要完整的 protobuf 运行时。
/// </summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buffer = buffer;
    private int _position;

    public readonly bool HasMore => _position < _buffer.Length;

    public (int Field, int WireType) ReadTag()
    {
        var tag = (int)ReadVarint();
        return (tag >> 3, tag & 0x7);
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        var shift = 0;
        while (_position < _buffer.Length)
        {
            var b = _buffer[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift >= 64)
            {
                throw new InvalidOperationException("manifest 里的变长整数过长。");
            }
        }

        throw new InvalidOperationException("manifest 提前结束。");
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = (int)ReadVarint();
        if (length < 0 || _position + length > _buffer.Length)
        {
            throw new InvalidOperationException("manifest 里的字段长度越界。");
        }

        var slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                _position += 8;
                break;
            case 2:
                ReadBytes();
                break;
            case 5:
                _position += 4;
                break;
            default:
                throw new InvalidOperationException($"manifest 里出现无法跳过的 wire type {wireType}。");
        }

        if (_position > _buffer.Length)
        {
            throw new InvalidOperationException("manifest 提前结束。");
        }
    }
}
