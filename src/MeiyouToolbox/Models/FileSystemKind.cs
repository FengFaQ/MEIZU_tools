namespace MeiyouToolbox.Models;

/// <summary>
/// 分区格式化的目标类型。
/// <para>
/// 这里只保留<b>安卓真实支持</b>的类型，不再提供 NTFS / FAT32 / exFAT。
/// 原因（已核对 AOSP 源码）：<c>fastboot/fs.cpp</c> 的生成器表只有 ext4 与 f2fs 两项，
/// 其它类型会直接 <c>die("Formatting is not supported ...")</c>；
/// <c>system/vold/fs/</c> 下也没有 Ntfs.cpp；GKI 六个版本的 gki_defconfig 里
/// NTFS 相关符号全部为 NONE。FAT32 / exFAT 只用于外置 SD 卡与 U 盘（vold 的 PublicVolume），
/// 永远不用于系统分区或 /data。
/// </para>
/// </summary>
public enum FileSystemKind
{
    /// <summary>按设备自身 fstab 恢复默认（擦除后由 fs_mgr 的 formattable 自动重建）。最安全。</summary>
    Default,

    /// <summary>强制 f2fs —— 现代机型的 /data 与 metadata 用这个。</summary>
    F2fs,

    /// <summary>强制 ext4 —— 老机型、模拟器，以及多数只读系统分区。</summary>
    Ext4,
}

public static class FileSystemKindNames
{
    /// <summary>界面显示名。</summary>
    public static string Display(this FileSystemKind kind) => kind switch
    {
        FileSystemKind.Default => "恢复默认（推荐）",
        FileSystemKind.F2fs => "f2fs",
        FileSystemKind.Ext4 => "ext4",
        _ => kind.ToString(),
    };

    /// <summary>
    /// 给 <c>fastboot format</c> 用的类型名。返回 <c>null</c> 表示不走 format，
    /// 而是走 <c>fastboot erase</c> 让设备开机时自己重建。
    /// </summary>
    public static string? FormatType(this FileSystemKind kind) => kind switch
    {
        FileSystemKind.F2fs => "f2fs",
        FileSystemKind.Ext4 => "ext4",
        _ => null,
    };

    /// <summary>一句话说明，直接显示在界面上。</summary>
    public static string Description(this FileSystemKind kind) => kind switch
    {
        FileSystemKind.Default =>
            "擦除分区内容，设备下次开机时按自身 fstab 自动重建为正确类型。这是唯一不会选错的方式。",
        FileSystemKind.F2fs =>
            "强制建成 f2fs。仅当确认该机 /data 是 f2fs 时使用；选错会导致无法开机。",
        FileSystemKind.Ext4 =>
            "强制建成 ext4。仅当确认该机 /data 是 ext4（老机型或模拟器）时使用；与 f2fs 互不兼容。",
        _ => string.Empty,
    };

    /// <summary>
    /// 这些分区绝对不能格式化。用于界面上直接拒绝，而不是让用户点了才失败。
    /// <para>
    /// persist 存传感器校准与 DRM 密钥，格式化后校准数据<b>永久丢失</b>；
    /// boot / init_boot / vbmeta / misc 是裸设备，没有文件系统概念；
    /// system / vendor / product 等由 AVB 校验，只能整块刷镜像。
    /// </para>
    /// </summary>
    public static bool IsProtectedPartition(string partitionName)
    {
        var name = partitionName.Trim().ToLowerInvariant();

        // 只读系统分区：AVB / dm-verity 保护，format 等于毁掉校验数据。
        if (name is "system" or "system_ext" or "product" or "vendor" or "vendor_dlkm"
            or "system_dlkm" or "odm" or "odm_dlkm" or "super")
        {
            return true;
        }

        // 裸设备 / 关键元数据。
        if (name is "boot" or "init_boot" or "vboot" or "vbmeta" or "vbmeta_system"
            or "vbmeta_vendor" or "misc" or "dtbo" or "recovery" or "persist"
            or "modem" or "fsg" or "fsc" or "modemst1" or "modemst2" or "dsp"
            or "bluetooth" or "aboot" or "abootbak" or "xbl" or "tz")
        {
            return true;
        }

        return name.StartsWith("preisp", StringComparison.Ordinal)
            || name.StartsWith("oplus", StringComparison.Ordinal);
    }
}
