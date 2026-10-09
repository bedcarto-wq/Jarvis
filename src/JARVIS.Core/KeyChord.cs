namespace Jarvis.Core;

/// <summary>Коды виртуальных клавиш Windows (подмножество, используемое JARVIS).</summary>
public enum VirtualKey : ushort
{
    Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Ctrl = 0x11, Alt = 0x12, Pause = 0x13,
    CapsLock = 0x14, Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24,
    Left = 0x25, Up = 0x26, Right = 0x27, Down = 0x28, PrintScreen = 0x2C, Insert = 0x2D, Delete = 0x2E,
    D0 = 0x30, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Win = 0x5B, Apps = 0x5D,
    NumPad0 = 0x60, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF,
    MediaNext = 0xB0, MediaPrev = 0xB1, MediaStop = 0xB2, MediaPlayPause = 0xB3,
    Plus = 0xBB, Comma = 0xBC, Minus = 0xBD, Period = 0xBE,
}

/// <summary>Сочетание клавиш вида «Ctrl+Shift+T».</summary>
public sealed class KeyChord
{
    private static readonly Dictionary<string, VirtualKey> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VirtualKey.Ctrl, ["control"] = VirtualKey.Ctrl, ["контрол"] = VirtualKey.Ctrl,
        ["shift"] = VirtualKey.Shift, ["шифт"] = VirtualKey.Shift,
        ["alt"] = VirtualKey.Alt, ["альт"] = VirtualKey.Alt,
        ["win"] = VirtualKey.Win, ["windows"] = VirtualKey.Win, ["вин"] = VirtualKey.Win,
        ["enter"] = VirtualKey.Enter, ["return"] = VirtualKey.Enter, ["ввод"] = VirtualKey.Enter,
        ["esc"] = VirtualKey.Escape, ["escape"] = VirtualKey.Escape,
        ["del"] = VirtualKey.Delete, ["delete"] = VirtualKey.Delete,
        ["ins"] = VirtualKey.Insert, ["backspace"] = VirtualKey.Back, ["back"] = VirtualKey.Back,
        ["space"] = VirtualKey.Space, ["пробел"] = VirtualKey.Space, ["tab"] = VirtualKey.Tab,
        ["pgup"] = VirtualKey.PageUp, ["pgdn"] = VirtualKey.PageDown, ["pagedown"] = VirtualKey.PageDown,
        ["pageup"] = VirtualKey.PageUp, ["prtsc"] = VirtualKey.PrintScreen, ["printscreen"] = VirtualKey.PrintScreen,
        ["+"] = VirtualKey.Plus, ["plus"] = VirtualKey.Plus, ["-"] = VirtualKey.Minus, ["minus"] = VirtualKey.Minus,
        [","] = VirtualKey.Comma, ["."] = VirtualKey.Period,
    };

    private static readonly VirtualKey[] ModifierOrder = [VirtualKey.Ctrl, VirtualKey.Alt, VirtualKey.Shift, VirtualKey.Win];

    public KeyChord(IReadOnlyList<VirtualKey> modifiers, VirtualKey key)
    {
        Modifiers = modifiers.Distinct().OrderBy(m => Array.IndexOf(ModifierOrder, m)).ToList();
        Key = key;
    }

    public IReadOnlyList<VirtualKey> Modifiers { get; }
    public VirtualKey Key { get; }

    public static bool IsModifier(VirtualKey k) => Array.IndexOf(ModifierOrder, k) >= 0;

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        // «Ctrl++» — плюс как клавиша
        var parts = new List<string>();
        foreach (var raw in t.Replace("++", "+plus").Split('+'))
        {
            var p = raw.Trim();
            if (p.Length > 0) parts.Add(p);
        }
        if (parts.Count == 0) return false;
        var keys = new List<VirtualKey>();
        foreach (var p in parts)
        {
            if (!TryParseKey(p, out var vk)) return false;
            keys.Add(vk);
        }
        var main = keys[^1];
        var mods = keys.Take(keys.Count - 1).ToList();
        if (mods.Any(m => !IsModifier(m))) return false;
        chord = new KeyChord(mods, main);
        return true;
    }

    public static bool TryParseKey(string p, out VirtualKey vk)
    {
        if (Aliases.TryGetValue(p, out vk)) return true;
        if (p.Length == 1 && char.IsAsciiLetter(p[0])) { vk = (VirtualKey)char.ToUpperInvariant(p[0]); return true; }
        if (p.Length == 1 && char.IsAsciiDigit(p[0])) { vk = (VirtualKey)p[0]; return true; }
        return Enum.TryParse(p, ignoreCase: true, out vk) && Enum.IsDefined(vk);
    }

    public override string ToString() =>
        string.Join("+", Modifiers.Select(KeyName).Append(KeyName(Key)));

    private static string KeyName(VirtualKey k) => k switch
    {
        >= VirtualKey.D0 and <= VirtualKey.D9 => ((char)k).ToString(),
        _ => k.ToString(),
    };
}
