![Valheim Mannequins](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/valheim-mannequins-banner.png)

**Player-shaped mannequins for your base.** Dress them with your real gear, pose them, give them a name and keep several gear sets on each one. Swap a whole set with what you are wearing in one key press.

**Required on the server and on every client.** Valheim 1.0 · BepInExPack · Jotunn.

![](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/divider-norse.png)

## Features

| | |
|---|---|
| ![Build](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/icon-build.png) | **Build it.** Hammer → Furniture → Mannequin. Rotate it like any piece, it uses the normal build system and ward rules. Default cost 20 Wood and 1 Iron, configurable. |
| ![Armor](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/icon-armor.png) | **Dress it.** Aim at it and press the hotbar key of an item: armor, cape, belt, weapons and shields appear on the mannequin exactly like on a player. Everything is a real item you can take back. |
| ![Sets](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/icon-sets.png) | **Gear sets.** Up to 8 sets per mannequin (5 by default, change it with `SetCount`). The mannequin shows one set at a time. |
| ![Swap](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/icon-swap.png) | **Swap with yourself.** Aim at it and press **Alt + number**. Your worn gear and the set trade places, weapons on the back come along. |
| ![Scroll](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/icon-scroll.png) | **Scroll to switch.** Aim at it and use the **mouse wheel** to show the next set. |
| ![Nameplate](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/icon-nameplate.png) | **Name above the head.** Shown exactly like another player's name. Every gear set has its own name, leave it empty to hide it. |
| ![Poses](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/icon-poses.png) | **15 poses and a look of your own.** Male or female body, hair style and colour, beard, skin tone and eye colour. |

![](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/divider-norse.png)

## How to use

**Dress it**
- Put an item on your hotbar, aim at the mannequin and press the item's hotbar key. It goes into the first free slot that fits.
- Slots: helmet, chest, legs, cape, belt, right hand, left hand, weapon on the back, shield on the back.

**Manage it** (press **E**)
- **Set tabs** at the top switch between gear sets.
- **Take** returns an item to your inventory.
- **< >** next to *Pose* changes the pose.
- **Rename** names the active gear set.
- **Look** opens the appearance settings.

**Swap gear sets** (aim at a mannequin, press **Alt + 1…8**, one key per gear set you have)
- Your worn gear (armor, cape, belt, both hands) moves into that set and the set's gear is equipped on you.
- The weapon or shield on the set's back slots goes to your inventory. The set remembers it and takes it back the next time you swap with the same set.
- If your inventory is too full, nothing changes.

Removing a mannequin drops every stored item of every set.

![](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/divider-norse.png)

## Configuration

`BepInEx/config/morda.ValheimMannequins.cfg`

| Setting | Default | Description |
|---|---|---|
| `Materials` | `Wood:20,Iron:1` | Build cost as `Item:Amount,...` |
| `DefaultPose` | `0` | Pose of a new mannequin |
| `SetCount` | `5` | Gear sets per mannequin (1–8) |
| `SwapModifier` | `LeftAlt` | Key held together with the number. Use `LeftControl` if another mod already uses Alt + number |
| `ScrollSwitchesSet` | `true` | Mouse wheel switches the shown set |
| `PanelScale` | `1.6` | Size of the management window |
| `NameDistance` | `30` | Distance up to which names are shown |

The build-menu icon is rendered once and saved as `BepInEx/config/ValheimMannequins_icon.png`. Replace it with your own picture or delete it to render a new one.

## Installation

1. Install [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/)
2. Install [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/)
3. Install Valheim Mannequins on the server **and** on every client

## Known limitations (beta)

- Tested in single player. Multiplayer and dedicated servers have not been tested yet.
- Texts are English only.
- Hair, skin and eye colours are presets.

## Bug reports

**[DISCORD](https://discord.gg/UcpPJps3n)** · **[GITHUB](https://github.com/morda0511/ValheimMannequins/issues)**

Please attach `BepInEx/LogOutput.log` to your report.


![](https://raw.githubusercontent.com/morda0511/ValheimMannequins/main/assets/valheim-mannequins-footer.png)
