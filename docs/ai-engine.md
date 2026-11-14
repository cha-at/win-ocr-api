# The AI engine (NPU): how to enable it, and why it fails without a signed package

`Microsoft.Windows.AI.Imaging.TextRecognizer` is the OCR that ships with Copilot+ PCs. It is implemented in
[`AiTextRecognizerEngine.cs`](../AiTextRecognizerEngine.cs) and selectable with `?engine=ai`.

**Status:** ✅ working, as long as the service is installed as a **signed package**. An unsigned development package
is denied with `Capability requirements not met` (`0x80070005`), which is what an earlier version of this project
reported.

## Why bother

On a 960×420 mixed Chinese/English image the two engines differ in more than speed:

| Engine | 960×420 result | Output |
| --- | --- | --- |
| Classic | 38 ms, 5 lines, 60 words | `设备型号． CopiIot+ PC (Snapdragon XI E80100 ）` |
| AI (NPU) | 147 ms, 5 lines, 41 words | `设备型号:Copilot+ PC(Snapdragon X1E80100)` |

The classic engine splits CJK text into words and rejoins them with spaces (`clean=true` patches the text back up),
and it misreads some Latin characters. The NPU model needs no language hint and no post-processing.

## What it requires

1. A Copilot+ PC with a working NPU, Windows 11 24H2 or later, AI components installed.
2. Windows App SDK 2.6.5 runtime.
3. A process with **package identity** — a process started from an installed package, not a bare `.exe`.
4. A manifest declaring the restricted capability `systemAIModels`.
5. A package that is **signed and installed** (a dev certificate in *Local Machine / Trusted People* is enough).

Point 5 is the one that bites: `Add-AppxPackage -Register -ExternalLocation` (the sparse-package path used by
`package/register.ps1`) gives you identity but `Package.SignatureKind = None`, and the capability is denied at
runtime.

## Enabling it

```powershell
# 1. publish
.\publish.ps1

# 2. pack + self-sign (generates out\WinOcrApi.msix, out\WinOcrApi.cer, out\WinOcrApi.pfx)
.\package\build-msix.ps1

# 3. trust the dev certificate and install the package — elevated, once
Import-Certificate -FilePath .\out\WinOcrApi.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage .\out\WinOcrApi.msix

# 4. start it with package identity
Start-Process explorer.exe "shell:AppsFolder\$((Get-AppxPackage WinOcrApi).PackageFamilyName)!WinOcrApi"
```

`build-msix.ps1` prints the generated `.pfx` password; the certificate is a throwaway development certificate, do not
trust it outside your machine.

## Verifying

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

`ImageScaler` showing `NotReady` is normal — this project does not use it. `GET /health` then reports
`defaultEngine: "ai"`, and:

```powershell
curl.exe -s --data-binary "@sample.png" "http://127.0.0.1:8737/ocr?engine=ai&format=text"
```

returns text with correct CJK spacing.

## Why the unsigned path fails

Everything below was tried and produced the same `0x80070005 Access is denied`:

| Axis | Variants tested |
| --- | --- |
| Package layout | Sparse (`-ExternalLocation`, exe outside the package) and full package (exe inside) |
| Framework dependency | `Microsoft.WindowsAppRuntime.1.8` and `Microsoft.WindowsAppRuntime.2` (2.6.5) |
| Manifest details | `ProcessorArchitecture="arm64"`, `uap10:PackageIntegrity`, `unvirtualizedResources`, `EntryPoint="Windows.FullTrustApplication"` with `uap10:RuntimeBehavior="win32App"` |
| Device side | Surface Pro 11 (Snapdragon X Elite): NPU healthy, `HardwareCompatibility=1`, `MeetsAdditionalDriverRequirements=1`, AI models already cached by Click to Do |
| Developer mode | Enabled (`AllowDevelopmentWithoutDevLicense=1`) |

The only difference that mattered was step 5 above: once the package was self-signed and installed
(`SignatureKind = Developer`), `systemAIModels` flipped from `Denied` to `Allowed` and `GetReadyState` returned
`Ready`.

The diagnostic output that shows the difference:

| | Sparse registration | Signed MSIX install |
| --- | --- | --- |
| `packaged` | `true` | `true` |
| `signature` | `None` | `Developer` |
| `capability.access` | `Denied` | `Allowed` |
| `TextRecognizer.GetReadyState` | `0x80070005` | `Ready` |

## If it still fails

- `NotSupportedOnCurrentSystem` / `NotCompatibleWithSystemHardware` — not a Copilot+ PC, or the AI components are
  missing. Check Windows Update and the *Windows AI* features.
- `NotReady` on the first call — the model may need downloading. `--selftest` calls `EnsureReadyAsync` and triggers
  the download (minutes; the API engine never does this on a normal request).
- Still `0x80070005` with `signature: Developer` — the capability is gated on the Microsoft side for that build. The
  workable alternative is Windows ML or ONNX Runtime with a bundled model, replacing `AiTextRecognizerEngine` behind
  the same `engine=ai` switch.

If you have this working or failing on another machine, `GET /diag/ai` plus the Windows build is exactly the data
that helps. Please attach it to an issue.
