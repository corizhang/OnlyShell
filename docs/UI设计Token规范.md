# OnlyShell UI 设计 Token 规范

## 目的

集中管理颜色、字体、字号、间距和常用控件尺寸，让全局视觉调整只需修改少数 token。XAML 控件优先引用语义资源，不再散落直接颜色值和字号常数。终端模拟屏幕、脚本语法高亮等内容专属颜色保留独立语义 token。

Token 资源入口是 `src/OnlyShell.App/Themes/DesignTokens.xaml`，由 `App.xaml` 合并加载。

## 页面与组件盘点

| 区域 | 页面/组件 | 应统一的样式 |
|---|---|---|
| 应用框架 | `MainWindow` 顶栏、左侧导航、状态栏 | 页面底色、分隔线、导航选中态、标题/辅助字号、图标和间距 |
| 主机 | 主机分组列表、主机概览卡片、主机快速连接列表 | 列表行高、悬浮/选中/禁用态、状态色、卡片边框和标题层级 |
| 终端 | 标签栏、分屏标签栏、终端工具栏、搜索栏、快捷命令菜单、复制粘贴浮动工具栏 | 标签状态色、操作按钮层级、辅助文字、弹出层背景、焦点边框 |
| 终端画面 | `TerminalPaneView`、终端输出和选择工具栏 | 终端字体/字号、终端底色、默认文字色、终端专属选中/光标状态；这些不应被普通页面正文样式覆盖 |
| SFTP | 本地/远程文件表格、路径工具栏、文件类型图标、传输队列 | 表头、行高、选择/悬浮态、文件图标色、进度和传输状态色 |
| 脚本中心 | 目录树、脚本编辑器、输入模板表格、运行参数表单、执行输出、运行历史 | 树节点状态、编辑器底色与语法色、表格状态、字段标签层级、成功/失败输出色 |
| 快捷命令 | `QuickCommandsWindow` 命令列表、变量 `DataGrid`、模板编辑区 | 弹窗标题、列表状态、表格、输入区、变量敏感态和按钮层级 |
| 设置 | 字体/字号/缓冲区下拉框、确认项、诊断信息 | 表单标签、控件高度、焦点态、说明文字与危险操作样式 |
| 连接与确认弹窗 | 主机编辑、凭据、主机指纹、SFTP 输入、脚本主机选择、脚本输入、同步确认 | 弹窗尺寸、标题、字段间距、输入控件、确认/取消/危险操作按钮 |
| 通用控件 | `ListView`/`GridView`、`DataGrid`、`ComboBox`、`TextBox`、按钮、CheckBox、TabControl、ScrollBar、ProgressBar | 背景/边框、控件行高、内边距、焦点/悬浮/选中/禁用状态 |

当前检查覆盖 `src/OnlyShell.App` 下 13 个 XAML 文件。样式属性约 484 处，常用颜色仍以硬编码形式重复出现；例如输入背景 `#111015` 在多个页面重复。应按页面域逐步迁移，保留终端显示和语法着色的特殊需求。

## Token 分类与约定

命名采用 `类别.语义[.状态]`，例如 `Color.Surface.Hover`、`Brush.Text.Primary`、`Type.Size.Body`。颜色先定义 Color，再定义可直接用于控件的 Brush。设计意图写进名称，避免 `DarkGray2` 这类只能靠记忆理解的名字。

### 颜色

| 语义组 | 当前 token | 用途 |
|---|---|---|
| 页面与内容表面 | `Color.Canvas`、`Color.Surface`、`Color.Surface.Raised` | 页面、分区、浮起的卡片或表头 |
| 输入与专用内容 | `Color.Surface.Input`、`Color.Surface.Editor`、`Color.Surface.Terminal`、`Color.Surface.Output` | 普通输入、代码编辑器、终端屏幕、脚本输出 |
| 交互状态 | `Color.Surface.Hover`、`Color.Surface.Selected`、`Color.Surface.SelectedHover`、`Color.Surface.Disabled` | 鼠标悬浮、选中、选中且悬浮、禁用 |
| 边框 | `Color.Border.Subtle`、`Color.Border.Default`、`Color.Border.Strong`、`Color.Border.Focus`、`Color.Border.Selected`、`Color.Border.Disabled` | 分隔线、控件边框、弹层边框、焦点和选中提示 |
| 文字 | `Color.Text.Primary`、`Color.Text.Secondary`、`Color.Text.Disabled`、`Color.Text.OnAccent`、`Color.Text.OnSelected`、`Color.Text.Header`、`Color.Text.Highlight` | 正文、说明、禁用、强调底色上的文字、表头 |
| 强调与状态 | `Color.Accent.Primary`、`Color.Status.Success/Info/Warning/Error` | 主操作、成功、信息、警告、失败；状态不能只靠颜色表达，仍需配合文字/图标 |
| 语法高亮 | `Color.Syntax.Keyword/String/Comment/Number/Variable` | 脚本编辑器语法色，独立于产品状态色 |

常用 Brush 由对应 Color 派生，资源名与颜色语义一致，例如 `Brush.Surface.Input`。旧资源名暂时映射到新资源，迁移期间不破坏现有页面。

### 字体和字号

| Token | 当前值/用途 |
|---|---|
| `Font.UI` | Segoe UI，界面正文和一般控件 |
| `Font.Mono` | Cascadia Code，技术标签、路径、快捷键和表格标识 |
| `Font.Terminal` | Cascadia Mono，终端等宽显示 |
| `Font.Emoji` | Segoe UI Emoji，类型图标回退字体 |
| `Type.Size.Caption` / `Label` | 8 / 9，紧凑标签、表格标题和状态元信息 |
| `Type.Size.Small` / `Body` / `BodyLarge` | 10 / 11 / 12，控件、正文、较醒目正文 |
| `Type.Size.Terminal` | 14，终端默认字号；用户设置可覆盖 |
| `Type.Size.Heading` / `PageTitle` | 18 / 22，区块标题、页面标题 |

字体/字号统一由 `Font.*` 与 `Type.Size.*` 控制。表格和技术标签可以使用 Mono，但普通说明文字不应默认使用等宽字体。

### 间距和控件尺寸

`Space.XXS/XS/SM/MD/LG/XL/XXL` 当前对应 2/4/6/8/12/16/24 DIP；`Space.HeaderPadding`、`Space.RowPadding`、`Space.ComboPadding`、`Space.ComboOptionPadding` 用于控件内边距；`Control.RowHeight` 是通用下拉选项的 27 DIP 行高。

后续可扩展 `Control.ButtonHeight.*`、`Control.InputHeight.*`、`Control.DialogWidth.*`、`Radius.*`、`Elevation.*`。新增前先确认至少两个组件共享同一用途，避免过度 token 化。

已补充 `Space.Inset.LG`、`Space.Gap.Top.*`、`Space.Gap.Bottom.*`、`Space.Gap.Right.*`，以及 `Space.Input.Padding`、`Space.Button.Padding`。`Control.InputHeight` 和 `Control.RowHeight` 当前均为 27 DIP，分别用于输入控件和通用下拉选项行高。

## 使用约定

1. 新增视图先找语义 token；只有内容专用颜色或真正的一次性尺寸才使用字面值。
2. 禁止把终端黑底、日志成功色等内容语义机械替换成普通 Surface/Status；应使用专用 token。
3. 交互态要成套定义：默认、悬浮、按下、选中、焦点、禁用。文字与底色需保持可辨识对比。
4. 组件样式优先使用 `App.xaml` 的共享隐式样式或具名样式，避免各页面复制模板。
5. WPF `StaticResource` 用于静态主题；如果未来支持运行时切换主题，主题可变资源需改用 `DynamicResource`，并在主题字典之间替换颜色/Brush。
6. Token 改值前检查终端、编辑器和输出区等专用内容表面，避免影响可读性或终端模拟的颜色语义。
7. 注意 WPF 属性类型：控件的 `Width`/`Height` 使用 `Double`；`RowDefinition.Height` 和 `ColumnDefinition.Width` 使用 `GridLength`。两种 token 即使数值相同也必须分开定义，避免 XAML 运行时解析失败。

## 迁移顺序

1. 应用框架与通用控件：表格、下拉框、输入框、按钮、标签页和弹窗基础样式。
2. SFTP、主机、脚本中心和快捷命令等重复表格/表单。
3. 终端工具栏、分屏与专用终端内容颜色。
4. 其余确认弹窗、诊断和低频页面；检查并清理兼容别名与散落字面值。

每个阶段保持现有布局不变，先统一样式入口，再做有明确设计目标的视觉调整，减少样式迁移引入的行为变化。

## 迁移进度

- **第一阶段已完成**：`App.xaml` 中的表格和下拉框共享样式已改用语义 token；`MainWindow.xaml` 的工作区样式、控件色、技术字体和字号已迁移。终端画面、脚本编辑器及运行输出采用专用内容 token。
- **第二阶段已完成**：SFTP 文件与传输列表、服务器监控卡片、终端工具栏/查找/复制粘贴浮层，以及主机编辑、凭据、指纹确认、脚本参数、快捷命令、SFTP 输入和同步弹窗均已改用语义颜色、字体和字号 token。补充了列表悬浮色、鲜明成功色和紧凑字号档位。终端屏幕仍使用终端专属表面与文字 token。
- **第三阶段已完成**：对话框边距、常用上下/右侧间距、输入框和按钮内边距、常用输入控件高度已抽为布局 token。代码动态创建的脚本参数/快捷命令输入控件也改为引用语义颜色/字号资源；所有旧 Brush 别名已无引用并清理。
- **第四阶段已完成**：主机配置与 SSH 指纹弹窗共享尺寸 token；凭据/主机选择弹窗共用最小宽度 token；表单行高、传输按钮尺寸和所有主窗口分隔拖拽条厚度已抽为组件 token。终端分屏比例与文件表格列宽仍由各自布局控制。
- **第五阶段已完成**：脚本运行表单和快捷命令动态创建的输入控件也引用颜色、字体、字号、间距和高度 token；服务器监控趋势图网格与曲线色，以及脚本语法高亮色都来自设计 token。下拉选项模板的 `Height` 保持 WPF 字面值 `27`，避免运行时模板解析失败。
- **最终审计已完成**：抽取快捷命令变量输入区、脚本运行参数行及分组标题、终端选区浮动工具栏的剩余共享尺寸和间距。当前 App XAML/C# 中没有散落的十六进制颜色、字体名或字号常量；页面专属窗口尺寸、列宽、网格比例、图标几何和控件内部细节仍留在原视图，避免为一次性布局值建立难以维护的 token。分屏工具栏行高使用单独的 `GridLength` token，与控件高度类型区分。
- **标签组件**：主终端与分屏标签使用共享 `TabItem` 模板；各交互状态保持固定内边距、边框厚度和指标线占位，通过背景/文字/边框色与固定高度强调线表示激活状态，不再依赖标签尺寸变化。标签的间距及激活线行高已纳入 token。
- **状态**：设计 token 基础建设和已识别的迁移阶段已完成。后续新建/改造视图时按本规范直接使用；需要调整现有视觉尺度时再按明确目标扩展语义 token。
