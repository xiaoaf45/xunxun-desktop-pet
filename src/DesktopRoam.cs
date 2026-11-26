using System;
using System.Windows;

namespace XunXun;

sealed class DesktopRoam
{
    private const double NoticeRadius = 210;
    private const double FollowRadius = 410;
    private const double FollowSpeed = 150;
    internal const double CompactScale = 0.50;
    private static readonly double[] DetourAngles = { 32, -32, 52, -52, 66, -66 };

    private Point? destination;
    private double pickAt;
    private double pauseUntil;
    private Point? pointerSample;
    private double sampleAt;
    private double followUntil;

    public void Reset(double time)
    {
        destination = null;
        pauseUntil = 0;
        pointerSample = null;
        sampleAt = time + 0.8;
        followUntil = 0;
    }

    public bool TrackPointer(Point position, Point pointer, Rect workArea, double time)
    {
        double distance = (pointer - position).LengthSquared;
        bool onScreen = workArea.Contains(pointer);
        bool following = time < followUntil;
        if (!onScreen || distance > FollowRadius * FollowRadius) followUntil = 0;

        if (time >= sampleAt)
        {
            double radius = following ? FollowRadius : NoticeRadius;
            if (onScreen && pointerSample is { } previous && (pointer - previous).LengthSquared >= 9
                && distance <= radius * radius)
                followUntil = time + 2.2;
            pointerSample = pointer;
            sampleAt = time + 0.09;
        }
        return time < followUntil;
    }

    public Point Step(Point position, Rect bounds, double time, double frame, Point? pointer, Rect[] icons)
    {
        if (!bounds.Contains(position))
            return MoveTowards(position, Clamp(position, bounds), 58 * frame);

        if (pointer is { } target)
        {
            destination = null;
            pauseUntil = 0;
            var offset = target - position;
            double distance = offset.Length;
            if (distance <= 45) return position;
            var stop = Clamp(target - offset * (45 / distance), bounds);
            return MoveTowards(position, stop, FollowSpeed * frame);
        }

        if (time < pauseUntil) return position;
        if (destination is { } last && (last - position).Length < 12)
        {
            destination = null;
            pauseUntil = time + 0.3 + Random.Shared.NextDouble() * 0.8;
            return position;
        }
        if (destination is null || time >= pickAt || !bounds.Contains(destination.Value))
        {
            destination = new Point(bounds.Left + Random.Shared.NextDouble() * bounds.Width,
                bounds.Top + Random.Shared.NextDouble() * bounds.Height);
            pickAt = time + 3.5 + Random.Shared.NextDouble() * 5;
        }

        double speed = 58 + 10 * Math.Sin(time * 0.55);
        return WanderToward(position, destination.Value, bounds, speed * frame, icons);
    }

    internal static Point WanderToward(Point position, Point target, Rect bounds, double step, Rect[] icons)
    {
        Vector direction = target - position;
        double distance = direction.Length;
        if (distance < 18 || TouchesIcon(position, position, icons))
            return MoveTowards(position, target, step);

        direction /= distance;
        double lookAhead = Math.Min(88, distance);
        if (TouchesIcon(position, position + direction * lookAhead, icons))
        {
            foreach (double angle in DetourAngles)
            {
                double radians = angle * Math.PI / 180;
                double sin = Math.Sin(radians), cos = Math.Cos(radians);
                var turn = new Vector(direction.X * cos - direction.Y * sin,
                    direction.X * sin + direction.Y * cos);
                var ahead = position + turn * lookAhead;
                if (bounds.Contains(ahead) && !TouchesIcon(position, ahead, icons)) return position + turn * step;
            }
        }
        // An unavailable detour still leaves a usable path at a smaller size.
        return MoveTowards(position, target, step);
    }

    internal static double ScaleFor(Point start, Point next, Rect[] icons)
    {
        Vector direction = next - start;
        if (direction.LengthSquared > 0.01)
        {
            direction.Normalize();
            next += direction * 23;
        }
        return TouchesIcon(start, next, icons) ? CompactScale : 1;
    }

    private static bool TouchesIcon(Point start, Point end, Rect[] icons)
    {
        Vector offset = end - start;
        foreach (var icon in icons)
        {
            // Expand the icon by the pet's walking silhouette.
            double enter = 0;
            double exit = 1;
            if (Clip(start.X, offset.X, icon.Left - 53, icon.Right + 53, ref enter, ref exit)
                && Clip(start.Y, offset.Y, icon.Top - 40, icon.Bottom + 63, ref enter, ref exit))
                return true;
        }
        return false;
    }

    private static bool Clip(double start, double offset, double min, double max, ref double enter, ref double exit)
    {
        if (Math.Abs(offset) < 0.0001) return start >= min && start <= max;
        double first = (min - start) / offset;
        double second = (max - start) / offset;
        enter = Math.Max(enter, Math.Min(first, second));
        exit = Math.Min(exit, Math.Max(first, second));
        return enter <= exit;
    }

    private static Point Clamp(Point point, Rect bounds) =>
        new(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom));

    private static Point MoveTowards(Point from, Point to, double step)
    {
        var offset = to - from;
        double distance = offset.Length;
        return distance <= step ? to : from + offset * (step / distance);
    }
}
