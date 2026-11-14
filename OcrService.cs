using System.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WinOcrApi;

/// <summary>
/// 对 Windows 内置 OCR（Windows.Media.Ocr）的薄封装。
/// OcrEngine 不计成本可复用，但并非线程安全，因此每个语言配一把锁。
/// </summary>
public sealed class OcrService
{
    private sealed class EngineSlot(OcrEngine engine, string tag, string name)
    {
        public OcrEngine Engine { get; } = engine;
        public string Tag { get; } = tag;
        public string Name { get; } = name;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly Dictionary<string, EngineSlot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private EngineSlot? _defaultSlot;

    public OcrService()
    {
        foreach (var language in OcrEngine.AvailableRecognizerLanguages)
        {
            // 只有能真正建起引擎的语言才登记，避免列表里出现「看起来有但其实不能用」的项。
            if (OcrEngine.TryCreateFromLanguage(language) is { } engine)
            {
                _slots[language.LanguageTag] = new EngineSlot(engine, language.LanguageTag, language.DisplayName);
            }
        }

        // 默认引擎：跟随系统显示语言，失败则退到第一个可用语言。
        if (OcrEngine.TryCreateFromUserProfileLanguages() is { } userEngine)
        {
            var tag = _slots.Values.FirstOrDefault(s => ReferenceEquals(s.Engine, userEngine))?.Tag
                      ?? userEngine.RecognizerLanguage.LanguageTag;
            _defaultSlot = new EngineSlot(userEngine, tag, userEngine.RecognizerLanguage.DisplayName);
            _slots.TryAdd(tag, _defaultSlot);
        }

        _defaultSlot ??= _slots.Values.OrderBy(s => s.Tag, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>本机可用的 OCR 语言（按标签排序）。</summary>
    public IReadOnlyList<LanguageInfo> Languages =>
        _slots.Values
            .OrderBy(s => s.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(s => new LanguageInfo(s.Tag, s.Name, s.Engine.RecognizerLanguage.NativeName))
            .ToArray();

    public string? DefaultLanguageTag => _defaultSlot?.Tag;

    public int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    /// <summary>
    /// 识别图片。<paramref name="language"/> 可为空（跟随系统），否则按标签匹配，
    /// 支持 "zh"、"zh-Hans"、"zh-CN" 这类前缀简写。返回 null 表示没有可用引擎。
    /// </summary>
    public async Task<OcrOutcome> RecognizeAsync(
        byte[] imageBytes,
        string? language,
        double scale,
        int rotate,
        bool includeLines,
        bool includeWords,
        bool clean,
        CancellationToken ct)
    {
        var slot = Resolve(language) ?? throw new OcrLanguageException(
            "本机没有可用的 OCR 引擎。请在「设置 → 时间和语言 → 语言和区域」中为要识别的语言安装「可选功能 → 光学字符识别」。");

        if (imageBytes.Length == 0)
        {
            throw new OcrImageException("请求体为空，请把图片字节作为请求体发送。");
        }

        using var bitmap = await ImageDecoder.DecodeAsync(imageBytes, scale, rotate, (int)OcrEngine.MaxImageDimension, ct).ConfigureAwait(false);

        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();
            OcrResult result;
            try
            {
                result = await slot.Engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new OcrImageException($"Windows OCR 识别失败：{ex.Message}");
            }
            sw.Stop();

            var lines = new List<LineResult>(result.Lines.Count);
            var text = new List<string>(result.Lines.Count);
            var wordCount = 0;

            foreach (var line in result.Lines)
            {
                var words = new List<WordResult>(line.Words.Count);
                double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;

                foreach (var word in line.Words)
                {
                    var r = word.BoundingRect;
                    words.Add(new WordResult(word.Text, Crop(r.X), Crop(r.Y), Crop(r.Width), Crop(r.Height)));
                    minX = Math.Min(minX, r.X);
                    minY = Math.Min(minY, r.Y);
                    maxX = Math.Max(maxX, r.X + r.Width);
                    maxY = Math.Max(maxY, r.Y + r.Height);
                }

                wordCount += line.Words.Count;
                if (line.Words.Count == 0)
                {
                    minX = minY = maxX = maxY = 0;
                }

                var lineText = line.Text ?? string.Empty;
                if (clean)
                {
                    lineText = RemoveCjkSpaces(lineText);
                }

                text.Add(lineText);
                lines.Add(new LineResult(
                    lineText,
                    Crop(minX), Crop(minY), Crop(maxX - minX), Crop(maxY - minY),
                    includeWords ? words : null));
            }

            return new OcrOutcome(
                string.Join('\n', text),
                "windows",
                slot.Tag,
                slot.Name,
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                lines.Count,
                wordCount,
                includeLines ? lines : null);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private EngineSlot? Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return _defaultSlot;
        }

        var wanted = language.Trim();

        // 1) 精确标签：zh-Hans-CN / en-US
        if (_slots.TryGetValue(wanted, out var exact))
        {
            return exact;
        }

        // 2) 忽略大小写与下划线的精确匹配：zh_hans_cn
        var normalized = wanted.Replace('_', '-');
        foreach (var candidate in _slots.Values)
        {
            if (candidate.Tag.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        // 3) 前缀匹配：zh → zh-Hans-CN；zh-Hans → zh-Hans-CN
        var byPrefix = _slots.Values
            .Where(s => s.Tag.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Tag.Length)
            .FirstOrDefault();
        if (byPrefix is not null)
        {
            return byPrefix;
        }

        // 4) 语言标签前缀匹配：en 匹配 en-GB / en-US 的父语言
        var wantedLanguage = normalized.Split('-')[0];
        var byLanguage = _slots.Values
            .Where(s => s.Tag.Split('-')[0].Equals(wantedLanguage, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Tag.Equals(_defaultSlot?.Tag, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Tag, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (byLanguage is not null)
        {
            return byLanguage;
        }

        // 5) 再试一次显示名/本地名：方便直接传「中文(简体)」这类名字
        var byName = _slots.Values.FirstOrDefault(s =>
            string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Engine.RecognizerLanguage.NativeName, wanted, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        var supported = string.Join(", ", _slots.Keys.Order(StringComparer.OrdinalIgnoreCase));
        throw new OcrLanguageException($"不支持的语言「{language}」。本机已安装：{supported}。");
    }

    private static double Crop(double value) => Math.Round(value, 2);

    /// <summary>
    /// Windows OCR 会把中文按「词」拆开并用空格连接，这里去掉两个 CJK 字符之间的空格，
    /// 同时保留拉丁文/数字之间的空格。
    /// </summary>
    internal static string RemoveCjkSpaces(string input)
    {
        if (input.Length == 0)
        {
            return input;
        }

        var builder = new System.Text.StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c is ' ' or '\u00A0' or '\t' && builder.Length > 0 && i + 1 < input.Length)
            {
                if (IsCjk(builder[^1]) && IsCjk(input[i + 1]))
                {
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static bool IsCjk(char c) =>
        c is >= '\u2E80' and <= '\u9FFF'   // 部首、假名、CJK 统一表意文字
            or >= '\uF900' and <= '\uFAFF'  // 兼容表意文字
            or >= '\u3000' and <= '\u303F'  // CJK 标点
            or >= '\uFF00' and <= '\uFFEF'; // 全角形式
}
