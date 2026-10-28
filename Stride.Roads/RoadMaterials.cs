using System.Runtime.CompilerServices;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Materials;
using Stride.Rendering.Materials.ComputeColors;

namespace Stride.Roads;

/// <summary>
/// The materials a road is drawn with where it has none of its own set: plain colours, made in code (not assets), once
/// per graphics device. Their shaders are compiled the first time they're drawn.
/// </summary>
public static class RoadMaterials
{
    public static readonly Color4 Asphalt = new(0.045f, 0.045f, 0.05f, 1f);
    public static readonly Color4 WhiteLine = new(0.85f, 0.85f, 0.85f, 1f);
    public static readonly Color4 YellowLine = new(0.9f, 0.62f, 0.05f, 1f);
    public static readonly Color4 Sidewalk = new(0.42f, 0.41f, 0.39f, 1f);
    public static readonly Color4 Concrete = new(0.5f, 0.49f, 0.46f, 1f);
    public static readonly Color4 Steel = new(0.55f, 0.57f, 0.6f, 1f);
    public static readonly Color4 SignRed = new(0.7f, 0.04f, 0.04f, 1f);
    public static readonly Color4 SignalBlack = new(0.02f, 0.02f, 0.02f, 1f);
    public static readonly Color4 LampAmber = new(1f, 0.6f, 0.05f, 1f);
    public static readonly Color4 LampGreen = new(0.1f, 1f, 0.35f, 1f);

    private sealed class Set
    {
        public Material Asphalt = null!, WhiteLine = null!, YellowLine = null!, Sidewalk = null!, Concrete = null!, Steel = null!, SignRed = null!, SignalBlack = null!, LampAmber = null!, LampGreen = null!;
    }

    private static readonly ConditionalWeakTable<GraphicsDevice, Set> Sets = new();

    public static Material DefaultAsphalt(GraphicsDevice device) => For(device).Asphalt;
    public static Material DefaultWhiteLine(GraphicsDevice device) => For(device).WhiteLine;
    public static Material DefaultYellowLine(GraphicsDevice device) => For(device).YellowLine;
    public static Material DefaultSidewalk(GraphicsDevice device) => For(device).Sidewalk;
    public static Material DefaultConcrete(GraphicsDevice device) => For(device).Concrete;
    public static Material DefaultSteel(GraphicsDevice device) => For(device).Steel;
    public static Material DefaultSignRed(GraphicsDevice device) => For(device).SignRed;
    public static Material DefaultSignalBlack(GraphicsDevice device) => For(device).SignalBlack;
    public static Material DefaultLampAmber(GraphicsDevice device) => For(device).LampAmber;
    public static Material DefaultLampGreen(GraphicsDevice device) => For(device).LampGreen;

    private static Set For(GraphicsDevice device) => Sets.GetValue(device, d => new Set
    {
        // Paint a little glossy; asphalt and concrete dull. Banks are drawn as asphalt, where a road has nothing else for them.
        Asphalt = Make(d, Asphalt, 0.1f),
        WhiteLine = Make(d, WhiteLine, 0.4f),
        YellowLine = Make(d, YellowLine, 0.4f),
        Sidewalk = Make(d, Sidewalk, 0.15f),
        Concrete = Make(d, Concrete, 0.1f),
        Steel = Make(d, Steel, 0.6f, metal: true),
        SignRed = Make(d, SignRed, 0.4f),
        SignalBlack = Make(d, SignalBlack, 0.3f),
        LampAmber = Make(d, LampAmber, 0.8f),
        LampGreen = Make(d, LampGreen, 0.8f),
    });

    private static Material Make(GraphicsDevice device, Color4 color, float gloss, bool metal = false) => Material.New(device, new MaterialDescriptor
    {
        Attributes =
        {
            Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(color)),
            DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(gloss)),
            Specular = new MaterialMetalnessMapFeature(new ComputeFloat(metal ? 1f : 0f)),
            SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
        },
    });
}
