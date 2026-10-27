# Stride Roads

Roads through points for the Stride engine, built in Game Studio's scene editor as well as the game. A port of a Flax Engine roads tool.

- `Stride.Roads`: the runtime package: the road maths (`Maths/`), `RoadComponent` (lanes, shoulders, median, sidewalks, banks, materials), `RoadPointComponent`, and `RoadProcessor`, which rebuilds a road's model whenever its points or settings change, in the editor too.
- `Stride.Roads.Editor`: the Game Studio companion: the road gizmo.
- `Stride.Roads.Tests`: tests of the road maths, without the engine.

## Using it

This repository is a submodule of the [unpleasantmedia/stride](https://github.com/unpleasantmedia/stride) fork at `sources/plugins/Roads`, and builds only there: its projects reference the engine by relative path. Building Game Studio builds and packs `Stride.Roads` and `Stride.Roads.Editor` with the engine's version.

A game uses it like the engine's own plugins:

```xml
<PackageReference Include="Stride.Roads" Version="4.5.0-dev" />
```

Add an entity with a Road component, and give it child entities as its points, in order. Move the points to shape the road.
