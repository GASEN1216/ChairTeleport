# 椅子传送 Chair Teleport

### 功能介绍

椅子传送 Mod 提供了便捷的椅子传送功能，让你可以快速在已激活的椅子之间传送，还可以自定义复活点。

> ⚠️ 请注意：传送至未解锁区域可能导致炸挡或毁挡，请谨慎操作。

**主要功能：**

- 🪑 **椅子传送**：在已激活的椅子之间快速传送，打开地图的标记选中椅子进行传送，也支持使用椅子列表UI进行传送。长按F3/X键查看椅子在地图上的位置，松开回到UI（被选中的椅子会有动态效果）
- ⭐ **收藏功能**：双击→键可收藏场景/椅子，置顶显示
- 🎮 **键位自定义**：支持键盘的完全自定义
- ✏️ **椅子重命名**：为椅子设置自定义名称，方便识别
- 🌐 **多语言支持**：完整的中英文界面切换

#### 其他功能

- **椅子重命名**：在传送界面中按 `空格键` 或手柄 `Y` 键编辑椅子名称（短按）
- **椅子删除**：长按 `空格键` 或手柄 `Y` 键 2 秒自动删除选中椅子
- **父条目整理**：单按 `空格键` 或手柄 `Y` 键 即可重命名父条目
- **自定义整理**：椅子双击 `空格键` 或手柄 `Y` 键可以重命名自己的父条目便于整理
- **复活点标记**：按 `F2`（默认）或手柄 `LB+RB+Y` 在当前位置设置复活点（再次按 F2/组合取消），双击则立即传送到该复活点（若无则为当前椅子复活点）
- **难度选项**：必须坐在椅子上才能打开 UI（默认关闭，可在配置里开启）
- **开启全部椅子**：设置配置项里的ImportAllBenchesOnEnter为true，然后重进游戏

### 使用方法

#### 基本操作

1. **激活椅子**：正常游戏中坐椅子即可自动记录
2. **打开传送界面**：按 `F1`（默认），或手柄 `LB+RB+X`
3. **选择目标椅子**：使用方向键选择（支持长按快速导航）
4. **鼠标滚轮**：在UI界面内使用鼠标滚轮快速选择椅子
5. **确认传送**：按 `Enter` 键，或手柄 `A`
6. **关闭界面**：按 `F1` 或 `ESC`，或手柄 `B`

#### 翻页显示功能

- **智能翻页**：椅子列表采用翻页显示，每页显示13-15个椅子（根据屏幕大小自动调整）
- **快速导航**：
  - 上下方向键：逐项选择（支持长按快速导航）
  - 鼠标滚轮：快速选择椅子
- **自动翻页**：当选择超出当前页面时，自动跳转到对应页面
- **二级列表**：支持父级分类 -> 子条目；

- **页面信息**：显示当前页数和总页数，方便定位

### 键位自定义

#### 键盘设置

在 BepInEx 配置文件中可以自定义以下键位：

**配置文件位置：** `BepInEx/config/chairteleport.cfg`

```ini
[Keyboard]
# 打开椅子传送界面的按键（支持组合键）
OpenTeleportUI = F1
# 随地休息的按键（支持组合键）
QuickRestAndSave = F2
# 在 UI 内确认/传送的按键（默认 Enter）。支持组合键；始终允许数字小键盘 Enter
ConfirmTeleport = Return
```

**支持的组合键：**

- `Ctrl + F1`
- `Alt + F2`
- `Shift + Space`
- 等等...

#### 手柄设置

- 组合键使用“物理按键”识别，不受游戏内动作映射影响：A=0、B=1、X=2、Y=3、LB=4、RB=5、SELECT=6、START=7。
- 默认组合：打开/关闭 UI = `LB+RB+X`；F2（随地复活/保存）= `LB+RB+Y`。
- UI 内：A=确认，B=返回/关闭，Y=短按重命名/长按2秒删除。

```ini
[Gamepad]
EnableGamepadSupport = true
OpenUIButtons = LB+RB+X
QuickRestButtons = LB+RB+Y
```

#### UI界面设置

```ini
[UI]
# 自动根据屏幕分辨率调整界面大小
AutoScale = true
# 手动界面缩放倍数（当AutoScale=false时生效）
UIScale = 1.0
```

**完整配置文件示例：**

```ini
[Keyboard]
OpenTeleportUI = F1
QuickRestAndSave = F2
ConfirmTeleport = Return

[Gamepad]
EnableGamepadSupport = true
OpenUIButtons = LB+RB+X
QuickRestButtons = LB+RB+Y

[General]
# 界面语言：Chinese, English, Auto
UILanguage = Chinese

[UI]
# 自动缩放（推荐开启）
AutoScale = true
# 手动缩放倍数（0.5-3.0）
UIScale = 1.0

[Debug]
EnableDebugLog = false
```


# Chair Teleport

### Feature Introduction

The Chair Teleport Mod provides a convenient chair teleport feature, allowing you to quickly teleport between active chairs and customize your spawn point.

> ⚠️ Please note: Teleporting to an unlocked area may cause the block to explode or be destroyed, so please operate with caution.

**Main Features:**

- 🪑 **Chair Teleport**: Quickly teleport between activated chairs. Open the map marker and select the chair to teleport. You can also use the chair list UI to teleport.Press and hold the F3/X key to view the chair's location on the map, then release it to return to the UI (the selected chair will animate).
- ⭐ **Favorite Feature**: Double-click → key to favorite scenes/chairs, pinned to top
- 🎮 **Key Customization**: Full keyboard customization
- ✏️ **Chair Rename**: Set custom names for chairs for easy identification
- 🌐 **Multi-language**: Complete Chinese/English interface switching

#### Other Features

- **Chair Rename**: In teleport UI press `Spacebar` or gamepad `Y` to edit chair name (short press)
- **Chair Delete**: Press and hold `Spacebar` or gamepad `Y` for 2 seconds to delete the selected chair
- **Parent Item Organization**: Simply press the `Spacebar` or the `Y` key on the controller to rename the parent item.
- **Custom Organization**: Double-click the `Spacebar` or the `Y` key on the controller to rename the parent item for easier organization.
- **Respawn Point Mark**: Press `F2` (default) or gamepad `LB+RB+Y` to set a respawn point at current location. Press again to clear. Double-click to teleport to the resurrection point immediately (if there is no resurrection point, it will be the current chair resurrection point)
- **Difficulty Option**: Must sit in a chair to open the UI (off by default, can be enabled in the configuration)
- **Open all chairs**: Set ImportAllBenchesOnEnter in the configuration to true, then re-enter the game

### Instructions

#### Basic Operations

1. **Activate Chair**: Sit in a chair during normal gameplay to automatically record
2. **Open Teleport Interface**: Press `F1` (default), or gamepad `LB+RB+X`
3. **Select Target Chair**: Use arrow keys to select (support long press for quick navigation)
4. **Mouse Wheel**: Use mouse wheel in UI to quickly select chairs
5. **Confirm Teleport**: Press `Enter` key, or gamepad `A`
6. **Close Interface**: Press `F1` or `ESC`, or gamepad `B`

#### Pagination Display Features

- **Smart Pagination**: Chair list uses pagination display, showing 13-15 chairs per page (auto-adjusted based on screen size)
- **Quick Navigation**:
  - Up/Down arrow keys: Select item by item (support long press for quick navigation)
  - Mouse wheel: Quick chair selection
- **Auto Page Turn**: Automatically jump to corresponding page when selection exceeds current page
- **Two-level list**: supports parent category -> child items; 

- **Page Information**: Display current page and total pages for easy location

### Keyboard Customization

#### Keyboard Settings

The following key bindings can be customized in the BepInEx configuration file:

**Configuration file location:** `BepInEx/config/chairteleport.cfg`

```ini
[Keyboard]
# Key to open the chair teleport interface (supports key combinations)
OpenTeleportUI = F1
# Key to rest anywhere (supports key combinations)
QuickRestAndSave = F2
# Keyboard confirm/teleport key inside UI (default Enter). Supports combos; Keypad Enter always allowed
ConfirmTeleport = Return
```

**Supported key combinations:**

- `Ctrl + F1`
- `Alt + F2`
- `Shift + Space`
- etc...

#### Gamepad Settings

- Gamepad combos use physical buttons and are not affected by in-game action remapping: A=0, B=1, X=2, Y=3, LB=4, RB=5, SELECT=6, START=7.

```ini
[Gamepad]
EnableGamepadSupport = true
OpenUIButtons = LB+RB+X
QuickRestButtons = LB+RB+Y
```

#### UI Interface Settings

```ini
[UI]
# Auto-adjust interface size based on screen resolution
AutoScale = true
# Manual interface scale factor (effective when AutoScale=false)
UIScale = 1.0
```

**Complete Configuration File Example:**

```ini
[Keyboard]
OpenTeleportUI = F1
QuickRestAndSave = F2
ConfirmTeleport = Return

[Gamepad]
EnableGamepadSupport = true
OpenUIButtons = LB+RB+X
QuickRestButtons = LB+RB+Y

[General]
# Interface language: Chinese, English, Auto
UILanguage = English

[UI]
# Auto-scaling (recommended)
AutoScale = true
# Manual scale factor (0.5-3.0)
UIScale = 1.0

[Debug]
EnableDebugLog = false
```
