namespace Jarvis.Core.Execution;

public sealed record UndoEntry(string Description, Action Undo);

/// <summary>Журнал отменяемых операций (переименование/перемещение, создание файла).</summary>
public sealed class UndoJournal
{
    private readonly object _lock = new();
    private readonly LinkedList<UndoEntry> _stack = new();
    private const int Capacity = 20;

    public void Push(UndoEntry entry)
    {
        lock (_lock)
        {
            _stack.AddLast(entry);
            while (_stack.Count > Capacity) _stack.RemoveFirst();
        }
    }

    public bool CanUndo
    {
        get { lock (_lock) return _stack.Count > 0; }
    }

    public string? PeekDescription
    {
        get { lock (_lock) return _stack.Last?.Value.Description; }
    }

    public UndoEntry? UndoLast()
    {
        UndoEntry? e;
        lock (_lock)
        {
            e = _stack.Last?.Value;
            if (e is not null) _stack.RemoveLast();
        }
        e?.Undo();
        return e;
    }
}
