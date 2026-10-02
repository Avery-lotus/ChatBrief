using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;
using Windows.Storage;

namespace WeChatSummary.Desktop.Services;

public sealed class LocalOcrService
{
    private readonly OcrEngine? _engine = CreateEngine();

    public bool IsAvailable => _engine is not null;

    public string Status => _engine is null ? "Windows OCR 不可用或未安装语言包。" : $"Windows OCR 已启用：{_engine.RecognizerLanguage.DisplayName}";

    public async Task<string> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        if (_engine is null || !File.Exists(imagePath))
        {
            return "";
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath).AsTask(cancellationToken);
            await using var stream = await file.OpenStreamForReadAsync();
            var randomAccessStream = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
            var result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            return string.Join("\n", result.Lines.Select(line => line.Text)).Trim();
        }
        catch
        {
            return "";
        }
    }

    private static OcrEngine? CreateEngine()
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is not null)
        {
            return engine;
        }

        foreach (var languageTag in new[] { "zh-Hans-CN", "zh-CN", "en-US" })
        {
            if (OcrEngine.IsLanguageSupported(new Language(languageTag)))
            {
                engine = OcrEngine.TryCreateFromLanguage(new Language(languageTag));
                if (engine is not null)
                {
                    return engine;
                }
            }
        }

        return null;
    }
}
