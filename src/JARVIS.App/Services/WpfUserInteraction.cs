using System.Windows;
using Jarvis.App.Views;
using Jarvis.Core.Abstractions;
using Jarvis.Core.Security;

namespace Jarvis.App.Services;

/// <summary>
/// Диалоги с пользователем: подтверждение (кнопкой или голосом «да/нет»),
/// просьба указать элемент, выбор варианта, уведомления.
/// </summary>
public sealed class WpfUserInteraction : IUserInteraction
{
    private TaskCompletionSource<bool>? _confirm;
    private TaskCompletionSource<ScreenPoint?>? _point;
    private TaskCompletionSource<int?>? _choice;
    private ConfirmWindow? _confirmWindow;
    private PointOverlay? _overlay;
    private ChooseWindow? _chooseWindow;

    public event Action<string, NotifyLevel>? Notified;
    public event Action? StateChanged;
    public Func<IInputService>? Input { get; set; }

    public bool AwaitingConfirmation => _confirm is { Task.IsCompleted: false };
    public bool AwaitingPoint => _point is { Task.IsCompleted: false };
    public bool AwaitingChoice => _choice is { Task.IsCompleted: false };
    public IReadOnlyList<string> CurrentOptions { get; private set; } = [];

    public async Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirm = tcs;
        await OnUi(() =>
        {
            _confirmWindow = new ConfirmWindow(request.Description, request.Category.ToRussian());
            _confirmWindow.Answered += ok => tcs.TrySetResult(ok);
            _confirmWindow.Show();
            _confirmWindow.Activate();
        });
        StateChanged?.Invoke();
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task; }
        finally
        {
            await OnUi(() => { _confirmWindow?.CloseSilently(); _confirmWindow = null; });
            StateChanged?.Invoke();
        }
    }

    /// <summary>Голосовой ответ на подтверждение.</summary>
    public bool AnswerConfirmation(bool ok) => _confirm?.TrySetResult(ok) == true;

    public async Task<ScreenPoint?> RequestPointAsync(string message, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ScreenPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _point = tcs;
        Notify(message, NotifyLevel.Warning);
        await OnUi(() =>
        {
            _overlay = new PointOverlay(message);
            _overlay.Picked += p => tcs.TrySetResult(p);
            _overlay.Cancelled += () => tcs.TrySetResult(null);
            _overlay.Show();
            _overlay.Activate();
        });
        StateChanged?.Invoke();
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task; }
        finally
        {
            await OnUi(() => { _overlay?.Close(); _overlay = null; });
            await Task.Delay(150, CancellationToken.None); // окно-накладка должно исчезнуть до чтения элемента
            StateChanged?.Invoke();
        }
    }

    /// <summary>Голосом «здесь»: берётся текущее положение курсора.</summary>
    public bool AnswerPointWithCursor()
    {
        if (_point is null || _point.Task.IsCompleted || Input is null) return false;
        return _point.TrySetResult(Input().GetCursorPosition());
    }

    public async Task<int?> ChooseAsync(string question, IReadOnlyList<string> options, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choice = tcs;
        CurrentOptions = options;
        await OnUi(() =>
        {
            _chooseWindow = new ChooseWindow(question, options);
            _chooseWindow.Chosen += i => tcs.TrySetResult(i);
            _chooseWindow.Show();
            _chooseWindow.Activate();
        });
        StateChanged?.Invoke();
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        try { return await tcs.Task; }
        finally
        {
            await OnUi(() => { _chooseWindow?.CloseSilently(); _chooseWindow = null; });
            CurrentOptions = [];
            StateChanged?.Invoke();
        }
    }

    public bool AnswerChoice(int? index) => _choice?.TrySetResult(index) == true;

    public void CancelAll()
    {
        _confirm?.TrySetResult(false);
        _point?.TrySetResult(null);
        _choice?.TrySetResult(null);
    }

    public void Notify(string message, NotifyLevel level = NotifyLevel.Info) => Notified?.Invoke(message, level);

    private static Task OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { a(); return Task.CompletedTask; }
        return d.InvokeAsync(a).Task;
    }
}
