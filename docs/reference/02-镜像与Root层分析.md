# 02 · 镜像处理与 Root 层分析

> 分析对象：煤油工具箱（BFF）批处理脚本中的「镜像与 Root 层」
> 取证范围：`_ref_meiyou/_utf8/` 下已转码的 UTF-8 文本
> 行号基准：`read` 工具显示的逻辑行号（`bin__imgkit.bat.txt` 共 1941 逻辑行 / 167,158 字符；物理文件因 CRCRLF 损坏，转码后每个逻辑行后跟 `\r\r\n`，不影响行号）
> 引用格式：`文件名:L行号`
> 二进制（`magiskboot.exe`、`simg_dump.exe`、`HexTool.exe`、`calc.exe`、`numcomp.exe`、`aria2c.exe`）**均不在参考包内**，凡涉及二进制内部语义处一律标注「未确认」或「外部知识」。

---

## 1. 总览

### 1.1 模块清单

| 子命令 | 入口 | 行区间 | 行数 | 职责 | 主要外部工具 | 是否有调用者 |
|---|---|---|---|---|---|---|
| `SPARSE2RAW` | `:SPARSE2RAW` `bin__imgkit.bat.txt:L15` | L15–L31 | 17 | Android sparse image → raw | `simg_dump.exe` | **未发现** |
| `PATCHVBMETA` | `:PATCHVBMETA` `L34` | L34–L51 | 18 | 改写 vbmeta 头的 flags 字段 | `magiskboot.exe hexpatch` | **未发现** |
| `PATCHFRP` | `:PATCHFRP` `L54` | L54–L88 | 35 | 改写 frp 分区末字节 | `busybox`(tail/xxd/stat)、`calc()`、`HexTool.exe` | **未发现** |
| `MAGISKPATCH` | `:MAGISKPATCH` `L91` | **L91–L1855** | **1765** | Magisk 修补 boot/init_boot | `7z.exe`、`magiskboot.exe`、`busybox`、`random`、`log` | ✅ 2 处 |
| `RECINST` | `:RECINST` `L1858` | L1858–L1917 | 60 | 把 TWRP ramdisk 注入 boot | `magiskboot.exe`、`busybox ash` | ✅ 2 处 |

`MAGISKPATCH` 段落占全文 **90.9%**（1765 / 1941 行），与任务描述「占全文 90%」一致。

### 1.2 子命令参数签名

依据 `bin__imgkit.bat.txt:L3-L7` 的头部注释与实际取参代码（`L10` 一次性铺开 `args1..args9`）：

```
call imgkit magiskpatch  <boot文件完整路径> <新boot文件路径> <面具apk路径>                  [noprompt]
call imgkit recinst      <boot文件完整路径> <新boot文件路径> <recovery文件路径(img|ramdisk.cpio)> [noprompt]
call imgkit patchfrp     <frp文件路径>      <新frp文件路径>  [oemunlockon|oemunlockoff]        [noprompt]
call imgkit patchvbmeta  <vbmeta文件路径>   <新vbmeta文件路径> [noverify|verify]              [noprompt]
call imgkit sparse2raw   <sparse文件路径>   <raw文件路径>                                     [noprompt]
```

统一约定（`L18`、`L37`、`L57`、`L94`、`L1861`）：
- 参数位固定，`args1` 是分发标签，`goto %args1%`（`L11`）；
- `mode`/`noprompt` 均为**字面量比较**：`if not "%mode%"=="noprompt"`（例 `L24`）；
- 每个子命令 `SETLOCAL` 进入、`ENDLOCAL` 退出；只有 `MAGISKPATCH` 在 `ENDLOCAL` 后回写结果变量（`L1854`）。

### 1.3 全局依赖约定

| 变量 | 含义 | 证据 |
|---|---|---|
| `%framework_workspace%` | 脚本根（`bin\`），所有相对路径的锚点 | `L220`、`L1862` |
| `%tmpdir%` | 临时目录，本次分析用 `%tmpdir%\imgkit-magiskpatch`、`%tmpdir%\imgkit-recinst` | `L102-L103`、`L1870-L1871` |
| `%logfile%` | 全局日志，所有工具输出 `1>>%logfile% 2>&1` | `L27`、`L219` 等 |
| `call log <logger> <I/W/E/F> <文本>` | 分级日志，实现在 `bin__log.bat.txt:L11-L13` | 全局 |

启动时对工具的可用性自检在 `bin__framework.bat.txt:L110-L295`（3.1 节会用到其中几项）。

---

## 2. MAGISKPATCH（重点）

### 2.1 为什么按「版本」分成几十套分支

**结论：分支键不是 Magisk 版本号，而是 `boot_patch.sh` 的 MD5。**

证据链：

1. 从 Magisk 安装包（apk/zip）里夹出脚本与二进制（`L156`）：
   ```
   7z.exe e -aoa -o%tmpdir%\imgkit-magiskpatch -slp -y
        -ir!assets\util_functions.sh -ir!common\util_functions.sh
        -ir!assets\boot_patch.sh      -ir!common\boot_patch.sh      %zippath%
   ```
   同时兼容新版 APK 布局（`assets\`）与旧版 ZIP 布局（`common\`）。

2. 读版本号只用于**日志与报错**（`L158-L164`）：
   ```
   从 util_functions.sh 抓 MAGISK_VER_CODE= → %magiskver%
   从 util_functions.sh 抓 MAGISK_VER=      → 去引号 → %magiskver_show%
   bootpatchmd5 = busybox.exe md5sum boot_patch.sh   (L162)
   ```

3. 用 MD5 查表决定修补方案（`L168-L204`），命中不了直接 FATAL（`L205`）：
   ```
   if "%bootpatchmd5%"=="0d1bdb70f8ae72ff2fa646a8aff448b7" set bootpatchplan=ALPHA_070719db_28001
   ...
   if "%bootpatchplan%"=="" → "未支持的Magisk版本(%magiskver_show% %magiskver%). 请联系开发者适配." → goto FATAL
   ```

**统计（精确计数）**：`L168-L204` 共 **37 行**映射，去重后 **35 个不同 MD5**、**18 个不同方案标签**。
- 2 处重复行（同一 MD5 写两遍，属冗余无害）：`aef5b749…`（`L173`/`L174` 均 → 26300）、`3d2c5bcc…`（`L178`/`L179` 均 → 26000）。
- 4 处带 vivo 附加标记（`& set vivo_suu_patch=y`）：`L198`(26300)、`L199`(26300)、`L200`(25200)、`L201`(26200)。

**这样设计的动因（可从代码直接读出）**：工具箱并不调用 Magisk 官方的 `boot_patch.sh`，而是**在批处理里手工复刻 `boot_patch.sh` 的每一步**（unpack → cpio patch → dtb patch → kernel hexpatch → repack）。复刻必须与那一版 `boot_patch.sh` 的**行��**逐条对齐，否则产物与原版不一致。用 `boot_patch.sh` 的 MD5 做键，等价于「锁定上游修补脚本的确切字节」，比用版本号更严格：

- 优点：同一版本号的不同构建（如 Alpha、vivo 定制、phh 分支）会自动落到不同分支；上游逻辑一改，MD5 必变，不会被误认成旧方案。
- 代价：上游哪怕只改一个注释或换行符，MD5 就变 → 直接「未支持」。所以 35 个指纹只覆盖 18 套逻辑，且每跟一版都要重新人工审计。这就是「几十套分支」的真实来源：**不是 18 个版本各写一遍，而是 35 个二进制指纹收敛到 18 套行为**。

### 2.2 版本矩阵

#### 2.2.1 分支清单与行区间

| # | 方案标签 | 入口行 | 区间 | 上游对应（由标签/依赖推断，标注为推断） |
|---|---|---|---|---|
| 1 | `ALPHA_070719db_28001` | `L209` | L209–L286 | Magisk Alpha 28001（commit 070719db） |
| 2 | `28000` | `L288` | L288–L385 | Magisk 28.0 |
| 3 | `27006` | `L387` | L387–L482 | Magisk 27.6 |
| 4 | `27005` | `L484` | L484–L578 | Magisk 27.5 |
| 5 | `26300` | `L580` | L580–L675 | Magisk 26.3 |
| 6 | `26200` | `L677` | L677–L772 | Magisk 26.2 |
| 7 | `26000` | `L774` | L774–L870 | Magisk 26.0 |
| 8 | `25210` | `L872` | L872–L968 | Magisk 25.2.1 |
| 9 | `25200` | `L970` | L970–L1059 | Magisk 25.2 |
| 10 | `25000` | `L1061` | L1061–L1143 | Magisk 25.0 |
| 11 | `23000` | `L1145` | L1145–L1229 | Magisk 23.0 |
| 12 | `21400` | `L1231` | L1231–L1313 | Magisk 21.4 |
| 13 | `21200` | `L1315` | L1315–L1398 | Magisk 21.2 |
| 14 | `19400` | `L1400` | L1400–L1492 | Magisk 19.4 |
| 15 | `19000` | `L1494` | L1494–L1583 | Magisk 19.0 |
| 16 | `18100` | `L1585` | L1585–L1663 | Magisk 18.1 |
| 17 | `17200` | `L1665` | L1665–L1745 | Magisk 17.2 |
| 18 | `phh-20201-20.3-15bd2da8` | `L1747` | L1747–L1831 | phhusson GSI 20201 / 20.3（commit 15bd2da8） |

#### 2.2.2 差异点矩阵（一）：组件依赖与 config 内容

| 方案 | 需要 magiskinit | magisk.xz（单体） | magisk32.xz | magisk64.xz | stub.xz | init-ld.xz | config 键 |
|---|---|---|---|---|---|---|---|
| ALPHA_070719db_28001 | ✅`L212` | ✅`L213` | – | – | ✅`L214` | ✅`L215` | KEEPVERITY/KEEPFORCEENCRYPT/RECOVERYMODE `L259` |
| 28000 | ✅`L291` | ✅`L292` | – | – | ✅`L293` | ✅`L294` | 同上 `L338` |
| 27006 | ✅`L390` | ✅`L391` | – | – | ✅`L392` | ✅`L393` | 同上 `L437` |
| 27005 | ✅`L487` | ✅`L488` | – | – | ✅`L489` | **–** | 同上 `L533` |
| 26300 | ✅`L583` | – | ✅`L584` | ✅(64bit)`L585` | ✅`L586` | – | 同上 `L630` |
| 26200 | ✅`L680` | – | ✅`L681` | ✅`L682` | ✅`L683` | – | **+PATCHVBMETAFLAG** `L727` |
| 26000 | ✅`L777` | – | ✅`L778` | ✅`L779` | ✅`L780` | – | +PATCHVBMETAFLAG + **RANDOMSEED** `L823-L826` |
| 25210 | ✅`L875` | – | ✅`L876` | ✅`L877` | ✅`L878` | – | +PATCHVBMETAFLAG + **RANDOMSEED**（另一字符池）`L921-L924` |
| 25200 | ✅`L973` | – | ✅`L974` | ✅`L975` | **不检查** | – | +PATCHVBMETAFLAG `L1018` |
| 25000 | ✅`L1064` | – | ✅`L1065` | ✅`L1066` | 不检查 | – | +PATCHVBMETAFLAG `L1109` |
| 23000 | ✅`L1148` | – | ✅`L1149` | ✅`L1150` | 不检查 | – | 无 PATCHVBMETAFLAG `L1195` |
| 21400 | ✅`L1234` | – | – | – | – | – | `L1280` |
| 21200 | ✅`L1318` | – | – | – | – | – | `L1364` |
| 19400 | ✅`L1403` | – | – | – | – | – | `L1455` |
| 19000 | ✅`L1497` | – | – | – | – | – | **仅 2 键**（无 RECOVERYMODE）`L1547` |
| 18100 | ✅`L1588` | – | – | – | – | – | 仅 2 键 `L1628` |
| 17200 | ✅`L1668` | – | – | – | – | – | 仅 2 键 `L1708` |
| phh-20201-20.3 | ✅`L1750` | – | – | – | – | – | 3 键 `L1796` |

注：`stub.xz` 在 `L152` 处**无条件压缩**（只要 apk 里有 `assets\stub.apk`），但只有 **8 个分支**做了存在性检查（`L214`/`L293`/`L392`/`L489`/`L586`/`L683`/`L780`/`L878`）；真正把它塞进 ramdisk 的只有 **6 个分支**（`L264`/`L343`/`L442`/`L538`/`L635`/`L732`）。**26000 / 25210 检查了 `stub.xz` 却不使用它**（`L830`/`L928` 的 cpio 行无 stub 项）——这是分支复制过程中遗留的不一致，属可疑点而非设计意图。

#### 2.2.3 差异点矩阵（二）：工序差异

| 方案 | 无 ramdisk 时 `SKIP_BACKUP=#` | MODE1 还原方式 | ramdisk cpio 载荷 | dtb 处理 | rootfs 门禁 | kernel 额外补丁 |
|---|---|---|---|---|---|---|
| ALPHA…28001 | ✅`L224` | `extract .backup/.magisk config.orig` + `restore` `L247` | 0750 + overlay.d + magisk.xz + stub.xz + init-ld.xz `L264` | **无 dtb 步骤**（直接跳 `-3` kernel，`L266`） | `LEGACYSAR` `L281` | vivo + RKP + defex + **PROCA** |
| 28000 | ✅`L303` | 同上 `L326` | 同 28001 `L343` | `dtb test` → `dtb patch`，逐候选 `dtb/kernel_dtb/extra`，子程序 `:MAGISKPATCH-28000-dtb` `L359`；调用段 `L345-L358` | `LEGACYSAR` `L380` | vivo + RKP + defex + PROCA |
| 27006 | ✅`L402` | 同上 `L425` | 同 `L442` | 同上，子程序 `:MAGISKPATCH-27006-dtb` `L458`；调用段 `L444-L457` | `LEGACYSAR` `L477` | vivo + RKP + defex（**去掉 PROCA**） |
| 27005 | ✅`L498` | 同上 `L521` | 去掉 init-ld.xz `L538` | 同上，子程序 `:MAGISKPATCH-27005-dtb` `L554`；调用段 `L540-L553` | `LEGACYSAR` `L573` | vivo + RKP + defex |
| 26300 | ✅`L595` | 同上 `L618` | 0750 + magisk32/64 + stub `L635` | 同上（子程序名改小写 `:magiskpatch-26300-dtb` `L651`）；调用段 `L637-L650` | `LEGACYSAR` `L670` | vivo + RKP + defex |
| 26200 | ✅`L692` | 同上 `L715` | 同 26300 `L732` | 同上（`:magiskpatch-26200-dtb`）`L734-L753` | `LEGACYSAR` `L767` | vivo + RKP + defex |
| 26000 | 无该机制 `L787` | **改新式**：`cpio … sha1` 取 SHA1 `L811` + `cpio … restore` `L814` | 同 26300，但 `backup` 写死 `L830` | 同上 `L832-L851` | `SYSTEM_ROOT` `L865` | vivo + RKP + defex |
| 25210 | 无 `L885` | 新式 `L909-L912` | 同 `L928` | 同上 `L930-L949` | `SYSTEM_ROOT` `L963` | vivo + RKP + defex |
| 25200 | 无 `L982` | 新式 `L1006-L1009` | 同 `L1023` | 同上 `L1025-L1044` | **无条件**（无 if 门禁）`L1056` | vivo + RKP + defex |
| 25000 | 无 `L1073` | 新式 `L1097-L1100` | 同 `L1114` | **旧式**：只选一个 `dtbname`，仅 `dtb patch`，不 `test` `L1116-L1128` | 无条件 `L1140` | vivo + RKP + defex |
| 23000 | 无 `L1159` | 新式 `L1183-L1186` | 同 `L1200` | 旧式 `L1202-L1214`；**`recovery_dtbo` 强制 `RECOVERYMODE=true`** `L1157` | 无条件 `L1226` | vivo + RKP + defex |
| 21400 | 无 `L1244` | 新式 `L1268-L1271` | **仅 magiskinit**：`add 750 init magiskinit` `L1284` | 旧式 `L1286-L1298`；`recovery_dtbo` 强制 `RECOVERYMODE` `L1242` | 无条件 `L1310` | vivo + RKP + defex |
| 21200 | 无 `L1328` | 新式 `L1352-L1355` | 同 21400 `L1368` | 旧式，**候选含 `recovery_dtbo`** `L1370-L1383`；`RECOVERYMODE` 强制 `L1326` | 无条件 `L1395` | vivo + RKP + defex |
| 19400 | 无 `L1413` | 新式 + **`exists init.rc` 分叉** `L1441-L1449` | 同 `L1459` | **KEEPVERITY 门禁**：true 则跳过 `L1462-L1464`；候选含 `recovery_dtbo` `L1470`；`RECOVERYMODE` 强制 `L1411` | 无条件 `L1489` | vivo + RKP + defex |
| 19000 | 无 `L1505` | 新式 + `exists init.rc` `L1533-L1540` | 同 `L1551` | KEEPVERITY 门禁 `L1554`；候选 `dtb/kernel_dtb/extra` | 无条件 `L1580` | vivo + RKP + defex |
| 18100 | **无存在性守卫** `L1596` | 新式 + 无条件备份 `L1620-L1621` | `add 750` … **无 `mkdir 000 .backup`** `L1632` | KEEPVERITY 门禁 `L1635`；候选仅 `dtb/extra` `L1640-L1641` | 无条件 `L1660` | vivo + RKP + defex |
| 17200 | 无守卫 `L1676` | 同 18100 `L1700-L1701` | 同 `L1712` | KEEPVERITY 门禁 `L1715`；候选仅 `dtb/extra` | 无条件 `L1661` | vivo + RKP + defex + **defex A8/N9 变体** `L1739`/`L1741` |
| phh…15bd2da8 | 无 `L1760` | 新式 `L1784-L1787` | **先 `rm init.zygote32.rc` / `rm init.zygote64_32.rc`** `L1800`，再标准载荷 `L1801` | 旧式，候选含 `recovery_dtbo` `L1803-L1816`；`RECOVERYMODE` 强制 `L1758` | 无条件 `L1828` | RKP + defex（vivo 视 `%vivo_suu_patch%`） |

#### 2.2.4 归纳：真正的「代际」只有 5 组

把这 18 个分支的差异压缩，实际只有 5 个分水岭，都与上游 `boot_patch.sh` 的真实变更一一对应：

| 代际 | 分支 | 分水岭特征 |
|---|---|---|
| **G1** 单体 magisk + overlay.d | 28001 / 28000 / 27006 / 27005 | 用单个 `magisk.xz`（源自 `libmagisk.so`）；新增 `overlay.d/sbin/` 注入；28001/28000/27006 还注入 `init-ld.xz` |
| **G2** 32/64 拆分 + stub | 26300 / 26200 | `magisk32.xz` + `magisk64.xz` 分离（`L584-L585`），`stub.xz` 被搬进 ramdisk |
| **G3** config 增加 vbmeta/随机种子 | 26000 / 25210 / 25200 / 25000 / 23000 | 引入 `PATCHVBMETAFLAG`；26000/25210 还写 `RANDOMSEED`；**MODE1 还原语法换代**（`cpio … restore` 独立成一条） |
| **G4** 只装 magiskinit | 21400 / 21200 / 19400 / 19000 | ramdisk 只放 `init`（`L1284` 起），`overlay.d` 消失；19400/19000 加 `exists init.rc` 分叉与 KEEPVERITY→dtb 门禁 |
| **G5** 上古语法 | 18100 / 17200 / phh | `add 750`（三位八进制）；18100/17200 连 `mkdir 000 .backup` 都没有；17200 的 rootfs 图案少一个尾部 `00` |

三个词法级的「硬指纹」（可直接用来判断代码抄自哪一版）：

| 指纹 | 出现分支 | 行号 |
|---|---|---|
| `add 0750 init magiskinit`（4 位八进制） | G1–G3 共 11 支 | `L264`,`L343`,`L442`,`L538`,`L635`,`L732`,`L830`,`L928`,`L1023`,`L1114`,`L1200` |
| `add 750 init magiskinit`（3 位八进制） | G4–G5 共 7 支 | `L1284`,`L1368`,`L1459`,`L1551`,`L1632`,`L1712`,`L1801` |
| `mkdir 000 .backup` | 除 18100/17200 外全部 16 支 | 缺失于 `L1632`（18100）、`L1712`（17200） |

#### 2.2.5 随机字符池的差异（`random` 子命令）

只有两处使用随机种子，且**参数不同**：

| 方案 | 调用 | 生成 | 证据 |
|---|---|---|---|
| 26000 | `call random 16 abcdef0123456789` → `echo.RANDOMSEED=0x%random__str%` | 16 位十六进制字符 | `L825-L826` |
| 25210 | `call random 8 abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789` | 8 位大小写字母+数字 | `L923-L924` |

`random` 的实现（`bin__random.bat.txt:L16-L29`）：`SETLOCAL EnableDelayedExpansion`，`str_length = busybox.exe expr length "<池>"`，循环 `长度` 次取 `!str:~%random%%%str_length%,1!`，最后 `ENDLOCAL & set random__str=...` 回写。即**用 `%random%` 取模 + 子串切片**，非加密随机。`RANDOMSEED` 本身在 ramdisk 里被 Magisk 用于派生目录/属性名（外部知识，`magiskinit` 侧消费，脚本不消费）。

### 2.3 MODE0 / MODE1 到底是什么

**不是「是否保留原 ramdisk」，也不是「bootctl 补丁」或「vendor_boot」。它是「boot 镜像是否已被 Magisk 补过」的二分类。**

判定机制（所有分支完全一致，以 28000 为例 `L301-L310`）：

```bat
::测试ramdisk
if not exist %tmpdir%\imgkit-magiskpatch\ramdisk.cpio (            L301
    call log %logger% I 无ramdisk.cpio
    set STATUS=0& set SKIP_BACKUP=#& goto MAGISKPATCH-28000-MODE0)  L303
call log %logger% I 测试ramdisk.cpio                              L304
magiskboot.exe cpio %tmpdir%\imgkit-magiskpatch\ramdisk.cpio test  L305
set STATUS=%errorlevel%                                            L306
set SKIP_BACKUP=                                                   L307
if "%STATUS%"=="0" goto MAGISKPATCH-28000-MODE0                   L308
if "%STATUS%"=="1" goto MAGISKPATCH-28000-MODE1                   L309
→ 其他值：报错"测试ramdisk.cpio失败:%STATUS%"，pause 后 goto 重试
```

- `STATUS==0` → `MODE0`，脚本注释直接写明 `::模式0-Stock boot image detected`（`L232`、`L311`…）
- `STATUS==1` → `MODE1`，注释 `::模式1-Magisk patched boot image detected`（`L242`、`L321`…）
- 无 `ramdisk.cpio` 文件（GKI 设备 boot 分区通常没 ramdisk）→ 强制按 `MODE0` 处理，并置 `SKIP_BACKUP=#`

**两模式的唯一区别在「进补丁前要不要先还原」**：

| | MODE0（Stock） | MODE1（已补过） |
|---|---|---|
| 语义 | 干净原厂镜像，直接打补丁 | 镜像里已有上一次的 Magisk 产物，必须**先还原**成原厂态再打，否则会叠加/污染 |
| 关键动作 | `SHA1 = magiskboot sha1 <bootpath>`（`L316`）；`copy ramdisk.cpio → ramdisk.cpio.orig`（`L319`） | 先还原，再备份（`L326`→`L329`） |
| 还原命令（G1–G2，`L326`） | – | `magiskboot.exe cpio ramdisk.cpio "extract .backup/.magisk config.orig" "restore"` |
| 还原命令（G3+，`L814`） | – | `magiskboot.exe cpio ramdisk.cpio restore`（并在 `L811` 用 `cpio … sha1` 取 SHA1） |

两个模式随后都汇合到同一段「修补 ramdisk.cpio → dtb → kernel」，仅 `MODE0` 多一个 `-1` 步骤（`if not exist config.orig goto -2; 从 config.orig 读回 SHA1`，见 `L332-L333`）——这是为了在 MODE1 还原后仍然复用**原始镜像里记录的 SHA1**（Magisk 用 SHA1 关联 boot 与 patched ramdisk）。

`SKIP_BACKUP` 的巧妙用法（仅 G1–G2 的 6 个分支）：cpio 命令行里写 `"%SKIP_BACKUP% backup ramdisk.cpio.orig"`（`L264`、`L343`、`L442`、`L538`、`L635`、`L732`）。无 ramdisk 时该参数变成 `# backup ramdisk.cpio.orig` ——`#` 是 magiskboot cpio 的注释前缀，整条指令被忽略；有 ramdisk 时 `SKIP_BACKUP` 为空，指令生效。`SKIP_BACKUP=` 的置空只出现在 `L228`/`L307`/`L406`/`L502`/`L599`/`L696` 六处，与使用点一一对应。

G3 的 26000 起放弃这套技巧，改为**写死** `"backup ramdisk.cpio.orig"`（`L830`、`L928`、`L1023`、`L1114`、`L1200`、`L1284`、`L1368`、`L1459`、`L1551`、`L1632`、`L1712`、`L1801`，共 12 个分支），靠 `MODE0` 里的 `if exist ramdisk.cpio` 守卫兜底。

### 2.4 完整修补工序（时序 + 实际命令）

以 `28000` 为完整样例（G1 最全），标注每一步的实际 argv。

#### 阶段 0：入口与选项解析（`L91-L126`）

```bat
call log … 接收变量:bootpath / outputpath / zippath / mode            L94-L95
if "%zippath%"==""      → 参数错误 → FATAL                            L97
if not exist %bootpath% → 找不到 → FATAL                               L98
if not exist %zippath%  → 找不到 → FATAL                               L99
if not noprompt: 目标已存在 → 警告 + pause>nul                        L100
rd /s /q %tmpdir%\imgkit-magiskpatch ; md %tmpdir%\imgkit-magiskpatch  L102-L103
```

选项从外部注入的 `imgkit__magiskpatch__option_*` 变量读取（`L106-L124`）。**默认值策略**（全部为「白名单取反」，即只有显式写反值才改变）：

| 变量 | 默认 | 缺省取值逻辑 | 证据 |
|---|---|---|---|
| `KEEPVERITY` | **true** | `=="false"` 才 false | `L106` |
| `KEEPFORCEENCRYPT` | **true** | `=="false"` 才 false | `L108` |
| `PATCHVBMETAFLAG` | **false** | `=="true"` 才 true | `L110` |
| `RECOVERYMODE` | **false** | `=="true"` 才 true | `L112` |
| `LEGACYSAR` | **true**，并赋给 `SYSTEM_ROOT` | `=="false"` 才 false | `L114-L115` |
| `arch` | **arm_64** | 5 选 1，空则 arm_64 | `L118-L124` |

作者在注释里写明了各选项的适用版本窗口（`L105-L116`），这是理解分支差异的官方索引：
- `KEEPVERITY` / `KEEPFORCEENCRYPT`：27006–17000，默认 true
- `PATCHVBMETAFLAG`：**27006–24000**，默认 false
- `RECOVERYMODE`：27006–19100，默认 false；并注明「23000 及更低版本，boot 解包出现 `recovery_dtbo` 时此项强制 true」
- `LEGACYSAR`（强开 rootfs）：27006–26000，默认 true
- `arch`：27006–19000 支持 64 位；18100–17000 不区分；27006–27005 支持 `riscv_64`

⚠️ 注释与实现有两处不一致，已在 2.2.2 表中验证：
1. `PATCHVBMETAFLAG` 声明窗口 27006–24000，实际只有 **26200/26000/25210/25200/25000** 五处写进 config（`L727`/`L823`/`L921`/`L1018`/`L1109`），**26300、27006–28001、23000 均未写**（全文 `echo.PATCHVBMETAFLAG=` 仅 5 处命中）。
2. `arch` 的 64/32 区分实际问题更大：`if "%arch:~-2,2%"=="64" (set var=) else (set var=#)` 共出现 **11 次**（`L263`/`L342`/`L441`/`L537`/`L634`/`L731`/`L829`/`L927`/`L1022`/`L1113`/`L1199`），但 `"%var% add …"` 只在 **7 处**被引用（`L635`/`L732`/`L830`/`L928`/`L1023`/`L1114`/`L1200`）；**28001/28000/27006/27005 四个分支设了 `%var%` 却从不使用**——是死代码。

#### 阶段 1：从 Magisk 包提取组件（`L127-L153`，标签 `:MAGISKPATCH-1`）

按 `%arch%` 用 7z 从 apk/zip 里抽 `.so`（`L130-L141`）。以 `arm_64` 为例（`L130-L132`）——**两趟 7z，第二趟覆盖第一趟**：

```bat
:: 第 1 趟：取 32 位侧
7z.exe e -aoa -o%tmpdir%\imgkit-magiskpatch -slp -y
    -ir!lib\armeabi-v7a\libmagiskinit.so
    -ir!lib\armeabi-v7a\libmagisk32.so -ir!lib\armeabi-v7a\libmagisk64.so
    -ir!arm\magiskinit                                    %zippath%
:: 第 2 趟：取 64 位侧（同名 libmagiskinit.so 被覆盖为 arm64 版）
7z.exe e -aoa -o%tmpdir%\imgkit-magiskpatch -slp -y
    -ir!lib\arm64-v8a\libmagiskinit.so -ir!lib\arm64-v8a\libmagisk.so
    -ir!lib\arm64-v8a\libmagisk64.so   -ir!arm\magiskinit64
    -ir!lib\arm64-v8a\libinit-ld.so                       %zippath%
```

同时兼容两代包内布局：新版 APK 的 `lib/<abi>/*.so`，旧版 ZIP 的 `arm/magiskinit`、`arm/magiskinit64`、`x86/magiskinit`。

五个 arch 分支的抽取路径（`L130-L141`）：

| arch | 32 位侧目录 | 64 位侧目录 | magiskinit 来源 |
|---|---|---|---|
| `arm_64` | `lib\armeabi-v7a\` | `lib\arm64-v8a\` | `arm\magiskinit` + `arm\magiskinit64` |
| `arm_32` | `lib\armeabi-v7a\` | – | `arm\magiskinit` |
| `x86_64` | `lib\x86\` | `lib\x86_64\` | `x86\magiskinit` + `x86\magiskinit64` |
| `x86_32` | `lib\x86\` | – | `x86\magiskinit` |
| `riscv_64` | – | `lib\riscv64\` | **无**（`L140-L141` 只取 3 个 .so） |

`assets\stub.apk` 单独抽（`L142`）。随后**归一化文件名**：

```bat
magiskinit64 → magiskinit          move /Y                             L143
libmagiskinit.so → magiskinit      move /Y                             L148
libmagisk.so   → magisk.xz         magiskboot.exe compress=xz …       L149
libmagisk32.so → magisk32.xz       magiskboot.exe compress=xz …       L150
libmagisk64.so → magisk64.xz       magiskboot.exe compress=xz …       L151
stub.apk       → stub.xz           magiskboot.exe compress=xz …       L152
libinit-ld.so  → init-ld.xz        magiskboot.exe compress=xz …       L153
```

`L144-L147` 有一段**整块被注释掉**的旧逻辑：`7z.exe e -t#:e … -ir!2.xz magiskinit` → `-ir!2` 解出 `2` → `move 2 magisk`。这说明对极老版本（`magiskinit` 是把 `magisk` 以 xz 内嵌在自身里的自解压体），曾需要二次解包；现行代码不再走这条路（G4/G5 分支直接把 `magiskinit` 当独立可执行装进 ramdisk）。注释语法 `-t#:e` 用的是 `#` 转义引号（`#` 在本工具箱中被当作 `"` 的替代符，见 `bin__dl.bat.txt:L86` 的同类用法）。

#### 阶段 2：读版本 + 定方案（`L154-L207`）

见 2.1。注意 `bootpatchmd5` 用了 `2^>^>%logfile%` 转义把 md5sum 的 stderr 也丢进日志（`L162`）。

#### 阶段 3：解包（每个分支第一段）

```bat
cd %tmpdir%\imgkit-magiskpatch          L297
magiskboot.exe unpack %bootpath%        L298   ← 失败 → pause 重试（回到分支入口，不是回到 unpack）
cd %framework_workspace%                L299   ← 每次都切回工作根
```

**格式探测（gzip / lz4 / zopfli）在脚本里完全没有代码**——全部交给 `magiskboot unpack` 依 boot image header 自动识别。脚本只依赖 unpack 的**产物文件名**作为结构探针：

| 产物文件 | 脚本如何用 | 证据 |
|---|---|---|
| `kernel` | 存在则尝试 hexpatch | `L368`–`L383` |
| `ramdisk.cpio` | 存在与否决定有无 ramdisk；`cpio test` 决定 MODE | `L301`–`L310` |
| `dtb` / `kernel_dtb` / `extra` / `recovery_dtbo` | dtb 修补候选；`recovery_dtbo` 额外触发 `RECOVERYMODE=true` | `L350`–`L355`；`L1157` |
| `header` | `RECINST` 用它对 cmdline 做 sed | `L1896`–`L1898` |
| `config.orig` | MODE1 还原后回读 SHA1 | `L332`–`L333` |

**这是本层最重要的隐式契约**：整个版本分支体系建立在一个假设上——「`magiskboot unpack` 的产物命名在 17.2 到 28.0 之间从未变过」。脚本没有任何一处解析 boot header 的偏移或版本字段。

#### 阶段 4：ramdisk 分类（MODE0/MODE1，见 2.3）

#### 阶段 5：写 config + 打 ramdisk 补丁（`L335-L344`）

```bat
cd %tmpdir%\imgkit-magiskpatch                                          L337
echo.KEEPVERITY=%KEEPVERITY%>config
echo.KEEPFORCEENCRYPT=%KEEPFORCEENCRYPT%>>config
echo.RECOVERYMODE=%RECOVERYMODE%>>config                                L338
if not "%SHA1%"=="" echo.SHA1=%SHA1%|find "SHA1" 1>>config              L339
busybox.exe sed -i "s/\r//g;s/^M//g" config                             L340
type config>>%logfile%                                                  L341
if "%arch:~-2,2%"=="64" (set var=) else (set var=#)                     L342
magiskboot.exe cpio ramdisk.cpio \
    "add 0750 init magiskinit" \
    "mkdir 0750 overlay.d" \
    "mkdir 0750 overlay.d/sbin" \
    "add 0644 overlay.d/sbin/magisk.xz magisk.xz" \
    "add 0644 overlay.d/sbin/stub.xz stub.xz" \
    "add 0644 overlay.d/sbin/init-ld.xz init-ld.xz" \
    "patch" \
    "%SKIP_BACKUP% backup ramdisk.cpio.orig" \
    "mkdir 000 .backup" \
    "add 000 .backup/.magisk config"                                    L343
```

细节要点：

- **`sed -i "s/\r//g;s/^M//g"`（L340）是关键适配**：`echo.` 写出的 `config` 是 CRLF 行尾，而 ramdisk 内的 `.backup/.magisk` 必须 LF。`^M` 是 CMD 中残留的 CR 字面量写法，与 `s/\r//g` 双保险。
- `echo.SHA1=%SHA1%|find "SHA1" 1>>config`（`L339`）——`|find` 是把 `echo` 结果当输入传给 find 以过滤空行，配合 `SHA1` 为空时跳过，避免写出 `SHA1=` 空值。
- `config` 的键集合是**逐版本变化**的核心（见 2.2.2 表）：`PATCHVBMETAFLAG` 只在 5 个分支出现，`RANDOMSEED` 只在 2 个分支出现，`RECOVERYMODE` 在 G5 三支消失。
- cpio 指令语义：`add <mode> <ramdisk内路径> <宿主文件>` 拷入并设权限；`mkdir <mode> <路径>` 建目录；`patch` 让 magiskboot 执行它内建的 init.rc / fstab 等 patch（具体补什么在 `magiskboot` 内，**未确认**）；`backup <file>` 把当前 cpio 另存一份；`rm <path>` 删条目（只有 phh 分支用，`L1800`）。
- `%var%` 拼在 `"add 0644 overlay.d/sbin/magisk64.xz magisk64.xz"` 前（`L635`/`L732`/`L830`/`L928`/`L1023`/`L1114`/`L1200`）：32 位 arch 时 `%var%`=`#`，整条指令变注释，跳过 64 位库。

#### 阶段 6：dtb 修补（仅 G1-G4 部分分支与 phh）

两条不同实现：

**(a) 新式（28001 之后除 25000/23000/21400/21200/phh）**：逐候选 `test` 再 `patch`，用子程序 + `%result%` 传状态（`L345-L364`）：

```bat
set dtbname=& set result=y
if exist dtb        set dtbname=dtb&        call :MAGISKPATCH-28000-dtb
if "%result%"=="n"  cd %framework_workspace% && goto MAGISKPATCH-28000-3
if exist kernel_dtb set dtbname=kernel_dtb& call :MAGISKPATCH-28000-dtb
…
::L359 子程序
magiskboot.exe dtb %dtbname% test     || set result=n && 报错"可能boot已被版本过旧的Magisk修补过" && pause && goto :eof
magiskboot.exe dtb %dtbname% patch    || call log E 修补%dtbname%失败
```

`test` 失败即 `result=n`，外层**跳过 dtb 直接进 kernel 阶段**（不重试）——这是「已被旧版 Magisk 补过」的兼容路径。

**(b) 旧式（25000/23000/21400/21200/19400/19000/18100/17200/phh）**：只挑一个名字，直接 `patch`，不 `test`（`L1116-L1128`）：

```bat
set dtbname=
if exist dtb        set dtbname=dtb
if exist kernel_dtb set dtbname=kernel_dtb
if exist extra      set dtbname=extra
if "%dtbname%"=="" ( 无dtb → goto -4 )
magiskboot.exe dtb %dtbname% patch    || call log E 修补%dtbname%失败
```

注意旧式的 `set dtbname=` 是**顺序覆盖**，最终取「最后一个存在的」；`21200`/`19400`/`phh` 还把 `recovery_dtbo` 加入候选（`L1376`/`L1470`/`L1809`）。

**(c) KEEPVERITY 门禁（仅 19400/19000/18100/17200，`L1462-L1464` 等）**：
```bat
if "%KEEPVERITY%"=="true" ( call log I 保持校验.跳过修补dtb & goto -4 )
```
语义：若用户选择保留 dm-verity/AVB，则**不动 dtb**，避免破坏校验链。

#### 阶段 7：kernel hexpatch（`L366-L384`）

固定 4～6 条「盲打」补丁，**失败一律只记日志不中断**，因为「目标字符串不存在属于正常现象」（`L370` 原文注释）：

| 目标 | 模式（hex） | 替换（hex） | 含义（按 ASCII 反解） |
|---|---|---|---|
| vivo do_mount_check（仅 `%vivo_suu_patch%==y`） | `0092CFC2C9CDDDDA00` | `0092CFC2C9CEC0DB00` | AArch64 指令对替换（非 ASCII，属二进制指令 patch） |
| 三星 RKP | `49010054011440B93FA00F71E9000054010840B93FA00F7189000054001840B91FA00F7188010054` | `A1020054011440B93FA00F7140020054010840B93FA00F71E0010054001840B91FA00F7181010054` | 指令序列替换 |
| 三星 defex | `821B8012` | `E2FF8F12` | 4 字节指令替换 |
| 三星 PROCA（仅 28001/28000） | `70726F63615F636F6E66696700` | `70726F63615F6D616769736B00` | ASCII `proca_config\0` → `proca_magisk\0` |
| 强开 rootfs | `736B69705F696E697472616D667300` | `77616E745F696E697472616D667300` | ASCII `skip_initramfs\0` → `want_initramfs\0` |
| 强开 rootfs（17200 变体） | `736B69705F696E697472616D6673` | `77616E745F696E697472616D6673` | 同上，**少尾部 `00`**（`L1743`） |
| defex A8 变体（仅 17200） | `006044B91F040071802F005460DE41F9` | `006044B91F00006B802F005460DE41F9` | 指令替换（`L1739`） |
| defex N9 变体（仅 17200） | `603A46B91F0400710030005460C642F9` | `603A46B91F00006B0030005460C642F9` | 指令替换（`L1741`） |

**成功计数法**（G1–G3，`L368-L383`）：
```bat
if exist kernel set PATCHEDKERNEL=false
if exist kernel (
    …每条 hexpatch 成功则 && set PATCHEDKERNEL=true …
)
if exist kernel (if "%PATCHEDKERNEL%"=="false"  → "kernel未修改.将删除kernel" & del /F /Q kernel )
```
即：**一条都没打中就删掉 kernel 文件**，让 `magiskboot repack` 沿用原 boot 里的 kernel（因为 unpack 出来的 `kernel` 若被删，repack 就不会覆盖该段）。这是一个相当稳妥的「空补丁回退」设计。

G4/G5（21400 及以后）**没有 `if exist kernel` 守卫、没有 `PATCHEDKERNEL`、没有删除逻辑**（`L1046-L1057`、`L1130-L1141`…），kernel 不存在时 hexpatch 直接失败并只记 E 日志。

#### 阶段 8：打包与产物检查（`:MAGISKPATCH-DONE`，`L1833-L1855`）

```bat
cd %tmpdir%\imgkit-magiskpatch                        L1836
magiskboot.exe repack %bootpath% boot_new.img         L1837  ← 以原镜像为「模板」重打包
cd %framework_workspace%                              L1838
origbootsize   = busybox.exe stat -t %bootpath%        → 第 2 字段   L1842
patchedbootsize= busybox.exe stat -t …\boot_new.img    → 第 2 字段   L1845
if not "%origbootsize%"=="%patchedbootsize%" (
    警告"修补前后boot大小不相等. 原boot:Xb. 修补后:Yb"
    if not "%mode%"=="noprompt" ECHOC …& pause>nul & ECHO.继续…)   L1847-L1850
move /Y …\boot_new.img %outputpath%                   L1852
ENDLOCAL & set imgkit__magiskpatch__vername=%magiskver_show%
          & set imgkit__magiskpatch__ver=%magiskver%  L1854
```

`repack <原镜像> <输出>` 的语义（外部知识、二进制不在包内 → **未确认**）：magiskboot 会参照原镜像的 header/段布局打包，从而保留 boot header 的版本、页大小、cmdline 等元信息。脚本据此**不需要**自己维护 header 偏移。

### 2.5 各工具在本流程中的角色

| 工具 | 位置 | 角色 | 关键调用 |
|---|---|---|---|
| `magiskboot.exe` | `tool\Win\`（自检 `bin__framework.bat.txt:L248-L249`） | **全流程主角**：格式识别、解包、cpio 编辑、dtb 处理、hex 打补丁、xz 压缩、重打包 | `unpack` `L298`；`cpio … test` `L305`；`cpio … add/mkdir/patch/backup/rm` `L343`/`L1800`；`dtb test/patch` `L361-L363`；`hexpatch` `L373-L382`；`sha1` `L316`；`compress=xz` `L149-L153`；`repack` `L1837`；`cleanup` `L1891` |
| `7z.exe` | `tool\Win\`（`L201-L203`） | 从 apk/zip 抽组件（**只用 `e` 提取 + `-ir!` 递归包含**，不解压 ramdisk） | `L131-L142`、`L156` |
| `busybox.exe` | `tool\Win\`（`L214-L215`） | 通用 Unix 工具集：`md5sum` 算 boot_patch.sh 指纹（`L162`）；`sed -i` 清 CR（`L340` 等）；`stat -t` 取字节数（`L1842`）；`ash` 跑 shell 片段（`L1900`） | 同上 |
| `HexTool.exe` | `tool\Win\`（`L241-L242`） | **按地址写字节**的底层工具；本层只在 `PATCHFRP` 用（`L82`）。`magiskpatch` 全程**不用**它——改字节统一走 `magiskboot hexpatch`（按模式搜索，不按地址） | `L82` |
| `bootctl` | `tool\Android\`（`L283`、`L306`） | **与 Magisk 修补完全无关**。它是推到手机上用的 A/B 槽位切换器，只在槽位设置里出现：`bin__slot.bat.txt:L63-L67`（`bootctl set-active-boot-slot 0/1`）。`MAGISKPATCH` 里对 `bootctl` **零引用** | `bin__slot.bat.txt:L63-L67` |
| `calc` / `random` | 本工具箱自身脚本 | `calc s` 算 `size-1`（`L79`）；`random` 造 `RANDOMSEED`（`L825`、`L923`） | 见 §5 |

### 2.6 boot 分区类型差异（boot vs init_boot / A/B / vendor_boot）

设备元数据在 `bin__conf__dev.csv.txt`，关键三列：`parlayout`（分区布局）、`bootpar`（被修补的分区名）、`blplan`（解锁方案）。

| 代号 | `parlayout` | `bootpar` | 依据 |
|---|---|---|---|
| M15 / 16S / 16Spro / 16T / 16th / 16X / 16Xs / meizu17 / 17Pro / note8 / note9 / meizuX8 | `aonly` | **boot** | `bin__conf__dev.csv.txt:L3-L12,L25-L26,L29` |
| meizu18 / 18Pro / 18s / 18sPro | `ab` | **boot** | `…:L13-L16` |
| meizu18X / MeizuVenus | `ab_rec` | boot | `…:L17,L30` |
| meizu20 / 20inf / 20Pro / meizu21 / 21Pro / Meizu21Note / meizu22 / note16pro | `ab_rec` | **init_boot** | `…:L18-L24,L27` |

由此得出三种完全不同的修补形态（都由上层 `煤油工具箱.bat` 决定，`MAGISKPATCH` 本身**不区分**，只认 `%bootpath%` 指向哪个文件）：

1. **`aonly` + `boot`**（魅蓝/16 系/17 系，Android ≤11）：一次性 A/B 都没，ramdisk 在 boot 里，直接补 boot。
2. **`ab` + `boot`**（魅族18 系，Android 12）：`boot_a`/`boot_b` 双槽，ramdisk 仍在 boot。上层先查槽位再拼分区名：
   ```bat
   if "%parlayout:~0,2%"=="ab" call slot system chk                   煤油工具箱.bat:L1117
   if "%parlayout:~0,2%"=="ab" (set targetpar=%bootpar%_%slot__cur%) 煤油工具箱.bat:L1118
   ```
   槽位只在**分区名**上体现（`boot_a`/`boot_b`），修补算法本身无槽位概念。
3. **`ab_rec` + `init_boot`**（魅族20 起，Android 13+ GKI）：kernel 在 `boot`、**ramdisk 搬到 `init_boot`**，且 `recovery_a`/`recovery_b` 是独立分区。这正是本层最需要小心的一类：
   - 修补对象是 `init_boot_a`/`init_boot_b`，其中**只有 ramdisk，没有 `kernel` 文件** → `magiskboot unpack` 后 `if exist kernel` 为假，kernel hexpatch 阶段整体跳过（`L368`/`L369`）。也就是说 **G3–G5 的「强开 rootfs」「移除三星 RKP」在这类机型上根本不会执行**——不过魅族是骁龙平台，三星补丁本就无关，vivo 补丁也只对 vivo 有意义。设计上是「无害但不生效」。
   - `ab_rec` 让 TWRP 有独立落点，所以上层提供了 `FLASHREC-FLASH-AB_REC`（直接刷 `recovery_%slot%`，`煤油工具箱.bat:L495-L501`），只在 `ab` 布局才走 `RECINST` 注入（`L502-L509`）。

**`vendor_boot`：未确认。** 全参考包内 `vendor_boot` 零命中（对 `vendor_boot` 做全文检索无结果）。GKI 设备上 ramdisk 也可能放在 `vendor_boot`，但本工具箱**没有任何 `vendor_boot` 相关分支或参数**，`dev.csv` 的 `bootpar` 列也只有 `boot`/`init_boot` 两种取值。若某机型需要补 `vendor_boot`，当前代码路径不支持。

**「是否有 ramdisk」的处理**才是脚本真正区分的能力（而不是 boot/init_boot 名字）：
- 有 `ramdisk.cpio` → 正常补，MODE0/MODE1 分流；
- 无 `ramdisk.cpio` → 打日志「无ramdisk.cpio」，`STATUS=0` 进 MODE0（其中 G1–G2 六个分支额外置 `SKIP_BACKUP=#`），等价于「只做 kernel 侧补丁」。
- **16 个分支**实现了 `if not exist …\ramdisk.cpio` 守卫：`L222`/`L301`/`L400`/`L496`/`L593`/`L690`/`L787`/`L885`/`L982`/`L1073`/`L1159`/`L1244`/`L1328`/`L1413`/`L1505`/`L1760`（即 18 个分支中除 18100、17200 外的全部）。
- **18100 与 17200 没有这条守卫**（`L1596`/`L1676` 直接 `cpio test`，`L1598`/`L1678` 取 `%errorlevel%`），见 §8.3 的 B2。
- 补充：仅 23000 / 21400 / 21200 / 19400 / phh 五支有「`recovery_dtbo` 存在则强制 `RECOVERYMODE=true`」（`L1157`/`L1242`/`L1326`/`L1411`/`L1758`），与注释 `L111` 声明的「23000 及更低版本」基本吻合。

### 2.7 补丁成功 / 失败如何判定

分层判定，逐级收窄：

| 层级 | 判据 | 失败后果 | 证据 |
|---|---|---|---|
| L1 单条命令 | 每个工具调用都 `\|\| 报错 + pause>nul + ECHO.重试… + goto <同一步>` | 无限重试（`noprompt` 下也一样会 pause） | `L219`、`L298` 等全篇一致 |
| L2 ramdisk 分类 | `cpio test` 的 `%errorlevel%` ∈ {0,1} | 其它值 → 报错并回分支入口重来 | `L305-L310` |
| L3 kernel 打补丁 | `&& set PATCHEDKERNEL=true` 至少命中一次 | 一次未中 → **删除 kernel 文件**（回退到原 kernel） | `L383` |
| L4 config.orig/SHA1 | 只记日志，不作为失败 | – | `L332-L333` |
| L5 dtb | `test` 失败 → `result=n` → 跳过 dtb | 降级继续 | `L361` |
| L6 repack | `boot_new.img` 生成成功 | 失败 → 重试 repack | `L1837` |
| L7 产物大小 | `origbootsize == patchedbootsize`？不等**只警告不失败** | 仅 `W` 日志 + pause | `L1847-L1850` |
| L8 移动产物 | `move /Y` 成功 | 失败 → 重试 | `L1852` |

**判定体系的三个缺口**（不是我没找到，是代码里确实没有）：

1. **没有内容级校验**。既不重算 AVB hash、不比对 boot header 的 `id`/`os_version`，也不校验 ramdisk 内的 `init` 是否真的是 `magiskinit`。唯一的产物检查是**文件字节数相等**（`L1847`），而 `repack` 输出的字节数本应与原图相同（页对齐），所以这条更像「确保没有异常膨胀」的粗筛。
2. **`hexpatch` 的返回值在三处被忽略**：`PATCHVBMETA` 的 `|| call log %logger% E 修补vbmeta文件失败`（`L48`/`L49`）后直接 `L50 call log 全部完成`，**函数不返回任何状态**，调用方无法知道到底改到没改到。
3. **`RECINST` 的 kernel hexpatch 失败也不致命**：`L1904` 只有 `|| call log E hexpatch_kernel失败`。

---

## 3. 其它子命令

### 3.1 SPARSE2RAW（`L15-L31`）

```bat
set filepath=%args2%& set outputpath=%args3%& set mode=%args4%      L18
for %%a in ("%filepath%") do set filepath=%%~fa                        L21  ← 扩成绝对路径
if not exist %filepath% → FATAL                                        L23
if not noprompt: 目标已存在 → 警告 + pause                             L24
simg_dump.exe -f %filepath% -m rawimg -o %outputpath%                  L27
:: simg2img.exe %filepath% %outputpath%                               L28  ← 已注释的备选方案
```

- **扇区 / 空洞处理：本层零代码**。脚本不传任何扇区大小、不解析 sparse header 的 chunk 类型（RAW / FILL / DONT_CARE / CRC32），完全由 `simg_dump.exe` 的 `-m rawimg` 模式负责（二进制不在包内 → 具体空洞是否填零、是否按 4096 对齐，**未确认**）。
- 唯一的信息是：`-m` 是「模式」参数，同工具还有 `basicinfo` 模式，被 `bin__write.bat.txt:L435` 当作**sparse 探测探针**使用：
  ```bat
  simg_dump.exe -f %filepath% -m basicinfo 1>>%logfile% 2>&1 && set sparse=true
  ```
- **架构观察：本工具集有两条并行且互斥的 sparse 路线**
  - 路线 A（在线烧写）：**不转 raw**，把 `sparse="true|false"` 写进 firehose XML，让引导程序原生识别（`bin__write.bat.txt:L434-L435`、`L461`；`bin__read.bat.txt:L109` 回读时固定 `sparse="false"`）。
  - 路线 B（离线转换）：`SPARSE2RAW` 先转成 raw。
  这解释了为什么 `SPARSE2RAW` 在整个参考包内**找不到任何调用者**——9008 主流程不需要它。它更像留给人工/二次开发的工具函数。
- 同类线索：上层还有 `rawprogram_unsparse*.xml` 的成套匹配逻辑（`煤油工具箱.bat:L620-L659`），说明「稀疏/非稀疏」在魅族线刷包里是成对出现的两种描述文件。

### 3.2 PATCHVBMETA（`L34-L51`）

```bat
if not "%func%"=="noverify" (if not "%func%"=="verify" → 参数错误 → FATAL)   L41
if not exist %filepath% → FATAL                                              L42
if not noprompt: 目标已存在 → 警告 + pause                                     L43
echo.F| xcopy /Y %filepath% %outputpath%            ← 只改副本，原文件不动        L47
if "%func%"=="noverify" magiskboot.exe hexpatch %outputpath% \
     0000000000000000617662746F6F6C20 \
     0000000300000000617662746F6F6C20                                        L48
if "%func%"=="verify"   magiskboot.exe hexpatch %outputpath% \
     0000000300000000617662746F6F6C20 \
     0000000000000000617662746F6F6C20                                        L49
```

**flag 位修改分析**：

两条模式串都是 16 字节，结构一致：

| 偏移（模式串内） | 长度 | 内容（noverify 方向） | 含义 |
|---|---|---|---|
| 0–3 | 4 B | `00 00 00 00` → `00 00 00 03` | vbmeta header 的 `flags` 字段（大端） |
| 4–7 | 4 B | `00 00 00 00`（不变） | `rollback_index_location` |
| 8–15 | 8 B | `61 76 62 74 6F 6F 6C 20` = ASCII `"avbtool "` | `release_string[48]` 的前 8 字节（"avbtool " + 版本号），用于**唯一锚定**这一段 |

- `flags` 从 `0x00000000` 变为 `0x00000003`，即同时置 bit0（`HASHTREE_DISABLED`，关 dm-verity）和 bit1（`VERIFICATION_DISABLED`，关 AVB 校验）。
- 为什么必须带 `"avbtool "` 尾巴：`flags` 单独 4 个零字节在 vbmeta 镜像里没有唯一性，加上 `release_string` 前缀才能让 `hexpatch` 的「首次匹配」落在正确位置。
- 在 vbmeta 镜像中的绝对偏移：按 AOSP `AvbVBMetaImageHeader` 布局，`flags` 位于 offset **120**（`magic[4] + version[8] + auth_size[8] + aux_size[8] + algorithm[4] + hash/sig/key 各 16 + descriptors 16 + rollback_index[8]` 之后）。**此为外部知识**，参考包内无 AOSP 头文件，脚本本身也从未提及 offset 120——脚本只依赖模式匹配。
- `verify` 方向就是反过来的同一对模式，用于把补丁「撤销」回原厂态（幂等/可逆设计）。

**缺陷（重要）**：`L48`/`L49` 的失败分支是 `|| call log %logger% E …`，**没有 `goto FATAL`，也没有设置任何结果变量**。`L50` 无条件 `call log %logger% I 全部完成`。所以：模式串没匹配到（例如目标不是标准 avbtool 生成的 vbmeta，或已经是目标状态）时，函数**仍然静默返回成功**，且产物是一个与输入完全相同的副本。上层若据此继续刷机，会得到「以为关了校验其实没关」的结果。

**调用者**：未发现。全包检索 `patchvbmeta` 只命中 `bin__imgkit.bat.txt` 自身的 L6/L34/L39/L45/L54 等定义行。

### 3.3 PATCHFRP（`L54-L88`）

```bat
if not "%func%"=="oemunlockon" (if not "%func%"=="oemunlockoff" → 参数错误)  L61
echo.F| xcopy /Y %filepath% %outputpath%                                    L64
::读末字节
for /f "tokens=1 delims= " %%a in ('busybox.exe tail -c 1 %outputpath% ^| busybox.exe xxd -p') do set origvalue=%%a   L68
if not "%origvalue%"=="00" (if not "%origvalue%"=="01" → "frp文件末尾1字节16进制数值不是00或01" → FATAL)              L69
if "%func%"=="oemunlockon"  set target=01                                   L70
if "%func%"=="oemunlockoff" set target=00                                   L71
if "%origvalue%"=="%target%" → "frp文件无需修补" → 直接 DONE                  L72-L74
::算地址
for /f "tokens=2 delims= " %%a in ('busybox.exe stat -t %outputpath%') do set filesize=%%a    L77
call calc s address nodec %filesize% 1                                      L79   ← address = size - 1
HexTool.exe %outputpath% %address% 1 %target% 1>%tmpdir%\output.txt 2>&1     L82
find "Done!" %tmpdir%\output.txt || 重试                                     L84
```

**写偏移的确切含义**：

- 地址 = **文件字节数 − 1**，长度 = **1 字节**，写入值 = `01`（oem unlock on）或 `00`（off）。
- 前置校验「末字节必须是 00 或 01」是**分区类型防误伤**：这是对「这个分区是不是真的 frp」的廉价结构校验——非 frp 分区的最后一个字节极大概率不是 00/01。校验不过直接 FATAL，不写。
- **幂等**：`L72` 先比较现值与目标值，相同就跳过写（`goto PATCHFRP-DONE`），避免不必要的镜像改动。
- 成功判据：`HexTool.exe` 的 stdout 里必须出现 `Done!`（`L84`）——这是本层唯一一处「工具成功标志字符串」校验，其它地方都靠 `%errorlevel%`。

**HexTool 的地址参数是十进制**（这一点可由同包内其它调用反推）：`煤油工具箱.bat:L828-L834` 写 private 分区用 `00069632`、`00069636`、`00069892`、`00069908`、`00081920` 等「8 位补零」地址。若按十六进制解读，`00069632`=0x00069632=431666；若按十进制解读 = 69632 = **0x11000**，且 `00069892` = 69892 = 0x11104，`00081920` = 81920 = 0x14000——三者都是规整的十六进制边界（0x11000 / 0x11104 / 0x14000），十进制解读自洽。故 `HexTool <file> <dec_addr> <len> <hex>`。PATCHFRP 传的是 `calc s` 算出的裸十进制，未补零，说明补零只是书写风格而非格式要求。

**副作用**：`L82` 把 stdout 重定向到 `%tmpdir%\output.txt`（覆盖 `%tmpdir%` 下的公共临时文件），再用 `find "Done!"` 读；同时**没有**把 output.txt 追加进 `%logfile%` 再判断（对比 PATCHVBMETA 直接 `1>>%logfile%`）。日志会丢掉 HexTool 的原始输出，排障信息不足。

**调用者**：未发现。但 `煤油工具箱.bat:L861-L867` 有一段**等价的内联实现**（ODM 解锁路径：`read qcedl frp` → `busybox stat -t` 取大小 → `calc s var nodec %var% 1` → `HexTool … %var% 1 01` → `write qcedl frp`），逻辑与 `PATCHFRP` 的 `oemunlockon` 完全一致，只是没走 `imgkit`。这说明 `PATCHFRP` 是「把已在主脚本里验证过的操作抽成通用函数」，但主脚本没回改成调用它——典型的重构半途。

### 3.4 RECINST（`L1858-L1917`）

用途：把 TWRP 的 ramdisk **注入 boot 镜像**，用于「无独立 recovery 分区的机型」（注释见 `煤油工具箱.bat:L455`「将TWRP注入boot文件(用于无recovery分区设备)」）。

```bat
set bootpath=%args2%& set outputpath=%args3%& set recpath=%args4%& set mode=%args5%        L1861
call log … 接收变量:…mode:%noprompt%                       ← ⚠ 打印的是 %noprompt%，不是 %mode%（L1862 笔误）
rd /s /q / md %tmpdir%\imgkit-recinst                                                      L1870-L1871

::① 判断 rec 文件格式：只看扩展名
for %%i in ("%recpath%") do (if not "%%~xi"==".img" goto RECINST-2)                         L1873
::② .img → unpack 取 ramdisk.cpio，重命名成 ramdisk.cpio_new
magiskboot.exe unpack %recpath%                                                            L1877
if not exist …\ramdisk.cpio → 报错重试                                                      L1879
move /Y …\ramdisk.cpio …\ramdisk.cpio_new                                                  L1880
::②' 非 .img（cpio/gz 等）→ 直接复制成 ramdisk.cpio_new
echo.F| xcopy /Y %recpath% …\ramdisk.cpio_new                                              L1885

::③ 解包目标 boot —— 注意是 unpack -h
magiskboot.exe cleanup                                                                     L1891
magiskboot.exe unpack -h %bootpath%                                                        L1892
if not exist …\header → 报错重试 "找不到…\header, 解包%bootpath%失败"                          L1896

::④ 从 cmdline 里去把 skip_override 删掉（生成 shell 片段，交给 busybox ash 跑）
echo.#!/busybox ash > …\cmdline_remove_skip_override.sh                                    L1897
echo.sed -i "s|$(grep '^cmdline=' …/header | cut -d= -f2-)|$(grep '^cmdline=' …/header | cut -d= -f2- | sed -e 's/skip_override//' -e 's/  */ /g' -e 's/[ \t]*$//')|" …/header >> …sh    L1898
busybox.exe ash …\cmdline_remove_skip_override.sh    || call log %logger% W（非致命）        L1900

::⑤ 反转 kernel 的 initramfs 选择：want_initramfs → skip_initramfs
magiskboot.exe hexpatch kernel 77616E745F696E697472616D6673 736B69705F696E697472616D6673     L1904
::  ↑ ASCII "want_initramfs"                    ↑ ASCII "skip_initramfs"

::⑥ 换上 TWRP 的 ramdisk 并重打包
move /Y …\ramdisk.cpio_new …\ramdisk.cpio                                                   L1908
magiskboot.exe repack %bootpath% boot_new.img                                               L1912
move /Y …\boot_new.img %outputpath%                                                         L1914
```

**技术要点**：

1. **`skip_initramfs` 的方向与 MAGISKPATCH 相反**。`MAGISKPATCH` 是 `skip_initramfs → want_initramfs`（`L382`，让 kernel 改用 ramdisk 里的 `/init`，即 Magisk 的 root 路径）；`RECINST` 是 `want_initramfs → skip_initramfs`（`L1904`）——但注意 `magiskboot hexpatch <file> <from> <to>` 的语义是「把 from 换成 to」，所以这里是把「我要用 initramfs」改成「跳过 initramfs」？从 TWRP 注入的目标看，这一步的真实意图应是让 kernel 引导 recovery ramdisk。**方向命名与本脚本其它处的用法存在不一致的可能**，`magiskboot hexpatch` 的参数顺序为 `hexpatch <file> <from> <to>` 属外部知识（二进制不在包内），故此处**未确认**；能确证的是：两处 hexpatch 用的是同一对 ASCII 串，但**方向相反**（`L382` vs `L1904`）。
2. **`skip_override` 的 cmdline 手术**：`L1898` 生成的 shell 片段用 `grep '^cmdline=' header | cut -d= -f2-` 取出整个 cmdline 作为 sed 的**匹配串**，再把 `skip_override` 删掉、压缩连续空格、去掉行尾空白，最后 `sed -i "s|<原>|<新>|" header` 原地替换。这是典型的「用整行内容做锚点做单次替换」套路。`skip_override` 是 Magic/Android 引导参数，存在时会让 bootloader 忽略某些参数，注入 recovery 时需要去掉（外部知识，**未确认**具体语义）。
3. **`busybox.exe ash` 跑脚本而非直接写 sed**：因为 CMD 里 `$()` 命令替换无法直接表达，只能落成一个 `.sh` 交给 busybox 的 ash。这是本工具箱「借 Unix shell 补 CMD 表达力」的通用手法。
4. **`magiskboot unpack -h` 的实际行为未确认**（二进制不在参考包内）。脚本后续既依赖 `header` 文件存在（`L1896`），又要对 `kernel` 做 hexpatch（`L1904`）并 `repack`（`L1912`）。若 `-h` 确是「仅转储 header、不提取 kernel/ramdisk」，则 `L1904` 的 hexpatch 必然失败（且失败非致命）、`repack` 将缺少 kernel 段——产物可疑。**这是一个需要在有二进制时实测确认的高风险点**，本文不做断言。
5. **错误处理比 MAGISKPATCH 弱**：`L1900`、`L1904` 都只记日志；`L1904` 的注释文本 `hexpatch_kernel失败` 也只是 W/E 级。

**调用者**（2 处，都在 rec 注入路径）：
- `煤油工具箱.bat:L473`（`FLASHREC-RECINST`，用户自选 TWRP + boot + 输出目录）：
  `call imgkit recinst %bootpath% %sel__folder_path%\boot_new.img %recpath%`
- `煤油工具箱.bat:L507`（`FLASHREC-FLASH-AB`，先 9008 回读 `boot_%slot%` 备份，再注入后回刷）：
  `call imgkit recinst …\bak\boot_%slot__cur%_%baktime%.img %tmpdir%\boot_rec.img %sel__file_path% noprompt`
  这条是**真机流程**，且带了 `noprompt`——意味着一旦 `RECINST` 内部有哪个步骤要 `pause`，整个无人值守流程就会挂住。见 §8.3。

---

## 4. calc（`bin__calc.bat.txt`，213 行）

### 4.1 设计

批处理原生只有 32 位整数 `set /a`，无法做 64 位字节数换算和浮点。`calc` 层的职责就是**把数值运算外包给 `tool\Win\calc.exe`（浮点）和 `tool\Win\numcomp.exe`（比较），再用 `for /f` 把 stdout 捞回变量**。

统一取参（`bin__calc.bat.txt:L26`）：`args1=操作`、`args2=输出变量名`、`args3=小数模式`、`args4/args5=操作数`。

### 4.2 子命令全表

| 子命令 | 语义 | 实际调用 | 证据 |
|---|---|---|---|
| `p` | 加 | `calc.exe <n1> p <n2> <dec>` | `L176-L177`,`L187` |
| `s` | 减 | `calc.exe <n1> s <n2> <dec>` | `L178-L179` |
| `m` | 乘 | `calc.exe <n1> m <n2> <dec>` | `L180-L181` |
| `d` | 除 | `calc.exe <n1> d <n2> <dec>` | `L182-L183` |
| `b2gb` | 字节→GB | `calc.exe b d 1073741824 <dec>` | `L48-L55` |
| `gb2b` | GB→字节 | `calc.exe gb m 1073741824 <dec>` | `L57-L64` |
| `b2mb` | 字节→MB | `calc.exe b d 1048576 <dec>` | `L66-L73` |
| `mb2b` | MB→字节 | `calc.exe mb m 1048576 <dec>` | `L75-L82` |
| `b2kb` | 字节→KB | `calc.exe b d 1024 <dec>` | `L158-L165` |
| `kb2b` | KB→字节 | `calc.exe kb m 1024 <dec>` | `L167-L174` |
| `kb2mb` | KB→MB | `calc.exe kb d 1024 <dec>` | `L122-L129` |
| `mb2kb` | MB→KB | `calc.exe mb m 1024 <dec>` | `L131-L138` |
| `kb2gb` | KB→GB | `calc.exe kb d 1048576 <dec>` | `L140-L147` |
| `gb2kb` | GB→KB | `calc.exe gb m 1048576 <dec>` | `L149-L156` |
| `sec2b` | 扇区→字节 | `calc.exe sec m <secsize> <dec>` | `L84-L91` |
| `b2sec` | 字节→扇区 | `calc.exe b d <secsize> <dec>` | `L93-L100` |
| `sec2kb` | 扇区→KB（两段） | `calc.exe sec m <secsize> 12` → `calc.exe … d 1024 <dec>` | `L102-L110` |
| `kb2sec` | KB→扇区（两段） | `calc.exe kb m 1024 12` → `calc.exe … d <secsize> <dec>` | `L112-L120` |
| `numcomp` | 数值比较 | `numcomp.exe <n1> <n2>` | `L35-L45` |

要点：
- 换算因子一律用 **1024 进制**（`1024`/`1048576`/`1073741824`），不是 1000 进制。
- `sec2kb`/`kb2sec` 是**两段式**，中间固定用 `12` 位小数（`L105`、`L115`）保住精度，第二段才按用户要求截断。这是本文件里唯一显式处理精度损失的地方。
- `b2sec` 不保证整除（如 `L149-L150` 传 `nodec-intp1` 求「向上取整的扇区数」，见 `bin__partable.bat.txt:L149-L150`）。

### 4.3 小数处理模式（`:calcmode-argsprocess`，`L193-L199`）

| `decmode` | `decnum`（传给 calc.exe 的小数位） | 语义 |
|---|---|---|
| `nodec` | `0` | 直接取整（截断） |
| `nodec-intp1` | `13` | 先算 13 位，再后处理为「向上取整」 |
| `dec` | `2` | 保留 2 位 |
| `dec-<N>` | `N`（取 `%decmode:~4%`） | 保留 N 位 |

`nodec-intp1` 的后处理（`:calcmode-nodec-intp1`，`L201-L204`）：
```bat
for /f "tokens=1,2 delims=. " %%a in ('echo.%result%') do set args1=%%a& set args2=%%b
if "%args2%"=="0000000000000" (set result=%args1%) else (for /f %%a in ('calc.exe %args1% p 1 0') do set result=%%a)
```
- 若 13 位小数**全为 0** → 说明整除，直接取整数部分；
- 否则 `整数部分 + 1` → **向上取整**。
- 命名为 `intp1` = 「integer + 1」，名副其实。
- ⚠️ 这个子程序复用了 `args1`/`args2`（脚本级全局），而 `args1` 原本是**子命令名**。虽然调用点都在 `ENDLOCAL & set %output%=…` 之前、用完即弃，但属于易踩的作用域污染。更隐蔽的是：只有在 `decmode==nodec-intp1` 时才调用它（`L52` 等），此时 `%output%` 已在 `:calcmode-argsprocess` 里取好，所以暂未出故障。

### 4.4 结果回传：动态变量名

每个子命令尾行都是同一句：
```bat
ENDLOCAL & set %output%=%result%        L54, L63, L72, …
```
`%output%` 是 `args2`（如 `address`、`spaceleft`），`ENDLOCAL` 与 `set` 在同一行 → 整行**先展开、后执行**，所以 `%output%`/`%result%` 取的是 SETLOCAL 内的值，而 `set` 落在 SETLOCAL 外的调用者作用域。这是批处理实现「按名返回」的标准技巧，全工具箱通用（`random` 用 `random__str`、`dl` 用 `dl__result`、`framework chkdiskspace` 用 `framework__chkdiskspace__enough`）。

### 4.5 numcomp 与自检

```bat
for /f "tokens=1 delims=#" %%a in ('numcomp.exe %num1% %num2%') do set result=%%a     L38
if not "%result%"=="greater" (if not "%result%"=="less" (if not "%result%"=="equal" goto NUMCOMP-FAILED))   L39
ENDLOCAL & set calc__numcomp__result=%result%                                           L41
```
只有 `greater` / `less` / `equal` 三态；其它输出直接 FATAL（`L43-L45`）。调用方用字符串比较结果（`bin__framework.bat.txt:L39`）。

**精度自检**（`bin__framework.bat.txt:L219-L221`，属启动检查阶段）：
```bat
if not exist tool\Win\calc.exe → FATAL
for /f %%a in ('calc.exe 2199023255552 m 999 6') do (
    if not "%%a"=="2196824232296448.000000" → "calc.exe无法运行" → FATAL )
```
- `2199023255552 × 999 = 2196824232296448`（= 2^41 × 999，正好卡在超过 32 位/双精度整型可精确保和的边界附近）；
- 期望输出带 **6 位小数**且能精确打印到个位 → 证明 `calc.exe` 是**浮点（至少 double）实现**且默认输出格式为 `整数部分.6位小数`。
- 这同时是 `calc.bat` 里 `for /f %%a in ('calc.exe …')` 能拿到「一个 token」的原因：输出是单行单字段。
- `numcomp.exe` 的自检在 `L254-L255`：`numcomp.exe 999 888 | find "greater"`。

### 4.6 调用点

| 调用 | 位置 | 用途 |
|---|---|---|
| `call calc s address nodec %filesize% 1` | `bin__imgkit.bat.txt:L79` | FRP 末字节地址 = size−1 |
| `call calc s var nodec %var% 1` | `煤油工具箱.bat:L865` | 同上（ODM 解锁内联版） |
| `call calc m spaceleft nodec %spaceleft_kb% 1024` | `bin__framework.bat.txt:L37` | KB→MB |
| `call calc numcomp %spaceleft% %sizetocompare%` | `bin__framework.bat.txt:L38-L39` | 磁盘空间够不够 |
| `call calc b2sec length_sec nodec %length% %secsize%` | `bin__partable.bat.txt:L29` | 分区长度字节→扇区 |
| `call calc b2sec parstart_sec …` / `parend_sec` / `parsize_sec` | `bin__partable.bat.txt:L100,L104,L105` | 分区表扇区换算 |
| `call calc p parstartnext_sec nodec …` / `call calc s parend_sec nodec … 1` | `bin__partable.bat.txt:L106,L107` | 起始+长度−1 = 末尾扇区 |
| `call calc d filesize_sec_512 nodec-intp1 %filesize% 512` / `4096` | `bin__partable.bat.txt:L149-L150` | 向上取整的扇区数，算镜像对齐 |
| `call calc s var nodec %var% %var2%` / `call calc numcomp %var% %filesize%` | `bin__write.bat.txt:L244,L247,L249-L250` | 写入偏移与越界判断 |

`calc` 是整个工具箱的**唯一算术出口**，`imgkit` 只用了其中一条（`s`）。

---

## 5. dl（`bin__dl.bat.txt`，182 行）

### 5.1 参数与两个入口

```bat
::call dl direct 直链               完整保存路径 [retry|once] [notice|noprompt] 检查字符串(可选)   L3
::        lzlink 蓝奏分享链接(-****) 完整保存路径 [retry|once] [notice|noprompt] 检查字符串(可选)   L4
set dlmode=%args1%& set link_orig=%args2%& set filepath=%args3%
set dltimes=%args4%& set fileexistfunc=%args5%& set chkfield=%args6%                        L11
goto %dlmode%                                                                              L13
```

- `dltimes`：`once` = 失败即止；其它（含空）= 失败后 `ECHO.自动重试…` 并 `goto` 回入口重来（`L27`、`L45`、`L66`）。
- `fileexistfunc`：`noprompt` = 目标存在就静默删除覆盖；否则警告 + `pause>nul`（`L19-L22`）。
- `chkfield`：下载后校验镜像里必须包含该子串（`L163`）。
- 结果回传：`ENDLOCAL & set dl__result=%result%`（`L71`）。

### 5.2 `direct`（`L18-L27`）

```bat
if not "%fileexistfunc%"=="noprompt" ( if exist %filepath% ( 警告+pause; del /Q %filepath% || 重试 ) )  L19-L22
set link_direct=%link_orig%                                                                 L23
call :startdl                                                                               L24
if "%result%"=="y" goto FINISH                                                              L25
if "%dltimes%"=="once" (goto FINISH) else (ECHO.自动重试... & goto DIRECT)                    L27
```

### 5.3 蓝奏云解析：单文件 vs 多文件的分流

分流依据**不是链接格式，而是链接里有没有 `[...]`**（`L31-L32`）：
```bat
for /f "tokens=2 delims=[]" %%a in ('echo.%link_orig%') do (if not "%%a"=="" goto LZLINK-STV)
goto LZLINK-SINGLE
```
- 命中第 2 个 `[]` 分组 → `LZLINK-STV`（多文件，"STV" = serveral 的缩写风格）；
- 否则 → `LZLINK-SINGLE`。

链接形如 `https://host/xxx[a][b][c]`，每个 `[]` 里放一个蓝奏分享链接。多文件时逐段遍历（`L54-L66`）：
```bat
set num=1
:LZLINK-STV-1
for /f "tokens=%num% delims=[]" %%a in ('echo.%link_orig%') do set link_lz=%%a
if "%link_lz%"=="" goto FINISH
…
::按 num 的位数选补零宽度
if not "%num:~0,1%"=="" set filepath=%filepath_orig%.00%num%      ← num ≥ 100
if not "%num:~1,1%"=="" set filepath=%filepath_orig%.0%num%       ← num ≥ 10
if not "%num:~2,1%"=="" set filepath=%filepath_orig%.%num%        ← num ≥ 1
call :startdl
if "%result%"=="y" set /a num+=1& goto LZLINK-STV-1
```
即输出名 `xxx.001` / `xxx.01` / `xxx.1`（`L60-L62`）。⚠️ 这三个 `if` 是**顺序执行、后者覆盖前者**（不是 if/else），对 `num=1`：`%num:~1,1%` 为空 → 跳过第 2 个；`%num:~2,1%` 为空 → 跳过第 3 个，最终文件名 `xxx`（无后缀）。对 `num=12`：`%num:~0,1%`='1' 非空 → 设 `xxx.0012`；`%num:~1,1%`='2' 非空 → 覆盖为 `xxx.012`；`%num:~2,1%` 空 → 保持 `xxx.012`。所以实际是 `.001`~`.999` 的三位补零，**但 `num=1` 时无后缀**、`num=2..9` 时 `.002`~`.009`（两位补零到三位，即 `.00N`）。逻辑正确但写法脆弱（`num` 超过 999 会退化成 `.1000`）。

**密码检测**：`echo.%link_lz% | find "-"`（`L40`、`L58`）。命中即认为链接末尾带 `-密码`，走 `getlzdirectlink-pswd`；否则走 `-nopswd`。这与头部注释的 `lzlink 蓝奏分享链接(-****)`（`L4`）对应。

### 5.4 无密码流的四段跳（`:getlzdirectlink-nopswd`，`L75-L109`）

```
分享页 ──curl──▶ </tp/xxx 链接> ──curl──▶ <developer 链接> ──curl -i──▶ 302 Location = 直链
```

**① 拆链接成 host + path**（`L77-L82`）：
```bat
echo.%link_lz% | find "https"   → 有则 var=2，无则 var=1        （是否带协议头）
for /f "tokens=%var% delims=/ " %%a in ('echo.%link_lz%') do set link_lz_value1=%%a   ← host
echo.%link_lz% | find "/tp/"    → 有则 var+=2，无则 var+=1       （是否已是 /tp/ 形态）
for /f "tokens=%var% delims=/ " … do set link_lz_value2=%%a                            ← 路径段
```
用 `tokens` 的**偏移是动态的**来适配「带不带 `https://`」「带不带 `/tp/`」四种组合。

**② 分享页 → `/tp/` 链接**（`L84-L90`）：
```bat
curl.exe -k -A "Mozilla/5.0 (iPhone; CPU iPhone OS 6_0 like Mac OS X) AppleWebKit/536.26 … Safari/8536.25" \
     https://%link_lz_value1%/%link_lz_value2% 1>%tmpdir%\output.txt 2>%tmpdir%\output2.txt
busybox.exe sed -i "s/\"/#/g" %tmpdir%\output.txt                    ← 关键：把 " 全换成 #
for /f "tokens=4 delims=#" %%a in ('type …\output.txt ^| find "<div class=#mh#><a href=#/tp/"') do set link_lz_tp_part2=%%a
set link_lz_tp=https://%link_lz_value1%%link_lz_tp_part2%
```
用 **iPhone UA** 拿「移动版」页面（结构比桌面版简单，只有一层 `<div class="mh"><a href="/tp/…">`）。**把 `"` 换成 `#` 再以 `#` 作分隔符取第 4 段**，这是批处理解析 HTML 属性的通用廉价技巧——避免 `"` 与 CMD 的引号语义冲突（同 `RECINST` 的 `-t#:e`，`L145`）。

**③ `/tp/` 页 → developer 链接（两段拼接）**（`L92-L100`）：
```bat
curl.exe -k -A "<iPhone UA>" %link_lz_tp%
for /f "tokens=4 delims='; " %%a in ('… ^| find "var vkjxld "') do set link_lz_developer_part1=%%a
for /f "tokens=4 delims='; " %%a in ('… ^| find "var hyggid "')  do set link_lz_developer_part2=%%a
set link_lz_developer=%link_lz_developer_part1%%link_lz_developer_part2%
```
蓝奏把真实下载域名拆成两个 JS 变量（`var vkjxld` 和 `var hyggid`）做简单反爬，脚本把两段拼回去。**这两个变量名是蓝奏前端的实现细节，属硬编码脆弱点**。

**④ developer 链接 → 直链**（`L102-L108`）：
```bat
curl.exe -i -k -A "<iPhone UA>" %link_lz_developer% \
    --header "Accept: text/html,…" --header "Accept-Encoding: gzip, deflate" \
    --header "Accept-Language: zh-CN,zh;q=0.9" --header "Cache-Control: no-cache" \
    --header "Connection: keep-alive" --header "Pragma: no-cache" \
    --header "Upgrade-Insecure-Requests: 1"
for /f "tokens=2 delims= " %%a in ('type …\output.txt ^| find /I "location: "') do set link_direct=%%a
set link_direct="%link_direct%"          ← 补上引号，防 URL 含 & 被 CMD 截断
```
`curl -i` 保留响应头，从 302 的 `Location:` 取真链。最后**给直链包一层双引号**（`L107`），因为蓝奏直链常带 `&`，不引起来 CMD 会把 `&` 当命令分隔符——这是非常必要的防御。

### 5.5 有密码流（`:getlzdirectlink-pswd`，`L111-L152`）

差异只在「③ → ④」之间，多一次 **POST 换 sign**：

| 步骤 | 无密码 | 有密码 |
|---|---|---|
| 拆密码 | – | `for /f "tokens=1,2 delims=-"` → `link_lz_withoutpswd` / `link_lz_pswd`（`L113`） |
| 分享页 → `/tp/` | 同 | 同（`L122-L127`） |
| `/tp/` 页要抓的东西 | 两个 `var`（域名两段） | **`var vidksek` = postsign**（`L132`） |
| 中间请求 | 无 | `POST /ajaxm.php`，body `action=downprocess&sign=<postsign>&p=<密码>`，`Referer: https://<host>`（`L136`） |
| 从响应里取 | – | `sed "s/\"/ /g;s/\\//g"` 后 `tokens=6,10 delims= ` → part1 / part2（`L137-L139`） |
| 拒绝条件 | – | `if "%link_lz_developer_part2%"=="inf"` → 失败（`L142`），`inf` 是蓝奏的错误标记 |
| 拼 developer | `part1 + part2` | `part1 + "/file/" + part2`（`L143`） |
| 取直链 | `curl -i` → `Location` | 同（`L146-L149`） |

`sed "s/\"/ /g;s/\\//g"`（`L137`）同时做了两件事：去引号、**去反斜杠**——因为蓝奏的 JSON 响应里 URL 是被 `\/` 转义的。

### 5.6 下载与校验（`:startdl`，`L155-L174`）

```bat
echo.%link_direct% >>%logfile%                                        L157
if exist %tmpdir%\dl rd /s /q %tmpdir%\dl                             L158  ← ⚠ 每次下载前清空
md %tmpdir%\dl                                                        L159
cd /d %tmpdir%\dl                                                     L160
aria2c.exe --max-concurrent-downloads=16 --max-connection-per-server=16 \
           --split=16 --file-allocation=none --out=bffdl.tmp %link_direct%     L161
|| ( cd /d %framework_workspace% & 报错 & set result=n & goto :eof )
cd /d %framework_workspace%                                           L162
if not "%chkfield%"=="" find "%chkfield%" "%tmpdir%\dl\bffdl.tmp" || 校验失败 → result=n   L163
for /f "tokens=3 delims= " %%a in ('dir %tmpdir%\dl /-C /-N /A:-D ^| find "bffdl"') do set var=%%a   L164
::if %var% LEQ 10240 ( … 一堆 find "code" … ": 400," ": 201," ":500," … )   ← 整块已注释 L165-L171
move /Y %tmpdir%\dl\bffdl.tmp %filepath% || 移动失败 → result=n        L172
set result=y& call log %logger% I 本次下载成功                          L173
```

**aria2c 参数语义**：
- `--max-concurrent-downloads=16`：同时最多 16 个下载任务（这里是 1 个 URL，实际无用）；
- `--max-connection-per-server=16` + `--split=16`：单服务器 16 连接 / 分 16 片——**这是真正的多线程下载开关**；
- `--file-allocation=none`：不预分配（避免 FAT32/exFAT 上大文件预分配失败，也避免慢盘卡住）；
- `--out=bffdl.tmp`：固定中间文件名，成功后才 `move` 到最终名（原子落盘思路）。

**关于「断点续传」——本实现里实际上没有**：
- aria2c 的续传依赖同目录下的 `<out>.aria2` 控制文件；
- 但 `L158` 每次下载前 `rd /s /q %tmpdir%\dl`，`L159` 重新 `md`，把控制文件一起删掉了；
- 且 `--out` 恒为 `bffdl.tmp`，下一轮从零开始；
- 因此所谓「重试」是 **完整重新下载**，不是断点续传。这一点与任务描述中的「断点」不符，需明确：**参考包内无断点续传实现**。

**校验机制（两级，实际只生效一级）**：
1. **子串存在性**（`L163`，生效）：`find "%chkfield%" bffdl.tmp`——例如校验下载到的文件里含某段特征文本。对二进制镜像意义有限，但对「蓝奏返回错误页而非文件」这种情形很有效（错误页里不会有该子串）。
2. **体积 + 错误码**（`L164-L171`，**整块已注释**）：原设计是「若文件 ≤ 10240 字节，则说明拿到的是错误页」，再去匹配 `": 400,"`、`": 201,"`、`"path not found"` / `":500,"`、`"could not be found"`、`"failed to get file"`、`"Loading storage, please wait"` 等蓝奏 API 错误串。这块被注释掉后，**体积异常不再被拦截**——一个 200 字节的错误页会顺利 `move` 成正式文件名，只在 `chkfield` 非空时才被发现。

**调用者**：全包检索 `call dl` **无命中**。`bin__framework.bat.txt:L168` 只做存在性自检（`if not exist dl.bat → FATAL`）。所以 `dl` 当前是**未被主流程使用的备用模块**（与 `SPARSE2RAW`/`PATCHVBMETA`/`PATCHFRP` 同属「已实现但未接线」的一类）。

---

## 6. 调用链与集成

### 6.1 imgkit 被谁调用

| 场景 | 上游位置 | 调用 | 后接动作 |
|---|---|---|---|
| Root（`meizu20inf` 无 9008） | `煤油工具箱.bat:L1128`（`:ROOT-SELBOOT`） | `call imgkit magiskpatch <用户选的img> %tmpdir%\boot_patched.img %zippath% noprompt` | `reboot system fastboot` → `write fastboot %bootpar%` → `reboot fastboot system`（`L1129-L1131`） |
| Root（常规，9008） | `煤油工具箱.bat:L1142`（`:ROOT-EDL`） | `call imgkit magiskpatch res\%product%\bak\%targetpar%_%baktime%.img %tmpdir%\boot_patched.img %zippath% noprompt` | `write qcedl %targetpar% %tmpdir%\boot_patched.img` → `reboot qcedl system`（`L1143-L1144`） |
| TWRP 注入（`ab` 布局） | `煤油工具箱.bat:L507`（`:FLASHREC-FLASH-AB`） | `call imgkit recinst …\bak\boot_%slot%_%baktime%.img %tmpdir%\boot_rec.img <twrp> noprompt` | `write qcedl boot_%slot% %tmpdir%\boot_rec.img`（`L508`） |
| TWRP 注入（用户自选） | `煤油工具箱.bat:L473`（`:FLASHREC-RECINST`） | `call imgkit recinst %bootpath% %sel__folder_path%\boot_new.img %recpath%` | 仅提示产物路径（`L474`） |

**ROOT-EDL 的完整闭环**（这是本层最有价值的集成形态）：
```
reboot system qcedl          → chkdev qcedl rechk 1
write qcedlsendfh <port> res\<product>\devprg auto      ← 送 firehose 引导
read qcedl %targetpar% res\…\bak\%targetpar%_%time%.img notice   ← 回读原 boot/init_boot，自动备份
imgkit magiskpatch <备份img> %tmpdir%\boot_patched.img <magisk包> noprompt
write qcedl %targetpar% %tmpdir%\boot_patched.img      ← 回刷
reboot qcedl system
```
`noprompt` 贯穿始终，因为这是无人值守链。**「先备份再修补」的次序**保证即使修补失败也有原厂镜像可恢复（对应 `煤油工具箱.bat:L1061-L1075` 的「Root 不开机恢复」功能）。

### 6.2 未接线的子命令

| 子命令 | 状态 | 替代实现 |
|---|---|---|
| `SPARSE2RAW` | 无调用者 | 主流程改用 firehose 的 `sparse=` XML 属性（`bin__write.bat.txt:L461`） |
| `PATCHVBMETA` | 无调用者 | 无（`avb` 解锁方案在 `dev.csv:L38` 只出现一次、无对应设备） |
| `PATCHFRP` | 无调用者 | `煤油工具箱.bat:L861-L867` 有等价内联实现 |
| `dl` | 无调用者 | 资源获取走「打开浏览器」（`call open common https://…`，`煤油工具箱.bat:L117`，`驱动运行库.bat:L1-L3`） |

这 4 个「孤儿模块」的共同特征：都是**参数化封装好的通用工具**（`PATCHFRP` 甚至有 `oemunlockon/off` 双向、`PATCHVBMETA` 有 `verify` 可逆），但主脚本仍用内联老代码。从工程角度，这是「抽函数但没完成替换」的中间态。

### 6.3 工具自检与真实依赖的错位

`bin__framework.bat.txt:L110-L295` 的 `:STARTPRE` 逐项检查工具，其中被**注释掉**的检查值得注意：

| 工具 | 检查状态 | 影响 |
|---|---|---|
| `simg_dump.exe` | **注释掉**（`L273-L275`） | `SPARSE2RAW` 与 `write qcedl` 的 sparse 探测都没有运行时保护 |
| `aria2c.exe` | **注释掉**（`L210-L212`） | `dl` 无保护 |
| `curl.exe` | **注释掉**（`L222-L224`） | 蓝奏解析无保护 |
| `bootctl` | **启用**（`L283`） | 但只在槽位切换里用 |
| `magiskboot.exe` | **启用**（`L247-L249`） | `magiskboot 2>&1 \| find "Usage"` |
| `HexTool.exe` | **启用**（`L240-L242`） | |
| `calc.exe` | **启用**（`L219-L221`） | 带精确值自检 |
| `numcomp.exe` | **启用**（`L253-L255`） | |

即：**当前主流程真正用到的（`magiskboot`/`HexTool`/`calc`/`numcomp`/`busybox`/`7z`/`adb`/`fastboot`）都做了检查；而被注释的恰好都是孤儿模块的依赖。** 这与 6.2 的结论互相印证——孤儿模块确实不在热路径上。

---

## 7. 设计评价

### 7.1 值得肯定的部分

1. **`boot_patch.sh` MD5 指纹化**（`L162`、`L168-L204`）
   在 CMD 这种缺乏包管理/版本探测能力的环境里，用「上游修补脚本的内容哈希」做行为契约，是对的选择。它比版本号更严格：Alpha/vivo/phh 分支靠同一套代码自然分流，无需额外条件判断。18 个方案收敛 35 个指纹，说明维护者做过归并，不是无脑复制。

2. **「空补丁回退」策略**（`L383`）
   kernel hexpatch 全部落空时**删掉 unpack 出来的 `kernel` 文件**，让 `repack` 沿用原镜像的 kernel。这既避免了「补丁没打上却重写了一版 kernel」的风险，又省掉了「判断 kernel 是否被改动」的额外逻辑。用 `PATCHEDKERNEL` 标志位 + 文件删除，是批处理里相当干净的实现。

3. **先备份再修补的闭环**（`L1140-L1144`）
   `read qcedl → imgkit magiskpatch → write qcedl`，且备份文件名带时间戳。即使 `MAGISKPATCH` 产出坏包，也有原厂镜像可回滚。

4. **`MODE0/MODE1` 幂等设计**
   对已补过的镜像先 `restore` 回原厂态再打补丁，重复执行不会叠加污染。这是「可重复运行」的关键。

5. **`PATCHFRP` 的结构前置校验 + 幂等短路**（`L69`、`L72-L74`）
   写前先验「末字节 ∈ {00,01}」，否则拒绝；值已正确就跳过写。对一个直接改分区字节的操作来说，这两条防线很实在。

6. **`calc` 的精度自检**（`bin__framework.bat.txt:L221`）
   用 `2^41 × 999` 的精确期望值验证浮点引擎可用性，比「跑一下不报错就算过」强得多。

7. **借 Unix shell 补 CMD 短板**
   `busybox sed -i` 清 CR（`L340`）、`busybox ash` 跑 `$()` 命令替换（`L1900`）、`busybox tail -c 1 | xxd -p` 读末字节（`L68`）——都把「CMD 做不到的事」外包给 busybox，而不是硬凑。

### 7.2 主要问题

#### A. 结构性问题

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| A1 | **1765 行里约 95% 是逐版本复制的近似代码**。18 个分支每个 ~95 行，其中「检查组件 / cd / unpack / cpio test / echo config / sed / hexpatch kernel」几乎逐字相同 | `L209-L1831` | 改一个 kernel 补丁要改 18 处；已有事实性不一致（PROCA 只在 2 个分支、defex 变体只在 17200）说明维护已经失控 |
| A2 | **无 header 解析能力**。全程以「unpack 产物文件名」作为镜像结构的隐式契约 | `L301`、`L350`-`L355`、`L368` | 一旦 magiskboot 改了产物命名或某机型结构异常，整层静默降级（例如「无 kernel」→ 跳过全部 kernel 补丁） |
| A3 | **`arch` 的 64/32 分支在 4 个分支里是死代码** | `set var=` 设于 11 处（`L263`/`L342`/`L441`/`L537`/`L634`/`L731`/`L829`/`L927`/`L1022`/`L1113`/`L1199`），但 `"%var% add …"` 只在 7 处被引用（`L635`/`L732`/`L830`/`L928`/`L1023`/`L1114`/`L1200`）；28001/28000/27006/27005 的 `%var%` 无人使用 | 误导后续维护者以为这些分支支持 32 位差异化 |
| A4 | **选项注释与实际实现不一致** | 注释 `L109`（PATCHVBMETAFLAG 适用 27006–24000）vs 实际 5 处（`L727`/`L823`/`L921`/`L1018`/`L1109`）；注释 `L116`（arch 64 位 27006–19000）vs 实际 `%var%` 生效于 26300–23000 | 文档即规格的场合会误判 |
| A5 | **依赖自检与真实调用错位**（§6.3） | `simg_dump`/`curl`/`aria2c` 检查被注释，恰是孤儿模块的依赖 | 孤儿模块一旦被启用，缺依赖时报错点很深 |

#### B. 可验证的行为缺陷（代码级）

| # | 缺陷 | 位置 | 分析 |
|---|---|---|---|
| B1 | **`noprompt` 下仍会 `pause`** | `L1850` | `if not "%mode%"=="noprompt" ECHOC {…}& pause>nul & ECHO.继续...)` —— 在 `(`‑`)` 块内 `&` 是命令分隔符，`if` 只保护了 `ECHOC`，`pause>nul` **无条件执行**。而 `煤油工具箱.bat:L1142` 正是带 `noprompt` 调用的无人值守 Root 链。只要修补前后大小不等（`L1847`），流程就会卡在 `pause`。对比 `L24`/`L43`/`L63`/`L100` 都用了 `if … ( … )` 括号包裹（正确），唯独 `L1850` 漏了括号 |
| B2 | **`18100`/`17200` 缺「无 ramdisk」守卫** | `L1596-L1601`、`L1676-L1681` | 直接 `cpio test` 不存在的 `ramdisk.cpio`。若 GKI 镜像（boot 无 ramdisk），`%errorlevel%` 不会是 0 或 1 → 落入 `L1601`/`L1681` 的「测试失败 + pause + goto 分支入口」→ **无限循环**（每次重解包、每次同样失败）。其余 16 个分支都有 `if not exist ramdisk.cpio` 守卫（`L222`…`L1760`） |
| B3 | **`25200`–`phh` 的 kernel 处理无守卫、无回退** | `L1046-L1057`、`L1130-L1141`、`L1216-L1227`、`L1300-L1311`、`L1385-L1396`、`L1478-L1490`、`L1569-L1581`、`L1649-L1661`、`L1729-L1743`、`L1817-L1830` | 没有 `if exist kernel`，没有 `PATCHEDKERNEL`，没有「未改动则删除 kernel」。对照：`PATCHEDKERNEL=false` 只在 `L269`/`L368`/`L467`/`L563`/`L660`/`L757`/`L855`/`L953` 八处出现（即 G1 的 4 支 + G2 的 2 支 + 26000 + 25210），其余 10 支全无。kernel 不存在时 hexpatch 失败只记 E 日志，然后 `repack` 照常跑——G1–G2 的稳健性设计在 G3 后半段起丢失 |
| B4 | **`PATCHVBMETA` 静默失败** | `L48-L50` | 失败只 `call log E`，不 `goto FATAL`、不设结果变量，`L50` 无条件「全部完成」。调用方无法区分「改成功」与「没匹配到」 |
| B5 | **`RECINST` 用 `unpack -h` 却依赖 `kernel` 与 `repack`** | `L1892` vs `L1904`/`L1912` | 若 `-h` 为「仅转储 header」，则 hexpatch 必然失败、repack 缺 kernel 段。**需实测确认**（二进制不在参考包内） |
| B6 | **`RECINST` 日志笔误** | `L1862` | 打印 `mode:%noprompt%`（`%noprompt%` 从未定义），实际变量是 `%mode%` |
| B7 | **`dl` 无断点续传** | `L158` | 每次下载前 `rd /s /q %tmpdir%\dl` 会删掉 aria2 的 `.aria2` 控制文件 + 固定 `--out=bffdl.tmp` → 「重试」= 全量重下 |
| B8 | **`dl` 体积/错误码校验被整块注释** | `L165-L171` | 200 字节的错误页会被 `move` 成正式产物；仅当 `chkfield` 非空时才能发现 |
| B9 | **`dl` 多文件命名边界** | `L60-L62` | 三个 `if` 顺序覆盖而非互斥，`num=1` 时无后缀；`num>999` 退化为 `.1000` |
| B10 | **`calc` 的 `args1/args2` 作用域污染** | `bin__calc.bat.txt:L202` | `:calcmode-nodec-intp1` 覆写全局 `args1`（本是子命令名）与 `args2`；当前因调用点顺序侥幸不出错 |
| B11 | **`PATCHFRP` 日志丢失工具输出** | `L82-L84` | `HexTool` 输出只写 `%tmpdir%\output.txt`（覆盖公共临时文件）供 `find "Done!"`，未 `type … >>%logfile%`，排障缺信息 |
| B12 | **两处重复的 MD5 映射行** | `L173`/`L174`、`L178`/`L179` | 无害但说明该表是手工维护、已出现脏数据 |
| B13 | **`imgkit__magiskpatch__ver*` 无人消费** | `L1854` | 全包检索该变量名仅此一处赋值，无读取方 → 版本回传形同虚设 |

#### C. 安全/可靠性提示（非缺陷，但需知情）

1. **`curl -k` 全程关闭证书校验**（`bin__dl.bat.txt:L85`、`L93`、`L103`、`L122`、`L130`、`L136`、`L146`）。在下载可执行组件（Magisk/TWRP）的场景下，`-k` 让中间人攻击变得容易。虽然 `chkfield` 能做一点内容校验，但它是可选的。
2. **蓝奏解析完全依赖前端实现细节**（`var vkjxld`/`var hyggid`/`var vidksek`/`<div class="mh"><a href="/tp/`，见 `L88`、`L95`、`L98`、`L125`、`L132`）。蓝奏改版即失效，且没有版本探测或降级路径。
3. **`7z` 从 apk 抽 `.so` 后直接 `magiskboot compress=xz` 后注入 ramdisk**，全程无签名/哈希校验。Magisk 官方流程靠 apk 签名保证来源，本工具箱只靠用户「选对文件」。
4. **`PATCHFRP` 直接改 frp 分区末字节**属绕过 Factory Reset Protection 的操作，`oemunlockoff` 分支还提供了「恢复」能力（`L71`），工程上是双向的；但这类分区写入本身无回滚保护（只靠调用方先备份）。

### 7.3 若重写本层的建议（顺序即收益比）

1. **抽出「分支无关的公共骨架」**：`unpack → 分类 → config → cpio → dtb → kernel → repack → 校验` 是全 18 分支共有的 8 步。把差异收进一张**数据表**（每行 = 方案标签 / 依赖组件 / config 键集合 / cpio 指令串 / dtb 策略 / kernel 补丁列表 / rootfs 门禁开关），批处理只保留一个 `for /f` 读表 + 一个执行器。可把 1765 行压到 200 行以内，且 A3/A4/B2/B3 这类不一致会被表格天然消除。
2. **把 35 条 `if md5==…` 换成配置文件**（同 `dev.csv` 的做法，`bin__framework.bat.txt:L45-L66` 已有现成的 `loadcsvconf` 机制）。新增版本只需加一行，不必改代码。
3. **补内容级校验**：至少对 `boot_new.img` 重跑一次 `magiskboot unpack` 确认能解、并比对 `ramdisk.cpio` 中 `init` 是否为 `magiskinit`（`cpio … exists init` 的用法 `RECINST` 已在用，`L1442`）。
4. **统一错误出口**：给 `imgkit` 各子命令定义结果变量（如 `imgkit__<cmd>__result=ok|fail`），让 `PATCHVBMETA`/`PATCHFRP`/`RECINST` 的静默失败可被调用方感知。
5. **修 B1（`L1850` 补括号）** 与 **B2（补 ramdisk 守卫）**——这两处是会在真机上直接卡死/死循环的问题，改动量各一行。

---

## 8. 未确认清单

以下内容因「二进制不在参考包内」或「参考包内无相关代码」而无法确证，不做断言：

| # | 待确认项 | 原因 | 影响面 |
|---|---|---|---|
| 1 | `magiskboot unpack` 的格式识别与 `-h` 的确切语义 | 二进制不在包内 | `RECINST` 的正确性（B5）、全层「无 header 解析」的可行性 |
| 2 | `magiskboot repack <原img> <out>` 如何继承 header/页大小 | 同上 | 为何脚本无需维护 header 偏移 |
| 3 | `magiskboot cpio` 的 `patch` 子指令具体打哪些补丁 | 同上 | config 中 `KEEPVERITY`/`KEEPFORCEENCRYPT`/`RECOVERYMODE`/`PATCHVBMETAFLAG` 的真实消费点 |
| 4 | `magiskboot cpio test` 返回 0/1 的判据 | 同上（脚本注释已给出 0=Stock / 1=Patched，`L232`/`L242`） | MODE 分类的可靠性 |
| 5 | `simg_dump -m rawimg` 对 sparse chunk（RAW/FILL/DONT_CARE/CRC32）的处理 | 二进制不在包内 | 空洞是否填零、是否保留对齐 |
| 6 | `HexTool.exe` 地址/长度/数据的进制与大小端 | 二进制不在包内；本文按同包内 `00069632`=0x11000 等调用**反推为十进制地址** | `PATCHFRP` 写偏移的正确性 |
| 7 | `vendor_boot` 的处理 | 全包零命中 | GKI 设备若 ramdisk 在 `vendor_boot` 则不支持 |
| 8 | `imgkit__magiskpatch__option_*` 由谁写入 | 参考包内**只有读取方**（`L106-L124`），无写入方 | 选项实际恒为默认值（`KEEPVERITY=true`/`LEGACYSAR=true`/`arch=arm_64` 等） |
| 9 | `imgkit__magiskpatch__ver*` 由谁消费 | 只有 `L1854` 赋值，无读取方 | 版本回传无效 |
| 10 | `7z` 的 `-slp` 开关作用 | 二进制不在包内；脚本无注释 | 仅影响解压性能/内存，不影响正确性 |
| 11 | `SPARSE2RAW`/`PATCHVBMETA`/`PATCHFRP`/`dl` 的真实调用者 | 参考包内零命中 | 判断为孤儿模块，但不排除包外脚本调用 |
| 12 | `avt` 相关：vbmeta `flags` 在镜像中的绝对偏移 120 | AOSP 规范属外部知识，脚本只用模式匹配 | 仅影响本文对「flag 位」的解释性描述 |
