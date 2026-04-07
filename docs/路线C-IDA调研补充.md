# 路线 C — IDA 调研补充

> 2026-04-07 调研。承接 `PBR材质属性独立化调研.md` 和 `材质替换路线研究.md`。
> 通过 IDA Pro MCP 连接 ffxiv_dx11.exe（pid 11708）做了首轮反编译，确认了几个对路线 C 关键的事实。

## 调研目的

回答 `PBR材质属性独立化调研.md` 末尾"路线 C 待研究项"的 6 个未知点（特别是 #1/#2/#5），为 v2 spec 收集 vanilla 引擎的 shader package / material 加载机制的具体行为。

## 已确认的事实

### 事实 11：Vanilla 全 shader package 共 57 个

`sub_1402AAF30`（轻量加载器）和 `sub_1402ACEE0`（渲染系统 init）都遍历同一个 string array `off_14279FC00`，循环退出条件 `v3 >= 0x39`，即 **57 个 shader package**。

string array 的全部内容已通过 `find_regex` 提取（地址 `0x14206d3a0` 起的连续段），关键条目：

| 路径 | 地址 |
|---|---|
| `shader/sm5/shpk/skin.shpk` | 0x14206d3a0 |
| `shader/sm5/shpk/character.shpk` | 0x14206d3c0 |
| `shader/sm5/shpk/iris.shpk` | 0x14206d3e0 |
| `shader/sm5/shpk/hair.shpk` | 0x14206d400 |
| `shader/sm5/shpk/characterglass.shpk` | 0x14206d420 |
| `shader/sm5/shpk/charactertransparency.shpk` | 0x14206d5b8 |
| `shader/sm5/shpk/charactertattoo.shpk` | 0x14206dca8 |
| `shader/sm5/shpk/characterocclusion.shpk` | 0x14206dcd0 |
| `shader/sm5/shpk/characterreflection.shpk` | 0x14206dd00 |
| `shader/sm5/shpk/characterlegacy.shpk` | 0x14206dd30 |
| `shader/sm5/shpk/characterinc.shpk` | 0x14206dd58 |
| `shader/sm5/shpk/characterscroll.shpk` | 0x14206dd80 |
| `shader/sm5/shpk/characterstockings.shpk` | 0x14206dbe8 |
| `shader/sm5/shpk/hairmask.shpk` | 0x14206dbb8 |

加载结果存到 caller 提供的 `a1 + 8 + i*8` slot 数组中。

### 事实 12：charactertattoo.shpk 是 vanilla 自带的角色贴花 shader

这是 IDA 调研最有价值的意外发现。

**含义**：vanilla 引擎已经存在专门的角色贴花 shader package，名字直译就是"character tattoo"。它在 .shpk 字符串表中跟 character.shpk / characterlegacy.shpk 同级。

**对 v2 路线 C 的潜在意义**：

- 之前路线 C 的目标是把 skin.shpk → character.shpk
- 但 character.shpk 是装备 shader，可能比身体材质需要的属性更多（dye / tile / sphere map 等）
- charactertattoo.shpk 名字暗示它是为身体贴花设计的，可能是更轻量、更直接的转换目标
- **v2 spec 应该对比这三个 shader 文件本身的 sampler 列表 + ConstantBuffer 字段，选择最合适的转换目标**

**注意**：这个对比**不需要 IDA**——可以直接用 Penumbra 的 `Penumbra.GameData/Files/ShpkFile.cs` 解析 vanilla `.shpk` 文件，提取 sampler 名 / CBuffer 字段名 / shader keys 列表。这是比 IDA 反编译更准确、更便捷的路径。

### 事实 13：Shader package 加载入口 = ResourceManager 通用文件加载器

具体调用链（init 时）：

```
sub_1402AAF30 / sub_1402ACEE0
  → sub_1402ED040 (when byte_14298F490 != 0)  // 通用 file resource loader
  → sub_1402ECFA0 (otherwise)                  // 同上的另一个变体（cache disabled?）
      → sub_1402ECD10  // path → hash + canonicalize
      → sub_140304A50  // ResourceManager.LoadFile(qword_14298F518, ...)
```

`qword_14298F518` 是全局 ResourceManager 实例（`Client.System.Resource.ResourceManager` 单例的指针）。

**关键认知**：shader package **不是通过专用加载器加载的**。它走的是和 .tex / .mdl / .mtrl 完全相同的 `ResourceManager.LoadFile` 路径，唯一的区分点是 file extension `'shpk'` 在 `sub_1402ECD10` 内部参与 hash 计算。

### 事实 14：OnRenderMaterial = sub_14026EE10

SkinTatoo `EmissiveCBufferHook` 的 hook 目标。通过签名 `E8 ?? ?? ?? ?? 44 0F B7 28` 在 `0x14026f87e` 处定位到 call 指令，target 是 `0x14026EE10`。

**函数签名**（IDA 推断）：

```c
_WORD * __fastcall sub_14026EE10(
    __int64    a1,    // ModelRenderer this
    _WORD     *a2,    // out: 16-bit material flags
    __int64   *a3,    // ModelRenderInfo / DrawCommand
    __int64    a4,    // MaterialContext (a4 + 16 = MaterialResourceHandle*)
    int        a5     // pass / submesh index
);
```

**函数职责**：构建该 material 在当前 draw command 的 16-bit 渲染 flags（写到 `*a2`），根据 ShaderPackage 的 flag bits、material flags、ModelRenderer 状态做位运算合成。

它本身**不调用 LoadSourcePointer，也不直接更新 CBuffer**。SkinTatoo 的 EmissiveCBufferHook 通过 detour 在该函数前执行，借助"这是每帧每材质都会调一次"的特性，在 detour 内主动写 CBuffer 数据。

### 事实 15：MaterialResourceHandle 字段 offset

通过 `sub_14026EE10` 的字段访问模式间接确认（`a4 + 16` 解引用得到的对象，按 offset 访问）：

| Offset | 字段 | 用途 |
|---|---|---|
| `+200` (0xC8) | `ShaderPackage*` | ShaderPackageResourceHandle 指针 |
| `+232` (0xE8) | `ShaderPackageFlags` (DWORD) | 渲染分支位（`& 0x4000`、`& 0x8000` 等） |

**符合 FFXIVClientStructs 已有的 MaterialResourceHandle 定义**——可以信任 SkinTatoo 项目里已经引用的 FFXIVClientStructs 结构体定义，不需要在 IDA 里重新建模。

### 事实 16：vanilla 引擎自己存在 "shader package fast-path 分流" 机制

`sub_14026EE10` 内有这样的代码：

```c
v17 = *(QWORD*)(*(QWORD*)(a4 + 16) + 200);  // material->ShaderPackage
if      (v17 == *(QWORD*)(a1 + 536)) { /* fast path A */ }
else if (v17 == *(QWORD*)(a1 + 544)) { /* fast path B */ }
// ... 共比较 5 个不同的 ShaderPackage 指针
```

`a1 + 536/544/552/560/568` 是 ModelRenderer 实例缓存的 5 个 ShaderPackage 指针，通过指针相等比较识别 material 用的是哪个 shader 类型，然后走不同的 flag 分支。

**意义**：vanilla 引擎本身就需要"按 shader 类型分流"的能力——这证明 shader package 不是平等对待的，而是有明确的"特殊种类"区分。这 5 个 slot 具体存哪 5 个 shader 没有进一步追溯（需要找到 ModelRenderer init 函数才能确认），但 highly likely 包括 skin / character / characterlegacy / characterglass / hair（或类似组合），即 vanilla 引擎认为"重要"的 shader。

**对路线 C 的影响**：如果我们把 .mtrl 的 ShaderPackage 切到 character.shpk，OnRenderMaterial 内的 fast-path 比较会自动走 character 分支，不需要任何 hook 干预——只要替换 ShaderPackage handle 即可。

## 对路线 C 的具体影响

基于上述事实，路线 C 的实现路径可以**大幅简化**：

### 不需要的工作

| 原计划 | 取消理由 |
|---|---|
| Hook shader package 加载函数 | shader package 是通过 ResourceManager 通用 loader 加载的，hook 它会污染所有材质 |
| 通过 IDA 提取 sampler 列表 | 直接用 Penumbra `ShpkFile.cs` 解析 vanilla `.shpk` 文件即可 |
| Hook 运行时切 shader package | OnRenderMaterial 已经按指针比较自动分流，只要 .mtrl 文件里 ShaderPackageName 改对，加载后引擎就会自动走对应 shader |
| 在 `ConstantBuffer 逆向分析.md` 范围之外深挖 cbuffer | 我们已经能通过 hook OnRenderMaterial 的 detour 改 cbuffer，路线 C 切了 shader 之后可能就根本不需要这条路（character.shpk 的 PBR 通过 ColorTable 解决） |

### 仍然需要做的事

1. **解析 vanilla 的三个候选 shader package 文件**（用 Penumbra ShpkFile）：
   - `shader/sm5/shpk/skin.shpk`
   - `shader/sm5/shpk/character.shpk`
   - `shader/sm5/shpk/charactertattoo.shpk` ← 新候选
   - 提取每个的 sampler 列表 + ConstantBuffer 字段名 + ShaderKey 选项
2. **决定转换目标**：根据三者对比，选最适合身体材质的目标 shader（极有可能是 charactertattoo.shpk 而非 character.shpk）
3. **构造 .mtrl 重写器**：用 Penumbra 的 `MtrlFile.AddRemove` API（已有）写一个新 .mtrl，包含：
   - 新 `ShaderPackageName`
   - 目标 shader 期望的全部 sampler 引用（差集需要新生成 placeholder 纹理或重定向到现有纹理）
   - 完整 ColorTable（32 行 Dawntrail 布局）
   - 必要的 ShaderKey 选项
4. **测试**：通过 Penumbra 临时 mod 把目标身体 .mtrl 重定向到我们重写后的版本，观察游戏是否正常渲染

## 仍然未知的事

| 未知项 | 影响 | 解决路径 |
|---|---|---|
| ModelRenderer +536..+568 缓存的 5 个 ShaderPackage 是哪些 | 影响"切到 character.shpk 后是否会触发某个 fast-path 副作用" | 找 ModelRenderer init 函数（xref `+ 536` 写入位置）— v2 spec 阶段再做 |
| 三个候选 shader 的 sampler 列表差集 | 决定 .mtrl 重写需要补哪些纹理 | 用 Penumbra ShpkFile 解析 — v2 spec 阶段做 |
| .mtrl 加载是否对 ShaderPackageName 做特殊预处理 | 影响是否能通过简单替换字符串切 shader | 找 MaterialResourceHandle 加载链路 — v2 实施期间做 |

## 资源索引（IDA 地址）

| 地址 | 含义 |
|---|---|
| `off_14279FC00` | Shader package 字符串 array 起点（57 entries） |
| `sub_1402AAF30` | 轻量 shader package 批量加载（init helper） |
| `sub_1402ACEE0` | 渲染系统完整 init（57 shaders + vanilla 纹理 + compute shader） |
| `sub_1402ED040` / `sub_1402ECFA0` | ResourceManager file load wrapper（按 cache flag 选择） |
| `sub_140304A50` | ResourceManager.LoadFile 实际入口 |
| `qword_14298F518` | ResourceManager 全局单例指针 |
| `sub_14026EE10` | OnRenderMaterial（EmissiveCBufferHook 目标） |
| `sub_14026F790` | OnRenderMaterial 的 caller（render loop） |
| `0x142075d90` | RTTI string `Client.System.Resource.Handle.MaterialResourceHandle` |
| `0x142075e60` | RTTI string `Client.System.Resource.Handle.ShaderPackageResourceHandle` |
| `0x14207c9f8` | RTTI string `Client.Graphics.ShaderPackage.Allocator` |
| `sub_1402EF6D0` | MaterialResourceHandle::GetTypeName (8-byte stub) |
| `sub_14031E810` | ShaderPackageResourceHandle::GetTypeName (8-byte stub) |

## 调研结论

路线 C 的工程难度**比之前估计的低**。核心因为：

1. shader package 切换不需要 hook，靠 .mtrl 文件的 ShaderPackageName 字段就能改
2. 引擎按指针比较自动分流，不需要任何 runtime 干预
3. 关键信息（sampler/CBuffer 列表）通过解析 .shpk 文件就能得到，不需要继续 IDA
4. **charactertattoo.shpk 的发现可能是 game-changer**，让 v2 转换目标更轻量

但路线 C 的难点不变：
- .mtrl 文件重写的细节正确性
- 必要时新增 placeholder 纹理（如 index/mask 纹理）
- 跟 SkinTatoo 现有合成器的协作（normal.a 写 row pair 号必须在 .mtrl 重写完成后才有意义）

**v2 spec 写作可以并行启动**——剩余的未知项完全可以通过解析 vanilla `.shpk` / `.mtrl` 文件解决，不必等更多 IDA 工作。
