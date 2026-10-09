using Jarvis.Core.Abstractions;
using Jarvis.Core.Storage;

namespace Jarvis.Core.Vision;

public sealed class AnchorStore
{
    public const int CurrentSchema = 1;
    private readonly string _path;
    private readonly string? _corruptDir;
    private readonly object _lock = new();
    private List<ElementAnchor> _anchors = [];

    public AnchorStore(string path, string? corruptDir = null)
    {
        _path = path;
        _corruptDir = corruptDir;
    }

    public event Action? Changed;
    public string? LastLoadMessage { get; private set; }

    public IReadOnlyList<ElementAnchor> All
    {
        get { lock (_lock) return _anchors.ToList(); }
    }

    public void Load()
    {
        var r = JsonStore.Load(_path, CurrentSchema, () => new List<ElementAnchor>(), _corruptDir);
        lock (_lock) _anchors = r.Value;
        LastLoadMessage = r.Status == LoadStatus.Corrupt
            ? $"Файл привязок повреждён, копия: {r.BackupPath}. Список привязок очищен." : null;
    }

    public void Save()
    {
        lock (_lock) JsonStore.Save(_path, CurrentSchema, _anchors);
        Changed?.Invoke();
    }

    public ElementAnchor? Get(Guid id)
    {
        lock (_lock) return _anchors.FirstOrDefault(a => a.Id == id);
    }

    public ElementAnchor? FindByLabel(string label, string? appId = null)
    {
        var n = Text.TextNormalizer.Normalize(label);
        lock (_lock)
            return _anchors
                .Where(a => appId is null || a.AppId is null || a.AppId == appId)
                .FirstOrDefault(a => Text.TextNormalizer.Normalize(a.Label) == n
                                     || Text.TextNormalizer.Normalize(a.ElementName ?? "") == n);
    }

    public void Upsert(ElementAnchor anchor)
    {
        lock (_lock)
        {
            var i = _anchors.FindIndex(a => a.Id == anchor.Id);
            if (i >= 0) _anchors[i] = anchor; else _anchors.Add(anchor);
        }
        Save();
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (_lock) removed = _anchors.RemoveAll(a => a.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    public void Clear()
    {
        lock (_lock) _anchors.Clear();
        Save();
    }

    /// <summary>Создаёт привязку из сведений об элементе под курсором.</summary>
    public static ElementAnchor FromElement(UiElementInfo e, ScreenPoint point, string label, string? appId)
    {
        double? rx = null, ry = null;
        if (e.WindowBounds is { IsEmpty: false } wb)
        {
            var c = e.Bounds.IsEmpty ? point : e.Bounds.Center;
            rx = Math.Clamp((c.X - wb.X) / (double)wb.Width, 0, 1);
            ry = Math.Clamp((c.Y - wb.Y) / (double)wb.Height, 0, 1);
        }
        var center = e.Bounds.IsEmpty ? point : e.Bounds.Center;
        return new ElementAnchor
        {
            Label = string.IsNullOrWhiteSpace(label) ? (e.Name ?? "элемент") : label,
            AppId = appId,
            ProcessName = e.ProcessName,
            WindowTitle = e.WindowTitle,
            ElementName = string.IsNullOrWhiteSpace(e.Name) ? null : e.Name,
            AutomationId = string.IsNullOrWhiteSpace(e.AutomationId) ? null : e.AutomationId,
            ControlType = e.ControlType,
            ClassName = e.ClassName,
            VisibleText = string.IsNullOrWhiteSpace(e.Name) ? null : e.Name,
            RelativeX = rx,
            RelativeY = ry,
            ScreenX = center.X,
            ScreenY = center.Y,
            Width = e.Bounds.Width,
            Height = e.Bounds.Height,
        };
    }
}
