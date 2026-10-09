namespace Jarvis.Core.Abstractions;

public readonly record struct ScreenPoint(int X, int Y);

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public ScreenPoint Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(ScreenPoint p) => p.X >= X && p.Y >= Y && p.X < X + Width && p.Y < Y + Height;
}
