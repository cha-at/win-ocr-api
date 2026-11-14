using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http.Features;
using WinOcrApi;

const long MaxBodyBytes = 64L * 1024 * 1024; // 64 MB

var builder = WebApplication.CreateBuilder(args);

// 只监听本机回环地址；想暴露到局域网时用 --urls / ASPNETCORE_URLS 或环境变量 OCR_API_URLS 覆盖。
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    var envUrls = Environment.GetEnvironmentVariable("OCR_API_URLS");
    builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(envUrls) ? "http://127.0.0.1:8737" : envUrls);
}

// 只输出到控制台：默认的 EventLog provider 在没有权限写系统日志时会抛异常，
// 一旦进入错误路径就会把已处理的错误变成断连。
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
});

// 上传体全部留在内存里：不依赖 %TEMP%（受限账户/沙箱下写临时文件可能直接被拒），
// 也避免大图先落盘再读一次。上限统一由 MaxBodyBytes 兜住。
builder.Services.Configure<FormOptions>(options =>
{
    options.MemoryBufferThreshold = (int)MaxBodyBytes;
    options.ValueLengthLimit = (int)MaxBodyBytes;
    options.MultipartBodyLengthLimit = MaxBodyBytes;
    options.MultipartHeadersLengthLimit = 16384;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaxBodyBytes);

builder.Services.AddSingleton<OcrService>();
builder.Services.AddSingleton<AiTextRecognizerEngine>();
builder.Services.AddSingleton<OcrRouter>();
builder.Services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(60) });

var app = builder.Build();

// 启动即初始化引擎，顺带确认本机确实装了 OCR 语言包。
var ocr = app.Services.GetRequiredService<OcrService>();
var ai = app.Services.GetRequiredService<AiTextRecognizerEngine>();
var router = app.Services.GetRequiredService<OcrRouter>();
var http = app.Services.GetRequiredService<HttpClient>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/", () => Results.Redirect("/index.html"));
app.MapGet("/health", () => Results.Json(new
{
    ok = true,
    version = AppVersion(),
    engine = "Windows.Media.Ocr + Windows AI TextRecognizer",
    defaultEngine = ai.IsReady ? "ai" : "windows",
    defaultLanguage = ocr.DefaultLanguageTag,
    maxImageDimension = ocr.MaxImageDimension,
    languages = ocr.Languages,
    ai = new { ready = ai.IsReady, state = ai.ReadyState.ToString(), status = ai.StatusMessage },
}));
app.MapGet("/engines", () => Results.Json(new { ok = true, engines = router.DescribeEngines() }));
app.MapGet("/diag/ai", () => Results.Json(new
{
    ok = true,
    packaged = OcrRouter.HasPackageIdentity(out var identityError),
    identityError,
    package = SafePackageId(),
    signature = SafeSignatureKind(),
    capability = SafeCapabilityCheck("systemAIModels"),
    apis = AiTextRecognizerEngine.DiagnoseApis(),
}));
app.MapGet("/languages", () => Results.Json(new
{
    ok = true,
    @default = ocr.DefaultLanguageTag,
    languages = ocr.Languages,
}));

app.MapMethods("/ocr", ["POST", "GET"], HandleOcr);

ai.Probe();

Console.WriteLine($"Windows OCR API · 传统引擎 Windows.Media.Ocr · 单图尺寸上限 {ocr.MaxImageDimension}px");
Console.WriteLine($"AI 引擎 TextRecognizer：{ai.StatusMessage}（包身份：{(OcrRouter.HasPackageIdentity(out _) ? "有" : "无")}）");
Console.WriteLine($"默认语言：{ocr.DefaultLanguageTag ?? "(无)"}");
foreach (var language in ocr.Languages)
{
    Console.WriteLine($"  · {language.Tag,-14} {language.DisplayName} / {language.NativeName}");
}

// 自带自检：WinOcrApi.exe --selftest [图片路径]
if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
{
    await SelfTestAsync(ocr, ai, router, args);
    return;
}

app.Run();

async Task<IResult> HandleOcr(HttpRequest request, OcrRouter service, HttpClient client, CancellationToken ct)
{
    if (!IsAuthorized(request, out var authError))
    {
        return Error(StatusCodes.Status401Unauthorized, authError);
    }

    var query = request.Query;
    var engine = query["engine"].FirstOrDefault() ?? "auto";
    var lang = query["lang"].FirstOrDefault() ?? query["language"].FirstOrDefault();
    var includeLines = QueryBool(query, "lines", true);
    var includeWords = QueryBool(query, "words", true);
    var clean = QueryBool(query, "clean", false);
    var scale = Math.Clamp(QueryDouble(query, "scale", 1.0), 0.05, 4.0);
    var rotate = NormalizeRotate(QueryInt(query, "rotate", 0));
    var format = (query["format"].FirstOrDefault() ?? "json").Trim().ToLowerInvariant();

    var (bytes, readError) = await ReadImageAsync(request, query, client, ct);
    if (readError is not null)
    {
        return readError;
    }

    var sw = Stopwatch.StartNew();
    try
    {
        var outcome = await service.RecognizeAsync(bytes!, engine, lang, scale, rotate, includeLines, includeWords, clean, ct);
        sw.Stop();

        if (format is "text" or "plain")
        {
            return Results.Text(outcome.Text, "text/plain; charset=utf-8");
        }

        return Results.Json(new
        {
            ok = true,
            text = outcome.Text,
            engine = outcome.Engine,
            language = outcome.LanguageTag,
            languageName = outcome.LanguageName,
            width = outcome.Width,
            height = outcome.Height,
            lineCount = outcome.LineCount,
            wordCount = outcome.WordCount,
            elapsedMs = outcome.ElapsedMs,
            totalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            bytes = bytes!.Length,
            lines = outcome.Lines,
        });
    }
    catch (OcrEngineSelectionException ex)
    {
        return Error(StatusCodes.Status400BadRequest, ex.Message);
    }
    catch (AiEngineUnavailableException ex)
    {
        return Error(StatusCodes.Status503ServiceUnavailable, ex.Message);
    }
    catch (OcrLanguageException ex)
    {
        return Error(StatusCodes.Status400BadRequest, ex.Message);
    }
    catch (OcrImageException ex)
    {
        return Error(StatusCodes.Status422UnprocessableEntity, ex.Message);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        return Error(499, "客户端已断开。");
    }
    catch (Exception ex)
    {
        return Error(StatusCodes.Status500InternalServerError, $"识别失败：{ex.Message}");
    }
}

/// <summary>
/// 取出图片字节，支持三种来源：?url= 远程下载、multipart 的 file 字段、原始请求体。
/// 客户端把图片当 multipart 发却塞了裸字节时，会回退到「按原始图片处理」。
/// </summary>
static async Task<(byte[]? Bytes, IResult? Error)> ReadImageAsync(
    HttpRequest request, IQueryCollection query, HttpClient client, CancellationToken ct)
{
    try
    {
        var url = query["url"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(url))
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return (null, Error(StatusCodes.Status502BadGateway, $"下载图片失败：HTTP {(int)response.StatusCode}"));
            }

            var downloaded = await response.Content.ReadAsByteArrayAsync(ct);
            if (downloaded.Length == 0)
            {
                return (null, Error(StatusCodes.Status400BadRequest, "下载到的内容为空。"));
            }

            if (downloaded.Length > MaxBodyBytes)
            {
                return (null, Error(StatusCodes.Status413PayloadTooLarge, $"图片超过 {MaxBodyBytes / 1024 / 1024} MB 上限。"));
            }

            return (downloaded, null);
        }

        // 允许表单解析失败后回退读取原始请求体；阈值取上限本身，保证只缓冲到内存、从不落盘。
        request.EnableBuffering((int)MaxBodyBytes, MaxBodyBytes);

        if (request.HasFormContentType)
        {
            IFormCollection? form = null;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
            {
                // 不是合法的 multipart，按原始图片处理。
            }

            var file = form?.Files.FirstOrDefault();
            if (file is { Length: > 0 })
            {
                if (file.Length > MaxBodyBytes)
                {
                    return (null, Error(StatusCodes.Status413PayloadTooLarge, $"图片超过 {MaxBodyBytes / 1024 / 1024} MB 上限。"));
                }

                using var buffer = new MemoryStream((int)Math.Min(file.Length, MaxBodyBytes));
                await file.CopyToAsync(buffer, ct);
                return (buffer.ToArray(), null);
            }

            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }

        return await ReadRawBodyAsync(request, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        return (null, Error(StatusCodes.Status400BadRequest, $"读取图片失败：{ex.Message}"));
    }
}

static async Task<(byte[]? Bytes, IResult? Error)> ReadRawBodyAsync(HttpRequest request, CancellationToken ct)
{
    if (request.ContentLength == 0)
    {
        return (null, Error(StatusCodes.Status400BadRequest,
            "请求体为空。请把图片字节直接 POST 到 /ocr，或用 multipart 的 file 字段 / ?url= 参数。"));
    }

    if (request.ContentLength > MaxBodyBytes)
    {
        return (null, Error(StatusCodes.Status413PayloadTooLarge, $"图片超过 {MaxBodyBytes / 1024 / 1024} MB 上限。"));
    }

    using var buffer = new MemoryStream();
    var chunk = new byte[81920];
    int read;
    while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
    {
        buffer.Write(chunk, 0, read);
        if (buffer.Length > MaxBodyBytes)
        {
            return (null, Error(StatusCodes.Status413PayloadTooLarge, $"图片超过 {MaxBodyBytes / 1024 / 1024} MB 上限。"));
        }
    }

    if (buffer.Length == 0)
    {
        return (null, Error(StatusCodes.Status400BadRequest,
            "请求体为空。请把图片字节直接 POST 到 /ocr，或用 multipart 的 file 字段 / ?url= 参数。"));
    }

    return (buffer.ToArray(), null);
}

static IResult Error(int status, string message) =>
    Results.Json(new { ok = false, error = message }, statusCode: status);

static bool IsAuthorized(HttpRequest request, out string error)
{
    error = string.Empty;
    var expected = Environment.GetEnvironmentVariable("OCR_API_TOKEN");
    if (string.IsNullOrWhiteSpace(expected))
    {
        return true;
    }

    var provided = request.Headers["X-Api-Key"].FirstOrDefault();
    if (string.IsNullOrEmpty(provided))
    {
        var authorization = request.Headers.Authorization.FirstOrDefault();
        if (authorization is not null &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            provided = authorization["Bearer ".Length..].Trim();
        }
    }

    if (string.Equals(provided, expected, StringComparison.Ordinal))
    {
        return true;
    }

    error = "缺少或错误的 API Token（请求头 X-Api-Key 或 Authorization: Bearer <token>）。";
    return false;
}

/// <summary>程序版本，来自 csproj 的 &lt;Version&gt;（去掉 SourceLink 附加的 +hash）。</summary>
static string AppVersion()
{
    var informational = typeof(OcrService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (string.IsNullOrWhiteSpace(informational))
    {
        return "0.0.0";
    }

    var plus = informational.IndexOf('+');
    return plus >= 0 ? informational[..plus] : informational;
}

static bool QueryBool(IQueryCollection query, string key, bool fallback)
{
    var raw = query[key].FirstOrDefault();
    return string.IsNullOrWhiteSpace(raw)
        ? fallback
        : raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
}

static double QueryDouble(IQueryCollection query, string key, double fallback) =>
    double.TryParse(query[key].FirstOrDefault(), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value)
        ? value
        : fallback;

static int QueryInt(IQueryCollection query, string key, int fallback) =>
    int.TryParse(query[key].FirstOrDefault(), out var value) ? value : fallback;

static int NormalizeRotate(int rotate)
{
    var normalized = ((rotate % 360) + 360) % 360;
    return normalized is 90 or 180 or 270 ? normalized : 0;
}

static string SafePackageId()
{
    try
    {
        return Windows.ApplicationModel.Package.Current.Id.FullName;
    }
    catch
    {
        return "(无包身份)";
    }
}

static string SafeSignatureKind()
{
    try
    {
        return Windows.ApplicationModel.Package.Current.SignatureKind.ToString();
    }
    catch (Exception ex)
    {
        return ex.Message;
    }
}

static object SafeCapabilityCheck(string capability)
{
    try
    {
        var cap = Windows.Security.Authorization.AppCapabilityAccess.AppCapability.Create(capability);
        return new { name = capability, access = cap.CheckAccess().ToString() };
    }
    catch (Exception ex)
    {
        return new { name = capability, access = $"error: {ex.Message}" };
    }
}

async Task SelfTestAsync(OcrService windows, AiTextRecognizerEngine ai, OcrRouter router, string[] argv)
{
    Console.WriteLine("\n===== 自检 =====");

    var hasIdentity = OcrRouter.HasPackageIdentity(out var identityError);
    Console.WriteLine($"包身份   : {(hasIdentity ? "有" : "无")}{(hasIdentity ? "" : " · " + identityError)}");

    ai.Probe();
    Console.WriteLine($"AI 状态  : {ai.ReadyState} · {ai.StatusMessage}");

    var idx = Array.FindIndex(argv, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
    var imagePath = idx >= 0 && idx + 1 < argv.Length && !argv[idx + 1].StartsWith("--")
        ? argv[idx + 1]
        : "sample.png";
    if (!File.Exists(imagePath))
    {
        Console.WriteLine($"找不到测试图：{Path.GetFullPath(imagePath)}");
        return;
    }

    var bytes = await File.ReadAllBytesAsync(imagePath);

    if (hasIdentity && !ai.IsReady)
    {
        Console.WriteLine("AI 模型未就绪，尝试 EnsureReadyAsync（首次会下载模型，可能需要几分钟）…");
        using var ensureCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            await ai.EnsureReadyAsync(ensureCts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EnsureReady 异常：{ex.Message}");
        }

        Console.WriteLine($"AI 状态  : {ai.ReadyState} · {ai.StatusMessage}");
    }

    foreach (var (name, fn) in new (string, Func<Task<OcrOutcome>>)[]
    {
        ("windows", () => windows.RecognizeAsync(bytes, null, 1.0, 0, true, true, true, CancellationToken.None)),
        ("ai", () => ai.RecognizeAsync(bytes, 1.0, 0, true, true, CancellationToken.None)),
    })
    {
        Console.WriteLine($"\n[{name}] -----");
        try
        {
            var outcome = await fn();
            Console.WriteLine($"{outcome.Width}×{outcome.Height} · {outcome.LineCount} 行 / {outcome.WordCount} 词 · {outcome.ElapsedMs} ms");
            var preview = outcome.Text.Length > 400 ? outcome.Text[..400] + "…" : outcome.Text;
            Console.WriteLine(preview);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"失败：{ex.GetType().Name}: {ex.Message}");
        }
    }
}
