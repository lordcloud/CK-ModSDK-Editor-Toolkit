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

## License
MIT, see [LICENSE](LICENSE).

## Disclaimer
Unofficial community tool, not affiliated with or endorsed by Pugstorm or Fireshine Games. Core Keeper and related names are trademarks of their respective owners.
This repository contains no game code or assets. It only refers to Core Keeper SDK types by name. Using it requires the official Core Keeper Mod SDK, which is subject to its own terms.
