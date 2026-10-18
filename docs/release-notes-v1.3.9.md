# PaperCoin v1.3.9

## 修复：Android 与 Windows 之间同步失败

1.3.8 里 Android 与 Android 能同步，但 Android 与 Windows 之间必失败：
Android 端提示“非法的日期”，Windows 端提示“协议版本不兼容”。

原因是 Android 的线缆 JSON 用了 `encodeDefaults = false`，而账单模型的构造参数都带默认值。
今天记的账，`date` 正好等于默认值 `LocalDate.now()`，这个键就整个不写进快照；
Windows 按 `yyyy-MM-dd` 校验，收到缺字段的快照直接判“非法的日期”。
Android 与 Android 之所以没事，是因为接收方缺 `date` 时也默认成今天，哈希刚好对得上。

- **Android**：线缆 JSON 改为写出全部非空字段（`encodeDefaults = true`），
  `category` 为 null 时仍然省略，与桌面端的 `WhenWritingNull` 对齐。
- **Windows**：对端快照内容不合法归类为 `invalid` 而不是 `protocol`，
  并把具体原因接到提示后面（“对方返回的数据不合法：非法的日期: …”），
  不再把所有失败都显示成“协议版本不兼容”。
- 协议文档写明：即使字段值等于本地默认值，也必须写上线缆。
- 新增回归测试：Android 线缆字段完整性、Windows 对缺字段对端的处理。

## 版本

- Android：**1.3.9 / versionCode 10309**。
- Windows：程序集版本升到 **1.3.9**；`ProductCode`/`PackageCode` 已换成新的，
  `UpgradeCode` 不变，可原地升级 1.3.8。本次未重新打 MSI，需要时按 README 的
  “清 → publish → 只打包”流程出包。

## 验证

- Android：`sync.*` + `SyncHash` 单元测试全绿；新增的 `SyncWireFormatTest`
  在修复前失败、修复后通过；Release APK 用同一把 release 密钥签名，覆盖安装数据保留。
- Windows：`tools/SyncTests` **15 / 15** 通过（含新增的第 15 项：
  字段缺失的对端快照必须返回 `invalid` 且带出“非法的日期”）。

## 升级说明

- Windows 端与 Android 端可以混用版本号：这次真正修复在 Android 的编码侧，
  只装新 APK，旧的 1.3.8 桌面版也能正常同步。
- Android 覆盖安装保留账单与同步码；Windows 的 MSI 若重新出包，升级会复用安装位置。

## 发布资产

| 文件 | 说明 |
|---|---|
| `PaperCoin-1.3.9-android.apk` | Android 1.3.9，约 6.62 MB |
