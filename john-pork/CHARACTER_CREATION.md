# Creating a Melee-compatible character: John Pork

This records the actual authoring process, not just the commands needed to rebuild an existing asset. The committed art inputs let the model be rebuilt without rerunning AI generation. The original reference photograph, Nintendo files, model weights, and workstation-specific generation environment are not distributed.

## What was created, and what was reused

John Pork is a hybrid model:

- Generated pig-head geometry and its texture atlas.
- Script-authored eyes, nostrils, plaid clothing, jeans, and shoe textures.
- Luigi's original body geometry, skeleton, animation weights, and native hand variants.
- Luigi's movement and most animations. In the expanded roster, neutral-B is Donkey Kong's chargeable Giant Punch, with animations retargeted to the existing skeleton.

The initial implementation replaced the default green costume, `PlLgNr.dat`. Its separate ISO is retained as the working baseline.

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
3. Remove remaining garment-colored faces below the jaw, checking face corners as well as centers so the dark collar fringe is removed.
4. Decimate toward a 2,800-triangle head budget, remove pinched boundary fans, and use smooth normals.
5. Resize the head atlas to 512 × 512.
6. Transform the head into the original bind pose and attach its vertices to **Luigi joint 23**.
7. Export the mesh, normals, UVs, joint assignment, and coordinate transform as `character/john-pork-generated-head.json`.

The scale, cut planes, color threshold, and attachment joint in that script are specific to this generated head and Luigi's rig. They are not universal settings for another character.

## Build clothing and facial details

`tools/build_character.py` preserves the original body vertex positions and animation weights while assigning new materials and UV coordinates. It creates the plaid overshirt, gray T-shirt opening, jeans, and sneaker textures with a fixed NumPy random seed (`260910`).

The original short neck mesh is replaced by a continuous collar-to-jaw bridge. Its boundary rings follow the body collar and generated head, with intermediate weights across joints **5, 22, and 23**. This closes the rear and side openings while the head moves without changing the original skeleton.

The playable archive retains the native hand display objects and their open/fist/low-detail visibility variants. Replacing them with a single always-visible hand mesh would lose the original animation behavior.

The neck texture and all eight native hand drawables share `skin_rgb = (238, 155, 138)`, sampled from the generated head's nape. Keep this palette shared rather than maintaining separate neck and hand colors. The skin match was checked in Dolphin in idle and jumping poses; native lighting and the original geometry, weights, and visibility variants remain intact.

The generated face had dark, recessed eyes. The script adds eye whites, brown irises, pupils, small highlights, and nostril surfaces. These share the head joint; no new facial bones or blinking animation were created.

The result is `character/john-pork-mesh.json`, plus generated textures. Both are derived build outputs and remain out of Git. `tools/render_character_blender.py` can produce an editable preview and render it, but is not required to build the playable costume.

## Write a native costume archive

`importer/MeshImporter.cs` converts the authored mesh back into Melee's HSD display objects, materials, texture formats, and skinning data. The original costume supplies the compatible joint structure and preserved native hand objects.

The resulting costume is a model asset. Fighter registration, gameplay callbacks, animations, and character-select registration are separate concerns; a renamed DAT alone does not create another roster slot.

Opaque textures retain their existing RGB565 colors. When indexing saves space, the importer uses CI4 for at most 16 colors or CI8 for at most 256 colors; otherwise it uses RGB565. Hawking's solid-color atlas also drops unused rows and adjusts UVs without resampling visible texels. The combined builder reimports both costumes so cached DATs cannot bypass this encoding.

The importer saves with `optimize: true, trim: false`, removing unreachable data and sharing identical buffers without changing the live skeleton or geometry. Preserve every native DOBJ, material, and TOBJ slot: `ftParts_80075240` looks up textures by flat ordinal, including empty drawables. Slots behind empty drawables may share existing same-format image/palette storage, but visible texture objects must remain untouched. Removing an unused TOBJ is not safe.

Hawking's entire visible model is rigidly bound to seated pelvis/chair joint **4**. Keep the animated 118-joint combat hierarchy intact: freezing its attack tracks would also change hitbox, held-item, and projectile-emitter motion. Results clips instead remove all pose tracks, including root motion, while retaining their native durations and subaction scripts. Editable Blender results actions use the same seated bind pose and original frame ranges.

The combined runtime also budgets stage, fighter, and audio memory separately:

- Load the primary stage DAT through the existing `lbArchive_800171CC` scene-heap fallback after selection-screen memory is released, rather than preloading it into the fighter archive cache. Stage-specific scratch allocations remain intact.
- Transfer 256 KiB from the fighter archive cache to the live scene heap. Smaller costume archives leave room in the cache while restoring runtime headroom for effects and Kirby's copy animations.
- Transfer 576 KiB of ARAM from the animation cache to the sound-bank allocation before the native allocator computes bank sizes. Hawking's eight-clip bank needs 355,744 bytes, 75,776 more than the earlier six-clip bank. Preserve the source sample rates and keep both sides of the ARAM transfer matched.

Regression scenarios include John Pork and Hawking on Hyrule Temple (the earlier RGB565-only costume overflow) and Hawking/John Pork/Pichu/Kirby on Yoshi's Story (the later stage-preload overflow). Large-stage checks also cover Venom's audio budget, Big Blue's live copy-animation allocations, and Pokémon Stadium's scratch buffers.

The pinned SSM writer leaves the eight-sample Hawking payload 16 bytes short of Melee's required 32-byte alignment. Normalize only `audio/us/hawking.ssm` after `image.Save`, before rebuilding the ISO: pad the header, update its size field, and retain the declared ARAM payload size. The normalized bank has a 592-byte header field, payload at file offset 608, and total size 356,352 bytes. Do not rewrite vanilla sound banks to fix this private bank.

The sleep recording loops from sample start with its initial ADPCM predictor/history copied into the loop context. A behavior-6 voice wrapper enters `FuraSleepLoop` once, then jumps to the original animation script. Its muted native sound event remains a no-op; the animation's self-loop must not return to the wrapper and restart the recording. Native wake, damage, and removal paths own voice cleanup.

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

`tools/build_ui.py` extracts and identifies the original UI textures. The roster tile and character-select portrait use the photographic face cutout in `art/john-pork-menu-head.png`; the generated portrait is `interface/john-pork-menu-portrait.png`. The expanded-roster builders regenerate these assets before assembling the game.

The HUD/results stock head still uses the model-rendered `character/john-pork-portrait.png`, independently of the menu photo. The script composes JOHN PORK from the game's existing bitmap letter shapes. `importer/UiTextures.cs` repoints the matching texture references.

For the original replacement build, Luigi is external character ID 7, the main portrait is bank 1/frame 7, and two executable name strings are patched in place. Those replacement-specific indices must not be reused to label a new fighter while leaving Luigi intact.

Melee's fixed preload heaps matter. Keeping superseded images in an archive caused a real allocation failure at character select. The UI importer now removes unused data and deduplicates buffers when saving. A file that opens in an editor is not proof that it fits the game's runtime memory budget.

## Register an additional fighter

The expanded build is driven by `tools/build_custom_smash.py`. Follow the [expanded-roster build instructions](../.github/README.md#build-the-expanded-roster) for the prerequisites and command. It starts from a verified, unmodified NTSC-U 1.02 disc, not from the older Luigi-replacement ISO.

The `roster/` builder uses the pinned m-ex source and checksum-verified runtime resources:

- `JohnPorkFighter.cs` clones Luigi's fighter configuration into a new entry before the non-roster special fighters. John Pork receives internal ID **27** and external ID **26**; original Luigi stays at **17/7**. The costume is installed under its own archive name rather than overwriting `PlLgNr.dat`.
- The new entry owns its portrait, character-select tile and name, and stock head. `tools/build_ui.py --assets-only` generates the new images without applying the old Luigi-slot replacements.
- `ResultNames.cs` adds the new winner and results-card name frames. The optional m-ex “Skip Result Screen” code is disabled so those surfaces remain visible.
- `importer/JohnPorkAnimations.cs` creates private `PlJp.dat` and `PlJpAJ.dat` archives with eight retargeted Giant Punch clips and ten ground/air states. `JohnPorkGameplay.cs` installs the native charge/punch callbacks, keeps DK's attributes separate from Luigi's, and stores John's persistent charge at `Fighter+0x2238`.
- `KirbyClone.cs` provides a separate DK copy-cap archive and native callback adapters. DK's full-body copy requires both the cap and costume runtime tables to be bound during ability gain and loss; binding only the cap causes a crash when Kirby swallows John without DK present. The adapters restore the donor entries afterward and preserve John's copied identity. These PowerPC addresses and instructions are specific to the verified game/runtime revision.

Tap neutral-B to charge, shield to store a partial charge, and press B again to punch. Full charge is stored automatically after ten arm swings. Ground and aerial punches are supported; charge survives the other specials and clears on a KO. Luigi-based movement, the other three specials, and non-vocal effects remain unchanged; inherited Luigi vocals, the donor announcer name, and crowd chant are muted. Kirby copies Giant Punch and its DK hat rather than Luigi's fireball.

This John-Pork-only roster build writes `playable/Melee - Custom Smash.iso`, which can be opened directly in Dolphin. Its extracted filesystem, m-ex working data, and separate Dolphin profile live under `output/custom-smash/`. Rebuilding regenerates the two working-data trees but preserves the profile and the older `Melee - John Pork.iso`.

The combined Stephen Hawking/John Pork game uses the repository-root `play-custom-smash.cmd`. That launcher opens `stephen-hawking/playable/Melee - Custom Smash.iso` with its separate profile under `stephen-hawking/output/custom-smash/DolphinUser`.

The Hawking build also generates Kirby's brown, side-parted hair/glasses headpiece with `stephen-hawking/tools/build_kirby_hat_blender.py`, fitted to Kirby's native head geometry. Raised overlapping locks sit over recessed brown roots so the hair has neither a smooth cap silhouette nor exposed scalp gaps. `HawkingKirbyCopy.cs` replaces only the model in private `PlKbHw.dat`, retaining Samus's copy animations and Charge Shot article. It does not replace Kirby's body costumes or the original Samus copy cap.

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
- John charged Giant Punch to ten swings, stored partial charge by shielding, retained charge through the other three specials, and released partial/full punches on the ground and in the air. Charge cleared after a KO. Observed hits against Hawking dealt 16% for a partial punch and an additional 27.3% for a full punch in that match.
- A completed match showed **JOHN PORK** as the winner while the player cards retained separate **LUIGI** and **JOHN PORK** names.
- After a fresh emulator boot, Kirby swallowed John in a match containing no DK, acquired the DK hat, charged the copied punch to ten swings, and dealt 30% with a full punch. A subsequent KO removed the copied ability and cleared its charge without a crash.
- Rear and side neck views were checked in Dolphin, including punch poses. The collar-to-head bridge closed the opening and the generated dark collar fringe was removed.
- In the combined game, Hawking charged, stored, and fired Charge Shot, and fired regular/Super Missiles on the ground and in the air. Observed hits dealt 25% for a full Charge Shot and 12% for a Super Missile.
- Hawking's Bomb, Zelda jab, and directional ground/air teleport were exercised. Stored charge survived Bomb, jab, and ground teleport, cleared on stock loss, and could be charged again after respawning. A second match loaded successfully.
- Kirby swallowed Hawking without Samus present and used the copied ground/air Charge Shot while retaining Hawking's copied identity. Taking damage removed the held shot and cleared its stored charge.
- With the earlier six-clip Hawking bank, the match-memory fixes let the normal launcher load four-player matches with Hawking, John Pork, Pichu, and Kirby on Yoshi's Story, Venom, Big Blue, Pokémon Stadium, and Final Destination. Sustained play included Kirby copying both custom fighters, Stadium's live video screen, and repeated results/character-select/stage-select transitions. Final Destination loaded 6,613,408 bytes of audio within that build's 6,637,568-byte allocation. All 112 SSM files remained byte-identical to the full-quality voice build.
- After making Hawking's visible body rigid, a normal Dolphin match on N64 Dream Land exercised the jab, Charge Shot, and missile action without limb deformation. The jab dealt 5% and a full Charge Shot dealt 25%. Hawking remained seated on the winner screen, and both players returned to character selection normally. The gameplay animation archive remained byte-identical; all nine unique results clips retained their original durations and joint counts with pose motion removed.
- Hawking's inherited Win3 magic graphics were removed only from his result scripts. A fresh Dolphin match forced the third victory pose with X; native result state/action 5 was observed, with no floating flame during the winner reveal or completed results screen. All other demo events, including intro and ending scripts, were preserved.
- The eight-clip audio build was exercised on Final Destination with Hawking and Jigglypuff. Ground and aerial up-B each played one instance of “a short cut.” Sing kept one snore handle active across five animation loops, then stopped it on natural wake-up; a separate real jab also stopped it immediately. Captured game PCM identified both new recordings and five complete snore repetitions approximately 1.452 seconds apart, rather than restarting every 80 animation frames.
- The initial glasses/hair headpiece was checked close-up in Dolphin after a fresh no-Samus copy. Kirby retained Hawking's copied identity, fired a full Charge Shot for 25%, and lost both headpiece and ability on a KO. All six original Kirby body costumes, the vanilla Samus cap, and the Hawking/Samus fighter DATs remained byte-identical.
- Hawking's hair-only texture was retinted to warm brown from the original diffuse and existing mask. All unmasked pixels and alpha values were preserved, as were all prototype vertices, faces, transforms, and native bind data. The brown diffuse reached the runtime texture exactly, and the rebuilt model was inspected in Dolphin.
- The eight private recordings were peak-normalized to -1 dBFS before DSP encoding. The rebuilt bank gained 1.9–6.6 dB per recording, retained every sample count and its 356,352-byte size, and decoded with peaks around -1 dBFS. Source MP3s remained byte-identical. Captured game PCM identified the louder teleport and bomb recordings over the music/effects mix.
- The denser brown Kirby hairstyle was checked from the front and above in Dolphin after closing the scalp gaps with wider overlapping locks and recessed roots. The final 2,992-triangle headpiece retained the glasses and original body costumes. A fresh no-Samus copy still fired a full Charge Shot for 25%, and a subsequent KO removed the headpiece and copied ability.

These checks cover the additional-slot and copy-ability integration. They are not an exhaustive test of every move, stage, game mode, or character interaction.
