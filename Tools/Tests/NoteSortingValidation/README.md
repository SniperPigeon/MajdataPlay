# Gameplay note sorting validation

Run with the repository-pinned Unity Editor:

```powershell
./Tools/Tests/NoteSortingValidation/run.ps1
./Tools/Tests/NoteSortingValidation/run.ps1 -SkipRendering
```

The script stages the complete production `NoteHelper.cs`, its sensor/slide enums,
the repository sorting-layer table and this harness in the ignored isolated project
`Temp/NoteSortingValidation`. It never opens a scene or changes settings in the main
project. The two unused sensor extension symbols are narrow substitutes that throw;
sensor mapping and other `NoteHelper` methods are outside this check.

The checks cover all category bands, signed 16-bit boundaries, sampled long-chart
orders and int32 extremes; body/end/EX priorities through orders +/-65536; pooled
SpriteRenderer layer/order/pivot reset; world and local XY movement under a rotated,
scaled, translated parent; and preserving depth while a Touch fan moves.
Collapsed and expanded XY scales preserve each child's depth as well.
The serialized scan checks complete SpriteRenderer records in `Game.unity`, every
Game prefab and UI TouchFeedback, plus sorting overrides in prefab variants and the
scene. It also checks the Hold particle, RawSprite TouchHold border and three
migrated world canvases. Relevant gameplay and feedback animation files cannot
restore renderer sorting properties. The real imported animation Z curves and
`AnimationClip.SampleAnimation` on disposable transform hierarchies are checked at
31 points per binding: local position Z stays zero, local scale Z stays one and the
mapped root depth is preserved. The scan does not load the complete game or resolve
arbitrary prefab inheritance.

The default command creates two overlapping opaque sprites at different nonzero
XY positions and checks rendered pixels with a real orthographic camera using
`CustomAxis`/forward sorting. Their camera distances oppose their Z priority, and
swapping their depth categories must swap the visible color. Direct3D11 is required
on the host; `-SkipRendering` uses `-nographics` and leaves rendering unverified.

The helper returns floats. Adjacent priorities can share a depth at extreme orders;
the sampled int32 checks establish category isolation and broad ordering, not unique
depths for every integer. This harness does not validate the gameplay lifecycle,
Animator component integration, real chart playback, other graphics APIs, Player builds or
mobile devices. Success markers and the Editor exit code are checked; all generated
assets, assemblies and logs remain under `Temp/NoteSortingValidation`.

Local result (2026-10-16): Unity 6000.3.17f1 on Windows, Direct3D11,
AMD Radeon RX 580 2048SP passed the full command: 1,330 complete renderer records,
37 SpriteRenderer overrides across 71 scene/prefab files, and 26 animation clips.
The separate structural asset audit resolves inherited instances; its renderer
total can therefore differ from this direct-record count.
