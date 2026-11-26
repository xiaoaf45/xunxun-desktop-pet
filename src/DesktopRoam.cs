using System;
using System.Linq;
using System.Windows;

namespace XunXun;

sealed class DesktopRoam(Random? random = null)
{
    private const double NoticeRadius = 210;
    private const double FollowRadius = 410;
    private const double FollowSpeed = 150;
    internal const double CompactScale = 0.50;
    private static readonly double[] DetourAngles = { 32, -32, 52, -52, 66, -66 };

    private readonly Random random = random ?? Random.Shared;
    private Point? destination;
    private string? destinationDisplay;
    private double pickAt;
    private double pauseUntil;
    private Point? pointerSample;
    private double sampleAt;
    private double followUntil;

    internal readonly record struct Display(string Name, Rect Frame, Rect Walk);

    internal readonly record struct Crossing(Point Entrance, Point Exit, bool Horizontal)
    {
        public Point Aim(Point position)
        {
            bool aligned = Horizontal
                ? Math.Abs(position.Y - Entrance.Y) < 0.01
                : Math.Abs(position.X - Entrance.X) < 0.01;
            double forward = (position - Entrance) * (Exit - Entrance);
            return aligned && forward >= -0.01 ? Exit : Entrance;
        }
    }

    public void Reset(double time)
    {
        destination = null;
        destinationDisplay = null;
        pauseUntil = 0;
        pointerSample = null;
        sampleAt = time + 0.8;
        followUntil = 0;
    }

    public bool TrackPointer(Point position, Point pointer, bool reachable, double time)
    {
        double distance = (pointer - position).LengthSquared;
        bool following = time < followUntil;
        if (!reachable || distance > FollowRadius * FollowRadius) followUntil = 0;

        if (time >= sampleAt)
        {
            double radius = following ? FollowRadius : NoticeRadius;
            if (reachable && pointerSample is { } previous && (pointer - previous).LengthSquared >= 9
                && distance <= radius * radius)
                followUntil = time + 2.2;
            pointerSample = pointer;
            sampleAt = time + 0.09;
        }
        return time < followUntil;
    }

    internal static Crossing? FindCrossing(Point position, Rect source, Rect target, Rect sourceWalk, Rect targetWalk)
    {
        // Use the monitor edges for adjacency and the work-area margins for the crossing route.
        if (Near(source.Right, target.Left) || Near(source.Left, target.Right))
        {
            double top = Math.Max(sourceWalk.Top, targetWalk.Top);
            double bottom = Math.Min(sourceWalk.Bottom, targetWalk.Bottom);
            if (top < bottom)
            {
                double y = Math.Clamp(position.Y, top, bottom);
                bool right = Near(source.Right, target.Left);
                return new Crossing(new Point(right ? sourceWalk.Right : sourceWalk.Left, y),
                    new Point(right ? targetWalk.Left : targetWalk.Right, y), true);
            }
        }
        if (Near(source.Bottom, target.Top) || Near(source.Top, target.Bottom))
        {
            double left = Math.Max(sourceWalk.Left, targetWalk.Left);
            double right = Math.Min(sourceWalk.Right, targetWalk.Right);
            if (left < right)
            {
                double x = Math.Clamp(position.X, left, right);
                bool down = Near(source.Bottom, target.Top);
                return new Crossing(new Point(x, down ? sourceWalk.Bottom : sourceWalk.Top),
                    new Point(x, down ? targetWalk.Top : targetWalk.Bottom), false);
            }
        }
        return null;
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) < 1.5;

    public Point Step(Point position, Rect bounds, double time, double frame, Point? pointer, Rect[] icons,
        Crossing? crossing = null, Display? currentDisplay = null, Display[]? displays = null)
    {
        if (pointer is { } target)
        {
            destination = null;
            destinationDisplay = null;
            pauseUntil = 0;
            if (crossing is { } route)
                return MoveTowards(position, route.Aim(position), FollowSpeed * frame);
            var offset = target - position;
            double distance = offset.Length;
            if (distance <= 45) return position;
            var stop = Clamp(target - offset * (45 / distance), bounds);
            return MoveTowards(position, stop, FollowSpeed * frame);
        }

        double speed = 58 + 10 * Math.Sin(time * 0.55);
        if (destinationDisplay is { } planned && currentDisplay is { } active)
        {
            if (planned == active.Name)
            {
                destinationDisplay = null;
                pickAt = time + 3.5 + random.NextDouble() * 5;
            }
            else if (displays is not null)
            {
                var nextDisplay = displays.FirstOrDefault(display => display.Name == planned);
                if (nextDisplay.Name is not null
                    && FindCrossing(position, active.Frame, nextDisplay.Frame, bounds, nextDisplay.Walk) is { } route)
                    return WanderAcross(position, bounds, speed * frame, icons, route);
                destinationDisplay = null;
                destination = null;
            }
        }
        if (!bounds.Contains(position))
            return MoveTowards(position, Clamp(position, bounds), 58 * frame);

        if (time < pauseUntil) return position;
        if (destination is { } last && (last - position).Length < 12)
        {
            destination = null;
            pauseUntil = time + 0.3 + random.NextDouble() * 0.8;
            return position;
        }
        if (destination is null || time >= pickAt || !bounds.Contains(destination.Value))
        {
            if (currentDisplay is { } source && displays is { Length: > 1 } && random.NextDouble() < 0.36)
            {
                var neighbours = displays.Where(display => display.Name != source.Name)
                    .Select(display => (Display: display, Route: FindCrossing(position,
                        source.Frame, display.Frame, bounds, display.Walk)))
                    .Where(choice => choice.Route.HasValue).ToArray();
                if (neighbours.Length != 0)
                {
                    var other = neighbours[random.Next(neighbours.Length)];
                    if (other.Route is { } route)
                    {
                        destinationDisplay = other.Display.Name;
                        destination = RandomPoint(other.Display.Walk);
                        return WanderAcross(position, bounds, speed * frame, icons, route);
                    }
                }
            }
            destination = RandomPoint(bounds);
            pickAt = time + 3.5 + random.NextDouble() * 5;
        }

        return WanderToward(position, destination.Value, bounds, speed * frame, icons);
    }

    private Point RandomPoint(Rect bounds) =>
        new(bounds.Left + random.NextDouble() * bounds.Width, bounds.Top + random.NextDouble() * bounds.Height);

    private static Point WanderAcross(Point position, Rect bounds, double step, Rect[] icons, Crossing route)
    {
        var aim = route.Aim(position);
        return aim == route.Entrance && bounds.Contains(position)
            ? WanderToward(position, aim, bounds, step, icons)
            : MoveTowards(position, aim, step);
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
