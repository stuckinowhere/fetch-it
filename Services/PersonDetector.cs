using System.Buffers;
using FetchIt.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FetchIt.Services;

/// <summary>Finds people in a picture, on this PC. Nothing leaves the machine.</summary>
public sealed class PersonDetector : IDisposable
{
    internal const string ModelUrl = "https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_s.onnx";
    internal const string ModelSha256 = "c5c2d13e59ae883e6af3b45daea64af4833a4951c92d116ec270d9ddbe998063";

    internal const int InputSize = 640;

    // Each candidate box is x, y, w, h, objectness, then 80 COCO classes. "person" is the first class.
    internal const int ValuesPerBox = 85;
    private const int Objectness = 4;
    private const int PersonClass = 5;

    // Tuned on real Instagram photos: below this are scenery, food and hands.
    // A borderline photo stays with the others, because that mistake is the cheap one.
    internal const float MinScore = 0.7f;

    private readonly InferenceSession _session;
    private readonly string _inputName;

    // One picture at a time: two at once measured no faster, the model already uses the cores.
    internal PersonDetector(string modelPath)
    {
        using var options = new SessionOptions
        {
            // Leave cores free so the window stays responsive.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
        };
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>Gets the model on first use and checks its hash every time.</summary>
    public static async Task<PersonDetector> LoadAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(ToolBootstrapper.LocalToolsDirectory, "yolox_s.onnx");
        await ToolBootstrapper.EnsureVerifiedFileAsync(ModelUrl, path, ModelSha256, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => new PersonDetector(path), cancellationToken).ConfigureAwait(false);
    }

    public PeopleCheck CheckImage(string path)
        => PersonScore(path) switch
        {
            null => PeopleCheck.Unknown,
            >= MinScore => PeopleCheck.HasPeople,
            _ => PeopleCheck.NoPeople
        };

    /// <summary>The best person score, or null when the picture cannot be read.</summary>
    internal float? PersonScore(string path)
    {
        using var bitmap = SKBitmap.Decode(path);
        if (bitmap is null)
            return null;

        // One picture needs 5 MB of input: rented, or thousands of pictures churn the large heap.
        var buffer = ArrayPool<float>.Shared.Rent(InputLength);
        try
        {
            FillInput(bitmap, buffer.AsSpan(0, InputLength));
            var input = new DenseTensor<float>(buffer.AsMemory(0, InputLength), [1, 3, InputSize, InputSize]);
            using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, input)]);
            var output = results.First().AsTensor<float>();
            return output is DenseTensor<float> dense
                ? BestPersonScore(dense.Buffer.Span)
                : BestPersonScore(output.ToArray());
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private const int InputLength = InputSize * InputSize * 3;

    /// <summary>
    /// The model takes raw 0-255 values in blue, green, red order, letterboxed
    /// from the top left onto grey. Other input gives no error, only low scores,
    /// so this order was measured on real photos.
    /// </summary>
    internal static float[] ToInput(SKBitmap bitmap)
    {
        var input = new float[InputLength];
        FillInput(bitmap, input);
        return input;
    }

    private static void FillInput(SKBitmap bitmap, Span<float> input)
    {
        var scale = Math.Min((float)InputSize / bitmap.Width, (float)InputSize / bitmap.Height);
        using var letterboxed = new SKBitmap(InputSize, InputSize, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(letterboxed))
        using (var image = SKImage.FromBitmap(bitmap))
        {
            canvas.Clear(new SKColor(114, 114, 114));
            canvas.DrawImage(
                image,
                new SKRect(0, 0, bitmap.Width * scale, bitmap.Height * scale),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        var pixels = letterboxed.GetPixelSpan();
        var area = InputSize * InputSize;
        for (var i = 0; i < area; i++)
        {
            input[i] = pixels[i * 4];
            input[area + i] = pixels[i * 4 + 1];
            input[area * 2 + i] = pixels[i * 4 + 2];
        }
    }

    /// <summary>Yes or no needs only the best box, so there is no box decoding or NMS.</summary>
    internal static float BestPersonScore(ReadOnlySpan<float> output)
    {
        var best = 0f;
        for (var i = 0; i + ValuesPerBox <= output.Length; i += ValuesPerBox)
            best = Math.Max(best, output[i + Objectness] * output[i + PersonClass]);
        return best;
    }

    public void Dispose() => _session.Dispose();
}
