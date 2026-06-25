# UnityUIFlow 新页面接入最小模板

本文档给出一个可以直接复制改造的最小模板，目标是让一个新的 UIToolkit `EditorWindow` 页面在最短路径下接入 `UnityUIFlow`。

模板文件已放到：

- `docs/templates/unityuiflow-minimal-page/MinimalPageWindow.uxml`
- `docs/templates/unityuiflow-minimal-page/MinimalPageWindow.uss`
- `docs/templates/unityuiflow-minimal-page/MinimalPageWindow.cs`
- `docs/templates/unityuiflow-minimal-page/minimal-page.yaml`

## 1. 最小接入步骤

1. 复制模板文件到你的业务目录
2. 修改 `namespace`、窗口类名、菜单路径
3. 修改 UXML/USS 路径常量
4. 保留关键控件名：
   - `username-input`
   - `password-input`
   - `submit-button`
   - `status-label`
   - `toast-host`
   - `toast-message`
5. 先跑模板 YAML，确认链路通
6. 再逐步替换为真实业务文案和真实交互

## 2. 模板设计原则

这个模板刻意保留了当前项目最常用、最稳定的测试点：

- 两个 `TextField`
- 一个按钮
- 一个状态标签
- 一个 toast 宿主
- 一个可自动消失的 toast

这样可以直接覆盖：

- `type_text_fast`
- `click`
- `assert_text_contains`
- `assert_visible`
- `repeat_while`
- `assert_not_visible`

## 3. 推荐改造方式

### 3.1 如果是表单页

直接沿用模板结构，只改字段和业务处理逻辑。

### 3.2 如果是列表页

保留：

- 页面根
- 工具栏
- 状态标签
- 列表根
- 空态标签

再把 `submit-button` 替换成 `refresh-button`、`create-button` 等真实动作。

### 3.3 如果是设置页

保留：

- `save-button`
- `reset-button`
- `status-label`

toast 结构依然建议保留，便于自动化断言保存结果。

## 4. 首个 YAML 冒烟用例建议

每个新页面接入后，建议先写一个最小冒烟 YAML，满足：

1. 能打开窗口
2. 能定位关键元素
3. 能完成一次主操作
4. 能断言一个稳定结果

如果页面有异步反馈，再补一条 toast 或 loading 的等待用例。

## 5. 何时再扩展

当最小模板跑通后，再逐步增加：

- `[data-*]` 业务语义选择器
- 列表项类名
- dialog 宿主
- loading 宿主
- 批量 YAML 目录

不要一开始就把页面做得很复杂再接自动化，先让主流程通，再迭代扩展。
