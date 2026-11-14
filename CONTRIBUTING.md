# Contributing

Thanks for taking a look. Issues and pull requests are welcome, in English or Chinese — write in whichever you are
comfortable with.

## Before you start

- Read [README.md](README.md) and, if you touch the AI engine, [docs/ai-engine.md](docs/ai-engine.md).
- For anything larger than a typo or an obvious bug fix, open an issue first so we can agree on the approach.
- Check [CHANGELOG.md](CHANGELOG.md) to see where the project is.

## Environment

- Windows 10 build 19041+ (the project only builds and runs on Windows; the OCR engines are WinRT APIs).
- .NET 10 SDK (`dotnet --version` should report 10.x). `global.json` pins the SDK band.
- Optional for the AI engine: Windows App SDK 2.6.5 runtime, a Copilot+ PC, and `package/register.ps1` + `package/launch.ps1`.

```powershell
dotnet build -c Release            # must finish with 0 warnings, 0 errors
.\dist\WinOcrApi.exe --selftest sample.png   # smoke test both engines
```

## Making a change

1. Fork, branch from `main`, keep the diff focused.
2. Match the existing style: file-scoped namespaces, `Nullable` enabled, 4 spaces, comments in Chinese or English
   (the surrounding file decides), no new dependencies unless they earn their place.
3. Keep the API backwards compatible. New query parameters must have a default that preserves current behaviour.
4. Update the docs in the same PR: `README.md`, `README.zh-CN.md`, and `CHANGELOG.md` under `## [Unreleased]`.
5. Run the build and, when the change is behavioural, start the service and exercise the endpoint you touched:

   ```powershell
   .\dist\WinOcrApi.exe                  # then:
   curl.exe -s --data-binary "@sample.png" "http://127.0.0.1:8737/ocr?format=text"
   ```

6. Open the PR and fill in the template.

## Touching the AI engine

Attach the output of `GET /diag/ai` (with `packaged`, `signature`, `capability` and `apis`) and the exact Windows
build and Windows App SDK version from the machine that produced it. Without that, AI-engine changes cannot be
verified — the failure mode is machine- and package-dependent.

## Repository conventions

- `main` is the release branch; PRs are squash-merged.
- Commit messages: short imperative subject, optionally `feat:` / `fix:` / `docs:` / `chore:` prefixes.
- Do not commit build output (`bin/`, `obj/`, `dist/`, `out/`, `msix_stage/`), certificates (`*.pfx`, `*.cer`) or
  anything under `package/Images/` that you are not allowed to license.
- No CI is configured yet, so a green local build is the gate.

## License

By contributing you agree that your contribution is licensed under the
[GNU General Public License v3.0](LICENSE), the same license as the project. There is no CLA.
