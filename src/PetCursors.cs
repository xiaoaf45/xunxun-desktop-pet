using System;
using System.Windows;
using System.Windows.Input;

namespace XunXun;

static class PetCursors
{
    public static Cursor Open { get; } = Load("pet-hand.cur");
    public static Cursor Pressed { get; } = Load("pet-press.cur");

    private static Cursor Load(string name)
    {
        using var stream = typeof(PetCursors).Assembly.GetManifestResourceStream("XunXun.Assets." + name);
        if (stream is null) return Cursors.Hand;
        try { return new Cursor(stream, scaleWithDpi: true); }
        catch (ArgumentException) { return Cursors.Hand; }
    }
}

sealed class HighFivePointer(Point cursor, Point contact)
{
    private Point last = cursor;
    private readonly Vector offset = cursor - contact;

    internal bool TryFollow(Point current, Point contact, double elapsed, out Point target)
    {
        target = current;
        // Input from the user takes precedence over the animation.
        if ((current - last).LengthSquared > 9 || elapsed >= Play.HighFive.Duration()) return false;

        double blend = Math.Clamp(elapsed / 0.28, 0, 1);
        double remaining = 1 - blend * blend * (3 - 2 * blend);
        var position = contact + offset * remaining;
        target = new Point(Math.Round(position.X), Math.Round(position.Y));
        last = target;
        return true;
    }
}
