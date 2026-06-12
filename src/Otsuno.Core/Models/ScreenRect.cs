namespace Otsuno.Core.Models;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height) {
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
