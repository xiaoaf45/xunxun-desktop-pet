using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace XunXun;

enum Play { None, Pat, Feed, Fetch, Dance, Nap, Wake, Rub, HighFive, Drink, Stretch }
enum PointerPose { None, Sniff, Paw }
enum RoamTrick { None, Hop, Dribble, Kick }

readonly record struct RoamPose(RoamTrick Trick, double Progress);

sealed class RoamTricks(Random? random = null)
{
    private static readonly (RoamTrick Trick, double Duration)[] Sequence =
        [(RoamTrick.Hop, 1.55), (RoamTrick.Dribble, 2.5), (RoamTrick.Kick, 1.7)];
    private readonly Random random = random ?? Random.Shared;
    private int next;
    private double startsAt = 3.5;
    private double? activeSince;

    public RoamPose Update(double elapsed, bool free)
    {
        if (activeSince is { } started)
        {
            var (trick, duration) = Sequence[next];
            if (free && elapsed - started < duration)
                return new RoamPose(trick, (elapsed - started) / duration);

            next = (next + 1) % Sequence.Length;
            activeSince = null;
            startsAt = elapsed + 4 + random.NextDouble() * 4;
        }
        if (!free || elapsed < startsAt) return default;
        activeSince = elapsed;
        return new RoamPose(Sequence[next].Trick, 0);
    }
}

static class PlayTiming
{
    public static double Duration(this Play activity) => activity switch
    {
        Play.Pat => 1.05,
        Play.Feed => 1.75,
        Play.Fetch => 2.0,
        Play.Dance => 2.9,
        Play.Nap => double.PositiveInfinity,
        Play.Wake => 0.8,
        Play.Rub => 1.45,
        Play.HighFive => 1.15,
        Play.Drink => 2.25,
        Play.Stretch => 2.3,
        _ => 0
    };
}

sealed class PetArt : FrameworkElement
{
    private readonly Dictionary<string, Geometry> paths = new();
    private readonly Dictionary<string, Brush> brushes = new();
    private readonly Dictionary<(string, double), Pen> pens = new();
    private const string Ink = "#282744";
    private const string Cream = "#FFF4D9";
    private const string Shadow = "#D7C8BE";
    private const string Mint = "#8FF2D0";
    private const string Peach = "#FFAA78";
    private const string Purple = "#B8A6FC";

    public double Time { get; set; }
    public Play Activity { get; set; }
    public RoamPose RoamPose { get; set; }
    public bool IsBeingDragged { get; set; }
    public double PlayTime { get; set; }
    public bool FacingLeft { get; set; }
    public bool Walking { get; set; }
    public Vector LookAt { get; set; }
    public PointerPose PointerPose { get; set; }

    public PetArt()
    {
        Width = 136;
        Height = 122;
        Cursor = PetCursors.Open;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double progress = Activity == Play.Nap
            ? Math.Clamp(PlayTime / 0.45, 0, 1)
            : Math.Clamp(PlayTime / Math.Max(Activity.Duration(), 0.01), 0, 1);
        double beat = Math.Sin(PlayTime * 10);
        double reach = Math.Sin(progress * Math.PI);
        bool resting = Activity == Play.Nap;
        bool chewing = Activity == Play.Feed && progress is > 0.28 and < 0.78;
        bool walking = Walking || Activity == Play.Fetch;
        double wiggle = IsBeingDragged ? Math.Sin(Time * 23) : 0;
        double stride = IsBeingDragged ? 7 * wiggle : walking ? Math.Sin(Time * 10) * 4 : 0;
        double bounce = walking ? -Math.Abs(stride) : Math.Sin(Time * 3) * 1.5;
        var roam = Activity == Play.None && !IsBeingDragged ? RoamPose : default;
        double jump = roam.Trick == RoamTrick.Hop ? Math.Abs(Math.Sin(roam.Progress * Math.PI * 3)) : 0;
        double kick = roam.Trick == RoamTrick.Kick
            ? Math.Sin(Math.Clamp((roam.Progress - 0.05) / 0.5, 0, 1) * Math.PI) : 0;
        double eyeX = Math.Clamp(LookAt.X / 22, -4.5, 4.5) * (FacingLeft ? -1 : 1);
        double eyeY = Math.Clamp(LookAt.Y / 28, -2.2, 2.6);
        var (shift, lift, tilt, scale) = Activity switch
        {
            Play.Pat => (reach * -3, reach * 3, reach * -11, 1 - reach * 0.10),
            Play.Feed => (0.0, chewing ? -Math.Abs(beat) * 2 : -reach * 4, reach * 7, chewing ? 0.97 + beat * 0.026 : 1),
            Play.Fetch => (-reach * 24, -Math.Abs(beat) * 5, -beat * 6, 1.0),
            Play.Dance => (beat * 7, -Math.Abs(beat) * 11, beat * 15, 1.0),
            Play.Nap => (0.0, 2.0, -progress * 14, 1 - progress * (0.26 + Math.Sin(Time * 3) * 0.014)),
            Play.Wake => (0.0, -reach * 17, beat * 6 * reach, 1 + reach * 0.12),
            Play.Rub => (beat * reach * 4, -Math.Abs(beat) * 6, beat * reach * 10, 1 - reach * 0.06),
            Play.HighFive => (0.0, -reach * 8, reach * -8, 1.0),
            Play.Drink => (0.0, -reach * 3, reach * 5, 1.0),
            Play.Stretch => (0.0, -reach * 9, -reach * 5, 1 + reach * 0.17),
            Play.None when PointerPose == PointerPose.Sniff =>
                (eyeX * 0.6, Math.Sin(Time * 12) * 1.6, eyeX * 1.9, 1.0),
            _ => (0.0, 0.0, 0.0, 1.0)
        };
        lift -= jump * 17 + kick * 6;
        tilt += jump * Math.Sin(roam.Progress * Math.Tau) * 7 - kick * 8;
        scale += jump * 0.09;
        if (IsBeingDragged)
        {
            shift += 5 * wiggle;
            tilt += 12 * wiggle;
            lift -= 4 + Math.Abs(wiggle) * 4;
        }
        Oval(dc, 70, 108, IsBeingDragged ? 28 : 42, 6, "#33262541");

        if (FacingLeft) dc.PushTransform(new ScaleTransform(-1, 1, 68, 0));
        dc.PushTransform(new TranslateTransform(shift, bounce + lift));
        dc.PushTransform(new RotateTransform(tilt, 74, 91));
        dc.PushTransform(new ScaleTransform(1, scale, 74, 98));

        // Tail, boots, and the two long ears give the courier its silhouette.
        double wag = Activity is Play.Pat or Play.Rub ? Math.Sin(PlayTime * 25) * 25 : Math.Sin(Time * 5) * 12;
        dc.PushTransform(new RotateTransform(resting ? 1 : wag, 35, 84));
        Shape(dc, "M40,82 C24,89 12,81 14,72 C15,65 24,68 29,71 L43,73 Z", Purple, Ink, 2.4);
        dc.Pop();
        Oval(dc, 51 + stride, 99 - Math.Max(0, wiggle) * 8, 13, 7, Shadow, Ink, 2.4);
        Oval(dc, 85 - stride + kick * 18, 99 - kick * 8 + Math.Min(0, wiggle) * 8, 14, 7, Cream, Ink, 2.4);
        Shape(dc, "M36,71 C37,58 52,49 71,49 C98,49 111,66 108,84 C106,100 90,103 65,102 C44,102 32,91 36,71 Z", Cream, Ink, 2.6);
        Shape(dc, "M36,78 C46,91 84,98 105,89 C101,98 86,102 65,101 C48,101 36,93 36,78 Z", Shadow);
        dc.PushTransform(new RotateTransform(wiggle * 15, 48, 47));
        Shape(dc, "M44,54 C34,45 27,18 34,11 C41,4 53,19 60,37 L61,53 Z", Cream, Ink, 2.5);
        Shape(dc, "M44,44 C39,35 34,20 36,17 C41,13 48,26 51,37 Z", Peach);
        dc.Pop();
        dc.PushTransform(new RotateTransform(-wiggle * 17, 83, 47));
        Shape(dc, "M73,46 C77,30 89,7 98,11 C108,15 101,38 91,50 Z", Cream, Ink, 2.5);
        Shape(dc, "M83,40 C89,24 96,16 99,17 C101,21 97,34 91,41 Z", Peach);
        dc.Pop();
        Shape(dc, "M35,59 C39,42 55,34 76,35 C99,36 113,49 115,65 C117,83 101,93 76,93 C49,93 31,81 35,59 Z", Cream, Ink, 2.8);
        Shape(dc, "M47,57 C54,47 66,44 80,45 C97,46 107,53 108,66 C110,78 96,85 76,84 C58,84 45,77 45,67 C45,64 46,60 47,57 Z", Ink);
        Shape(dc, "M52,58 C58,50 71,48 84,50", null, "#565779", 1.7);
        Oval(dc, 105, 80, 4, 2, Peach);
        Oval(dc, 40, 76, 3, 2, Peach);

        bool blink = Time % 4.7 > 4.55;
        if (IsBeingDragged)
        {
            Oval(dc, 63, 66, 4.5, 6.5, Mint);
            Oval(dc, 91, 66, 4.5, 6.5, Mint);
        }
        else if (resting || blink && Activity == Play.None)
        {
            Shape(dc, "M58,66 Q63,72 68,66 M85,66 Q90,72 95,66", null, Mint, 2.5);
        }
        else if (Activity is Play.Pat or Play.Dance or Play.Wake or Play.Rub or Play.HighFive or Play.Stretch || chewing)
        {
            Shape(dc, "M58,67 Q63,58 69,67 M85,67 Q91,58 96,67", null, Mint, 2.8);
        }
        else
        {
            bool looks = Activity == Play.None;
            double x = looks ? eyeX : 0;
            double y = looks ? eyeY : 0;
            Oval(dc, 63 + x, 66 + y, 3.5, 5, Mint);
            Oval(dc, 91 + x, 66 + y, 3.5, 5, Mint);
        }
        if (IsBeingDragged)
            Oval(dc, 77, 75, 3.1, 2.5 + Math.Abs(wiggle) * 1.8, Mint);
        else if (chewing)
            Oval(dc, 77, 74, 3.6, 2 + Math.Abs(beat) * 3, Mint);
        else if (PointerPose == PointerPose.Sniff && Activity == Play.None)
            Oval(dc, 77 + eyeX * 0.7, 74 + Math.Sin(Time * 12), 2.6, 1.8, Mint);
        else
            Shape(dc, "M73,74 Q77,78 82,74", null, Mint, 1.8);
        if (Activity == Play.Pat)
        {
            Oval(dc, 56, 75, 3.5, 1.8, Peach);
            Oval(dc, 96, 75, 3.5, 1.8, Peach);
        }
        dc.Pop();
        dc.Pop();
        dc.Pop();
        DrawPlay(dc, progress, reach);
        DrawRoamTrick(dc, roam);
        if (IsBeingDragged)
            Shape(dc, "M12,66 L21,70 M10,78 L19,78 M120,53 L126,47 M122,63 L131,60",
                null, "#FFBE91", 2.2);
        if (Activity == Play.None && PointerPose == PointerPose.Paw)
            DrawPaw(dc, 106, 79 + Math.Sin(Time * 7) * 3, -15);
        if (FacingLeft) dc.Pop();
    }

    private void DrawRoamTrick(DrawingContext dc, RoamPose pose)
    {
        switch (pose.Trick)
        {
            case RoamTrick.Dribble:
                double beat = Math.Abs(Math.Sin(pose.Progress * Math.PI * 4));
                DrawPaw(dc, 111, 68 + (1 - beat) * 10, -25);
                Oval(dc, 122, 105, 9 - beat * 3, 2, "#3D282744");
                DrawBall(dc, 122, 97 - beat * 30, "#FFA26E");
                break;
            case RoamTrick.Kick:
                double flight = Math.Clamp((pose.Progress - 0.3) / 0.6, 0, 1);
                dc.PushOpacity(1 - Math.Clamp((pose.Progress - 0.76) / 0.22, 0, 1));
                DrawBall(dc, 108 + flight * 24, 95 - Math.Sin(flight * Math.PI) * 31, "#B9F5D8");
                dc.Pop();
                break;
        }
    }

    private void DrawBall(DrawingContext dc, double x, double y, string color)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        Oval(dc, 0, 0, 8, 8, color, Ink, 1.7);
        Shape(dc, "M-7,1 Q-1,-3 7,0 M-2,-7 Q3,-1 2,7", null, Ink, 1.1);
        dc.Pop();
    }

    private void DrawPlay(DrawingContext dc, double progress, double reach)
    {
        switch (Activity)
        {
            case Play.Pat:
                Heart(dc, 18 - progress * 10, 48 - progress * 35, 0.64, reach);
                Heart(dc, 78, 28 - progress * 23, 0.60, reach);
                break;
            case Play.Rub:
                Heart(dc, 27 - progress * 14, 44 - progress * 32, 0.72, reach);
                Heart(dc, 93 + progress * 16, 39 - progress * 32, 0.67, reach);
                Heart(dc, 68, 27 - progress * 24, 0.58, reach);
                break;
            case Play.HighFive:
                var (paw, angle) = HighFivePaw(reach);
                DrawPaw(dc, paw.X, paw.Y, angle);
                dc.PushOpacity(reach);
                Shape(dc, "M122,27 L125,34 L132,37 L125,40 L122,47 L119,40 L112,37 L119,34 Z", "#FFD398");
                Oval(dc, 103, 26, 2, 2, Mint);
                dc.Pop();
                break;
            case Play.Feed when progress < 0.32:
                double flight = progress / 0.32;
                dc.PushTransform(new TranslateTransform(124 - flight * 47, 104 - flight * 29 - Math.Sin(flight * Math.PI) * 28));
                dc.PushTransform(new RotateTransform(-75 + flight * 235));
                Shape(dc, "M-5,-7 Q0,-11 5,-7 Q8,-3 0,12 Q-2,14 -6,0 Z", "#FFA754", Ink, 1.4);
                Shape(dc, "M0,-8 Q-8,-10 -6,-16 Q-1,-17 1,-10 Q2,-19 6,-17 Q8,-11 2,-8 Z", Mint, Ink, 1.2);
                Shape(dc, "M-4,-1 L0,1 M1,4 L3,3", null, "#D77543", 1.3);
                dc.Pop();
                dc.Pop();
                break;
            case Play.Feed when progress < 0.78:
                double chew = (progress - 0.32) / 0.46;
                dc.PushOpacity(1 - chew);
                Oval(dc, 70 - chew * 14, 76 + chew * 5, 2.1, 2.1, Peach);
                Oval(dc, 83 + chew * 16, 73 - chew * 6, 2.5, 2.5, "#FFC185");
                Oval(dc, 85 + chew * 12, 80 + chew * 11, 1.8, 1.8, Peach);
                dc.Pop();
                break;
            case Play.Feed:
                double full = (progress - 0.78) / 0.22;
                Heart(dc, 79, 33 - full * 16, 0.65, Math.Sin(full * Math.PI));
                break;
            case Play.Fetch:
                double outbound = Math.Min(progress / 0.42, 1);
                double back = Math.Clamp((progress - 0.57) / 0.43, 0, 1);
                double x = 104 - 84 * outbound + 55 * back;
                double y = 99 - Math.Sin(Math.PI * outbound) * 36 - back * 18;
                Oval(dc, x, y, 7.5, 7.5, "#A4E7FF", Ink, 1.7);
                Oval(dc, x - 2.2, y - 2.5, 2.5, 1.6, "#EAFCFF");
                break;
            case Play.Dance:
                DrawNote(dc, 21, 46 - (PlayTime * 20 % 30), Purple);
                DrawNote(dc, 103, 30 - ((PlayTime + 0.5) * 16 % 24), Mint);
                break;
            case Play.Nap:
                DrawSleepMark(dc, 103, 38 - (PlayTime * 6 % 16), 1);
                DrawSleepMark(dc, 120, 18 - ((PlayTime + 0.5) * 4 % 10), 0.7);
                break;
            case Play.Wake:
                Heart(dc, 77, 30 - progress * 20, 0.55, reach);
                break;
            case Play.Drink:
                double cupY = 83 - reach * 16;
                DrawPaw(dc, 96, cupY + 4, -20);
                dc.PushTransform(new TranslateTransform(112 - reach * 15, cupY));
                dc.PushTransform(new RotateTransform(-reach * 15));
                Shape(dc, "M-10,-10 L-7,9 Q0,13 8,9 L10,-10 Z", "#B9EFFF", Ink, 1.7);
                Shape(dc, "M-7,1 Q-1,-3 7,0 L6,8 Q0,11 -6,8 Z", "#68C9F0");
                Oval(dc, 0, -10, 10, 2.8, "#E4FAFF", Ink, 1.5);
                dc.Pop();
                dc.Pop();
                break;
            case Play.Stretch:
                DrawPaw(dc, 45 - reach * 22, 83 - reach * 38, -20 - reach * 35);
                DrawPaw(dc, 99 + reach * 19, 83 - reach * 39, 25 + reach * 34);
                dc.PushOpacity(reach);
                Shape(dc, "M18,28 L20,18 M12,34 L5,30 M121,28 L127,19 M126,34 L134,31",
                    null, "#FFD398", 2);
                dc.Pop();
                break;
        }
    }

    internal Point HighFiveContact(double elapsed)
    {
        double progress = Math.Clamp(elapsed / Play.HighFive.Duration(), 0, 1);
        var (paw, angle) = HighFivePaw(Math.Sin(progress * Math.PI));
        var rotation = Matrix.Identity;
        rotation.Rotate(angle);
        var pad = paw + rotation.Transform(new Vector(1, 6));
        return FacingLeft ? new Point(136 - pad.X, pad.Y) : pad;
    }

    private static (Point Center, double Angle) HighFivePaw(double reach) =>
        (new Point(107 + reach * 9, 84 - reach * 36), -15 - reach * 19);

    private void DrawPaw(DrawingContext dc, double x, double y, double rotation)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new RotateTransform(rotation));
        Shape(dc, "M-8,12 C-14,5 -13,-6 -7,-8 C-6,-14 -1,-13 1,-9 C6,-13 11,-9 11,-3 C16,2 14,11 7,14 Q0,18 -8,12 Z",
            Cream, Ink, 2);
        Oval(dc, 1, 6, 4.4, 3.5, Peach);
        dc.Pop();
        dc.Pop();
    }

    private void Heart(DrawingContext dc, double x, double y, double scale, double opacity)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.PushOpacity(opacity);
        Shape(dc, "M0,12 C-19,-1 -9,-12 0,-6 C9,-12 19,-1 0,12 Z", "#FFAAA8", Ink, 1.5);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawNote(DrawingContext dc, double x, double y, string color)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        Shape(dc, "M2,5 L2,-10 Q8,-12 9,-6", null, color, 2.5);
        Oval(dc, -1, 5, 4, 2.6, color);
        dc.Pop();
    }

    private void DrawSleepMark(DrawingContext dc, double x, double y, double scale)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        Shape(dc, "M-5,-5 L5,-5 L-5,5 L5,5", null, "#DFD0FF", 2.2);
        dc.Pop();
        dc.Pop();
    }

    private Brush Paint(string color)
    {
        if (brushes.TryGetValue(color, out var brush)) return brush;
        brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brushes[color] = brush;
    }

    private Pen Stroke(string color, double width)
    {
        if (pens.TryGetValue((color, width), out var pen)) return pen;
        pen = new Pen(Paint(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pens[(color, width)] = pen;
    }

    private void Shape(DrawingContext dc, string data, string? fill, string? line = null, double width = 1)
    {
        if (!paths.TryGetValue(data, out var path))
        {
            path = Geometry.Parse(data);
            path.Freeze();
            paths.Add(data, path);
        }
        dc.DrawGeometry(fill is null ? null : Paint(fill), line is null ? null : Stroke(line, width), path);
    }

    private void Oval(DrawingContext dc, double x, double y, double rx, double ry,
        string fill, string? line = null, double width = 1) =>
        dc.DrawEllipse(Paint(fill), line is null ? null : Stroke(line, width), new Point(x, y), rx, ry);
}
