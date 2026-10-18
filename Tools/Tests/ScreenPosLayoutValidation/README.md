# Screen position layout validation

Run from the repository root with .NET SDK 9 or newer:

```powershell
dotnet run --project Tools/Tests/ScreenPosLayoutValidation/ScreenPosLayoutValidation.csproj
```

The harness links the production `ScreenPosCanvasLayoutInput` and
`ScreenPosCanvasLayout` sources from `Assets/Scripts/Misc/ScreenPos`. These are
the pure calculations that `ScreenPosCanvasLayoutProvider` runs on
`GlobalCanvas` and that `ScreenPosCanvasLayoutApplier` applies to every scene
canvas. `UnityStubs.cs` provides minimal stand-ins for `Vector2`, `Rect` and
`Mathf`; no NuGet packages are required.

Checks cover the reference resolution, canvas size and screen center for 9:16,
taller and wider screens; the `Main_Display` and `Sub_Display` positions with the
display transform disabled, at the default settings, with main display scaling
anchored at the screen center and with sub display offset and scale; the
`Sub_Cover`, `Sub_Cover_Bottom` and `Sub_Cover_Top` areas, including collapsing
to zero size instead of going negative; anchored position conversion for the
anchors used by `CanvasTemplate` and for stretched anchors; cover pivot
mirroring for negative scales; and value equality of the layout input.

Passing does not cover the Unity components themselves: finding the parts by
name, writing `RectTransform`/`CanvasScaler` values, execution order between the
provider and the appliers, or the one-frame delay before `CanvasScaler` applies a
new reference resolution. Check those in Play Mode.
