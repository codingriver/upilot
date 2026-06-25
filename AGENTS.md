# UnityUIFlow Agent Instructions

鏈枃浠朵负 AI coding agent 鎻愪緵鍏充簬 UnityUIFlow 椤圭洰鐨勬満鍣ㄥ彲璇荤害瀹氫笌鍏ㄦ櫙淇℃伅銆傞槄璇昏€呭簲瀵规湰椤圭洰涓€鏃犳墍鐭ワ紝鍥犳鏈枃鍔涙眰鑷寘鍚€佸噯纭€佸彲鎿嶄綔銆?
---

## 0. MCP 鏈嶅姟鍣ㄨ繛鎺ラ厤缃?
> **淇敼绔彛 / 鍦板潃鏃跺彧闇€鏀规湰鑺傦紝鏂囨。鍏朵綑浣嶇疆鍧囧紩鐢ㄦ澶勫畾涔夈€?*

```ini
MCP_HTTP  = http://127.0.0.1:8011/mcp   # HTTP 绔偣锛圫treamable HTTP锛孭OST /mcp锛?MCP_WS    = ws://127.0.0.1:8765         # WebSocket 绔偣锛圲nityPilotBridge锛?MCP_PROTO = 2024-11-05                  # MCP 鍗忚鐗堟湰
```

### 0.1 MCP HTTP 地址强制约束

- **唯一允许配置给 AI 工具 / MCP client 的 HTTP MCP endpoint 是 `http://127.0.0.1:8011/mcp`。**
- **禁止**把 `http://127.0.0.1:8011` 或 `http://127.0.0.1:8011/` 当作 MCP endpoint 使用。
- `http://127.0.0.1:8011` 和 `http://127.0.0.1:8011/` 只允许作为浏览器/人工探测用的 landing page，用于提示真实 MCP endpoint 在 `/mcp`。
- 所有协议握手、工具调用、Unity 编译触发、YAML headed 测试验证，都必须 `POST http://127.0.0.1:8011/mcp`。
- 如果 AI 工具从根路径响应中读取到地址，必须继续使用响应里声明的 `mcpEndpoint` 字段；不得自行截断 `/mcp`。

**鏈€灏忔帰娴嬭姹傦紙curl锛屼娇鐢ㄤ笂鏂?`MCP_HTTP`锛夛細**

```bash
curl -s -X POST http://127.0.0.1:8011/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"agent","version":"1.0"}}}'
```

---

## 1. 椤圭洰姒傝

**UnityUIFlow** 鏄竴涓熀浜?Unity Editor + UIToolkit 鐨?YAML 椹卞姩 UI 鑷姩鍖栨祴璇曟鏋躲€傚畠璁╁紑鍙戣€呭拰鑷姩鍖栧伐鍏疯兘澶熼€氳繃缂栧啓 YAML 鐢ㄤ緥锛屽 `EditorWindow` 涓殑 `VisualElement` 鏍戞墽琛岀偣鍑汇€佽緭鍏ャ€佹嫋鎷姐€佹柇瑷€銆佹埅鍥剧瓑鎿嶄綔锛岃€屾棤闇€鎵嬪啓澶ч噺 C# 娴嬭瘯浠ｇ爜銆?
- **鏍稿績瀹氫綅**锛欵ditor-Only 鐨勮嚜鍔ㄥ寲娴嬭瘯鍩虹璁炬柦锛堥潪 Runtime锛夈€?- **娴嬭瘯瀵硅薄**锛歎nity Editor 鍐呭熀浜?UIToolkit 鐨?`EditorWindow`锛堥潪 Game View / Play Mode锛夈€?- **鐢ㄤ緥褰㈠紡**锛歒AML 鏂囦欢涓轰富锛孋# Fixture 涓鸿緟銆?- **鎵ц妯″紡**锛?  - **Headed 妯″紡**锛氬甫鍙鍖栫獥鍙ｃ€侀珮浜€佹杩涳紝鐢ㄤ簬鏈湴寮€鍙戜笌 Agent 楠岃瘉锛堝己鍒讹級銆?  - **Headless / CI 妯″紡**锛氶€氳繃 Unity CLI `-executeMethod` 鎵归噺鎵ц锛岀敤浜庢寔缁泦鎴愩€?
---

## 2. 鎶€鏈爤涓庣増鏈害鏉?
| 缁勪欢 | 鐗堟湰 / 璇存槑 | 鏉ユ簮 |
|------|-------------|------|
| Unity Editor | `6000.6.0a2` | `ProjectSettings/ProjectVersion.txt` |
| `com.unity.test-framework` | `1.7.0` | `Packages/manifest.json` |
| `com.unity.ui.test-framework` | `6.3.0` | 瀹樻柟 UI Test Framework锛圥anelSimulator 妗ユ帴锛?|
| `com.unity.inputsystem` | `1.19.0` | 閿洏/杈撳叆楂樹繚鐪熼摼璺?|
| `com.unity.ui` | `2.0.0` | UIToolkit 鏍稿績 |
| YAML 瑙ｆ瀽 | `YamlDotNet.dll` | 鍐呭祵棰勭紪璇?DLL |
| 鍗曞厓娴嬭瘯 | NUnit + Unity Test Framework | 浠?Editor Mode锛屾棤 PlayMode 娴嬭瘯 |
| MCP 鏈嶅姟鍣?| `unityuiflow` | WebSocket / HTTP / stdio 澶氬崗璁敮鎸?|

**鍏抽敭绾︽潫**锛?- 鎵€鏈夌敓浜т唬鐮佸潎鍦?`Editor/` 涓嬶紝缂栬瘧鐩爣涓?**Editor only**銆?- 娴嬭瘯浠ｇ爜鍦?`Samples~/Tests/` 涓嬶紝鍚屾牱涓?**Editor only**銆?- 涓嶅瓨鍦?PlayMode 娴嬭瘯锛屼篃涓嶅瓨鍦?Runtime 閫昏緫銆?
---

## 3. 浠ｇ爜缁勭粐涓庢ā鍧楀垝鍒?
### 3.1 鐩綍缁撴瀯

```
Assets/
鈹溾攢鈹€ UnityUIFlow/Editor/          鈫?妗嗘灦鏍稿績锛?5 涓?C# 鏂囦欢锛屽崟涓€鍚嶇О绌洪棿 UnityUIFlow锛?鈹?  鈹溾攢鈹€ Actions/                 鈫?38 涓唴缃姩浣滃疄鐜帮紙IAction锛?鈹?  鈹溾攢鈹€ Cli/                     鈫?鍛戒护琛岃В鏋愪笌 CI 鍏ュ彛
鈹?  鈹溾攢鈹€ Core/                    鈫?棰嗗煙妯″瀷銆佸紓甯搞€侀厤缃€佸伐鍏风被
鈹?  鈹溾攢鈹€ Execution/               鈫?鎵ц寮曟搸锛圱estRunner銆丼tepExecutor銆丒lementFinder锛?鈹?  鈹溾攢鈹€ Fixtures/                鈫?NUnit Fixture 鍩虹被涓庡畼鏂规祴璇曟鏋舵ˉ鎺?鈹?  鈹溾攢鈹€ Headed/                  鈫?Editor 鍙鍖栫獥鍙ｏ紙TestRunnerWindow锛屽惈鎵归噺鎵ц涓庡崟鏉℃杩涜皟璇曪級
鈹?  鈹溾攢鈹€ Parsing/                 鈫?YAML 瑙ｆ瀽銆侀€夋嫨鍣ㄧ紪璇戙€佹墽琛岃鍒掓瀯寤?鈹?  鈹斺攢鈹€ Reporting/               鈫?鎴浘銆丮arkdown/JSON 鎶ュ憡
鈹溾攢鈹€ Examples/                    鈫?绀轰緥绐楀彛銆佹祴璇曠晫闈?UI 涓庢祴璇曠敤渚?鈹?  鈹溾攢鈹€ Editor/                  鈫?绀轰緥 EditorWindow锛圓cceptance + Coverage锛?鈹?  鈹溾攢鈹€ Uxml/                    鈫?UXML 甯冨眬鏂囦欢锛堢ず渚?+ 娴嬭瘯鐣岄潰锛?鈹?  鈹溾攢鈹€ Uss/                     鈫?鍏变韩鏍峰紡琛?鈹?  鈹溾攢鈹€ Yaml/                    鈫?鑷姩鍖栫敤渚嬶紙绀轰緥 + 鍥炲綊娴嬭瘯锛?鈹?  鈹斺攢鈹€ Tests/                   鈫?娴嬭瘯姹囩紪锛圲nityUIFlow.Tests锛?鈹斺攢鈹€ Plugins/                     鈫?YamlDotNet.dll
```

### 3.2 鏍稿績妯″潡鍏崇郴

```
YAML 鏂囦欢
    鈫?Parsing锛圷amlTestCaseParser 鈫?SelectorCompiler 鈫?ExecutionPlanBuilder锛?    鈫?Execution锛圱estRunner 鈫?StepExecutor锛?    鈫?Actions锛圓ctionRegistry 瑙ｆ瀽 ~38 涓?IAction锛?+ Locators锛圗lementFinder锛?    鈫?Fixtures / TestIntegrations锛圲nityUIFlowFixture<TWindow> + PanelSimulator 妗ユ帴锛?    鈫?Reporting锛圫creenshotManager + MarkdownReporter + JsonResultWriter锛?```

### 3.3 鍏抽敭绫诲瀷閫熸煡

| 绫诲瀷 | 鏂囦欢 | 鑱岃矗 |
|------|------|------|
| `TestRunner` | `Execution/UnityUIFlow.Execution.cs` | 涓?API锛歚RunFileAsync`銆乣RunSuiteAsync` |
| `StepExecutor` | `Execution/UnityUIFlow.Execution.cs` | 鍗曟鎵ц銆佽秴鏃躲€侀珮浜€佸け璐ユ埅鍥?|
| `ElementFinder` | `Execution/UnityUIFlow.Locators.cs` | CSS-like 閫夋嫨鍣ㄥ紩鎿?|
| `ActionRegistry` | `Actions/UnityUIFlow.Actions.cs` | 鍔ㄤ綔鍙戠幇涓庤В鏋?|
| `UnityUIFlowFixture<TWindow>` | `Fixtures/UnityUIFlow.Fixtures.cs` | C# 娴嬭瘯鍩虹被 |
| `UnityUIFlowCliEntry` | `Cli/UnityUIFlow.Cli.cs` | CI 鍏ュ彛锛歚RunAllFromCommandLine` |
| `UnityUIFlowProjectSettings` | `Core/UnityUIFlow.Settings.cs` | `ProjectSettings/UnityUIFlowSettings.asset` |

---

## 4. 寮€鍙戣鑼冿紙UXML / USS / C# / YAML锛?
### 4.1 UXML 鍛藉悕瑙勮寖

- **鍏抽敭浜や簰鍏冪礌蹇呴』璁剧疆鍞竴 `name`**锛氳緭鍏ユ銆佹寜閽€佺姸鎬佹爣绛俱€乼oast 鏍硅妭鐐广€佸垪琛ㄦ牴鑺傜偣銆佹粴鍔ㄥ鍣ㄣ€?- **鍛藉悕椋庢牸**锛氬叏灏忓啓鐭í绾匡紝濡?`username-input`銆乣login-button`銆乣status-label`銆乣toast-host`銆?- **class 璐熻矗鏍峰紡锛屼笉璐熻矗涓诲畾浣?*锛歒AML 浼樺厛浣跨敤 `#name`锛岄伩鍏?`.class` 浣滀负涓婚€夋嫨鍣ㄣ€?- **涓氬姟璇箟鐢?`userData` 鎻愪緵 `data-*`**锛?  ```csharp
  saveButton.userData = new Dictionary<string, string>(StringComparer.Ordinal)
  {
      ["data-role"] = "primary",
  };
  ```
  瀵瑰簲 YAML 閫夋嫨鍣細`[data-role=primary]`

### 4.2 UXML 缁撴瀯瑙勮寖

- 椤甸潰鏍硅妭鐐规帹鑽?`feature-root`锛屼富闈㈡澘鎺ㄨ崘 `feature-panel`銆?- 鍔ㄦ€佸唴瀹逛繚鐣欑ǔ瀹氬涓伙細`toast-host`銆乣dialog-host`銆乣result-panel`銆?- 鏂█鏂囨湰蹇呴』钀藉湪绋冲畾鍛藉悕鍏冪礌涓婏紙濡?`#status-label`锛夛紝涓嶈鍙墦鍗板埌 Console銆?
### 4.3 USS 瑙勮寖

- `display: none`銆乣visibility: hidden`銆乣opacity: 0` 鐨勫厓绱犱細琚鏋惰涓?*涓嶅彲瑙?*銆?- 鎸夐挳涓嶈琚€忔槑閬僵瑕嗙洊锛岀‘淇濆彲鐐瑰嚮鍖哄煙绋冲畾銆?
### 4.4 C# 浜や簰瑙勮寖

- 鎸夐挳鐐瑰嚮閫昏緫**浼樺厛娉ㄥ唽 `MouseUpEvent`**锛屼笌褰撳墠鑷姩鍖栫偣鍑昏矾寰勫吋瀹规€ф渶濂斤細
  ```csharp
  loginButton.RegisterCallback<MouseUpEvent>(_ => HandleLogin());
  ```
- 琛ㄥ崟杈撳叆浼樺厛浣跨敤 `TextField`銆?- 椤甸潰鍒濆鍖栧繀椤诲彲閲嶅锛氶噸澶嶆墦寮€绐楀彛鍚庡簲鍥炲埌绋冲畾鍒濆鐘舵€併€?- 鑻ラ〉闈㈤€氳繃 YAML `fixture.host_window` 鎵撳紑锛屽彲瀹炵幇 `IUnityUIFlowTestHostWindow.PrepareForAutomatedTest()` 缁熶竴鏋勫缓鍏ュ彛銆?
### 4.5 YAML 璁捐瑙勮寖

- 閫夋嫨鍣?*浼樺厛浣跨敤 `#name`**銆?- 瀵瑰姩鎬佸満鏅厛 `wait_for_element` 鍐嶆柇瑷€銆?- 杈撳叆娴嬭瘯鍐掔儫鍦烘櫙浼樺厛 `type_text_fast`锛岄渶瑕佽瀵熼€愬瓧杈撳叆鏃剁敤 `type_text`銆?- 鐢ㄤ緥鏂囦欢鎵╁睍鍚嶅繀椤绘槸 `.yaml`锛堜笉鏀寔 `.yml`锛夈€?- 鏂伴〉闈氦浠樿嚦灏戝寘鍚細`.uxml`銆乣.uss`銆乣.cs`銆乣.yaml`锛堟渶灏忓啋鐑熺敤渚嬶級銆?
---

## 5. 娴嬭瘯绛栫暐

### 5.1 娴嬭瘯鍒嗗眰

| 灞傜骇 | 浣嶇疆 | 璇存槑 |
|------|------|------|
| 鍗曞厓娴嬭瘯 | `Samples~/Tests/UnityUIFlow.ParsingAndPlanningTests.cs` | 瑙ｆ瀽鍣ㄣ€侀€夋嫨鍣ㄧ紪璇戙€佽鍒掓瀯寤恒€佹ā鏉挎覆鏌?|
| 闆嗘垚娴嬭瘯 | `Samples~/Tests/UnityUIFlow.LocatorsAndActionsTests.cs` | 鐪熷疄 EditorWindow 涓婄殑鍔ㄤ綔涓庡畾浣嶅櫒 |
| 楠屾敹娴嬭瘯 | `Samples~/Tests/UnityUIFlow.ExamplesAcceptanceTests.cs` | 绔埌绔墽琛?`Samples~/Yaml/*.yaml` |
| Headed/Batch 娴嬭瘯 | `Samples~/Tests/UnityUIFlow.HeadedTests.cs` | 鍙鍖栭潰鏉裤€丷untimeController銆佸亸濂借缃?|
| CLI/鎶ュ憡娴嬭瘯 | `Samples~/Tests/UnityUIFlow.ExecutionReportingCliTests.cs` | CLI 鍙傛暟銆佹姤鍛婄敓鎴愩€丳rojectSettings 瑕嗙洊 |

### 5.2 娴嬭瘯鍛藉悕涓庢ā寮?
- 娴嬭瘯姹囩紪锛歚UnityUIFlow.Tests.asmdef`锛圗ditor only锛屽紩鐢?`UnityUIFlow`銆乣UnityUIFlow.Examples`銆佸畼鏂?UI Test Framework锛夈€?- 鍚屾娴嬭瘯鐢?`[Test]`锛岄渶瑕?Editor 绐楀彛鐢熷懡鍛ㄦ湡鐨勭敤 `[UnityTest]` 杩斿洖 `IEnumerator`銆?- Fixture 娴嬭瘯缁ф壙 `UnityUIFlowFixture<TWindow>`锛屾硾鍨嬪弬鏁颁负鍏蜂綋鐨?`EditorWindow` 绫诲瀷銆?- Strict 妯″紡娴嬭瘯楠岃瘉瀹樻柟椹卞姩寮哄埗瑕佹眰锛?  - `RequireOfficialPointerDriver = true`
  - `RequireInputSystemKeyboardDriver = true`
  - `RequireOfficialHost = true`

---

## 6. MCP 娴嬭瘯寮哄埗瑙勮寖锛圓gent 鎵ц YAML 蹇呰锛?
> **鏍稿績鍘熷垯锛歒AML 娴嬭瘯 = MCP 鏈嶅姟鍣?+ Headed 妯″紡銆傜己涓€涓嶅彲銆?*

### 6.1 纭€ц鍒?
1. **YAML 娴嬭瘯鍙兘閫氳繃 MCP 鏈嶅姟鍣ㄦ墽琛屻€?*
2. **YAML 娴嬭瘯蹇呴』浣跨敤 Headed 妯″紡銆?*
3. **娌℃湁鍙敤 MCP 鏈嶅姟鍣ㄦ椂锛岀姝㈣繍琛?YAML 娴嬭瘯銆?*
4. **绂佹鐢?CLI銆乁nity Test Runner銆佷复鏃惰剼鏈€佹墜宸ョ偣鍑绘垨鍏朵粬鏇夸唬鏂瑰紡鍐掑厖 YAML MCP 娴嬭瘯缁撴灉銆?*
5. **Agent 鍙互鍦ㄦ病鏈?MCP 鐨勬儏鍐典笅淇敼浠ｇ爜銆佷慨澶?Bug銆佸疄鐜伴渶姹傦紝浣嗕笉鑳藉０绉板凡瀹屾垚 YAML 娴嬭瘯楠岃瘉銆?*
6. 杈撳嚭鈥滃凡楠岃瘉 YAML 娴嬭瘯閫氳繃鈥濈殑鍓嶆彁锛屽繀椤绘槸 MCP 宸ュ叿鐪熷疄鎵ц鎴愬姛銆?
### 6.1.1 MCP 鏈嶅姟鍣ㄥ彲鐢ㄦ€ф帰娴嬭鍒欙紙寮哄埗锛?
> **鍦ㄤ换浣曟儏鍐典笅锛孉gent 涓嶅緱鍦ㄦ湭瀹屾垚鎺㈡祴鐨勬儏鍐典笅鏂█鈥滿CP 鏈嶅姟鍣ㄤ笉鍙敤鈥濇垨鈥濇病鏈?MCP 鏈嶅姟鍣ㄢ€濄€?*

鍒ゅ畾 MCP 鏈嶅姟鍣ㄤ笉鍙敤涔嬪墠锛屽繀椤绘寜椤哄簭瀹屾垚浠ヤ笅鎺㈡祴姝ラ锛?
1. **鍗忚鎻℃墜**锛氱洿鎺?`POST $MCP_HTTP`锛堣搂0锛夛紙3s 瓒呮椂锛夛紝鍙戦€?`initialize` 璇锋眰锛?
   ```json
   {鈥渏sonrpc鈥?鈥?.0鈥?鈥漣d鈥?1,鈥漨ethod鈥?鈥漣nitialize鈥?鈥漰arams鈥?{鈥減rotocolVersion鈥?鈥?024-11-05鈥?鈥漜apabilities鈥?{},鈥漜lientInfo鈥?{鈥渘ame鈥?鈥漚gent鈥?鈥漹ersion鈥?鈥?.0鈥潁}}
   ```

2. **纭 Unity 宸茶繛鎺?*锛氳嫢 `initialize` 鎴愬姛锛岃皟鐢?`unity_mcp_status` 纭 Unity Editor 宸茶繛鎺ワ紙`connected: true`锛夈€?3. **缁撹**锛氬彧鏈夊綋涓婅堪姝ラ**鍏ㄩ儴澶辫触**鍚庯紝鎵嶈兘鍒ゅ畾 MCP 鏈嶅姟鍣ㄤ笉鍙敤锛屽苟璁板綍澶辫触鍘熷洜銆?
> 杩炴帴鎷掔粷鎴栬秴鏃舵湰韬嵆璇存槑绔彛涓嶉€氾紝鏃犻渶鎻愬墠鐢?`netstat` / `Get-NetTCPConnection` 妫€鏌ョ鍙ｇ姸鎬併€?
**杩濊绀轰緥**锛氬湪鏈彂閫?`initialize` 璇锋眰鐨勬儏鍐典笅锛屼粎鍑€濅唬鐮佷腑鐪嬩笉鍒?MCP server 瀹氫箟鈥濇垨鈥濈鍙ｆ湭 Listen鈥濆氨澹扮О MCP 涓嶅彲鐢ㄣ€?
### 6.2 Headed 妯″紡妫€鏌?
鎵ц鍓嶅繀椤荤‘璁ら」鐩牴鐩綍 `.unityuiflow.json` 婊¤冻锛?
```json
{
  "headed": true,
  "reportPath": "./Reports",
  "screenshotOnFailure": true,
  "defaultTimeoutMs": 10000,
  "customActionAssemblies": ["UnityUIFlow.Tests"]
}
```

鑻?`headed` 涓嶆槸 `true`锛孉gent 搴斿厛淇閰嶇疆锛屽啀缁х画娴嬭瘯娴佺▼銆?
### 6.3 MCP 服务器连接流程
1. 检查当前是否已存在可用 MCP 服务器（不能只看进程，必须能直接调用 tool）。
2. 发送 `POST $MCP_HTTP` 协议握手请求，确认输出端点可达。
3. 调用 `unity_mcp_status` 确认 Unity Editor 已连接（`connected: true`）。
4. 若 HTTP 端点不可达，在 Unity 中通过 **UnityUIFlow → UnityPilot** 启动 MCP Server，确保端口配置正确。
5. 再次确认以下工具真实可调用：
### 6.4 鏍囧噯 YAML 鎵ц宸ュ叿绀轰緥

```text
宸ュ叿: mcp_unitypilot_unity_editor_e2e_run
鍙傛暟:
  - specPath: Samples~/Yaml/01-basic-login.yaml
  - artifactDir: D:\UnityUIFlow\artifacts
  - exportZip: true
  - stopOnFirstFailure: true
  - webhookOnFailure: true
```

Agent 搴旀牴鎹綋鍓嶄細璇濆疄闄呮毚闇茬殑宸ュ叿鍚嶉€夋嫨绛変环璋冪敤鏂瑰紡銆?
### 6.5 鏃?MCP 鏃剁殑姝ｇ‘琛ㄨ堪

- 鉁?鈥滀唬鐮佸凡淇敼锛屼絾 YAML 娴嬭瘯灏氭湭鎵ц锛屽洜涓哄綋鍓嶆病鏈夊彲鐢?MCP 鏈嶅姟鍣ㄣ€傗€?- 鉁?鈥滃綋鍓嶄粎瀹屾垚瀹炵幇锛屾湭瀹屾垚 MCP 楠岃瘉銆傗€?- 鉂?鈥滄祴璇曞凡閫氳繃鈥濓紙鏈湡瀹炴墽琛屾椂绂佹锛夈€?
### 6.6 鎵归噺娴嬭瘯鍒嗙墖寮哄埗瑙勫垯

> **鏍稿績鍘熷垯锛氬崟娆¤皟鐢ㄤ笉寰楄秴杩?15 涓?YAML 鏂囦欢锛涜秴杩囨椂蹇呴』鐢辫皟鐢ㄦ柟鍒嗙墖锛岄€愭壒鍙戦€併€?*

Unity Editor 鍚屼竴鏃堕棿鍙兘鎵ц涓€涓?`ExecutionContext`锛坄EDITOR_BUSY` 閿侊級銆傚崟娆′紶鍏ヨ繃澶?YAML 鏂囦欢浼氬鑷达細
- MCP 璋冪敤鍝嶅簲瓒呮椂锛堥粯璁?30s锛孶nity 渚у疄闄呮墽琛屽彲鑳?300s+锛?- Unity 涓荤嚎绋嬭闀挎椂闂村崰鐢紝鏃犳硶骞查
- 涓€旇嫢鍙戠敓 Domain Reload锛屾墽琛岀姸鎬佷涪澶变笖鏃犳硶鎭㈠

#### 纭€ц鍒?
1. **鍗曟 `unity_uiflow_run_batch` 鐨?`yamlPaths` 涓嶅緱瓒呰繃 15 涓枃浠躲€?*
2. **鐢ㄤ緥鎬绘暟瓒呰繃 15 鏃讹紝璋冪敤鏂瑰繀椤诲湪 Agent 渚у垎鐗囷紝閫愭壒鍙戦€併€?* 榛樿 `batch_size = 10`锛屽彲鏍规嵁鍗曟枃浠跺钩鍧囪€楁椂璋冩暣锛屼絾涓婇檺涓?15銆?3. **蹇呴』绛夊緟涓婁竴鎵?`status` 涓?`completed` / `failed` / `aborted` 鍚庯紝鎵嶈兘鍙戦€佷笅涓€鎵广€?* Unity 渚у瓨鍦?`EDITOR_BUSY` 閿侊紝骞跺彂璋冪敤浼氳繑鍥?`EDITOR_BUSY` 閿欒銆?4. **浼樺厛浣跨敤 `unitypilot~/scripts/batch_yaml_runner.py`** 鎵ц鎵归噺娴嬭瘯銆傝鑴氭湰鑷姩瀹屾垚锛氱洰褰曟壂鎻?鈫?鎸?`batch_size` 鍒嗙墖 鈫?閫愭壒璋冪敤 MCP 鈫?绛夊緟瀹屾垚 鈫?姹囨€荤粨鏋?鈫?淇濆瓨澶辫触娓呭崟鏀寔閲嶈瘯銆?5. **鑻ョ洿鎺ヨ皟鐢?MCP 宸ュ叿**锛屽繀椤诲鐜扮浉鍚岀殑鍒嗙墖閫昏緫锛?   - 鍒囩墖鍚庢瘡鎵硅皟鐢?`unity_uiflow_run_batch`锛堟垨 `unity_uiflow_run_file` 閫愭潯锛?   - 杞绛夊緟褰撳墠 batch 瀹屾垚锛堥€氳繃杩斿洖鐨?`executionId` 鏌ヨ `unity_uiflow_run_file` / `unity_uiflow_run_suite` 绛夌粨鏋滐級
   - 鍐嶅彂閫佷笅涓€鎵?
#### 鏍囧噯鎵归噺鎵ц绀轰緥

```powershell
# 浣跨敤 Agent 渚ф壒閲忚剼鏈紙鎺ㄨ崘锛?python unitypilot~/scripts/batch_yaml_runner.py `
  --yaml-dir Samples~/Yaml `
  --batch-size 10 `
  --report-dir Reports/AgentBatch `
  --headed true

# 閲嶈瘯鏌愪竴鎵?python unitypilot~/scripts/batch_yaml_runner.py `
  --retry-from Reports/AgentBatch/batch_003_failed.json
```

```text
# 鍗曟潯 MCP 宸ュ叿璋冪敤绀轰緥锛堜粎閫傜敤浜?鈮?5 涓枃浠讹級
宸ュ叿: unity_uiflow_run_batch
鍙傛暟:
  - yamlPaths: [file1.yaml, file2.yaml, ..., file10.yaml]
  - batchSize: 10
  - batchOffset: 0
  - headed: true
  - reportOutputPath: Reports/AgentBatch/batch_000
  - stopOnFirstFailure: false
  - defaultTimeoutMs: 10000
```

#### 鎵ц妯″瀷

Agent 鍒嗙墖 鈫?MCP 杞彂 鈫?Unity 涓茶鎵ц锛堝悓 batch 鍐呮枃浠堕€愪釜鎵ц锛夆啋 MCP 杞绛夊緟 鈫?Agent 鏀跺埌缁撴灉 鈫?鍙戦€佷笅涓€鎵?
- 鍚屼竴 batch 鍐呯殑鐢ㄤ緥鍦?Unity 涓荤嚎绋嬩笂**涓茶鎵ц**锛堟墦寮€绐楀彛 鈫?鎵ц 鈫?鍏抽棴绐楀彛锛屽惊鐜級
- MCP 杞闂撮殧 500ms锛屼笉闃诲 Unity
- 姣忔壒鏈夌嫭绔嬭秴鏃讹紙榛樿 `120s + defaultTimeoutMs/1000 脳 batch_size + 120s`锛?
---

## 7. 鏋勫缓涓庢墽琛屽懡浠?
### 7.1 鏈湴寮€鍙戞墽琛?
- **Headed 鍗曟潯璋冭瘯鎵ц**锛氶€氳繃 `UnityUIFlow > Test Runner` Editor 绐楀彛閫変腑鍗曚釜鐢ㄤ緥锛屽湪 Details 闈㈡澘浣跨敤 Run Mode = Step 杩涜姝ヨ繘璋冭瘯銆?- **Headed 鎵归噺鎵ц**锛歚UnityUIFlow > Test Runner`锛岄€夋嫨 YAML 鐩綍鎵归噺杩愯銆?- **C# 鍗曞厓/闆嗘垚娴嬭瘯**锛歎nity Test Runner 鈫?PlayMode / EditorMode 鏍囩 鈫?浠?Editor Mode 鍙敤銆?
### 7.2 CI / 鍛戒护琛屾墽琛?
椤圭洰浠呴€氳繃 Unity 鍘熺敓 `-executeMethod` 椹卞姩锛屾棤 Makefile銆丏ocker 鎴栧閮ㄦ瀯寤鸿剼鏈€?
**鍏ュ彛绫?*锛歚UnityUIFlow.UnityUIFlowCliEntry`
**鍏ュ彛鏂规硶**锛歚RunAllFromCommandLine`

绀轰緥鍛戒护锛堟潵鑷?`.github/workflows/unity-uiflow-sample.yml`锛夛細

```powershell
"C:\Program Files\Unity\Hub\Editor\6000.6.0a2\Editor\Unity.exe" `
  -projectPath $PWD `
  -quit `
  -executeMethod UnityUIFlow.UnityUIFlowCliEntry.RunAllFromCommandLine `
  -unityUIFlow.headed false `
  -unityUIFlow.reportPath ./Reports `
  -unityUIFlow.screenshotOnFailure true `
  -unityUIFlow.testFilter *
```

**娉ㄦ剰**锛欳LI 鏄庣‘绂佹 `-batchmode`锛屽繀椤讳娇鐢ㄥ甫绐楀彛鐨勭紪杈戝櫒妯″紡鎵ц娴嬭瘯锛堝嵆浣?`headed=false`锛屼篃涓嶈兘鍔?`-batchmode`锛夈€?
### 7.3 閰嶇疆浼樺厛绾?
杩愯鏃堕厤缃寜浠ヤ笅浼樺厛绾у悎骞讹紙楂樿鐩栦綆锛夛細
1. CLI 鍙傛暟锛坄-unityUIFlow.*`锛?2. 鐜鍙橀噺锛堝 `UNITY_UI_FLOW_HEADED`锛?3. 閰嶇疆鏂囦欢锛堥粯璁?`.unityuiflow.json`锛?4. ProjectSettings锛坄UnityUIFlowSettings.asset`锛?5. 浠ｇ爜纭紪鐮侀粯璁ゅ€?
### 7.4 閫€鍑虹爜

| 鐮佸€?| 鍚箟 |
|------|------|
| `0` | 鍏ㄩ儴閫氳繃 |
| `1` | 瀛樺湪娴嬭瘯澶辫触 |
| `2` | 鎵ц閿欒鎴栧紓甯?|

---

## 8. CI/CD 涓庤嚜鍔ㄥ寲

### 8.1 GitHub Actions

- **鍞竴宸ヤ綔娴?*锛歚.github/workflows/unity-uiflow-sample.yml`
- **瑙﹀彂鏂瑰紡**锛歚workflow_dispatch`锛堜粎鎵嬪姩瑙﹀彂锛?- **杩愯鐜**锛歚windows-latest`
- **浜х墿**锛歚./Reports` 鐩綍锛堝惈 `.md`銆乣.json`銆佸け璐ユ埅鍥撅級閫氳繃 `actions/upload-artifact@v4` 涓婁紶

### 8.2 鎶ュ憡鐩綍

鎵€鏈夋墽琛岃緭鍑洪粯璁よ惤鍏?`./Reports/`锛堟垨浼犲叆鐨?`reportPath`锛夈€?*鎶ュ憡鏍圭洰褰曚笅浠呬繚鐣欎袱涓?Markdown 姹囨€绘枃浠?*锛屽叾浣欐枃浠跺叏閮ㄥ綊鍏ュ瓙鐩綍锛?
```
Reports/
鈹溾攢鈹€ full_reports.md              鈫?Suite 鎵ц鍚庣敓鎴愶紙鍏ㄩ噺姹囨€伙細缁撴灉銆佽€楁椂銆佽捣姝㈡椂闂达級
鈹溾攢鈹€ single_reports.md            鈫?鍗曟枃浠舵墽琛屽悗鐢熸垚锛堝崟涓敤渚嬭鎯咃級
鈹溾攢鈹€ Cases/
鈹?  鈹溾攢鈹€ {caseName}.md            鈫?鍗曠敤渚?Markdown 鎶ュ憡
鈹?  鈹溾攢鈹€ {caseName}.json          鈫?鍗曠敤渚?JSON 鎶ュ憡
鈹?  鈹斺攢鈹€ suite-report.json        鈫?Suite JSON 姹囨€?鈹溾攢鈹€ Screenshots/
鈹?  鈹斺攢鈹€ {caseName}-{step}-{tag}-{timestamp}.png
鈹斺攢鈹€ Artifacts/
    鈹斺攢鈹€ artifacts.json           鈫?CLI 浜х墿娓呭崟
```

**瑙勫垯**锛?- `RunFileAsync`锛堝崟鏂囦欢锛夆啋 鐢熸垚 `single_reports.md` + `Cases/{caseName}.md+json`
- `RunSuiteAsync`锛圫uite锛夆啋 鐢熸垚 `full_reports.md` + `Cases/` 涓嬪悇鐢ㄤ緥鎶ュ憡
- MCP `unity_editor_e2e_run`锛堝崟鏂囦欢锛夆啋 `single_reports.md`
- MCP `unity_uiflow_run_batch`锛堝鏂囦欢锛夆啋 `full_reports.md`
- 鏃х殑 `suite-report.md` 宸叉洿鍚嶄负 `full_reports.md`锛屾棫鐨?`{caseName}.md` 宸茬Щ鑷?`Cases/` 瀛愮洰褰?
| 瀛愮洰褰?| 鏉ユ簮 |
|--------|------|
| `Examples/` | 绀轰緥鐢ㄤ緥鎵ц |
| `HeadedAll/` | Headed 妯″紡鍏ㄩ噺濂椾欢 |
| `McpQuickTest/`銆乣McpRegression/`銆乣McpUiFlowFile/` 绛?| MCP 椹卞姩鎵ц |

---

## 9. Unity 缂栬瘧瑙﹀彂绾﹀畾锛堝己鍒讹級

褰撲换浣?C# 鑴氭湰锛坄.cs`锛夎鍒涘缓鎴栦慨鏀癸紝涓旈渶瑕?Unity 閲嶆柊缂栬瘧鏃讹紝**蹇呴』閫氳繃 MCP 鏈嶅姟鍣ㄨЕ鍙戠紪璇?*銆備弗绂佷娇鐢?`SendKeys`銆乣AppActivate`銆佹墜鍔ㄥ垹闄?`Library/ScriptAssemblies/*.dll` 鎴栧叾浠栨姇鏈烘墜娈点€?
### 涓轰粈涔?
- Unity Editor 閫氳繃 WebSocket 妗?`UnityPilotBridge` 杩炴帴鍒?`unityuiflow-mcp` 鏈嶅姟鍣紙`$MCP_WS` / `$MCP_HTTP`锛岃搂0锛夈€?- `SendKeys` 涓嶅彲闈狅紙闇€瑕佺獥鍙ｇ劍鐐广€佸瓨鍦ㄧ珵鎬佹潯浠躲€佽秴鏃朵笉鍙帶锛夈€?- 鍒犻櫎 DLL 涓嶄細閫氱煡 AssetDatabase锛沀nity 鍙兘蹇界暐缂哄け鐨勭▼搴忛泦锛岀洿鍒版樉寮忓埛鏂般€?
### 姝ｇ‘娴佺▼

1. 纭 MCP 绔偣鍙揪锛坄$MCP_HTTP`锛岃搂0锛夈€?2. 閫氳繃 MCP 璋冪敤 `unity_compile` 宸ュ叿銆?3. 杞鐩爣绋嬪簭闆?`Library/ScriptAssemblies/<AssemblyName>.dll` 鐨?`LastWriteTime`锛岀‘淇濆叾鏅氫簬婧愭枃浠朵慨鏀规椂闂达紙鎴栦娇鐢?`unity_compile_status` / `unity_compile_errors`锛夈€?4. 纭缂栬瘧鎴愬姛鍚庯紝鍐嶇户缁悗缁搷浣溿€?
### 绀轰緥锛圥ython锛?
```python
import asyncio
from mcp import ClientSession
from mcp.client.streamable_http import streamablehttp_client

async def compile_unity():
    async with streamable_http_client(
        'http://127.0.0.1:8011/mcp',  # $MCP_HTTP锛堣搂0锛?        timeout=10,
        sse_read_timeout=1800,
        terminate_on_close=True,
    ) as (read, write, _):
        async with ClientSession(read, write) as session:
            await session.initialize()
            result = await session.call_tool('unity_compile', {}, read_timeout_seconds=None)
            return result.content[0].text
```

### 鑻?Unity 鏈繛鎺?
濡傛灉 `unity_compile` 杩斿洖 `UNITY_NOT_CONNECTED`锛屽簲璋冪敤 `unity_open_editor` 鎴栫瓑寰?Editor 閲嶆柊杩炴帴銆?*绂佹鍥為€€鍒?`SendKeys`銆?*

### 涓氬姟鐘舵€佸崱姝伙紙EDITOR_BUSY锛夌殑杞婚噺鎭㈠

褰?MCP 璋冪敤杩斿洖 `EDITOR_BUSY: A UnityUIFlow execution is already running`锛屼笖瀵瑰簲鎵ц宸叉槑鏄捐秴鏃讹紙瓒呰繃 5 鍒嗛挓鏃犺繘灞曪紝鎴栧閮ㄨ剼鏈凡閫€鍑轰絾 Unity 鍐呴儴浠嶆姤鍛?`running`锛夋椂锛屾寜浠ヤ笅浼樺厛绾ф仮澶嶏細

1. **棣栭€夛細`unity_uiflow_force_reset`**锛圲nity 渚у凡鏀寔锛?   - **瑙﹀彂鏉′欢**锛歁CP 璋冪敤杩斿洖 `EDITOR_BUSY`锛屼笖褰撳墠鎵ц宸叉寔缁?**鈮?60 绉?* 鏃犺繘灞曪紱鎴栧閮ㄨ剼鏈?杞宸插洜瓒呮椂鑰岄€€鍑猴紝浣?Unity 鍐呴儴浠嶆姤鍛?`running`銆?   - **鎿嶄綔**锛氱珛鍗宠皟鐢?`unity_uiflow_force_reset`锛屾棤闇€绛夊緟鏇翠箙銆?   - **鏁堟灉**锛氭绉掔骇杩斿洖锛屽己鍒?`Dispose` 褰撳墠鐨?`ExecutionContext`/`RuntimeController`锛屽叧闂祴璇曠獥鍙ｏ紝娓呯┖ `_isRunning` 閿侊紝骞跺皢鎵€鏈夎繘琛屼腑鐨勬墽琛屾爣璁颁负 `aborted`銆備笉瑙﹀彂鑴氭湰閲嶈浇锛屼笉褰卞搷鍏朵粬缂栬緫鐘舵€併€?   - **椋庨櫓**锛氳嫢鎵ц鍗″湪鏌愪釜 Unity 鍚屾闃诲 API 涓婏紝`force_reset` 鍚庤鍚庡彴浠诲姟浠嶅彲鑳藉湪杩涚▼涓畫鐣欙紝鐩村埌 Unity 涓嬫鑴氭湰閲嶈浇锛涗絾 `EDITOR_BUSY` 閿佸凡琚噴鏀撅紝鏂扮殑娴嬭瘯鍙互绔嬪嵆寮€濮嬨€?
2. **澶囬€夛細`unity_compile` 寮哄埗缂栬瘧**
   - **瑙﹀彂鏉′欢**锛氬湪璋冪敤 `unity_uiflow_force_reset` 鍚?**5 绉掑唴**锛屽啀娆″皾璇曟祴璇曚粛杩斿洖 `EDITOR_BUSY`锛涙垨 `force_reset` 鏈韩璋冪敤澶辫触/瓒呮椂锛岃鏄庨攣宸叉繁鍩嬫垨涓荤嚎绋嬩篃琚薄鏌撱€?   - **鎿嶄綔**锛氳皟鐢?`unity_compile`銆?   - **鏁堟灉**锛氳Е鍙?AppDomain Reload锛屽交搴曢噸缃墍鏈夊崟渚嬬姸鎬侊紝閫氬父 3鈥?5 绉掑畬鎴愶紙鍙栧喅浜庨」鐩剼鏈噺锛夈€備細涓柇褰撳墠缂栬緫涓婁笅鏂囷紙鎵€鏈夐潤鎬佺姸鎬佷涪澶憋級銆?
**绂佹浣跨敤 `taskkill`銆侀噸鍚?Unity 鎴栦换浣曡繘绋嬬骇鎿嶄綔浣滀负鎭㈠鎵嬫銆?* 鐜版湁 `force_reset` + `unity_compile` 鐨勭粍鍚堝凡鑳借鐩栧叏閮?`EDITOR_BUSY` 鍦烘櫙锛屾棤闇€鐮村潖缂栬緫鍣ㄨ繘绋嬨€?
**绂佹**鍦?`EDITOR_BUSY` 鐘舵€佷笅鐩茬洰绛夊緟鎴栭噸澶嶈皟鐢ㄦ祴璇曞伐鍏枫€?
---

## 10. 瀹夊叏涓庨€氱敤绾︽潫

- **浼樺厛浣跨敤 MCP 宸ュ叿璋冪敤** 涓?Unity 浜や簰锛岄伩鍏?OS 绾?GUI 鑷姩鍖栥€?- **淇濇寔鏈€灏忓彉鏇村師鍒?*锛氬彧鏀逛笌鐩爣鐩稿叧鐨勪唬鐮侊紝涓嶈澶ц妯￠噸鏋勩€?- **閬靛惊鐜版湁浠ｇ爜椋庢牸**锛欳# 浣跨敤鐜版湁缂╄繘涓庡懡鍚嶄範鎯€?- **闈炲钩鍑″彉鏇村悗蹇呴』璺戝洖褰?*锛氶€氳繃 MCP 鐨?`unity_uiflow_run_file` 鎴?`unity_uiflow_run_suite` 鎵ц鍥炲綊楠岃瘉銆?- **涓嶈缂栭€犳枃浠惰矾寰勬垨绫诲悕**锛氭墍鏈夎矾寰勫拰绫诲瀷鍚嶅繀椤讳笌浠ｇ爜涓湡瀹炲瓨鍦ㄧ殑涓€鑷淬€?- **git 鎿嶄綔璋ㄦ厧**锛氶櫎闈炵敤鎴锋槑纭姹傦紝鍚﹀垯涓嶈鎵ц `git commit`銆乣git push`銆乣git reset`銆乣git rebase`銆?
---

## 11. 褰撳墠宸茬煡鑷姩鍖栬竟鐣?
浠ヤ笅鑳藉姏灏氭湭瀹炵幇锛屾枃妗ｄ笌浠ｇ爜淇濇寔涓€鑷达紝涓嶅簲鍐欐垚鈥滃凡鍏ㄩ潰鏀寔鈥濓細

- `ObjectField` 鐨?Object Picker 寮圭獥涓?DragAndDrop 涓嶆敮鎸併€?- `CurveField` / `GradientField` 鐨勭嫭绔嬬紪杈戝櫒娴獥涓嶆敮鎸併€?- `ToolbarPopupSearchField` 鐨勫脊鍑虹粨鏋滃垪琛ㄤ笉鏀寔銆?- `ToolbarBreadcrumbs` 娌℃湁缁熶竴鐨勨€滄寜 label / index 瀵艰埅鈥濆皝瑁呭姩浣溿€?- `PropertyField` / `InspectorElement` 涓嶆彁渚涘鑷韩鐨勭粺涓€璇箟璧嬪€硷紝鍙兘閫氳繃宸茬敓鎴愮殑鍚庝唬鎺т欢鑷姩鍖栥€?- `IMGUIContainer`銆両ME銆佺郴缁熷壀璐存澘銆佸绐楀彛鍗忓悓銆佸儚绱犵骇瑙嗚 diff 涓嶅湪 V1 杈圭晫鍐呫€?
---

## 12. 蹇€熷弬鑰冮摼鎺?
| 璺緞 | 鍐呭 |
|------|------|
| `docs/01-UnityUIFlow-UXML-USS鑷姩鍖栧紑鍙戣鑼?md` | UXML/USS 璇︾粏瑙勮寖 |
| `docs/02-UnityUIFlow-鏂伴〉闈㈡帴鍏ユ渶灏忔ā鏉?md` | 鏈€灏忛〉闈㈡ā鏉夸笌澶嶅埗娴佺▼ |
| `docs/03-UnityUIFlow-Agent-MCP娴嬭瘯寮哄埗瑙勮寖.md` | Agent MCP 娴嬭瘯寮哄埗瑙勮寖鍏ㄦ枃 |
| `cocs/00-Overview.md` | 闇€姹傛枃妗ｆ€昏涓庢ā鍧楁竻鍗?|
| `cocs/00-API閫熸煡涓庢渶浣冲疄璺?md` | 38 涓姩浣溿€侀€夋嫨鍣ㄣ€丆LI 閫熸煡 |
| `.unityuiflow.json` | 鏈湴寮€鍙戦厤缃紙`headed: true`锛?|
| `README.md` | AI 宸ュ叿 HTTP MCP 閰嶇疆绀轰緥锛圇ursor / Claude Desktop / Windsurf 绛級 |
