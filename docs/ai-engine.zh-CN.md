# AI 引擎（NPU）：怎么启用，以及为什么未签名的包不行

Copilot+ PC 上的 NPU 版 OCR（`Microsoft.Windows.AI.Imaging.TextRecognizer`）实现在
[`AiTextRecognizerEngine.cs`](../AiTextRecognizerEngine.cs)，可以用 `?engine=ai` 指定。

**状态**：✅ 可用——前提是服务以**签名包**安装。未签名的开发包会被拒，报
`Capability requirements not met`（`0x80070005 拒绝访问`），这也是本项目早期文档里的结论。

## 为什么值得开

同一张 960×420 中英混排图，两个引擎差的不是速度：

| 引擎 | 960×420 结果 | 输出 |
| --- | --- | --- |
| 传统 | 38 ms，5 行 60 词 | `设备型号． CopiIot+ PC (Snapdragon XI E80100 ）` |
| AI（NPU） | 147 ms，5 行 41 词 | `设备型号:Copilot+ PC(Snapdragon X1E80100)` |

传统引擎会把中文按词切开再用空格拼回来（`clean=true` 只是把文本补回去），还会认错个别拉丁字母。NPU 模型
不用挑语言，也不用后处理。

## 这个 API 要求什么

1. Copilot+ PC，NPU 正常，Windows 11 24H2 及以上，AI 组件已安装。
2. Windows App SDK 2.6.5 运行时。
3. 进程有**包身份**：从已安装的包启动，而不是裸 `.exe`。
4. 包清单声明受限能力 `systemAIModels`。
5. 包**已签名并安装**（开发证书装进「本地计算机 / 受信任人」就够）。

卡人的是第 5 条：`Add-AppxPackage -Register -ExternalLocation`（也就是 `package/register.ps1` 走的
sparse package 路线）能拿到包身份，但 `Package.SignatureKind = None`，运行时能力照样被拒。

## 启用步骤

```powershell
# 1. 发布
.\publish.ps1

# 2. 打包 + 自签（生成 out\WinOcrApi.msix、out\WinOcrApi.cer、out\WinOcrApi.pfx）
.\package\build-msix.ps1

# 3. 信任开发证书并安装包 —— 管理员 PowerShell，只需一次
Import-Certificate -FilePath .\out\WinOcrApi.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\out\WinOcrApi.msix

# 4. 带包身份启动
Start-Process explorer.exe "shell:AppsFolder\$((Get-AppxPackage WinOcrApi).PackageFamilyName)!WinOcrApi"
```

`build-msix.ps1` 会打印本次生成的 `.pfx` 口令；那是随手用的开发证书，别信任到自己机器以外的地方。

## 验证

```
GET /diag/ai
{
  "ok": true,
  "packaged": true,
  "package": "WinOcrApi_1.0.0.0_neutral__37y8300kxgzqa",
  "signature": "Developer",
  "capability": { "name": "systemAIModels", "access": "Allowed" },
  "apis": [
    { "api": "TextRecognizer.GetReadyState", "ok": true, "state": "Ready" },
    { "api": "ImageScaler.GetReadyState", "ok": true, "state": "NotReady" },
    { "api": "LanguageModel.GetReadyState", "ok": true, "state": "Ready" }
  ]
}
```

`ImageScaler` 显示 `NotReady` 是正常的，本项目不用它。此时 `GET /health` 的 `defaultEngine` 是 `"ai"`，而且：

```powershell
curl.exe -s --data-binary "@sample.png" "http://127.0.0.1:8737/ocr?engine=ai&format=text"
```

返回的文字中文之间没有多余空格。

## 未签名路线为什么不行

下面这些全都试过，全是同一个 `0x80070005 拒绝访问`：

| 维度 | 试过的变体 |
| --- | --- |
| 包布局 | sparse（`-ExternalLocation`，exe 在包外）和完整包（exe 在包内） |
| 框架依赖 | `Microsoft.WindowsAppRuntime.1.8` 和 `Microsoft.WindowsAppRuntime.2`（2.6.5） |
| 清单细节 | `ProcessorArchitecture="arm64"`、`uap10:PackageIntegrity`、`unvirtualizedResources`、`EntryPoint="Windows.FullTrustApplication"` 配 `uap10:RuntimeBehavior="win32App"` |
| 设备侧 | Surface Pro 11（Snapdragon X Elite）：NPU 正常，`HardwareCompatibility=1`、`MeetsAdditionalDriverRequirements=1`，Click to Do 已缓存 AI 模型 |
| 开发模式 | 已开（`AllowDevelopmentWithoutDevLicense=1`） |

真正起作用的只有上面第 5 条：自签并安装后（`SignatureKind = Developer`），`systemAIModels` 从 `Denied` 变成
`Allowed`，`GetReadyState` 返回 `Ready`。

两条路线的诊断差异：

| | sparse 注册 | 签名 MSIX 安装 |
| --- | --- | --- |
| `packaged` | `true` | `true` |
| `signature` | `None` | `Developer` |
| `capability.access` | `Denied` | `Allowed` |
| `TextRecognizer.GetReadyState` | `0x80070005` | `Ready` |

## 还是不行的话

- `NotSupportedOnCurrentSystem` / `NotCompatibleWithSystemHardware`：不是 Copilot+ PC，或缺 AI 组件。检查 Windows
  Update 与「Windows AI」功能。
- 第一次调用是 `NotReady`：模型可能要下载。`--selftest` 会调 `EnsureReadyAsync` 触发下载（几分钟）；正常请求不会。
- `signature: Developer` 了还是 `0x80070005`：这条路由系统侧对该版本把关。可行的替代是 Windows ML 或 ONNX
  Runtime 自带模型，在同一个 `engine=ai` 开关后面替换掉 `AiTextRecognizerEngine`。

如果你在别的机器上跑通或跑不通，`GET /diag/ai` 加 Windows 版本就是最有用的信息，麻烦贴到 issue 里。
