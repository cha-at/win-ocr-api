using System.Diagnostics;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Imaging;

namespace WinOcrApi;

/// <summary>
/// 用 Windows App SDK 的 Windows AI API（Microsoft.Windows.AI.Imaging.TextRecognizer）跑 NPU OCR。
/// 需要 MSIX / sparse package 包身份 + systemAIModels 能力声明，见 package/AppxManifest.xml。
/// </summary>
public sealed class AiTextRecognizerEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TextRecognizer? _recognizer;

    public AIFeatureReadyState ReadyState { get; private set; } = AIFeatureReadyState.NotReady;
    public bool IsReady => ReadyState == AIFeatureReadyState.Ready;
    public string StatusMessage { get; private set; } = "尚未检查";

    /// <summary>检查包身份与模型状态，不触发下载。</summary>
    public void Probe()
    {
        if (!OcrRouter.HasPackageIdentity(out var identityError))
        {
            StatusMessage = identityError;
            return;
        }

        try
        {
            ReadyState = TextRecognizer.GetReadyState();
            StatusMessage = DescribeState(ReadyState);
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null)
            {
                root = root.InnerException;
            }

            StatusMessage = $"GetReadyState 失败：0x{root.HResult:X8} {root.Message.Replace("\r", " ").Replace("\n", " ").Trim()}";
        }
    }

    /// <summary>确保模型可用（必要时下载），返回是否成功。</summary>
    public async Task<bool> EnsureReadyAsync(CancellationToken ct)
    {
        Probe();
        if (IsReady)
        {
            return true;
        }

        if (ReadyState is AIFeatureReadyState.NotSupportedOnCurrentSystem
            or AIFeatureReadyState.NotCompatibleWithSystemHardware
            or AIFeatureReadyState.OSUpdateNeeded)
        {
            return false;
        }

        AIFeatureReadyResult result;
        try
        {
            result = await TextRecognizer.EnsureReadyAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var root = ex;
            while (root.InnerException is not null)
            {
                root = root.InnerException;
            }

            StatusMessage = $"EnsureReadyAsync 失败：0x{root.HResult:X8} {root.Message.Replace("\r", " ").Replace("\n", " ").Trim()}";
            throw new AiEngineUnavailableException($"AI 引擎不可用：{StatusMessage}");
        }

        Probe(); // 重新读状态（内部已容错，不会再抛）
        if (result.Status != AIFeatureReadyResultState.Success)
        {
            StatusMessage = $"EnsureReady 失败：{result.Status} {result.ErrorDisplayText} {result.ExtendedError?.Message}".Trim();
        }

        return result.Status == AIFeatureReadyResultState.Success && IsReady;
    }

    public async Task<OcrOutcome> RecognizeAsync(
        byte[] imageBytes,
        double scale,
        int rotate,
        bool includeLines,
        bool includeWords,
        CancellationToken ct)
    {
        if (!OcrRouter.HasPackageIdentity(out var identityError))
        {
            throw new AiEngineUnavailableException(identityError);
        }

        var recognizer = await GetRecognizerAsync(ct).ConfigureAwait(false);
        using var bitmap = await ImageDecoder.DecodeAsync(imageBytes, scale, rotate, 0, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var buffer = ImageBuffer.CreateForSoftwareBitmap(bitmap);
            var sw = Stopwatch.StartNew();
            RecognizedText recognized;
            try
            {
                recognized = await recognizer.RecognizeTextFromImageAsync(buffer).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new OcrImageException($"AI 文本识别失败：{ex.Message}");
            }
            sw.Stop();

            var lines = new List<LineResult>(recognized.Lines.Length);
            var texts = new List<string>(recognized.Lines.Length);
            var wordCount = 0;

            foreach (var line in recognized.Lines)
            {
                var words = new List<WordResult>(line.Words.Length);
                foreach (var word in line.Words)
                {
                    var box = word.BoundingBox;
                    var (x, y, w, h) = Normalize(box);
                    words.Add(new WordResult(word.Text, x, y, w, h, Math.Round(word.MatchConfidence, 4)));
                }

                wordCount += line.Words.Length;
                var (lx, ly, lw, lh) = Normalize(line.BoundingBox);
                texts.Add(line.Text ?? string.Empty);
                lines.Add(new LineResult(line.Text ?? string.Empty, lx, ly, lw, lh, includeWords ? words : null));
            }

            return new OcrOutcome(
                string.Join('\n', texts),
                "ai",
                "ai",
                "Windows AI TextRecognizer (NPU)",
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                lines.Count,
                wordCount,
                includeLines ? lines : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TextRecognizer> GetRecognizerAsync(CancellationToken ct)
    {
        if (_recognizer is not null)
        {
            return _recognizer;
        }

        if (!await EnsureReadyAsync(ct).ConfigureAwait(false))
        {
            throw new AiEngineUnavailableException($"AI 引擎不可用：{StatusMessage}");
        }

        _recognizer = await TextRecognizer.CreateAsync().AsTask(ct).ConfigureAwait(false);
        return _recognizer;
    }

    /// <summary>四边形包围盒 → 轴对齐矩形（左上角 + 宽高）。</summary>
    private static (double X, double Y, double Width, double Height) Normalize(RecognizedTextBoundingBox box)
    {
        var xs = new[] { box.TopLeft.X, box.TopRight.X, box.BottomLeft.X, box.BottomRight.X };
        var ys = new[] { box.TopLeft.Y, box.TopRight.Y, box.BottomLeft.Y, box.BottomRight.Y };
        var minX = xs.Min();
        var minY = ys.Min();
        return (Math.Round(minX, 2), Math.Round(minY, 2),
                Math.Round(xs.Max() - minX, 2), Math.Round(ys.Max() - minY, 2));
    }

    private static string DescribeState(AIFeatureReadyState state) => state switch
    {
        AIFeatureReadyState.Ready => "就绪",
        AIFeatureReadyState.NotReady => "模型未就绪（调用 EnsureReadyAsync 会下载）",
        AIFeatureReadyState.NotSupportedOnCurrentSystem => "系统不支持 Windows AI（需要 Copilot+ PC / 24H2+）",
        AIFeatureReadyState.NotCompatibleWithSystemHardware => "硬件不满足（需要 NPU）",
        AIFeatureReadyState.DisabledByUser => "用户或策略禁用了 Windows AI",
        AIFeatureReadyState.CapabilityMissing => "缺少 systemAIModels 能力声明（包清单里没写）",
        AIFeatureReadyState.OSUpdateNeeded => "需要更新 Windows",
        _ => state.ToString(),
    };

    /// <summary>逐个探测 Windows AI API，用来定位「到底哪一层被挡了」。</summary>
    public static IReadOnlyList<object> DiagnoseApis()
    {
        var results = new List<object>();

        void TryOne(string api, Func<object> probe)
        {
            try
            {
                results.Add(new { api, ok = true, state = probe()?.ToString(), error = (string?)null, hresult = (string?)null });
            }
            catch (Exception ex)
            {
                // 有时真正的原因藏在 InnerException 里
                var root = ex;
                while (root.InnerException is not null)
                {
                    root = root.InnerException;
                }

                results.Add(new
                {
                    api,
                    ok = false,
                    state = (string?)null,
                    error = root.Message.Replace("\r", " ").Replace("\n", " ").Trim(),
                    hresult = $"0x{root.HResult:X8}",
                });
            }
        }

        TryOne("TextRecognizer.GetReadyState", () => TextRecognizer.GetReadyState());
        TryOne("ImageScaler.GetReadyState", () => ImageScaler.GetReadyState());
        TryOne("LanguageModel.GetReadyState", () => Microsoft.Windows.AI.Text.LanguageModel.GetReadyState());
        return results;
    }
}
