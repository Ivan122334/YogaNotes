using System.Numerics;
using System.Windows.Ink;
using Windows.UI.Input.Inking;

namespace YogaNotes.Services;

public enum HandwritingLanguage
{
    English,
    Bulgarian
}

public class HandwritingRecognitionService
{
    private readonly InkRecognizerContainer _recognizerContainer;
    private readonly InkRecognizer? _englishRecognizer;
    private readonly InkRecognizer? _bulgarianRecognizer;

    public bool IsEnglishAvailable => _englishRecognizer != null;
    public bool IsBulgarianAvailable => _bulgarianRecognizer != null;

    public HandwritingRecognitionService()
    {
        _recognizerContainer = new InkRecognizerContainer();
        var recognizers = _recognizerContainer.GetRecognizers();

        foreach (var rec in recognizers)
        {
            var name = rec.Name ?? string.Empty;
            if (_englishRecognizer == null &&
                name.Contains("English", StringComparison.OrdinalIgnoreCase))
            {
                _englishRecognizer = rec;
            }

            if (_bulgarianRecognizer == null &&
                (name.Contains("България", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Bulgarian", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Български", StringComparison.OrdinalIgnoreCase)))
            {
                _bulgarianRecognizer = rec;
            }
        }
    }

    public async Task<string> RecognizeStrokesAsync(IEnumerable<Stroke> wpfStrokes, HandwritingLanguage language)
    {
        var strokeList = wpfStrokes.ToList();
        if (strokeList.Count == 0) return string.Empty;

        var targetRecognizer = language == HandwritingLanguage.Bulgarian
            ? (_bulgarianRecognizer ?? _englishRecognizer)
            : (_englishRecognizer ?? _bulgarianRecognizer);

        if (targetRecognizer == null) return string.Empty;

        _recognizerContainer.SetDefaultRecognizer(targetRecognizer);

        var winrtContainer = new InkStrokeContainer();
        var builder = new InkStrokeBuilder();

        foreach (var wpfStroke in strokeList)
        {
            if (wpfStroke.StylusPoints.Count == 0) continue;

            var inkPoints = new List<InkPoint>(wpfStroke.StylusPoints.Count);
            foreach (var sp in wpfStroke.StylusPoints)
            {
                float pressure = Math.Clamp(sp.PressureFactor, 0.01f, 1.0f);
                inkPoints.Add(new InkPoint(new Windows.Foundation.Point(sp.X, sp.Y), pressure));
            }

            var winrtStroke = builder.CreateStrokeFromInkPoints(inkPoints, Matrix3x2.Identity);
            winrtContainer.AddStroke(winrtStroke);
        }

        if (winrtContainer.GetStrokes().Count == 0) return string.Empty;

        var recognitionResults = await _recognizerContainer.RecognizeAsync(winrtContainer, InkRecognitionTarget.All);
        if (recognitionResults == null || recognitionResults.Count == 0) return string.Empty;

        var words = new List<string>();
        foreach (var result in recognitionResults)
        {
            var candidates = result.GetTextCandidates();
            if (candidates != null && candidates.Count > 0 && !string.IsNullOrWhiteSpace(candidates[0]))
            {
                words.Add(candidates[0].Trim());
            }
        }

        return string.Join(" ", words);
    }
}
