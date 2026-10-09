using Jarvis.Core.Abstractions;

namespace Jarvis.Core.Vision;

/// <summary>Сведения об элементе интерфейса (UI Automation и/или OCR).</summary>
public sealed record UiElementInfo(
    string? Name,
    string? AutomationId,
    string? ControlType,
    string? ClassName,
    ScreenRect Bounds,
    string? ProcessName,
    string? WindowTitle,
    ScreenRect? WindowBounds,
    bool SupportsInvoke);

/// <summary>Запомненная привязка к элементу интерфейса.</summary>
public sealed class ElementAnchor
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Пользовательское имя привязки («кнопка отправить»).</summary>
    public string Label { get; set; } = "";
    public string? AppId { get; set; }
    public string? ProcessName { get; set; }
    public string? WindowTitle { get; set; }
    public string? ElementName { get; set; }
    public string? AutomationId { get; set; }
    public string? ControlType { get; set; }
    public string? ClassName { get; set; }
    /// <summary>Текст, видимый на элементе (для OCR-поиска).</summary>
    public string? VisibleText { get; set; }
    /// <summary>Центр элемента относительно окна, доли 0..1.</summary>
    public double? RelativeX { get; set; }
    public double? RelativeY { get; set; }
    /// <summary>Абсолютные координаты — последний резервный способ.</summary>
    public int? ScreenX { get; set; }
    public int? ScreenY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastUsedAt { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Label) ? (ElementName ?? VisibleText ?? AutomationId ?? Id.ToString()) : Label;
}

public enum LocateMethod { None, Anchor, UiAutomationId, UiAutomationName, Ocr, RelativeCoordinates, AbsoluteCoordinates }

public sealed class ElementQuery
{
    public string? AppId { get; init; }
    public IReadOnlyList<string> ProcessNames { get; init; } = [];
    public nint? Window { get; init; }
    public string? Name { get; init; }
    public string? AutomationId { get; init; }
    public string? ControlType { get; init; }
    public Guid? AnchorId { get; init; }
    public string? Text { get; init; }
    public bool AllowOcr { get; init; } = true;
    public bool AllowCoordinates { get; init; } = true;

    public override string ToString() =>
        Name ?? Text ?? AutomationId ?? AnchorId?.ToString() ?? "(элемент)";
}

public sealed record LocateResult(bool Found, LocateMethod Method, ScreenRect Bounds, object? NativeElement, string? Detail)
{
    public static readonly LocateResult NotFound = new(false, LocateMethod.None, default, null, null);
}

public enum ClickKind { Single, Double, Right }

public sealed record OcrWord(string Text, ScreenRect Bounds, float Confidence);

public interface IVisionService
{
    bool OcrAvailable { get; }
    string OcrStatus { get; }
    UiElementInfo? GetElementAt(ScreenPoint point);
    Task<LocateResult> LocateAsync(ElementQuery query, CancellationToken ct);
    Task<bool> ActivateElementAsync(LocateResult located, ClickKind click, CancellationToken ct);
    Task<LocateResult> FindTextAsync(string text, nint? window, bool allowOcr, CancellationToken ct);
    /// <summary>Сбор сведений о доступных элементах окна (первичная инициализация).</summary>
    Task<IReadOnlyList<UiElementInfo>> SnapshotElementsAsync(nint window, int maxElements, CancellationToken ct);
}
