namespace MeiyouToolbox.Models;

/// <summary>payload.bin 里一个分区的摘要信息。</summary>
/// <param name="Name">分区名，例如 boot、system、vendor。</param>
/// <param name="UncompressedSize">解包后的字节数。</param>
/// <param name="OperationCount">安装操作数量，用于估算工作量。</param>
public sealed record PayloadPartition(string Name, long UncompressedSize, int OperationCount)
{
    /// <summary>给界面显示的友好体积。</summary>
    public string SizeText => FormatSize(UncompressedSize);

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "---";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.0} {units[unit]}";
    }
}

/// <summary>一次 payload 会话的只读描述。</summary>
public sealed record PayloadDescriptor(
    long PayloadSize,
    long DataOffset,
    int BlockSize,
    ulong FileFormatVersion,
    IReadOnlyList<PayloadPartition> Partitions)
{
    /// <summary>是否差分（增量）包。差分包需要源镜像，本工具不支持。</summary>
    public bool IsIncremental { get; init; }

    /// <summary>给界面用的摘要文案。</summary>
    public string Summary =>
        $"payload v{FileFormatVersion} · 块 {BlockSize} B · {Partitions.Count} 个分区 · " +
        $"{(IsIncremental ? "差分包（不支持）" : "完整包")}";
}

/// <summary>解包请求。</summary>
/// <param name="OutputDirectory">镜像输出目录，不存在会自动创建。</param>
/// <param name="Partitions">只解这些分区；null 或空表示全部。</param>
/// <param name="VerifySize">是否校验解出字节数与 manifest 一致。</param>
public sealed record ExtractRequest(
    string OutputDirectory,
    IReadOnlyCollection<string>? Partitions = null,
    bool VerifySize = true);

/// <summary>解包进度。</summary>
/// <param name="DoneBytes">已写出的字节数。</param>
/// <param name="TotalBytes">需要写出的总字节数。</param>
/// <param name="CurrentPartition">当前分区名。</param>
public sealed record ExtractProgress(long DoneBytes, long TotalBytes, string? CurrentPartition)
{
    /// <summary>0..1 的完成比例，可直接绑进度条。</summary>
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)DoneBytes / TotalBytes, 0, 1);

    /// <summary>给界面显示的文案。</summary>
    public string Text =>
        $"{CurrentPartition ?? "准备中"} · " +
        $"{PayloadPartition.FormatSize(DoneBytes)} / {PayloadPartition.FormatSize(TotalBytes)}";
}

/// <summary>解包结果。</summary>
public sealed record ExtractResult(
    IReadOnlyList<string> ExtractedPartitions,
    IReadOnlyList<string> MissingPartitions,
    long TotalBytesWritten,
    TimeSpan Duration)
{
    /// <summary>一行式结论文案。</summary>
    public string Summary =>
        $"解出 {ExtractedPartitions.Count} 个分区，共 " +
        $"{PayloadPartition.FormatSize(TotalBytesWritten)}，耗时 {Duration.TotalSeconds:0.0} 秒" +
        (MissingPartitions.Count > 0 ? $"；缺 {string.Join("、", MissingPartitions)}" : string.Empty);
}
