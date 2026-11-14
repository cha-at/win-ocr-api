namespace WinOcrApi;

/// <summary>一个可用的 OCR 语言。</summary>
public sealed record LanguageInfo(string Tag, string DisplayName, string NativeName);

/// <summary>识别出的单词（含像素级包围盒；AI 引擎额外给置信度）。</summary>
public sealed record WordResult(string Text, double X, double Y, double Width, double Height, double? Confidence = null);

/// <summary>识别出的一行（包围盒为所有单词的并集）。</summary>
public sealed record LineResult(string Text, double X, double Y, double Width, double Height, IReadOnlyList<WordResult>? Words);

/// <summary>一次识别的结果。</summary>
public sealed record OcrOutcome(
    string Text,
    string Engine,
    string LanguageTag,
    string LanguageName,
    int Width,
    int Height,
    double ElapsedMs,
    int LineCount,
    int WordCount,
    IReadOnlyList<LineResult>? Lines);

/// <summary>语言无法匹配 / 无法创建识别引擎。</summary>
public sealed class OcrLanguageException(string message) : Exception(message);

/// <summary>图片本身有问题（尺寸、格式、解码失败）。</summary>
public sealed class OcrImageException(string message) : Exception(message);

/// <summary>AI 引擎不可用（没包身份 / 没 NPU / 模型没装 / 用户禁用了）。</summary>
public sealed class AiEngineUnavailableException(string message) : Exception(message);
