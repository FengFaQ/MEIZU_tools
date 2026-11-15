namespace MeiyouToolbox.Models;

/// <summary>格式化的文件系统类型。</summary>
public enum FileSystemKind
{
    Fat32,
    Ntfs,
    ExFat,
    Ext4,
}

public static class FileSystemKindNames
{
    /// <summary>界面显示名。</summary>
    public static string Display(this FileSystemKind kind) => kind switch
    {
        FileSystemKind.Fat32 => "FAT32",
        FileSystemKind.Ntfs => "NTFS",
        FileSystemKind.ExFat => "exFAT",
        FileSystemKind.Ext4 => "EXT4",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 对应的 mkfs 工具名。参考实现靠 <c>framework adbpre</c> 把工具推进
    /// <c>/data/local/tmp</c> 再执行，这里只需要名字。
    /// </summary>
    public static string ToolName(this FileSystemKind kind) => kind switch
    {
        FileSystemKind.Fat32 => "mkfs.fat",
        FileSystemKind.Ntfs => "mkntfs",
        FileSystemKind.ExFat => "mkfs.exfat",
        FileSystemKind.Ext4 => "mke2fs",
        _ => "mkfs.fat",
    };

    /// <summary>
    /// 拼 mkfs 参数。<paramref name="devicePath"/> 是 <c>/dev/block/by-name/xxx</c>。
    /// <para>
    /// 注意每种工具的参数差异：fat32 是 <c>-F 32</c>，ntfs 是 <c>-Q</c>，
    /// exfat 只有 <c>-n</c>，ext4 是 <c>-F -v -t ext4</c>。参考实现逐个手写，
    /// 这里集中在一处，避免调用点记错。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(
        this FileSystemKind kind, string devicePath, string? label)
    {
        var hasLabel = !string.IsNullOrWhiteSpace(label);

        return kind switch
        {
            FileSystemKind.Fat32 => hasLabel
                ? ["-F", "32", "-n", label!, devicePath]
                : ["-F", "32", devicePath],
            FileSystemKind.Ntfs => hasLabel
                ? ["-Q", "-L", label!, devicePath]
                : ["-Q", devicePath],
            FileSystemKind.ExFat => hasLabel
                ? ["-n", label!, devicePath]
                : [devicePath],
            FileSystemKind.Ext4 => hasLabel
                ? ["-F", "-v", "-t", "ext4", "-L", label!, devicePath]
                : ["-F", "-v", "-t", "ext4", devicePath],
            _ => [devicePath],
        };
    }
}
