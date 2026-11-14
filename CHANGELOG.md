# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-11-14

First public release.

### Added

- `POST /ocr` — recognize text from PNG/JPEG/BMP/TIFF/GIF/HEIC images sent as a raw body, as `multipart/form-data`
  with a `file` field, or fetched server-side via `?url=`.
- Two engines behind one endpoint: `windows` (`Windows.Media.Ocr`, CPU, works out of the box) and `ai`
  (`Microsoft.Windows.AI.Imaging.TextRecognizer`, NPU, requires package identity).
- Query options: `engine`, `lang`, `clean` (strip spaces between CJK characters), `words`, `lines`, `scale`,
  `rotate`, `format=text|json`.
- Line- and word-level bounding boxes; word `confidence` from the AI engine.
- `GET /health`, `GET /engines`, `GET /languages`, `GET /diag/ai`.
- Drag-and-drop web page with engine/language pickers, clipboard paste and JSON view.
- Optional shared-secret auth via `OCR_API_TOKEN` (`X-Api-Key` or `Authorization: Bearer`).
- `--selftest [image]` for a no-server smoke test of both engines.
- Sparse-package scripts (`package/register.ps1`, `package/launch.ps1`) and MSIX self-signing
  (`package/build-msix.ps1`) to give the process package identity for the AI engine.
- `publish.ps1` that auto-detects `win-arm64` / `win-x64`.
- English and Chinese documentation, GPL-3.0 license, contributing and security policies.

### Notes

- The AI engine works only when the service is installed as a signed package — `SignatureKind: Developer`,
  `systemAIModels: Allowed`. A bare `dist\WinOcrApi.exe` or an unsigned sparse registration is denied with
  `0x80070005`. Setup and diagnostics: [docs/ai-engine.md](docs/ai-engine.md).
- Classic-engine OCR inserts spaces between CJK characters unless `clean=true`.
- Requests for the same language are serialized because `OcrEngine` is not thread-safe.
- `auto` never triggers an AI model download; `--selftest` does when the model is missing.

[Unreleased]: https://github.com/cha-at/win-ocr-api/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/cha-at/win-ocr-api/releases/tag/v0.1.0
