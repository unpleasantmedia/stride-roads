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
    public static readonly Color4 Bank = new(0.16f, 0.24f, 0.09f, 1f);

    private sealed class Set
    {
        public Material Asphalt = null!, WhiteLine = null!, YellowLine = null!, Sidewalk = null!, Bank = null!;
    }

    private static readonly ConditionalWeakTable<GraphicsDevice, Set> Sets = new();

    public static Material DefaultAsphalt(GraphicsDevice device) => For(device).Asphalt;
    public static Material DefaultWhiteLine(GraphicsDevice device) => For(device).WhiteLine;
    public static Material DefaultYellowLine(GraphicsDevice device) => For(device).YellowLine;
    public static Material DefaultSidewalk(GraphicsDevice device) => For(device).Sidewalk;
    public static Material DefaultBank(GraphicsDevice device) => For(device).Bank;

    private static Set For(GraphicsDevice device) => Sets.GetValue(device, d => new Set
    {
        // Asphalt and paint a little glossy, concrete and grass dull.
        Asphalt = Make(d, Asphalt, 0.35f),
        WhiteLine = Make(d, WhiteLine, 0.4f),
        YellowLine = Make(d, YellowLine, 0.4f),
        Sidewalk = Make(d, Sidewalk, 0.15f),
        Bank = Make(d, Bank, 0.05f),
    });

    private static Material Make(GraphicsDevice device, Color4 color, float gloss) => Material.New(device, new MaterialDescriptor
    {
        Attributes =
        {
            Diffuse = new MaterialDiffuseMapFeature(new ComputeColor(color)),
            DiffuseModel = new MaterialDiffuseLambertModelFeature(),
            MicroSurface = new MaterialGlossinessMapFeature(new ComputeFloat(gloss)),
            Specular = new MaterialMetalnessMapFeature(new ComputeFloat(0f)),
            SpecularModel = new MaterialSpecularMicrofacetModelFeature(),
        },
    });
}
