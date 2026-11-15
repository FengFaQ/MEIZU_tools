namespace MeiyouToolbox.Models;

/// <summary>
/// 一次刷写动作的目标模式。对应参考实现 <c>write.bat</c> 的子命令：
/// 模式名既是子命令名也是分发依据（<c>call write qcedl …</c>）。
/// </summary>
public enum FlashTransport
{
    /// <summary>系统下 adb shell dd。</summary>
    System,
    /// <summary>Recovery 下 adb shell dd。</summary>
    Recovery,
    /// <summary>Fastboot。</summary>
    Fastboot,
    /// <summary>FastbootD（用户空间 fastboot）。</summary>
    FastbootD,
    /// <summary>9008 / EDL。</summary>
    QcEdl,
    /// <summary>9008 批量 rawprogram XML（整包线刷）。</summary>
    QcEdlXml,
    /// <summary>9008 只发送引导（Sahara + configure）。</summary>
    QcEdlSendFh,
}
