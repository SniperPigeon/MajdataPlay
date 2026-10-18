# Maj Render Pipeline library

This directory contains the reusable render pipeline implementation. Project
presets, integration notes, and validation history live in
[`Assets/Rendering/RenderPipelines/MajRenderPipeline`](../../../../../Rendering/RenderPipelines/MajRenderPipeline/README.md).

## Layout

- `Runtime/`: pipeline, camera renderer, attachment management, diagnostics,
  display resolution support, and the `MajdataPlay.MajRenderPipe` assembly.
- `Runtime/Shaders/`: the required `Hidden/Maj/FinalCopy` shader and the reusable
  `Maj/Unlit` shader. Keep their `.meta` files with the shaders.
- `Editor/`: Scene View support and the Unlit material inspector.
- `Library/`: shared HLSL includes, upstream source notes, and Unity license.

The runtime assembly does not depend on URP, Core RP, Live2D, or game scripts.
`Editor/MajdataPlay.MajRenderPipe.asmref` deliberately joins the editor sources
to that assembly: `MajCameraRenderer.Editor.cs` is a partial declaration of the
runtime camera renderer. Editor sources must retain their `UNITY_EDITOR` guards.

## Project integration

Assign a `MajRenderPipelineAsset` in Graphics or Quality settings. This repository
keeps its configured assets under
`Assets/Rendering/RenderPipelines/MajRenderPipeline/Presets/`.
The pipeline asset retains a serialized reference to `Hidden/Maj/FinalCopy`;
moving the shader must preserve its GUID.

Project shaders import shared HLSL from
`Assets/Plugins/MajdataPlay/Rendering/RenderPipelines/MajRenderPipeline/Library/`.
The include files use relative paths internally. Their provenance and license
are recorded in [SOURCE.md](Library/SOURCE.md) and
[LICENSE-Unity.md](Library/LICENSE-Unity.md).

## Validation

From the repository root:

```powershell
./Tools/Tests/RenderingLayoutValidation/Validate.ps1
dotnet run --project Tools/Tests/SettingValidation/SettingValidation.csproj
```

Use the Unity version in `ProjectSettings/ProjectVersion.txt` for import and
script compilation. Static layout checks and the managed settings harness do
not verify rendered pixels, Player behavior, or platform hardware.
