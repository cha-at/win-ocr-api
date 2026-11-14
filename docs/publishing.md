# 发布到 GitHub（维护者备忘）

面向仓库维护者，不是用户文档。当前仓库已在本地初始化并提交，但没有远端。

## 首次推送

```powershell
cd path\to\win-ocr-api

git remote add origin https://github.com/cha-at/win-ocr-api.git
git branch -M main
git push -u origin main
```

推之前先确认没把构建产物带上去：

```powershell
git status --short          # 应该为空
git ls-files | Measure-Object -Line      # 只有源码、文档、脚本、图片
```

`bin/`、`obj/`、`dist/`、`out/`、`msix_stage/`、`*.pfx`、`*.cer`、`*.msix` 都在 `.gitignore` 里。

## 仓库设置建议

- **Description**：`Local HTTP API for the OCR engines built into Windows (Windows.Media.Ocr + Windows AI TextRecognizer)`
- **Topics**：`ocr`、`windows`、`dotnet`、`aspnet-core`、`windows-media-ocr`、`windows-ai`、`copilot-plus`、`npu`、`image-to-text`
- **License**：选 `GNU General Public License v3.0`（`LICENSE` 已是全文，GitHub 会自动识别）
- **Security**：开启 *Private vulnerability reporting*，`SECURITY.md` 里的上报渠道才有效
- **Features**：Issues、Discussions 按需开；Wiki 建议关掉，文档放 `docs/`

## 发版本

版本号在 `WinOcrApi.csproj` 的 `<Version>`，`/health` 会原样返回。发版步骤：

1. 改 `<Version>`，在 `CHANGELOG.md` 里加一节。
2. 提交并打标签：

   ```powershell
   git commit -am "chore(release): 0.1.1"
   git tag v0.1.1
   git push origin main --tags
   ```

3. GitHub 上 *Releases → Draft a new release*，选标签，正文直接从 `CHANGELOG.md` 抄。
4. 需要附件就本地打包再传：

   ```powershell
   .\publish.ps1 -Runtime win-x64 -OutDir dist-x64
   Compress-Archive dist-x64\* WinOcrApi-0.1.1-win-x64.zip
   .\publish.ps1 -Runtime win-arm64 -OutDir dist-arm64
   Compress-Archive dist-arm64\* WinOcrApi-0.1.1-win-arm64.zip
   ```

## 后续可加的东西

- GitHub Actions：`windows-latest` + `actions/setup-dotnet@v4`（`10.0.x`）跑 `dotnet build`、`dotnet test`、
  `dotnet format --verify-no-changes`。本次整理刻意没加，先保证本地可复现。
- 单元测试工程：目前纯函数（CJK 去空格、语言标签匹配、查询参数解析）还没有测试覆盖。
- `global.json` 已固定到 .NET 10 SDK（`rollForward: latestFeature`），换机器时注意。
