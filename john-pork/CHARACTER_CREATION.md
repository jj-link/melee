# Creating a Melee-compatible character: John Pork

This records the actual authoring process, not just the commands needed to rebuild an existing asset. The committed art inputs let the model be rebuilt without rerunning AI generation. The original reference photograph, Nintendo files, model weights, and workstation-specific generation environment are not distributed.

## What was created, and what was reused

John Pork is a hybrid model:

- Generated pig-head geometry and its texture atlas.
- Script-authored eyes, nostrils, plaid clothing, jeans, and shoe textures.
- Luigi's original body geometry, skeleton, animation weights, and native hand variants.
- Luigi's animations and moveset. A different appearance does not create new attacks or a new skeleton.

The initial implementation replaced the default green costume, `PlLgNr.dat`. Its separate ISO and `Play John Pork.cmd` launcher are retained as the working baseline.

## Reference art and 3D generation

The reference was the John Pork character, with a bald pink-tan pig head, drooping ears, human eyes and body, an open dark-red/black plaid overshirt, gray T-shirt, jeans, and dark sneakers. A neutral A-pose and a readable GameCube-era silhouette were explicit goals.

The original art-generation run used:

| Setting | Value |
| --- | --- |
| Image model | `black-forest-labs/FLUX.2-klein-4B` |
| Image size | 768 × 1024 |
| Inference steps / guidance | 4 / 1.0 |
| Front / side / back seeds | 14091 / 14092 / 14093 |
| Precision and memory mode | bfloat16, model CPU offload, VAE tiling |
| Hardware | RTX 4090, explicitly selected for the generation process |

The front image was generated first. The side and back requests used both the original reference and the generated front view to encourage consistent proportions and clothing. The resulting inputs are `art/john-pork-{front,side,back}.png`.

Pixal3D, through ComfyUI's native single-image reconstruction workflow, converted the **front image** into `art/john-pork-generated.glb`. That model was unrigged: 17,914 triangles, 14,428 vertices, one mesh, and three embedded textures. It was not a playable fighter. The generated orthographic previews are also retained under `art/`.

Generation is an authoring step, not a prerequisite for rebuilding the checked-in model. Different model revisions, software versions, or input art need fresh visual review; a seed alone is not a guarantee of identical geometry.

## Extract the authoritative bind rig

`importer/Program.cs` provides `export-rig`. It reads the user's original Luigi costume through HSDLib and exports the original geometry, joint hierarchy, and inverse-bind transforms to the ignored `importer/rig.json`.

The stored inverse binds are authoritative. Reconstructing a visually similar skeleton and guessing its transforms can produce a model that looks correct in Blender but collapses or deforms incorrectly during Melee animations.

## Prepare the generated head

`tools/rig_generated_character_blender.py` performs the repeatable Blender conversion:

1. Import the generated GLB and apply its object transforms.
2. Weld coincident vertices and cut away the generated body and raised shirt collar.
3. Remove remaining garment-colored faces below the jaw without removing the pink skin.
4. Decimate toward a 2,800-triangle head budget and use smooth normals.
5. Resize the head atlas to 512 × 512.
6. Transform the head into the original bind pose and attach its vertices to **Luigi joint 23**.
7. Export the mesh, normals, UVs, joint assignment, and coordinate transform as `character/john-pork-generated-head.json`.

The scale, cut planes, color threshold, and attachment joint in that script are specific to this generated head and Luigi's rig. They are not universal settings for another character.

## Build clothing and facial details

`tools/build_character.py` preserves the original body vertex positions and animation weights while assigning new materials and UV coordinates. It creates the plaid overshirt, gray T-shirt opening, jeans, and sneaker textures with a fixed NumPy random seed (`260910`).

The playable archive retains the native hand display objects and their open/fist/low-detail visibility variants. Replacing them with a single always-visible hand mesh would lose the original animation behavior.

The generated face had dark, recessed eyes. The script adds eye whites, brown irises, pupils, small highlights, and nostril surfaces. These share the head joint; no new facial bones or blinking animation were created.

The result is `character/john-pork-mesh.json`, plus generated textures. Both are derived build outputs and remain out of Git. `tools/render_character_blender.py` can produce an editable preview and render it, but is not required to build the playable costume.

## Write a native costume archive

`importer/MeshImporter.cs` converts the authored mesh back into Melee's HSD display objects, materials, texture formats, and skinning data. The original costume supplies the compatible joint structure and preserved native hand objects.

The resulting costume is a model asset. Fighter registration, gameplay callbacks, animations, and character-select registration are separate concerns; a renamed DAT alone does not create another roster slot.

## Identity assets are separate from the model

The original cleanup required independent replacements for all of these:

| Surface | Source/output size |
| --- | --- |
| Character-select portrait | 136 × 188 RGBA PNG |
| Roster tile | 64 × 56 |
| HUD/results stock head | 24 × 24 |
| Large winner name | 256 × 28, native intensity texture |
| Results-card name | 120 × 24, native intensity texture |
| Character-select text | Separate name data, not part of the portrait |

`tools/build_ui.py` extracts and identifies the original UI textures. It constructs the small head from the portrait, builds the roster tile, and composes JOHN PORK from the game's existing bitmap letter shapes. `importer/UiTextures.cs` repoints the matching texture references.

For the original replacement build, Luigi is external character ID 7, the main portrait is bank 1/frame 7, and two executable name strings are patched in place. Those replacement-specific indices must not be reused to label a new fighter while leaving Luigi intact.

Melee's fixed preload heaps matter. Keeping superseded images in an archive caused a real allocation failure at character select. The UI importer now removes unused data and deduplicates buffers when saving. A file that opens in an editor is not proof that it fits the game's runtime memory budget.

## Register an additional fighter

The expanded build is driven by `tools/build_custom_smash.py`. Follow the [expanded-roster build instructions](../.github/README.md#build-the-expanded-roster) for the prerequisites and command. It starts from a verified, unmodified NTSC-U 1.02 disc, not from the older Luigi-replacement ISO.

The `roster/` builder uses the pinned m-ex source and checksum-verified runtime resources:

- `JohnPorkFighter.cs` clones Luigi's fighter configuration into a new entry before the non-roster special fighters. John Pork receives internal ID **27** and external ID **26**; original Luigi stays at **17/7**. The costume is installed under its own archive name rather than overwriting `PlLgNr.dat`.
- The new entry owns its portrait, character-select tile and name, and stock head. `tools/build_ui.py --assets-only` generates the new images without applying the old Luigi-slot replacements.
- `ResultNames.cs` adds the new winner and results-card name frames. The optional m-ex “Skip Result Screen” code is disabled so those surfaces remain visible.
- `KirbyClone.cs` provides a separate cap archive and native callback adapter for the inherited Luigi copy ability. The adapter handles the original callbacks' Luigi-specific lookup and fireball registration; copying the fighter metadata alone is not enough. These PowerPC addresses and instructions are specific to the verified game/runtime revision.

John Pork still inherits Luigi's moves, animations, effects, sounds, announcer, and Kirby hat. An independent roster entry does not automatically provide a new moveset or voice pack.

This John-Pork-only roster build writes `playable/Melee - Custom Smash.iso`, launched locally through `john-pork/Play Custom Smash.cmd` from the repository root. Its extracted filesystem, m-ex working data, and separate Dolphin profile live under `output/custom-smash/`. Rebuilding regenerates the two working-data trees but preserves the profile, the older `Melee - John Pork.iso`, and `Play John Pork.cmd`.

The combined Stephen Hawking/John Pork game uses the repository-root `Play Custom Smash.cmd`. That launcher opens `stephen-hawking/playable/Melee - Custom Smash.iso` with its separate profile under `stephen-hawking/output/custom-smash/DolphinUser`.

## Adapting the process to another character

Choose the base fighter and animation plan before adapting the artwork. Then explicitly review:

- The target skeleton, inverse binds, attachment joints, and native visibility-controlled objects.
- Proportions and deformation during attacks, jumps, landing, shielding, and damage animations.
- Model polygon count, texture footprint, material formats, and runtime memory use.
- Every portrait, roster tile, name, stock head, and results asset.
- Which values are character-specific: drawable indices, UV mapping, head cuts, scales, and asset IDs.

Use the existing importer and build stages where their contracts match. Do not substitute a new GLB into the John Pork-specific scripts and assume the hardcoded cuts and joint mappings will generalize.

## Verification and publication

Inspect both editor renders and the actual game. For an additional fighter, verify that the original and added fighter are independently selectable and can occupy the same match. Check movement, attacks, special moves, hand variants, HUD identity, and the results screen. Copy abilities and character-specific interactions also need verification when the engine's new-slot behavior differs from vanilla.

Keep the original ISO read-only and write a separately named output. Commit authoring sources, generated source art, dependency pins, and instructions—not Nintendo archives, rig/mesh extracts, rebuilt discs, emulator state, or downloaded model weights.

### Expanded-roster change record

The expanded build was built successfully and exercised in Dolphin **2606a** on Windows:

- Luigi and John Pork were selected independently and appeared together in a match, with separate models and stock heads.
- John Pork fired a green fireball.
- A completed match showed **JOHN PORK** as the winner while the player cards retained separate **LUIGI** and **JOHN PORK** names.
- After a fresh emulator boot, Kirby swallowed John Pork in a match containing no Luigi, acquired the Luigi hat, and fired the copied green fireball.

These checks cover the additional-slot and copy-ability integration. They are not an exhaustive test of every move, stage, game mode, or character interaction.
