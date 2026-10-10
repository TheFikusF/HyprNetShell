# Typed shader programs

GLSL files are `AdditionalFiles` in the Rendering project. The incremental
`ShaderGenerator` embeds their exact text into generated code; there is no runtime
resource lookup, reflection, or dynamic code generation (NativeAOT safe).

Declare a top-level, non-generic, non-static partial class without a base class or
constructor:

```csharp
using System.Numerics;

[Shader("Shaders/textured.vert.glsl", "Shaders/spherical-image.frag.glsl")]
internal sealed partial class ExampleShader
{
    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }

    [Uniform("uHasImage")]
    public partial bool HasImage { get; set; }
}
```

Construct with `new ExampleShader(gl)` on the GL-context thread. The generated
class owns its linked program, but borrows `GL`; call `Dispose()` before destroying
the context. Disposal is idempotent and unbinds this program if currently bound.
`Bind()` explicitly selects the program for drawing. `Id` is available for
low-level GL operations; renderer texture batches retain the typed shader instance
rather than a copied program ID or uniform locations. Do not declare constructors
or shadow the ownership members.

## Uniform semantics

| C# | GLSL |
| --- | --- |
| `float` | `float` |
| `int` | `int`, `sampler2D` (texture unit index, not texture object ID) |
| `bool` | `bool` |
| `System.Numerics.Vector2/3/4` | `vec2/3/4` |
| `float[]` | `float[N]` |
| `System.Numerics.Vector2/3/4[]` | `vec2/3/4[N]` |

Properties must be instance partial properties with ordinary `get; set;` accessors
and public, internal, or private accessibility. Each GLSL uniform may be mapped
once per class. Names are exact and case-sensitive. Uniform locations are queried
once after linking, with no per-frame dictionary lookup.

Getters **read the actual linked program's GL uniform state**, without changing
the current binding. Setters immediately upload to their owning program and
restore the previous program in `finally`; callers need not bind first. All access
requires the owning context to be current on the calling thread. Access after
disposal throws. Optimized-out uniforms (location `-1`) read as zero/default and
ignore writes. Getters synchronize with GL and should not be used as a hot-path
application-state cache.

Generated internal `PropertyNameLocation` accessors are available for low-level
uploads, which must target the correct bound program. Renderer batch preparation
uses typed properties instead of retaining duplicate location fields. Setters do
not flush renderer batches: flush pending geometry **before** changing per-draw
uniforms, then flush that draw before subsequent changes. Sphere, rounded-image,
and image-shadow draws retain these boundaries.

## Texture batch preparation

Textured renderer shaders implement `ITextureBatchShader`. At flush time,
`PrepareTextureBatch(viewport)` binds the owning program, sets the viewport, and
selects sampler unit zero when the shader has a sampler. Texture and SVG shaders
also enable vertex colors. Crystal spheres have no sampler and only bind and set
the viewport. Preparation must not overwrite per-draw uniforms such as sphere
parameters, corner radius, or blur step.

The renderer owns geometry, rotation, UV padding, texture bindings, uploads, and
batch boundaries. Batches are keyed by texture ID and shader instance; preparation
runs immediately before submission, not when geometry is queued. `FontRenderer`
uses its own typed shader instances and uniform-color path rather than the
renderer texture-batch preparation that enables vertex colors.

Fixed-size float/vector arrays use ordinary typed partial properties, for example
`[Uniform("uGradientColors")] public partial Vector4[] GradientColors { get; set; }`.
The capacity must be a positive integer literal or a same-stage `const int` initialized
with an integer literal (such as `MAX_GRADIENT_STOPS`). Stage declarations must agree
on both element type and capacity. Multidimensional arrays and integer/bool arrays
are not supported.

Array setters reject null and lengths exceeding capacity, including for optimized-out
uniforms. They perform one bulk upload with the supplied array length, leaving the
remaining elements unchanged; empty arrays do nothing. Getters allocate a full-capacity
array and query each element's cached location separately: `GetUniform` does not read
an entire array from its first location. Inactive elements read as default values.
Rounded gradients use these generated setters with exactly the current stop count,
at their existing batch boundary. This path allocates two typed arrays per gradient
upload instead of the former stack buffers.

Matrices, uniform blocks, structs, and preprocessor-dependent declarations are not
supported as typed properties. The generator validates simple standalone
`uniform TYPE NAME;` and `uniform TYPE NAME[N];` declarations in both stages, ignoring
comments; OpenGL remains the authority for GLSL syntax/link validation. Shader visual
code is unchanged.

Diagnostics: `HNGL001` invalid class/property ownership declarations, `HNGL002`
missing/ambiguous/unreadable AdditionalFiles, `HNGL003` invalid property accessors,
missing uniforms, duplicate mappings, unsupported/mismatched types, or conflicting
stage declarations. Paths use exact matches or path-segment suffixes and must
identify exactly one AdditionalFile.
