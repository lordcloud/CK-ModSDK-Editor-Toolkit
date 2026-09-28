# Item Copy (Core Keeper Mod SDK, Unity Editor)

Editor tool to duplicate an item prefab together with its data blocks, text block, sprites and textures.

## Install
Option A, Package Manager: *Window > Package Manager > + > Add package from git URL* and enter the repository URL (ending in `.git`).

Option B, manual: copy the repository contents anywhere under `Assets/` in your mod SDK project.

Editor-only, own assembly (`ModTools.ItemCopy.Editor`), no dependencies besides Unity.

## Use
Right-click an item prefab (with `ObjectAuthoring` or legacy `EntityMonoBehaviourData`) → **Assets > Mod Tools > Copy Item...**

- **Name prefix**: optional mod prefix for object names (e.g. `MyMod_`). Saved per editor, stripped when deriving file names.
- Data blocks can be duplicated or kept shared with the source. The entity block is always duplicated.
- Legacy prefabs are converted to `ObjectAuthoring` + `InventoryItemAuthoring`.
- Everything else (stats, conditions, recipe, weapon/projectile setup) is copied unchanged.
