using System.Runtime.InteropServices;
using Jarvis.Core;
using Jarvis.Core.Abstractions;
using static Jarvis.Platform.Win32;

namespace Jarvis.Platform;

/// <summary>
/// Эмуляция мыши и клавиатуры через SendInput. Все удерживаемые клавиши и кнопки
/// отслеживаются, чтобы ReleaseAll() гарантированно отпустил их при стопе или ошибке.
/// </summary>
public sealed class InputService : IInputService
{
    private readonly HashSet<VirtualKey> _heldKeys = [];
    private readonly HashSet<MouseButton> _heldButtons = [];
    private readonly object _lock = new();
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private static readonly HashSet<VirtualKey> Extended =
    [
        VirtualKey.Left, VirtualKey.Right, VirtualKey.Up, VirtualKey.Down, VirtualKey.Insert, VirtualKey.Delete,
        VirtualKey.Home, VirtualKey.End, VirtualKey.PageUp, VirtualKey.PageDown, VirtualKey.Win, VirtualKey.Apps,
    ];

    public ScreenPoint GetCursorPosition()
    {
        GetCursorPos(out var p);
        return new ScreenPoint(p.X, p.Y);
    }

    public void MoveMouse(ScreenPoint p) => SetCursorPos(p.X, p.Y);

    public void Click(MouseButton button, int count = 1)
    {
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            MouseDown(button);
            MouseUp(button);
            if (i < count - 1) Thread.Sleep(40);
        }
    }

    public void MouseDown(MouseButton button)
    {
        SendMouse(button switch
        {
            MouseButton.Right => MOUSEEVENTF_RIGHTDOWN,
            MouseButton.Middle => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_LEFTDOWN,
        });
        lock (_lock) _heldButtons.Add(button);
    }

    public void MouseUp(MouseButton button)
    {
        SendMouse(button switch
        {
            MouseButton.Right => MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
            _ => MOUSEEVENTF_LEFTUP,
        });
        lock (_lock) _heldButtons.Remove(button);
    }

    public async Task DragAsync(ScreenPoint from, ScreenPoint to, CancellationToken ct)
    {
        MoveMouse(from);
        await Task.Delay(60, ct);
        MouseDown(MouseButton.Left);
        try
        {
            const int steps = 20;
            for (var i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();
                MoveMouse(new ScreenPoint(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps));
                await Task.Delay(15, ct);
            }
        }
        finally
        {
            MouseUp(MouseButton.Left);
        }
    }

    public void KeyDown(VirtualKey key)
    {
        SendKey(key, false);
        lock (_lock) _heldKeys.Add(key);
    }

    public void KeyUp(VirtualKey key)
    {
        SendKey(key, true);
        lock (_lock) _heldKeys.Remove(key);
    }

    public async Task PressChordAsync(KeyChord chord, int holdMs, CancellationToken ct)
    {
        try
        {
            foreach (var m in chord.Modifiers) KeyDown(m);
            KeyDown(chord.Key);
            if (holdMs > 0) await Task.Delay(holdMs, ct);
        }
        finally
        {
            KeyUp(chord.Key);
            foreach (var m in chord.Modifiers.Reverse()) KeyUp(m);
        }
    }

    public async Task TypeTextAsync(string text, int delayMs, CancellationToken ct)
    {
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n') { SendKey(VirtualKey.Enter, false); SendKey(VirtualKey.Enter, true); }
            else if (ch == '\t') { SendKey(VirtualKey.Tab, false); SendKey(VirtualKey.Tab, true); }
            else
            {
                var inputs = new[]
                {
                    new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } },
                    new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } },
                };
                SendInput(2, inputs, InputSize);
            }
            if (delayMs > 0) await Task.Delay(delayMs, ct);
        }
    }

    public void ReleaseAll()
    {
        VirtualKey[] keys;
        MouseButton[] buttons;
        lock (_lock)
        {
            keys = [.. _heldKeys];
            buttons = [.. _heldButtons];
            _heldKeys.Clear();
            _heldButtons.Clear();
        }
        foreach (var k in keys) SendKey(k, true);
        foreach (var b in buttons) MouseUp(b);
        // Подстраховка: модификаторы, которые система всё ещё считает нажатыми.
        foreach (var k in new[] { VirtualKey.Shift, VirtualKey.Ctrl, VirtualKey.Alt, VirtualKey.Win })
            if ((GetAsyncKeyState((int)k) & 0x8000) != 0) SendKey(k, true);
        if ((GetAsyncKeyState(0x01) & 0x8000) != 0) SendMouse(MOUSEEVENTF_LEFTUP);
        if ((GetAsyncKeyState(0x02) & 0x8000) != 0) SendMouse(MOUSEEVENTF_RIGHTUP);
    }

    private static void SendKey(VirtualKey key, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(key)) flags |= KEYEVENTF_EXTENDEDKEY;
        var input = new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)key, dwFlags = flags } } };
        SendInput(1, [input], InputSize);
    }

    private static void SendMouse(uint flags)
    {
        var input = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
        SendInput(1, [input], InputSize);
    }
}
