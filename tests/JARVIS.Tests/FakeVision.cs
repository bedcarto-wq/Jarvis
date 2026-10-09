using Jarvis.Core.Abstractions;
using Jarvis.Core.Vision;

namespace Jarvis.Tests;

public sealed class FakeVision : IVisionService
{
    public HashSet<string> VisibleTexts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int Activations;
    public bool OcrAvailable => true;
    public string OcrStatus => "fake";
    public UiElementInfo? GetElementAt(ScreenPoint point) => null;
    public Task<LocateResult> LocateAsync(ElementQuery query, CancellationToken ct) =>
        Task.FromResult(query.Name is not null && VisibleTexts.Contains(query.Name)
            ? new LocateResult(true, LocateMethod.UiAutomationName, new ScreenRect(10, 10, 20, 20), null, null)
            : LocateResult.NotFound);
    public Task<bool> ActivateElementAsync(LocateResult located, ClickKind click, CancellationToken ct) { Activations++; return Task.FromResult(true); }
    public Task<LocateResult> FindTextAsync(string text, nint? window, bool allowOcr, CancellationToken ct) =>
        Task.FromResult(VisibleTexts.Contains(text) ? new LocateResult(true, LocateMethod.Ocr, new ScreenRect(1, 1, 5, 5), null, null) : LocateResult.NotFound);
    public Task<IReadOnlyList<UiElementInfo>> SnapshotElementsAsync(nint window, int maxElements, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UiElementInfo>>([]);
}
