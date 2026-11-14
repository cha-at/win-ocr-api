using Windows.ApplicationModel;

namespace WinOcrApi;

/// <summary>引擎名无法识别。</summary>
public sealed class OcrEngineSelectionException(string message) : Exception(message);

/// <summary>
/// 在传统 Windows.Media.Ocr 与 NPU 版 Windows AI TextRecognizer 之间做选择。
/// <c>engine=auto</c> 只在 AI 模型已经就绪时用它，避免一次请求就触发模型下载。
/// </summary>
public sealed class OcrRouter(OcrService windows, AiTextRecognizerEngine ai)
{
    public OcrService Windows { get; } = windows;
    public AiTextRecognizerEngine Ai { get; } = ai;

    public static readonly string[] KnownEngines = ["auto", "windows", "win", "ai"];

    public object DescribeEngines() => new object[]
    {
        new
        {
            id = "windows",
            name = "Windows.Media.Ocr（传统 OCR，CPU）",
            available = true,
            languages = Windows.Languages,
            maxImageDimension = Windows.MaxImageDimension,
        },
        new
        {
            id = "ai",
            name = "Windows AI TextRecognizer（NPU）",
            available = Ai.IsReady,
            state = Ai.ReadyState.ToString(),
            status = Ai.StatusMessage,
            packaged = HasPackageIdentity(out _),
            languages = Array.Empty<string>(),
        },
    };

    public async Task<OcrOutcome> RecognizeAsync(
        byte[] bytes,
        string? engine,
        string? language,
        double scale,
        int rotate,
        bool includeLines,
        bool includeWords,
        bool clean,
        CancellationToken ct)
    {
        var choice = (engine ?? "auto").Trim().ToLowerInvariant();

        switch (choice)
        {
            case "windows" or "win" or "legacy":
                return await Windows.RecognizeAsync(bytes, language, scale, rotate, includeLines, includeWords, clean, ct)
                    .ConfigureAwait(false);

            case "ai" or "npu":
                return await Ai.RecognizeAsync(bytes, scale, rotate, includeLines, includeWords, ct)
                    .ConfigureAwait(false);

            case "auto" or "":
                if (Ai.IsReady)
                {
                    try
                    {
                        return await Ai.RecognizeAsync(bytes, scale, rotate, includeLines, includeWords, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is AiEngineUnavailableException or OcrImageException)
                    {
                        // AI 掉链子就退回传统引擎，别让请求整个失败。
                        var fallback = await Windows.RecognizeAsync(bytes, language, scale, rotate, includeLines, includeWords, clean, ct)
                            .ConfigureAwait(false);
                        return fallback with { Engine = "windows (fallback)", LanguageName = $"{fallback.LanguageName} · AI 失败：{ex.Message}" };
                    }
                }

                return await Windows.RecognizeAsync(bytes, language, scale, rotate, includeLines, includeWords, clean, ct)
                    .ConfigureAwait(false);

            default:
                throw new OcrEngineSelectionException(
                    $"未知引擎「{engine}」，可选：auto / windows / ai。");
        }
    }

    /// <summary>只有带包身份的进程才能用 Windows AI API。</summary>
    public static bool HasPackageIdentity(out string error)
    {
        error = string.Empty;
        try
        {
            _ = Package.Current.Id.FullName;
            return true;
        }
        catch (Exception ex)
        {
            error = $"进程没有包身份，AI 引擎不可用（先运行 package\\register.ps1 注册 sparse package）：{ex.Message}";
            return false;
        }
    }
}
