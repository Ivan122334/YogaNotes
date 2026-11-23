using System.Numerics;
using System.Windows;
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

    public HandwritingRecognitionService()
    {
        _recognizerContainer = new InkRecognizerContainer();
        foreach (var rec in _recognizerContainer.GetRecognizers())
        {
            var name = rec.Name ?? string.Empty;
            if (_englishRecognizer == null && name.Contains("English", StringComparison.OrdinalIgnoreCase))
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

    public async Task<List<string>> RecognizeLinesAsync(IEnumerable<Stroke> wpfStrokes, HandwritingLanguage language)
    {
        var strokeList = wpfStrokes.Where(s => s.StylusPoints.Count > 0).ToList();
        if (strokeList.Count == 0) return new List<string>();

        var targetRecognizer = language == HandwritingLanguage.Bulgarian
            ? (_bulgarianRecognizer ?? _englishRecognizer)
            : (_englishRecognizer ?? _bulgarianRecognizer);

        if (targetRecognizer == null)
        {
            throw new InvalidOperationException("No Windows handwriting recognizer is installed for the selected language.");
        }

        _recognizerContainer.SetDefaultRecognizer(targetRecognizer);

        var recognizedLines = new List<string>();
        foreach (var lineStrokes in GroupStrokesIntoLines(strokeList))
        {
            var text = await RecognizeGroupAsync(lineStrokes);
            if (!string.IsNullOrWhiteSpace(text))
            {
                recognizedLines.Add(text);
            }
        }

        return recognizedLines;
    }

    private async Task<string> RecognizeGroupAsync(List<Stroke> strokes)
    {
        var winrtContainer = new InkStrokeContainer();
        var builder = new InkStrokeBuilder();

        foreach (var wpfStroke in strokes)
        {
            var inkPoints = new List<InkPoint>(wpfStroke.StylusPoints.Count);
            foreach (var sp in wpfStroke.StylusPoints)
            {
                float pressure = Math.Clamp(sp.PressureFactor, 0.01f, 1.0f);
                inkPoints.Add(new InkPoint(new Windows.Foundation.Point(sp.X, sp.Y), pressure));
            }

            winrtContainer.AddStroke(builder.CreateStrokeFromInkPoints(inkPoints, Matrix3x2.Identity));
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

    private static List<List<Stroke>> GroupStrokesIntoLines(List<Stroke> strokes)
    {
        var items = strokes
            .Select(s => (Stroke: s, Bounds: s.GetBounds()))
            .OrderBy(x => x.Bounds.Top + x.Bounds.Height / 2.0)
            .ToList();

        var heights = items.Select(x => x.Bounds.Height).Where(h => h > 4).OrderBy(h => h).ToList();
        double medianHeight = heights.Count > 0 ? heights[heights.Count / 2] : 24.0;
        double lineGapThreshold = Math.Max(medianHeight * 0.85, 22.0);

        var lines = new List<(Rect Band, List<Stroke> Strokes)>();

        foreach (var item in items)
        {
            bool placed = false;
            double itemMidY = item.Bounds.Top + item.Bounds.Height / 2.0;

            for (int i = 0; i < lines.Count; i++)
            {
                var band = lines[i].Band;
                bool overlaps = item.Bounds.Top <= band.Bottom + lineGapThreshold * 0.45 &&
                                item.Bounds.Bottom >= band.Top - lineGapThreshold * 0.45;
                bool nearCenter = Math.Abs(itemMidY - (band.Top + band.Height / 2.0)) <= lineGapThreshold;

                if (overlaps || nearCenter)
                {
                    var updated = band;
                    updated.Union(item.Bounds);
                    lines[i] = (updated, lines[i].Strokes);
                    lines[i].Strokes.Add(item.Stroke);
                    placed = true;
                    break;
                }
            }

            if (!placed)
            {
                lines.Add((item.Bounds, new List<Stroke> { item.Stroke }));
            }
        }

        return lines
            .OrderBy(l => l.Band.Top)
            .Select(l => l.Strokes.OrderBy(s => s.GetBounds().Left).ToList())
            .ToList();
    }
}
