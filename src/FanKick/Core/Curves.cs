using System.Text.Json.Serialization;

namespace FanKick.Core;

public record struct CurvePoint(float Temp, float Percent);

// A named rule that turns readings into a fan %. Fans point at a curve by name, so one curve can drive many fans.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GraphCurve), "graph")]
[JsonDerivedType(typeof(FlatCurve), "flat")]
[JsonDerivedType(typeof(MixCurve), "mix")]
public abstract class Curve
{
    public string Name { get; set; } = "";
    // The % to run at, or null when a sensor it needs has no reading (the engine then fails safe).
    public abstract float? Evaluate(Func<string, float> read, Func<string, Curve?> find, int depth = 0);
}

// Temperature -> % through points, straight lines between them. Hysteresis: rises are followed at once, falls only once
// the sensor is Hysteresis degrees under the temperature last used, so the fans don't hunt around one point.
public sealed class GraphCurve : Curve
{
    public string Sensor { get; set; } = Engine.Hottest;
    public List<CurvePoint> Points { get; set; } = [];
    public float Hysteresis { get; set; } = 3;
    float? used;

    public static float Interpolate(IReadOnlyList<CurvePoint> pts, float t)
    {
        if (pts.Count == 0) return 100;
        if (t <= pts[0].Temp) return pts[0].Percent;
        for (int i = 1; i < pts.Count; i++)
            if (t <= pts[i].Temp)
            {
                var (a, b) = (pts[i - 1], pts[i]);
                return b.Temp <= a.Temp ? b.Percent : a.Percent + (t - a.Temp) * (b.Percent - a.Percent) / (b.Temp - a.Temp);
            }
        return pts[^1].Percent;
    }

    public override float? Evaluate(Func<string, float> read, Func<string, Curve?> find, int depth = 0)
    {
        float t = read(Sensor);
        if (float.IsNaN(t)) { used = null; return null; }
        if (used is not { } u || t > u || t <= u - Hysteresis) used = t;
        return Interpolate(Points, used.Value);
    }
}

public sealed class FlatCurve : Curve
{
    public float Percent { get; set; } = 50;
    public override float? Evaluate(Func<string, float> read, Func<string, Curve?> find, int depth = 0) => Percent;
}

public enum MixFunction { Max, Min, Average }

// Several curves combined, e.g. the max of a CPU curve and a GPU curve for a case fan. Any member without a reading
// makes the whole mix fail safe rather than quietly following the others.
public sealed class MixCurve : Curve
{
    public MixFunction Function { get; set; } = MixFunction.Max;
    public List<string> Curves { get; set; } = [];

    public override float? Evaluate(Func<string, float> read, Func<string, Curve?> find, int depth = 0)
    {
        if (depth > 8 || Curves.Count == 0) return null; // a mix that contains itself
        var v = new List<float>();
        foreach (var name in Curves)
        {
            if (find(name)?.Evaluate(read, find, depth + 1) is not { } p) return null;
            v.Add(p);
        }
        return Function switch { MixFunction.Min => v.Min(), MixFunction.Average => v.Average(), _ => v.Max() };
    }
}

public static class Presets
{
    public static readonly string[] Names = ["Silent", "Balanced", "Performance", "Full"];

    // All on the hottest CPU/GPU temperature, so they work on any machine before the user picks sensors.
    public static Curve Build(string name) => name switch
    {
        "Silent" => Graph(name, (40, 20), (60, 30), (70, 45), (80, 70), (90, 100)),
        "Balanced" => Graph(name, (40, 30), (55, 40), (65, 55), (75, 75), (85, 100)),
        "Performance" => Graph(name, (35, 40), (50, 55), (60, 70), (70, 85), (80, 100)),
        _ => new FlatCurve { Name = "Full", Percent = 100 },
    };

    static GraphCurve Graph(string name, params (float t, float p)[] pts) =>
        new() { Name = name, Points = pts.Select(x => new CurvePoint(x.t, x.p)).ToList() };
}
