# UnityUIFlow 娴嬭瘯娴佺▼鎸囧崡

## 馃搶 椤圭洰姒傝堪

**UnityUIFlow** 鏄竴涓?Unity Editor UIToolkit 鑷姩鍖栨祴璇曟鏋讹紝鏀寔 YAML 澹版槑寮忚剼鏈拰 C# 浠ｇ爜涓ょ鏂瑰紡椹卞姩 EditorWindow 鐣岄潰娴佺▼娴嬭瘯銆傝妗嗘灦鎻愪緵 16 涓唴缃姩浣溿€佹暟鎹┍鍔ㄣ€佹潯浠跺惊鐜€佸彲瑙嗗寲 Headed 妯″紡绛夊畬鏁村姛鑳姐€?
---

## 馃幆 娴嬭瘯鎵ц鏂瑰紡

鏍规嵁椤圭洰閰嶇疆鍜屼唬鐮佺粨鏋勶紝UnityUIFlow 鏀寔浠ヤ笅 **4 绉嶆祴璇曟墽琛屾柟寮?*锛?
### 1锔忊儯 **YAML 椹卞姩娴嬭瘯** (鎺ㄨ崘鐢ㄤ簬蹇€熼獙璇?

#### 鏂囦欢浣嶇疆
- **娴嬭瘯鏂囦欢鐩綍**: `Samples~/Yaml/`
- **娴嬭瘯鏂囦欢鏁伴噺**: 19 涓ず渚?YAML 鏂囦欢
- **鍛藉悕瑙勮寖**: `01-basic-login.yaml`, `02-selectors-and-assertions.yaml` 绛?
#### 鎵ц鏂瑰紡 (Editor UI)
1. 鎵撳紑 Unity Editor
2. 瀵艰埅鍒拌彍鍗? **UnityUIFlow > Samples > [绀轰緥绐楀彛]**
3. 鐐瑰嚮瀵瑰簲鐨勭ず渚嬬獥鍙ｆ墦寮€ EditorWindow
4. 妗嗘灦鑷姩鍔犺浇骞舵墽琛?YAML 娴嬭瘯鏂囦欢

#### 鏀寔鐨?YAML 鐗规€?| 鐗规€?| 璇存槑 |
|------|------|
| **鍐呯疆鍔ㄤ綔** | click銆乼ype_text銆乤ssert銆乻creenshot銆亀ait銆乨ouble_click銆乸ress_key銆乭over銆乨rag銆乻croll 绛?16 绉?|
| **鏁版嵁椹卞姩** | CSV銆丣SON銆佸唴鑱旀暟鎹笁绉嶆暟鎹簮 |
| **鏉′欢鎺у埗** | if 鏉′欢璇彞銆乺epeat_while 寰幆 |
| **閫夋嫨鍣?* | UIToolkit 閫夋嫨鍣ㄣ€佽嚜瀹氫箟閫夋嫨鍣?|
| **鏂█** | HaveText銆丠aveClass銆両sVisible銆両sEnabled 绛?|

#### 绀轰緥 YAML 娴嬭瘯鏂囦欢
```yaml
# Samples~/Yaml/01-basic-login.yaml
steps:
  - action: click
    selector: "#username"
  - action: type_text
    selector: "#username"
    text: "admin"
  - action: click
    selector: "#password"
  - action: type_text
    selector: "#password"
    text: "password123"
  - action: click
    selector: "#login-btn"
  - action: assert
    selector: ".dashboard"
    assertion: IsVisible
```

---

### 2锔忊儯 **C# 鍗曞厓娴嬭瘯** (Fixture 鏂瑰紡)

#### 鏂囦欢浣嶇疆
- **娴嬭瘯鏂囦欢鐩綍**: `Samples~/Tests/`
- **娴嬭瘯绋嬪簭闆?*: `UnityUIFlow.Tests.asmdef`
- **鍏抽敭娴嬭瘯鏂囦欢**:
  - `UnityUIFlow.ParsingAndPlanningTests.cs` - YAML 瑙ｆ瀽娴嬭瘯
  - `UnityUIFlow.LocatorsAndActionsTests.cs` - 鍔ㄤ綔鍜屽畾浣嶅櫒娴嬭瘯
  - `UnityUIFlow.ExamplesAcceptanceTests.cs` - 鎺ユ敹搴︽祴璇?  - `UnityUIFlow.ExecutionReportingCliTests.cs` - 鎶ュ憡鍜?CLI 娴嬭瘯

#### Fixture 缁ф壙鏂瑰紡
```csharp
using UnityUIFlow;
using UnityEngine.UIElements;

// 缁ф壙 UnityUIFlowFixture<TWindow>
public class MyWindowTests : UnityUIFlowFixture<ExampleBasicLoginWindow>
{
    [Test]
    public async Task TestLoginFlow()
    {
        // 鍐呯疆鏀寔閫氳繃 Fixture 椹卞姩娴嬭瘯
        await ExecuteYamlTest("Samples~/Yaml/01-basic-login.yaml");
    }
}
```

#### 鎵ц鏂瑰紡 (Unity Test Framework)
1. 鎵撳紑 **Window > General > Test Runner**
2. 鍒囨崲鍒?**EditMode** 鎴?**PlayMode**
3. 閫夋嫨娴嬭瘯鐢ㄤ緥 (濡?`UnityUIFlow.Tests`)
4. 鐐瑰嚮 **Run** 鎵ц

#### 娴嬭瘯绫诲瀷
| 娴嬭瘯绫诲瀷 | 鏂囦欢 | 鐢ㄩ€?|
|----------|------|------|
| **Parsing** | ParsingAndPlanningTests.cs | 楠岃瘉 YAML 瑙ｆ瀽鍜屾墽琛岃鍒掓瀯寤?|
| **Actions** | LocatorsAndActionsTests.cs | 楠岃瘉鍔ㄤ綔鎵ц鍜屽畾浣嶅櫒 |
| **Acceptance** | ExamplesAcceptanceTests.cs | 闆嗘垚娴嬭瘯 - 绔埌绔獙璇?|
| **Reporting** | ExecutionReportingCliTests.cs | 楠岃瘉鎶ュ憡鐢熸垚鍜?CLI 鍔熻兘 |
| **Headed** | HeadedTests.cs | 楠岃瘉鍙鍖栦氦浜掓ā寮?|

---

### 3锔忊儯 **CLI 鍛戒护琛屾墽琛?* (鎸佺画闆嗘垚)

#### 鎵ц鍛戒护
```bash
# 杩愯鍗曚釜 YAML 娴嬭瘯鏂囦欢
$UNITY_PATH -projectPath . \
  -executeMethod UnityUIFlow.Cli.RunYamlTest \
  Samples~/Yaml/01-basic-login.yaml

# 杩愯鎵€鏈?YAML 娴嬭瘯 (甯︽姤鍛婅緭鍑?
$UNITY_PATH -projectPath . \
  -executeMethod UnityUIFlow.Cli.RunAllYamlTests \
  --reportPath ./Reports \
  --format json
```

#### 杈撳嚭鏍煎紡
- **Markdown 鎶ュ憡**: `./Reports/report.md`
- **JSON 鎶ュ憡**: `./Reports/report.json`
- **鎴浘**: `./Reports/screenshots/` (澶辫触鏃?

#### CI 閰嶇疆鏂囦欢
- **椤圭洰閰嶇疆**: `.unityuiflow.json`

---

### 4锔忊儯 **Headed 鍙鍖栨ā寮?* (浜や簰寮忚皟璇?

#### 鍚敤鏂瑰紡
缂栬緫 `.unityuiflow.json`:
```json
{
  "headed": true,
  "reportPath": "./Reports",
  "screenshotOnFailure": true,
  "defaultTimeoutMs": 10000
}
```

#### 鍔熻兘鐗规€?- **瀹炴椂鍙鍖?*: Editor 涓珮浜洰鏍囧厓绱?- **浼杩涜皟璇?*: 鏀寔閫愭鎵ц娴嬭瘯姝ラ
- **浜や簰寮忔帶鍒?*: 鏆傚仠銆佹仮澶嶃€佽烦杩囨祴璇曟楠?- **瀹炴椂鍙嶉**: 鍗虫椂鐪嬪埌鍏冪礌鏌ユ壘鍜屽姩浣滄墽琛岀粨鏋?
---

## 锟斤笍 MCP 鏈嶅姟鍣ㄦ墽琛屾祴璇曪紙鎺ㄨ崘鏂瑰紡锛?
**MCP (Model Context Protocol) 鏈嶅姟鍣?* 鏄?UnityUIFlow 鐨勬牳蹇冩墽琛屽紩鎿庯紝鎻愪緵杩滅▼娴嬭瘯鎵ц銆佸疄鏃跺弽棣堝拰鍙鍖栬皟璇曡兘鍔涖€?
### 馃寪 MCP 鏈嶅姟鍣ㄤ俊鎭?
褰撳墠 UnityUIFlow MCP 鏈嶅姟鍣ㄩ厤缃細

```json
{
  "label": "UnityUIFlow",
  "host": "127.0.0.1",
  "port": 8767,
  "status": "connected",
  "serverReady": true,
  "sessionId": "2a928d0b29fc475cb0f3383ab37da326",
  "projectPath": "D:\\UnityUIFlow",
  "unityVersion": "6000.6.0a2",
  "platform": "windows"
}
```

**鍏抽敭鍙傛暟璇存槑锛?*
| 鍙傛暟 | 鍊?| 璇存槑 |
|------|-----|------|
| `host` | 127.0.0.1 | 鏈湴涓绘満锛堢紪杈戝櫒鎵€鍦ㄦ満鍣級 |
| `port` | 8767 | MCP 鏈嶅姟閫氫俊绔彛 |
| `status` | connected | 杩炴帴鐘舵€?鉁?|
| `serverReady` | true | 鏈嶅姟鍣ㄥ噯澶囧氨缁?鉁?|
| `projectPath` | D:\UnityUIFlow | Unity 椤圭洰璺緞 |
| `unityVersion` | 6000.6.0a2 | Unity 缂栬緫鍣ㄧ増鏈?|

### 瀹屾暣鐨?MCP 娴嬭瘯鎵ц娴佺▼

#### **姝ラ 1: 鍚敤 Headed 妯″紡骞堕厤缃?MCP**

缂栬緫椤圭洰鏍圭洰褰曠殑 `.unityuiflow.json` 閰嶇疆鏂囦欢锛?
```json
{
  "headed": true,
  "reportPath": "./Reports",
  "screenshotOnFailure": true,
  "defaultTimeoutMs": 10000,
  "customActionAssemblies": [
    "UnityUIFlow.Tests"
  ]
}
```

**閰嶇疆璇存槑锛?*
- `"headed": true` - **鍚敤鍙鍖栨ā寮?*锛堝叧閿紒锛?  - 缂栬緫鍣ㄤ腑鍙鐩爣鍏冪礌楂樹寒
  - 鏀寔浜や簰寮忚皟璇曞拰姝ラ鎺у埗
  - 瀹炴椂鏄剧ず鎵ц杩涘害
- `"defaultTimeoutMs": 10000` - 榛樿瓒呮椂 10 绉掞紙鍙牴鎹渶瑕佽皟鏁达級

#### **鎵ц鍓嶈ˉ鍏咃細stdio MCP 鏈嶅姟鍣ㄦ帴绠′笌鍚庡彴淇濇椿绛栫暐**

褰?MCP 鏈嶅姟鍣ㄩ噰鐢?`.vscode/mcp.json` 涓殑 `stdio` 鏂瑰紡鍚姩鏃讹紝鎵ц娴嬭瘯鍓嶅缓璁寜浠ヤ笅瑙勫垯澶勭悊锛?
- 浼樺厛妫€鏌ュ綋鍓嶆槸鍚﹀凡缁忓瓨鍦ㄥ彲鐢ㄧ殑 `unitypilot` MCP 鏈嶅姟鍣ㄣ€?- 瀵?`stdio` 鍨?MCP锛屼笉瑕佸彧鐪嬧€滆繘绋嬫槸鍚﹀瓨鍦ㄢ€濓紝鑰岃浠モ€滃綋鍓嶆墽琛岀幆澧冩槸鍚﹀凡缁忔垚鍔熸帴绠″苟鑳界洿鎺ヨ皟鐢?MCP tool鈥濅负鍑嗐€?- 濡傛灉褰撳墠鐜宸茬粡鎺ョ璇?MCP 鏈嶅姟鍣紝骞朵笖鍙互鐩存帴璋冪敤鐩稿叧宸ュ叿锛屽垯**鐩存帴澶嶇敤鐜版湁 MCP 鏈嶅姟鍣?*锛屼笉瑕侀噸澶嶅惎鍔ㄣ€?- 濡傛灉 MCP 鏈嶅姟鍣ㄦ湭鍚姩锛屾垨鑰呰櫧鐒跺凡鏈夎繘绋嬩絾褰撳墠鐜**鏃犳硶鎺ョ / 鏃犳硶鐩存帴璋冪敤**锛屽垯搴斿厛鍏抽棴鏃х殑 MCP 杩涚▼锛岄伩鍏嶄繚鐣欐棤鏁堝崰鐢ㄦ垨绔彛鍐茬獊銆?- 鍏抽棴鏃ц繘绋嬪悗锛屾寜 [`.vscode/mcp.json`](d:/UnityUIFlow/.vscode/mcp.json) 鐨勯厤缃噸鏂板惎鍔?`unitypilot` MCP 鏈嶅姟鍣ㄣ€?- 鏂板惎鍔ㄧ殑 MCP 鏈嶅姟鍣ㄥ簲淇濇寔**鍚庡彴甯搁┗杩愯**锛屽悗缁祴璇曠洿鎺ュ鐢紱闄ら潪鏄庣‘瑕佹眰鍏抽棴锛屽惁鍒欎笉瑕佸湪姣忔娴嬭瘯鍚庤嚜鍔ㄥ仠姝€?
**鎺ㄨ崘鍐崇瓥椤哄簭锛?*

1. 妫€鏌ュ綋鍓?MCP 鏄惁宸插瓨鍦ㄤ笖褰撳墠鐜鍙洿鎺ヤ娇鐢ㄣ€?2. 鑻ュ彲鐩存帴浣跨敤锛屽鐢ㄧ幇鏈夋湇鍔″櫒骞剁户缁墽琛屾祴璇曘€?3. 鑻ヤ笉鍙娇鐢紝鍏抽棴鏃?MCP 杩涚▼銆?4. 閲嶆柊鍦ㄥ悗鍙板惎鍔?MCP 鏈嶅姟鍣紝骞朵繚鎸佸叾鎸佺画杩愯銆?5. 纭 MCP 宸ュ叿鍙皟鐢ㄥ悗锛屽啀鎵ц YAML E2E 娴嬭瘯銆?
#### **姝ラ 2: 閫氳繃 MCP 鏈嶅姟鍣ㄦ寚瀹?YAML 娴嬭瘯骞跺惎鍔?*

浣跨敤 MCP 宸ュ叿杩愯 E2E 娴嬭瘯锛?
**绀轰緥 1: 杩愯鍩虹鐧诲綍娴嬭瘯**
```
宸ュ叿璋冪敤: unity_editor_e2e_run
鍙傛暟:
  - specPath: Samples~/Yaml/01-basic-login.yaml
  - artifactDir: D:\UnityUIFlow\artifacts
  - exportZip: true
  - stopOnFirstFailure: true
  - webhookOnFailure: true
```

**瀵瑰簲鐨勬祴璇曟枃浠跺唴瀹?* (`Samples~/Yaml/01-basic-login.yaml`):
```yaml
name: Example Basic Login
description: 鍩虹鐧诲綍娴佺▼娴嬭瘯 - 鍖呭惈鏂囨湰杈撳叆銆佺偣鍑汇€佹柇瑷€鍜屾埅鍥?fixture:
  host_window:
    type: UnityUIFlow.Examples.ExampleBasicLoginWindow
    reopen_if_open: true
steps:
  - name: 濉厖鐢ㄦ埛鍚?    action: type_text_fast
    selector: "#username-input"
    value: "alice"
  - name: 濉厖瀵嗙爜
    action: type_text_fast
    selector: "#password-input"
    value: "secret"
  - name: 鎻愪氦鐧诲綍
    action: click
    selector: "#login-button"
  - name: 楠岃瘉娆㈣繋淇℃伅
    action: assert_text_contains
    selector: "#status-label"
    expected: "alice"
  - name: 淇濆瓨鎴浘
    action: screenshot
    tag: "basic-login"
```

**Headed 妯″紡鎵ц娴佺▼鍙鍖栵細**
```
鏃堕棿杞?鈹?鎵ц姝ラ                          鈹?缂栬緫鍣ㄥ唴鍙鍖?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
T0    鈹?鎵撳紑 ExampleBasicLoginWindow      鈹?绐楀彛寮瑰嚭
      鈹?鍔犺浇娴嬭瘯璁″垝                       鈹?
鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
T1    鈹?銆愭楠?1銆戝～鍏呯敤鎴峰悕               鈹?
      鈹?action: type_text_fast            鈹?鉁?楂樹寒 #username-input
      鈹?selector: "#username-input"        鈹?鍏夋爣鍦ㄨ緭鍏ユ
      鈹?value: "alice"                     鈹?杈撳叆: "alice"
      鈹?鈫?鉁?PASS                          鈹?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
T2    鈹?銆愭楠?2銆戝～鍏呭瘑鐮?                鈹?      鈹?action: type_text_fast            鈹?鉁?楂樹寒 #password-input
      鈹?selector: "#password-input"        鈹?鍏夋爣鍦ㄨ緭鍏ユ
      鈹?value: "secret"                    鈹?杈撳叆: "secret"
      鈹?鈫?鉁?PASS                          鈹?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
T3    鈹?銆愭楠?3銆戞彁浜ょ櫥褰?                鈹?      鈹?action: click                     鈹?鉁?楂樹寒 #login-button
      鈹?selector: "#login-button"          鈹?妯℃嫙鐐瑰嚮浜嬩欢
      鈹?鈫?鉁?PASS                          鈹?绐楀彛鍝嶅簲鐧诲綍璇锋眰
鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
T4    鈹?銆愭楠?4銆戦獙璇佹杩庝俊鎭?            鈹?      鈹?action: assert_text_contains      鈹?鉁?楂樹寒 #status-label
      鈹?selector: "#status-label"          鈹?妫€鏌ユ枃鏈? "alice"
      鈹?expected: "alice"                  鈹?楠岃瘉閫氳繃锛?      鈹?鈫?鉁?PASS                          鈹?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
T5    鈹?銆愭楠?5銆戜繚瀛樻埅鍥?               鈹?      鈹?action: screenshot                鈹?馃摳 鎴浘宸蹭繚瀛?      鈹?tag: "basic-login"                 鈹?Reports/basic-login.png
      鈹?鈫?鉁?PASS                          鈹?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
缁撴潫    鈹?娴嬭瘯瀹屾垚                         鈹?鉁?鎵€鏈夋楠ら€氳繃
      鈹?鎶ュ憡宸茬敓鎴?                       鈹?Reports/report.md
```

#### **姝ラ 3: 瀹炴椂鐩戞帶鍜屼氦浜?*

鍦?Headed 妯″紡涓嬶紝娴嬭瘯鎵ц鏃跺彲浠ワ細

- **馃攳 瑙傚療楂樹寒鏁堟灉**锛?  - 姣忎釜姝ラ鎵ц鏃讹紝鐩爣鍏冪礌浼氶珮浜樉绀猴紙杈规銆佽儗鏅彉鑹诧級
  - 渚夸簬楠岃瘉閫夋嫨鍣ㄦ槸鍚︽纭畾浣?
- 鈴革笍 **鏆傚仠/鎭㈠鎵ц**锛堜吉姝ヨ繘璋冭瘯锛夛細
  - 鍦ㄧ紪杈戝櫒涓缃柇鐐?  - 閫愭鍗曟鎵ц
  - 瑙傚療姣忎竴姝ョ殑瀹為檯鏁堟灉

- 馃摳 **鎴浘璇佹嵁**锛?  - 姣忎釜鍏抽敭姝ラ鑷姩鎴浘
  - 澶辫触鏃跺己鍒舵埅鍥句繚瀛?  - 浣嶇疆锛歚Reports/screenshots/`

#### **姝ラ 4: 閫夋嫨鍣ㄥ拰鏂█鐨勫姩鎬侀獙璇?*

**绀轰緥 2锛氶€夋嫨鍣ㄥ拰鏂█娴嬭瘯** (`Samples~/Yaml/02-selectors-and-assertions.yaml`)
```yaml
name: Example Selectors And Assertions
description: 楠岃瘉澶氱閫夋嫨鍣ㄥ舰寮忓拰鏂█鏂规硶
fixture:
  host_window:
    type: UnityUIFlow.Examples.ExampleSelectorsWindow
    reopen_if_open: true
steps:
  - name: 鍖归厤瀛愬厓绱犻€夋嫨鍣?    action: assert_visible
    selector: "#selector-list > .selector-item:first-child"
    # Headed 妯″紡锛氣湪 楂樹寒绗竴涓瓙鍏冪礌

  - name: 鍖归厤灞炴€ч€夋嫨鍣?    action: assert_visible
    selector: "[tooltip=Inspect]"
    # Headed 妯″紡锛氣湪 楂樹寒 tooltip="Inspect" 鐨勫厓绱?
  - name: 鍖归厤鏁版嵁灞炴€ч€夋嫨鍣?    action: assert_visible
    selector: "[data-role=primary]"
    # Headed 妯″紡锛氣湪 楂樹寒 data-role="primary" 鐨勫厓绱?
  - name: 楠岃瘉鎸夐挳灞炴€?    action: assert_property
    selector: "Button"
    property: "tooltip"
    expected: "Inspect"
    # Headed 妯″紡锛氣湪 妫€鏌?Button 鐨?tooltip 灞炴€?
  - name: 鐐瑰嚮妫€鏌ユ寜閽?    action: click
    selector: "#inspect-button"
    # Headed 妯″紡锛氣湪 楂樹寒骞舵ā鎷熺偣鍑?#inspect-button

  - name: 鏈€缁堢姸鎬侀獙璇?    action: assert_text
    selector: "#selector-status"
    expected: "Inspect ready"
    # Headed 妯″紡锛氣湪 楠岃瘉鏂囨湰涓?"Inspect ready"
```

#### **姝ラ 5: 绛夊緟鍜屽姩鎬佸厓绱犳祴璇?*

**绀轰緥 3锛氱瓑寰呭厓绱犲姞杞?* (`Samples~/Yaml/03-wait-for-element.yaml`)
```yaml
name: Example Wait For Element
description: 娴嬭瘯寮傛鍔犺浇鍏冪礌鍜岀瓑寰呰秴鏃?fixture:
  host_window:
    type: UnityUIFlow.Examples.ExampleWaitForElementWindow
    reopen_if_open: true
steps:
  - name: 鍚姩寤惰繜鏄剧ず
    action: click
    selector: "#start-button"
    # Headed 妯″紡锛氱偣鍑诲悗寮€濮嬪紓姝ュ姞杞?
  - name: 绛夊緟娑堟伅鍏冪礌鍑虹幇
    action: wait_for_element
    selector: "#delayed-message"
    timeout: "2s"
    # Headed 妯″紡锛氣彸 绛夊緟鏈€澶?2 绉掞紝杞 #delayed-message
    # 鈫?T0-0.5s: 鍏冪礌涓嶅彲瑙侊紝缁х画绛夊緟
    # 鈫?T0.5-1.0s: 鍏冪礌涓嶅彲瑙侊紝缁х画绛夊緟
    # 鈫?T1.0-1.5s: 鍏冪礌涓嶅彲瑙侊紝缁х画绛夊緟
    # 鈫?T1.5-2.0s: 鉁?鍏冪礌鍑虹幇锛侀珮浜樉绀?
  - name: 鏈€缁堟柇瑷€
    action: assert_text
    selector: "#delayed-message"
    expected: "Ready"
    # 楠岃瘉鏂囨湰鍐呭涓?"Ready"
```

**Headed 妯″紡涓殑绛夊緟鍙鍖栵細**
```
杞鍛ㄦ湡 鈹?鍏冪礌鐘舵€?          鈹?缂栬緫鍣ㄥ弽棣?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
0ms     鈹?鉂?鍏冪礌涓嶅彲瑙?     鈹?"姝ｅ湪绛夊緟..."
250ms   鈹?鉂?鍏冪礌涓嶅彲瑙?     鈹?"姝ｅ湪绛夊緟..." (鏃嬭浆杩涘害)
500ms   鈹?鉂?鍏冪礌涓嶅彲瑙?     鈹?"姝ｅ湪绛夊緟..." (鏃嬭浆杩涘害)
750ms   鈹?鉂?鍏冪礌涓嶅彲瑙?     鈹?"姝ｅ湪绛夊緟..." (鏃嬭浆杩涘害)
1000ms  鈹?鉂?鍏冪礌涓嶅彲瑙?     鈹?"姝ｅ湪绛夊緟..." (鏃嬭浆杩涘害)
1250ms  鈹?鉂?鍏冪礌涓嶅彲瑙?     鈹?"姝ｅ湪绛夊緟..." (鏃嬭浆杩涘害)
1500ms  鈹?鉁?鍏冪礌鍑虹幇锛?     鈹?鉁?楂樹寒鏄剧ず锛岄€氳繃锛?```

### 馃幀 MCP 鏈嶅姟鍣ㄦ祴璇曞畬鏁寸ず渚?
**瀹屾暣鍛戒护娴侊細**

```
1锔忊儯  鍚敤 Headed 閰嶇疆
    Action: 缂栬緫 .unityuiflow.json
    鈹溾攢 "headed": true
    鈹斺攢 "defaultTimeoutMs": 10000

2锔忊儯  閫氳繃 MCP 鍚姩 E2E 娴嬭瘯
    Tool: unity_editor_e2e_run
    Parameters:
      specPath: Samples~/Yaml/01-basic-login.yaml
      artifactDir: D:\UnityUIFlow\artifacts
      exportZip: true
      stopOnFirstFailure: true

3锔忊儯  瀹炴椂鐩戞帶鎵ц杩囩▼
    鉁?缂栬緫鍣ㄤ腑瑙傚療锛?      - 鍏冪礌楂樹寒鍙樺寲
      - 姝ラ鎵ц椤哄簭
      - 鏂囨湰杈撳叆鍜岀偣鍑?
4锔忊儯  娴嬭瘯瀹屾垚鏀堕泦鎶ュ憡
    Output:
      鈹溾攢 Report: Reports/report.json
      鈹溾攢 Report: Reports/report.md
      鈹溾攢 Screenshots: Reports/screenshots/*.png
      鈹斺攢 Artifacts: artifacts/e2e-bundle.zip (濡傛灉鍚敤)

5锔忊儯  鍒嗘瀽澶辫触鍘熷洜锛堝鏈夛級
    Headed 妯″紡浼樺娍锛?      鉁?鑳界洿瑙傜湅鍒伴€夋嫨鍣ㄥ懡涓殑鍏冪礌
      鉁?鑳借瀵熷姩浣滄墽琛岃繃绋嬶紙濡傛枃鏈緭鍏ワ級
      鉁?鑳借瘖鏂秴鏃舵垨鏂█澶辫触鐨勫師鍥?      鉁?鏀寔浼杩涜皟璇曞揩閫熷畾浣嶉棶棰?```

### 馃搳 Headed 妯″紡 vs 鏃犲ご妯″紡瀵规瘮

| 鍦烘櫙 | 鏃犲ご妯″紡 | Headed 妯″紡 |
|------|---------|-----------|
| **鎵ц閫熷害** | 鈿?蹇?(鏃?UI 缁樺埗) | 馃攧 鎱?(鏈?UI 缁樺埗) |
| **璇婃柇鑳藉姏** | 馃搵 鎶ュ憡涓轰富 | 馃憗锔?鍙鍖栦负涓?|
| **閫夋嫨鍣ㄩ獙璇?* | 鈿狅笍 闅?(鐪嬩笉鍒伴珮浜? | 鉁?鏄?(瀹炴椂楂樹寒) |
| **鍔ㄤ綔璋冭瘯** | 鈿狅笍 闅?(鍙湁鏃ュ織) | 鉁?鏄?(鐪嬪埌杩囩▼) |
| **瓒呮椂闂** | 鈿狅笍 闅捐瘖鏂?| 鉁?鏄撹瘖鏂?|
| **CI/CD 鐜** | 鉁?鎺ㄨ崘 | 鉂?涓嶉€傜敤 (鏃?UI) |
| **寮€鍙戣皟璇?* | 鈿狅笍 鍙敤 | 鉁?寮虹儓鎺ㄨ崘 |
| **鎬ц兘娴嬭瘯** | 鉁?鍑嗙‘ | 鈿狅笍 鏈夌粯鍒跺紑閿€ |

**浣跨敤寤鸿锛?*
- **寮€鍙戦樁娈?*锛氱敤 Headed 妯″紡 + MCP 鏈嶅姟鍣ㄥ揩閫熻皟璇?- **CI/CD 鐜嚎**锛氱敤鏃犲ご妯″紡 + CLI 鍛戒护鑷姩鍖栨祴璇?- **澶辫触鎺掓煡**锛氬垏鎹㈠埌 Headed 妯″紡鏌ョ湅鍏蜂綋闂

---

## 锟金煋?娴嬭瘯閰嶇疆涓庡搴斿叧绯?
椤圭洰鍖呭惈涓や釜灞傜骇鐨勯厤缃枃浠讹細

### `.unityuiflow.json` - 椤圭洰绾ч厤缃?```json
{
  "headed": false,
  "reportPath": "./Reports",
  "screenshotOnFailure": true,
  "defaultTimeoutMs": 10000,
  "customActionAssemblies": ["UnityUIFlow.Tests"]
}
```

**閰嶇疆閫夐」璇存槑**:
| 閰嶇疆椤?| 璇存槑 | 榛樿鍊?|
|--------|------|--------|
| `headed` | 鍚敤鍙鍖栨ā寮?| false |
| `reportPath` | 娴嬭瘯鎶ュ憡杈撳嚭璺緞 | ./Reports |
| `screenshotOnFailure` | 澶辫触鏃舵埅鍥?| true |
| `defaultTimeoutMs` | 榛樿瓒呮椂鏃堕棿(姣) | 10000 |
| `customActionAssemblies` | 鑷畾涔夊姩浣滅▼搴忛泦鍒楄〃 | [] |

---

## 馃梻锔?娴嬭瘯鏂囦欢缁勭粐缁撴瀯

```
Samples~/Yaml/
鈹溾攢鈹€ 01-basic-login.yaml                 鉁?鍩虹娴佺▼ - 鐧诲綍
鈹溾攢鈹€ 02-selectors-and-assertions.yaml    鉁?UI 瀹氫綅鍜屾柇瑷€
鈹溾攢鈹€ 03-wait-for-element.yaml            鉁?鍏冪礌绛夊緟
鈹溾攢鈹€ 04-conditional-and-loop.yaml        鉁?鏉′欢鍜屽惊鐜?鈹溾攢鈹€ 05-data-driven-csv.yaml             鉁?CSV 鏁版嵁椹卞姩
鈹溾攢鈹€ 06-custom-action-and-json.yaml      鉁?鑷畾涔夊姩浣?+ JSON
鈹溾攢鈹€ 07-double-click.yaml                鉁?鍙屽嚮鎿嶄綔
鈹溾攢鈹€ 08-press-key.yaml                   鉁?鎸夐敭鎿嶄綔
鈹溾攢鈹€ 09-hover.yaml                       鉁?鎮仠鎿嶄綔
鈹溾攢鈹€ 10-drag.yaml                        鉁?鎷栨嫿鎿嶄綔
鈹溾攢鈹€ 11-scroll.yaml                      鉁?婊氬姩鎿嶄綔
鈹溾攢鈹€ 12-type-text.yaml                   鉁?鏂囨湰杈撳叆
鈹溾攢鈹€ 13-advanced-controls.yaml           鉁?楂樼骇鎺т欢
鈹溾攢鈹€ 14-15-16-fields.yaml                鉁?瀛楁鎿嶄綔
鈹溾攢鈹€ 17-collections.yaml                 鉁?闆嗗悎鎿嶄綔
鈹溾攢鈹€ 18-layout-and-scroller.yaml         鉁?甯冨眬鍜屾粴鍔ㄦ潯
鈹溾攢鈹€ 19-menus-and-commands.yaml          鉁?鑿滃崟鍜屽懡浠?鈹斺攢鈹€ ...
```

---

## 馃敡 绋嬪簭闆嗕緷璧栧叧绯?
```
鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹?  UnityUIFlow.Tests     鈹? 鈫?娴嬭瘯绋嬪簭闆?(C# 娴嬭瘯鐢ㄤ緥)
鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?             鈹?references
             鈻?鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹?    UnityUIFlow         鈹? 鈫?鏍稿績妗嗘灦绋嬪簭闆?(瑙ｆ瀽銆佹墽琛屻€佹姤鍛?
鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?             鈹?references
             鈻?鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹? External Dependencies  鈹?鈹溾攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹?鈥?Unity Test Framework  鈹?鈹?鈥?InputSystem           鈹?鈹?鈥?UI TestFramework      鈹?鈹?鈥?YamlDotNet.dll        鈹?鈹?鈥?nunit.framework.dll   鈹?鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?```

---

## 馃殌 娴嬭瘯鎵ц娴佺▼ (瀹屾暣姝ラ)

### **绗?1 姝? 妫€鏌ラ」鐩幆澧?* 鉁?```
鍓嶇疆鏉′欢:
  鉁?Unity 6000.6.0a2 鎴栨洿楂樼増鏈?  鉁?Editor 鏍稿績妗嗘灦瀛樺湪
  鉁?Samples~/Yaml YAML 娴嬭瘯鏂囦欢瀛樺湪
  鉁?.unityuiflow.json 閰嶇疆鏂囦欢瀛樺湪
  鉁?Packages/manifest.json 渚濊禆宸插畨瑁?```

### **绗?2 姝? 鍔犺浇 YAML 娴嬭瘯鏂囦欢** 馃摑
```
娴佺▼:
  1. 鎵弿 Samples~/Yaml/ 鐩綍
  2. 璇诲彇鎵€鏈?*.yaml 鏂囦欢
  3. 浣跨敤 YamlTestCaseParser 瑙ｆ瀽 YAML 缁撴瀯
     - 楠岃瘉 steps 鑺傜偣
     - 楠岃瘉 action 瀛楁
     - 楠岃瘉 selector 瀛楁
     - 瑙ｆ瀽鏁版嵁婧?(data: CSV/JSON/inline)
  4. 鏋勫缓 ExecutionPlan (鎵ц璁″垝)
     - 鎷嗚В鍚勪釜娴嬭瘯姝ラ
     - 楠岃瘉閫夋嫨鍣ㄨ娉?     - 缁戝畾鏁版嵁杩唬
```

### **绗?3 姝? 鍒濆鍖栨祴璇曠獥鍙?* 馃獰
```
娴佺▼:
  1. 鍔犺浇绀轰緥 EditorWindow (濡?ExampleBasicLoginWindow)
  2. 鍒濆鍖?UIDocument 鍜?VisualElement 鏍?  3. 缁戝畾绀轰緥绐楀彛鍒?UnityUIFlowFixture
  4. 鍑嗗鍏冪礌閫夋嫨鍣ㄤ笂涓嬫枃
```

### **绗?4 姝? 鎵ц娴嬭瘯姝ラ** 鈿?```
娴佺▼ (閫愭鎵ц):
  For Each step in ExecutionPlan:
    1. 瑙ｆ瀽 selector (UIToolkit 閫夋嫨鍣?
    2. 瀹氫綅鐩爣 VisualElement
    3. 楠岃瘉鍏冪礌鐘舵€?
       - IsVisible? IsEnabled? IsInViewport?
    4. 鏍规嵁 action 绫诲瀷鎵ц:
       - click        鈫?妯℃嫙鐐瑰嚮浜嬩欢
       - type_text    鈫?杈撳叆鏂囨湰
       - assert       鈫?楠岃瘉鏂█
       - screenshot   鈫?鎴浘
       - wait         鈫?绛夊緟鏉′欢
       - 鍏朵粬...
    5. 璁板綍鎵ц缁撴灉:
       - 鉁?PASS
       - 鉂?FAIL (浜х敓閿欒鐮?
       - 鈴笍  SKIP (鏉′欢璺宠繃)
```

### **绗?5 姝? 鏀堕泦娴嬭瘯缁撴灉** 馃搳
```
娴佺▼:
  1. 姹囨€绘墽琛岀粺璁?
     - Total Steps
     - Passed Steps
     - Failed Steps
     - Skipped Steps
  2. 璁板綍澶辫触淇℃伅:
     - 閿欒鐮?(40+ 鏍囧噯閿欒鐮?
     - 閿欒娑堟伅
     - 澶辫触姝ラ鍙?  3. 濡傛灉鍚敤 screenshotOnFailure:
     - 淇濆瓨澶辫触鏃剁殑鎴浘鍒?Reports/screenshots/
```

### **绗?6 姝? 鐢熸垚娴嬭瘯鎶ュ憡** 馃搫
```
娴佺▼:
  1. 鏍煎紡鍖栨姤鍛婃暟鎹?  2. 杈撳嚭鏍煎紡閫夐」:
     - Markdown: ./Reports/report.md
     - JSON: ./Reports/report.json
  3. 鍖呭惈鍐呭:
     - 娴嬭瘯鐢ㄤ緥鍚嶇О
     - 鎵ц鏃堕棿
     - 閫氳繃鐜?     - 澶辫触璇︽儏
     - 鎴浘閾炬帴 (濡傛湁)
```

### **绗?7 姝? 杈撳嚭鏈€缁堢粨鏋?* 鉁?```
鎴愬姛鍦烘櫙:
  鉁?鎵€鏈夋祴璇曢€氳繃
  鈫?閫€鍑轰唬鐮? 0
  鈫?鐢熸垚鎶ュ憡
  
澶辫触鍦烘櫙:
  鉂?閮ㄥ垎/鍏ㄩ儴娴嬭瘯澶辫触
  鈫?閫€鍑轰唬鐮? 1
  鈫?鐢熸垚鎶ュ憡 + 澶辫触鎴浘
  鈫?杈撳嚭閿欒鐮佸拰閿欒娑堟伅
```

---

## 馃攳 鏍稿績绫昏鏄?
### 1. **YamlTestCaseParser** (璧勬簮璺緞: 寰呭畾)
- **鑱岃矗**: 瑙ｆ瀽 YAML 鏂囦欢涓虹粨鏋勫寲娴嬭瘯鐢ㄤ緥
- **杈撳叆**: `.yaml` 鏂囦欢璺緞
- **杈撳嚭**: `YamlTestCase` 瀵硅薄 (鍖呭惈 steps 鍒楄〃)
- **鍏抽敭鏂规硶**: `Parse()`, `ValidateSyntax()`

### 2. **ExecutionPlanBuilder** (璧勬簮璺緞: 寰呭畾)
- **鑱岃矗**: 灏?YamlTestCase 杞崲涓哄彲鎵ц鐨?ExecutionPlan
- **杈撳叆**: `YamlTestCase`
- **杈撳嚭**: `ExecutionPlan` (姝ラ搴忓垪)
- **鍏抽敭澶勭悊**: 鏁版嵁椹卞姩灞曞紑銆佹潯浠跺垽鏂€佸惊鐜瀯寤?
### 3. **ExecutionEngine** (璧勬簮璺緞: 寰呭畾)
- **鑱岃矗**: 閫愭鎵ц ExecutionPlan
- **杈撳叆**: `ExecutionPlan` + 鐩爣绐楀彛寮曠敤
- **杈撳嚭**: `ExecutionResult` (鎵ц缁撴灉)
- **鍏抽敭鏂规硶**: `Execute()`, `HandleAction()`, `FindElement()`

### 4. **ActionFactory** (璧勬簮璺緞: 寰呭畾)
- **鑱岃矗**: 鏍规嵁 action 鍚嶇О鍒涘缓瀵瑰簲鐨勫姩浣滃疄渚?- **瀹炵幇**: 宸ュ巶妯″紡锛屾敮鎸?16+ 鍐呯疆鍔ㄤ綔
- **鎵╁睍**: 閫氳繃 `customActionAssemblies` 閰嶇疆鍔犺浇鑷畾涔夊姩浣?- **鎺ュ彛**: 鎵€鏈夊姩浣滃疄鐜?`IAction` 鎺ュ彛

### 5. **ReportGenerator** (璧勬簮璺緞: 寰呭畾)
- **鑱岃矗**: 鐢熸垚 Markdown/JSON 鏍煎紡娴嬭瘯鎶ュ憡
- **杈撳叆**: `ExecutionResult` 闆嗗悎
- **杈撳嚭**: 鎶ュ憡鏂囦欢 (Markdown/JSON)
- **閰嶇疆**: `reportPath`, `format`

### 6. **UnityUIFlowFixture<TWindow>** (璧勬簮璺緞: 寰呭畾)
- **鑱岃矗**: C# 鍗曞厓娴嬭瘯鐨勫熀绫伙紝鎻愪緵 YAML 娴嬭瘯椹卞姩鏀寔
- **娉涘瀷**: `TWindow` 涓虹洰鏍?EditorWindow 绫诲瀷
- **鍏抽敭鏂规硶**: `ExecuteYamlTest(string path)`, `Setup()`, `TearDown()`
- **妗嗘灦**: 涓?Unity Test Framework 鏃犵紳闆嗘垚

---

## 馃搶 鍏抽敭鏂囦欢娓呭崟

| 鏂囦欢璺緞 | 鐢ㄩ€?|
|----------|------|
| [README.md](README.md) | 椤圭洰鏂囨。銆乊AML 璇硶銆佸揩閫熷紑濮?|
| [.unityuiflow.json](.unityuiflow.json) | 椤圭洰閰嶇疆 |
| [Editor/](Editor/) | 鏍稿績妗嗘灦浠ｇ爜 |
| [Samples~/Yaml/](Samples~/Yaml/) | 19 涓ず渚?YAML 娴嬭瘯 |
| [Samples~/Editor/](Samples~/Editor/) | 绀轰緥 EditorWindow 瀹炵幇 |
| [Samples~/Tests/](Samples~/Tests/) | C# 鍗曞厓娴嬭瘯 |
| [Packages/manifest.json](Packages/manifest.json) | 椤圭洰渚濊禆閰嶇疆 |

---

## 馃帗 Headed 妯″紡娴嬭瘯鍦烘櫙绀轰緥

### 鍦烘櫙 1: 绠€鍗曠殑UI浜や簰鍜岄獙璇?鉁?**鏂囦欢**: `Samples~/Yaml/01-basic-login.yaml`

娴嬭瘯鐩爣锛氳緭鍏ョ敤鎴峰悕/瀵嗙爜 鈫?鐐瑰嚮鐧诲綍 鈫?楠岃瘉娆㈣繋淇℃伅

```yaml
name: Example Basic Login
steps:
  - name: 濉厖鐢ㄦ埛鍚?    action: type_text_fast
    selector: "#username-input"
    value: "alice"
    # 鉁?Headed 妯″紡锛?    #   1. 瀹氫綅 #username-input 鍏冪礌
    #   2. 楂樹寒鏄剧ず锛堣竟妗嗗彉鑹诧級
    #   3. 蹇€熻緭鍏?"alice"
    #   4. 缂栬緫鍣ㄥ疄鏃舵樉绀鸿緭鍏ヨ繃绋?```

**Headed 鎵ц鏁堟灉**锛?```
鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹?     鐧诲綍绐楀彛 (鍙鍖?           鈹?鈹溾攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹?鐢ㄦ埛鍚? [alice            ]  鉁? 鈹?鈫?楂樹寒杈规锛屽疄鏃舵樉绀鸿緭鍏?鈹?瀵嗙爜:   [secret           ]      鈹?鈹?[鐧?褰昡                          鈹?鈹?鐘舵€? alice 娆㈣繋锛?             鈹?鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?```

---

### 鍦烘櫙 2: 閫夋嫨鍣ㄩ獙璇侊紙鍏抽敭鐢ㄩ€旓紒锛夆湪
**鏂囦欢**: `Samples~/Yaml/02-selectors-and-assertions.yaml`

娴嬭瘯鐩爣锛氶獙璇佸绉?CSS 閫夋嫨鍣ㄦ槸鍚︽纭懡涓厓绱?
```yaml
name: Example Selectors And Assertions
steps:
  - name: 鍖归厤瀛愬厓绱犻€夋嫨鍣?    action: assert_visible
    selector: "#selector-list > .selector-item:first-child"
    # 鉁?Headed 浼樺娍锛?    #   鑳界湅鍒?#selector-list 涓殑绗竴涓?.selector-item 楂樹寒
    #   蹇€熼獙璇侀€夋嫨鍣ㄨ娉曟槸鍚︽纭?
  - name: 鍖归厤灞炴€ч€夋嫨鍣?    action: assert_visible
    selector: "[tooltip=Inspect]"
    # 鉁?楂樹寒鎵€鏈?tooltip="Inspect" 鐨勫厓绱?    # 閬垮厤閫夋嫨閿欒鐨勫厓绱狅紒

  - name: 鍖归厤鏁版嵁灞炴€?    action: assert_visible
    selector: "[data-role=primary]"
    # 鉁?楂樹寒鎵€鏈?data-role="primary" 鐨勫厓绱?```

**Headed 妯″紡鐨勮瘖鏂兘鍔?*锛?```
銆愰€夋嫨鍣ㄦ祴璇?- 鏃犲ご妯″紡銆?鉂?鏂█澶辫触锛氭壘涓嶅埌 "[tooltip=Inspect]"
   鍘熷洜涓嶆槑锛岄渶瑕佹煡鐪?HTML 缁撴瀯

銆愰€夋嫨鍣ㄦ祴璇?- Headed 妯″紡銆?鉁?鐪嬪埌楂樹寒鏁堟灉锛岃兘绔嬪嵆璇嗗埆锛?   鉁?閫夋嫨鍣ㄦ槸鍚﹀尮閰嶆纭厓绱?   鉁?鍏冪礌鏄惁鐪熺殑涓嶅彲瑙侊紙display:none锛?   鉁?鍏冪礌鏄惁琚叾浠栧厓绱犻伄鎸?   鉁?閫夋嫨鍣ㄨ娉曟槸鍚︽湁璇?```

---

### 鍦烘櫙 3: 寮傛鍔犺浇鍜岀瓑寰?鈴?**鏂囦欢**: `Samples~/Yaml/03-wait-for-element.yaml`

娴嬭瘯鐩爣锛氭祴璇曞姩鎬佸嚭鐜扮殑鍏冪礌

```yaml
name: Example Wait For Element
steps:
  - name: 鍚姩寤惰繜鏄剧ず
    action: click
    selector: "#start-button"
    # 鉁?鐐瑰嚮鍚庯紝瑙﹀彂寮傛鍔犺浇

  - name: 绛夊緟娑堟伅鍏冪礌鍑虹幇
    action: wait_for_element
    selector: "#delayed-message"
    timeout: "2s"
    # 鉁?Headed 妯″紡鏄剧ず锛?    #   鈴?绛夊緟杩涘害鏉″姩鐢?    #   馃搷 杞娆℃暟璁℃暟
    #   鉁?鍏冪礌鍑虹幇鏃剁珛鍗抽珮浜?
  - name: 楠岃瘉鏈€缁堢姸鎬?    action: assert_text
    selector: "#delayed-message"
    expected: "Ready"
    # 纭鍔犺浇瀹屾垚鐨勬枃鏈唴瀹?```

**瀹炴椂绛夊緟杩囩▼鍙鍖?*锛?```
鏃堕棿    绛夊緟鐘舵€?        鎺у埗鍙拌緭鍑?             缂栬緫鍣ㄥ彲瑙?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
0ms    [==          ] "杞 1/20..."         鈴?杩涘害鏉?250ms  [====        ] "杞 2/20..."         鈴?杩涘害鏉?500ms  [======      ] "杞 3/20..."         鈴?杩涘害鏉?750ms  [========    ] "杞 4/20..."         鈴?杩涘害鏉?1000ms [==========  ] "杞 5/20..."         鈴?杩涘害鏉?1500ms [============] "杞 6/20..."         鈴?杩涘害鏉?1750ms [鉁?鎵惧埌锛乚   "鍏冪礌宸插嚭鐜?            鉁?鍏冪礌楂樹寒
2000ms [鉁?瀹屾垚]    "姝ラ閫氳繃"              鉁?淇濆瓨鎴浘
```

---

### 鍦烘櫙 4: 鏉′欢鎺у埗鍜屽惊鐜?馃攧
**鏂囦欢**: `Samples~/Yaml/04-conditional-and-loop.yaml`

娴嬭瘯鐩爣锛氭潯浠舵墽琛屽拰绛夊緟寰幆瀹屾垚

```yaml
name: Example Conditional And Loop
steps:
  - name: 濡傛灉淇濆瓨鎸夐挳瀛樺湪鍒欑偣鍑?    action: click
    selector: "#save-button"
    if:
      exists: "#save-button"
    # 鉁?Headed 妯″紡锛?    #   鐪嬪埌鏉′欢鏄惁婊¤冻
    #   鎸夐挳鏄惁鐪熺殑瀛樺湪/涓嶅瓨鍦?
  - name: 绛夊緟 Toast 娑堟伅鍑虹幇
    action: assert_visible
    selector: "#toast-message"
    timeout: "1s"
    # 鉁?楂樹寒 Toast 娑堟伅

  - name: 寰幆绛夊緟 Toast 娑堝け
    repeat_while:
      condition:
        exists: "#toast-message"
      max_iterations: 20
      steps:
        - name: 绛夊緟 50ms
          action: wait
          duration: "50ms"
    # 鉁?Headed 妯″紡鏄剧ず锛?    #   鈹溾攢 寰幆杩唬璁℃暟
    #   鈹溾攢 Toast 鍏冪礌闂儊锛堟瘡娆℃鏌ワ級
    #   鈹斺攢 鏈€缁堟秷澶辨椂鍙樻殫

  - name: 楠岃瘉 Toast 宸茬Щ闄?    action: assert_not_visible
    selector: "#toast-message"
    timeout: "500ms"
    # 鉁?纭鍏冪礌涓嶅彲瑙?```

**寰幆鎵ц鍙鍖?*锛?```
杩唬  鏉′欢妫€鏌?       Headed 鏄剧ず
鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
1    鏉′欢澶嶆牳涓?..    Toast 鍏冪礌闂儊 鉁?2    鏉′欢澶嶆牳涓?..    Toast 鍏冪礌闂儊 鉁?3    鏉′欢澶嶆牳涓?..    Toast 鍏冪礌闂儊 鉁?...
18   鏉′欢澶嶆牳涓?..    Toast 鍏冪礌闂儊 鉁?19   鏉′欢澶嶆牳涓?..    Toast 娑堝け锛佲渽
20   鏉′欢閫€鍑?        鉁?寰幆瀹屾垚
     鉁?鎬昏€楁椂: ~950ms
```

---

### 鍦烘櫙 5: 鏁版嵁椹卞姩娴嬭瘯 馃搳
**鏂囦欢**: `Samples~/Yaml/05-data-driven-csv.yaml`

娴嬭瘯鐩爣锛氬鐢ㄦ埛鐧诲綍娴嬭瘯锛堜粠 CSV 鏁版嵁婧愶級

```yaml
name: Example Data Driven Csv
fixture:
  host_window:
    type: UnityUIFlow.Examples.ExampleCsvLoginWindow
    reopen_if_open: true
  setup:
    - name: 姣忚鍓嶉噸缃?      action: click
      selector: "#reset-button"
      # 鉁?鏁版嵁琛屼箣鍓嶇殑鍒濆鍖栧姩浣?  teardown:
    - name: 姣忚鍚庨噸缃?      action: click
      selector: "#reset-button"
      # 鉁?鏁版嵁琛屼箣鍚庣殑娓呯悊鍔ㄤ綔

data:
  from_csv: example-users.csv
  # 鍔犺浇 CSV 鏂囦欢锛屾瘡琛屼綔涓轰竴娆℃祴璇曡凯浠?
steps:
  - name: 濉厖鐢ㄦ埛鍚?{{ username }}
    action: type_text_fast
    selector: "#username-input"
    value: "{{ username }}"
    # 鉁?鏁版嵁缁戝畾锛歿{ username }} 鏇挎崲涓?CSV 鍊?
  - name: 濉厖瀵嗙爜
    action: type_text_fast
    selector: "#password-input"
    value: "{{ password }}"

  - name: 鎻愪氦
    action: click
    selector: "#login-button"

  - name: 楠岃瘉缁撴灉
    action: assert_text
    selector: "#status-label"
    expected: "{{ expected }}"
    # 鉁?楠岃瘉棰勬湡缁撴灉
```

**CSV 鏂囦欢绀轰緥** (`example-users.csv`):
```csv
username,password,expected
alice,secret,alice 娆㈣繋锛?bob,pass123,bob 娆㈣繋锛?charlie,xyz,charlie 娆㈣繋锛?```

**Headed 妯″紡杩唬杩囩▼**锛?```
銆愯凯浠?1 - Alice銆?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
Setup: 鐐瑰嚮閲嶇疆 鉁?姝ラ 1: 杈撳叆 "alice" 鉁?楂樹寒
姝ラ 2: 杈撳叆 "secret" 鉁?楂樹寒
姝ラ 3: 鐐瑰嚮鎻愪氦 鉁?楂樹寒
姝ラ 4: 楠岃瘉 "alice 娆㈣繋锛? 鉁?Teardown: 鐐瑰嚮閲嶇疆 鉁?鉁?杩唬瀹屾垚

銆愯凯浠?2 - Bob銆?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
Setup: 鐐瑰嚮閲嶇疆 鉁?姝ラ 1: 杈撳叆 "bob" 鉁?楂樹寒
姝ラ 2: 杈撳叆 "pass123" 鉁?楂樹寒
姝ラ 3: 鐐瑰嚮鎻愪氦 鉁?楂樹寒
姝ラ 4: 楠岃瘉 "bob 娆㈣繋锛? 鉁?Teardown: 鐐瑰嚮閲嶇疆 鉁?鉁?杩唬瀹屾垚

銆愯凯浠?3 - Charlie銆?鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
Setup: 鐐瑰嚮閲嶇疆 鉁?姝ラ 1: 杈撳叆 "charlie" 鉁?楂樹寒
姝ラ 2: 杈撳叆 "xyz" 鉁?楂樹寒
姝ラ 3: 鐐瑰嚮鎻愪氦 鉁?楂樹寒
姝ラ 4: 楠岃瘉 "charlie 娆㈣繋锛? 鉁?Teardown: 鐐瑰嚮閲嶇疆 鉁?鉁?杩唬瀹屾垚

鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹佲攣鈹?鎬荤粺璁? 3 琛?脳 4 姝?= 12 鏉℃祴璇曢€氳繃 鉁?```

---

### 鍦烘櫙 6: 鏂囨湰杈撳叆娴嬭瘯 鈱笍
**鏂囦欢**: `Samples~/Yaml/12-type-text.yaml`

娴嬭瘯鐩爣锛氶獙璇佷笉鍚岀殑鏂囨湰杈撳叆閫熷害

```yaml
name: Example Type Text
steps:
  - name: 鎱㈤€熻緭鍏ワ紙閫愪釜瀛楃锛?    action: type_text
    selector: "#type-text-input"
    value: "typed slowly"
    # 鉁?Headed 妯″紡锛?    #   鐪嬪埌姣忎釜瀛楃閫愪釜杈撳叆
    #   妯℃嫙鐪熷疄鐢ㄦ埛鎵撳瓧閫熷害
    # 鐢ㄩ€旓細娴嬭瘯杈撳叆浜嬩欢鏄惁姝ｇ‘瑙﹀彂

  - name: 楠岃瘉杈撳叆鍊?    action: assert_property
    selector: "#type-text-input"
    property: "value"
    expected: "typed slowly"
    # 鉁?纭鍊煎凡姝ｇ‘璁剧疆
```

**涓ょ杈撳叆妯″紡瀵规瘮**锛?```
銆恡ype_text_fast銆?杈撳叆杩囩▼: "typed slowly" 涓€娆℃€ц緭鍏?Headed 鏄剧ず: 鏂囨湰鐬棿濉厖 鈿?鐢ㄩ€? 楂樻晥娴嬭瘯锛岄€熷害蹇?椋庨櫓: 鍙兘璺宠繃浜嬩欢澶勭悊

銆恡ype_text銆?杈撳叆杩囩▼: t-y-p-e-d-...-l-y 閫愪釜瀛楃
Headed 鏄剧ず: 鉁?姣忎釜瀛楃渚濇鍑虹幇
鐢ㄩ€? 娴嬭瘯杈撳叆浜嬩欢澶勭悊锛屽彂鐜伴殣钘?bug
閫熷害: 杈冩參锛屼絾鏇寸湡瀹?```

---

## 馃悰 Headed 妯″紡鏁呴殰鎺掗櫎

### 甯歌闂 1: 閫夋嫨鍣ㄦ壘涓嶅埌鍏冪礌

**鐥囩姸**锛?```
鉂?[ERROR] Element not found: #username-input
```

**Headed 妯″紡璋冭瘯**锛?1. 鍚敤 Headed 妯″紡
2. 瑙傚療缂栬緫鍣ㄧ獥鍙ｏ細
   - 鉁?娌℃湁楂樹寒 鈫?閫夋嫨鍣ㄨ娉曢敊璇垨鍏冪礌涓嶅瓨鍦?   - 鉁?楂樹寒閿欒鐨勫厓绱?鈫?閫夋嫨鍣ㄥ畾浣嶆湁璇?3. 妫€鏌ョ偣锛?   - 閫夋嫨鍣ㄦ槸鍚︿娇鐢ㄤ簡姝ｇ‘鐨?ID 鎴?Class锛?   - 鍏冪礌鍦?Dom 鏍戜腑鐨勫眰绾ф槸鍚︽纭紵
   - 鏄惁闇€瑕佷娇鐢ㄥ瓙閫夋嫨鍣?(>) 鎴栧悗浠ｉ€夋嫨鍣?(绌烘牸)锛?
**瑙ｅ喅鏂规**锛?```yaml
# 鉂?閿欒锛氫笉瀛樺湪鐨?ID
selector: "#login-box"

# 鉁?姝ｇ‘锛氭鏌ュ厓绱犲疄闄?ID
selector: "#login-button"

# 鉁?姝ｇ‘锛氫娇鐢ㄥ瓙閫夋嫨鍣ㄧ簿纭畾浣?selector: "#form > #username-input"

# 鉁?姝ｇ‘锛氫娇鐢ㄥ睘鎬ч€夋嫨鍣?selector: "[tooltip=Login]"
```

---

### 甯歌闂 2: 瓒呮椂绛夊緟澶辫触

**鐥囩姸**锛?```
鉂?[ERROR] Element not appeared within 2000ms
```

**Headed 妯″紡璋冭瘯**锛?1. 瑙傚療缂栬緫鍣ㄤ腑鐨勭瓑寰呰繃绋嬶細
   - 鈴?杩涘害鏉℃槸鍚﹀湪鍔紵
   - 馃搷 鍏冪礌鏄惁鐪熺殑娌℃湁鍑虹幇锛?   - 鈴?鏄惁瓒呮椂鏃堕棿涓嶅闀匡紵

2. 妫€鏌ョ偣锛?   - 瑙﹀彂寮傛鍔犺浇鐨勫墠缃潯浠舵槸鍚︽墽琛岋紵
   - 寮傛鎿嶄綔鏄惁姝ｅ父锛堝缃戠粶璇锋眰銆佸姩鐢伙級锛?   - timeout 鍊兼槸鍚﹁缃繃鐭紵

**瑙ｅ喅鏂规**锛?```yaml
# 鉂?瓒呮椂璁剧疆杩囩煭
action: wait_for_element
selector: "#delayed-message"
timeout: "500ms"  # 澶煭

# 鉁?澧炲姞瓒呮椂鏃堕棿
action: wait_for_element
selector: "#delayed-message"
timeout: "3s"  # 缁欒冻鏃堕棿

# 鉁?鎴栬€呮坊鍔犲墠缃?wait
- name: 绛夊緟涓€娈垫椂闂?  action: wait
  duration: "500ms"

- name: 鍐嶇瓑寰呭厓绱?  action: wait_for_element
  selector: "#delayed-message"
  timeout: "2s"
```

---

### 甯歌闂 3: 鏂█澶辫触浣嗙湅涓嶅嚭鍘熷洜

**鐥囩姸**锛?```
鉂?[ERROR] Assert failed: expected "alice" but was "Alice"
```

**Headed 妯″紡璋冭瘯**锛?1. 鎵撳紑 Headed 妯″紡锛岃瀵熺紪杈戝櫒锛?   - 鉁?鍏冪礌鏄惁楂樹寒锛?   - 馃摑 瀹為檯鏄剧ず鐨勬枃鏈槸浠€涔堬紵
   - 馃攳 鏄惁瀛樺湪澶у皬鍐欏樊寮傦紵

2. 浠旂粏妫€鏌ワ細
   - 鏂囨湰澶у皬鍐欐槸鍚﹀尮閰嶏紙鍖哄垎澶у皬鍐欙級锛?   - 鏄惁鏈夊墠鍚庣┖鏍硷紵
   - 鏄惁浣跨敤浜嗘纭殑鏂█绫诲瀷锛?
**瑙ｅ喅鏂规**锛?```yaml
# 鉂?澶у皬鍐欎笉鍖归厤
expected: "alice"  # 棰勬湡灏忓啓

# 鉁?妫€鏌ュ疄闄呰緭鍑虹殑澶у皬鍐?expected: "Alice"  # 瀹為檯杈撳嚭鏄ぇ鍐?
# 鉁?鎴栦娇鐢ㄥ寘鍚柇瑷€锛堜笉鍖哄垎澶у皬鍐欙級
action: assert_text_contains
selector: "#status-label"
expected: "alice"  # 鍙鍖呭惈鍗冲彲
```

---

### 甯歌闂 4: 鍏冪礌琚伄鎸?
**鐥囩姸**锛?```
鉂?[ERROR] Element is not interactable: overlapped or hidden
```

**Headed 妯″紡璋冭瘯**锛?1. 瑙傚療 Headed 妯″紡涓殑鍏冪礌锛?   - 鉁?鏄惁鏈夊叾浠栧厓绱犻伄鎸★紵
   - 馃搷 鍏冪礌鏄惁鐪熺殑鍦ㄥ彲瑙嗗尯鍩熷唴锛?   - 馃帹 鏄惁琚埗鍏冪礌鐨?overflow:hidden 闅愯棌锛?
2. 妫€鏌ョ偣锛?   - 鏄惁闇€瑕佸厛婊氬姩椤甸潰锛圫croll锛夊埌鍏冪礌锛?   - 鏄惁闇€瑕佸叧闂伄鎸＄殑 Modal锛?   - 鏄惁闇€瑕佸睍寮€鎶樺彔鐨勫鍣紵

**瑙ｅ喅鏂规**锛?```yaml
# 鉂?鍏冪礌琚伄鎸?- name: 鐩存帴鐐瑰嚮
  action: click
  selector: "#hidden-button"

# 鉁?鍏堟粴鍔ㄥ埌鍏冪礌鍙
- name: 婊氬姩鍒板厓绱?  action: scroll
  selector: "#container"
  direction: down

- name: 鐒跺悗鐐瑰嚮
  action: click
  selector: "#hidden-button"

# 鉁?鎴栬€呭厛鍏抽棴 Modal
- name: 鍏抽棴寮圭獥
  action: click
  selector: "#close-modal"

- name: 鍐嶇偣鍑?  action: click
  selector: "#hidden-button"
```

---

### 甯歌闂 5: 寰幆鏃犳硶缁撴潫

**鐥囩姸**锛?```
鈿狅笍 [WARNING] Repeat loop reached max_iterations (20), exiting
```

**Headed 妯″紡璋冭瘯**锛?1. 瑙傚療寰幆杩囩▼锛?   - 馃搷 鏉′欢鏄惁鑳芥纭垽鏂紵
   - 馃攧 姣忔杩唬鏄惁鏈夎繘搴︼紵
   - 鈴?鏄惁闇€瑕佸鍔?max_iterations锛?
2. 妫€鏌ョ偣锛?   - 寰幆鏉′欢鏄惁姘歌繙涓?true锛?   - 鏄惁閬楁紡浜嗘煇涓竻鐞嗘楠わ紵
   - 鍏冪礌鏄惁鐪熺殑浼氭秷澶憋紵

**瑙ｅ喅鏂规**锛?```yaml
# 鉂?鏉′欢鍙兘姘镐笉婊¤冻
repeat_while:
  condition:
    exists: "#modal"
  max_iterations: 20
  steps:
    - name: 绛夊緟
      action: wait
      duration: "50ms"

# 鉁?纭繚寰幆浣撹兘鏀瑰彉鏉′欢
repeat_while:
  condition:
    exists: "#toast-message"
  max_iterations: 20
  steps:
    - name: 绛夊緟涓€娈垫椂闂?      action: wait
      duration: "100ms"
    # 鎴栬€呮坊鍔犱富鍔ㄥ叧闂楠?    - name: 鍏抽棴閫氱煡
      action: click
      selector: "#toast-close"
      if:
        exists: "#toast-close"
```

---

## 馃摓 Headed 妯″紡蹇€熷弬鑰?
| 浠诲姟 | 姝ラ | Headed 瑙傚療 |
|------|------|-----------|
| **楠岃瘉閫夋嫨鍣?* | 杩愯 assert_visible | 鉁?鍏冪礌楂樹寒 |
| **璋冭瘯鏂囨湰杈撳叆** | 杩愯 type_text 鎴?type_text_fast | 馃摑 鐪嬪瓧绗﹁緭鍏ヨ繃绋?|
| **璇婃柇瓒呮椂** | 杩愯 wait_for_element | 鈴?鐪嬭疆璇㈣繘搴?|
| **鏌ョ湅鏉′欢鍒ゆ柇** | 杩愯 if 鎴?repeat_while | 馃攳 鐪嬫潯浠堕珮浜?|
| **楠岃瘉鐐瑰嚮** | 杩愯 click | 馃柋锔?鐪嬪厓绱犻棯鐑佸搷搴?|
| **妫€鏌ユ柇瑷€** | 杩愯 assert_* | 鉁?鐪嬮獙璇佺粨鏋?|
| **閫熷害瀵规瘮** | type_text vs type_text_fast | 鈿?鐪嬫墽琛岄€熷害 |

---



## 鈿欙笍 鏁呴殰鎺掗櫎

| 闂 | 鍘熷洜 | 瑙ｅ喅鏂规 |
|------|------|---------|
| YAML 鏂囦欢瑙ｆ瀽澶辫触 | 璇硶閿欒 | 妫€鏌?YAML 缂╄繘銆佸瓧娈靛悕鏄惁姝ｇ‘ |
| 鍏冪礌鎵句笉鍒?| 閫夋嫨鍣ㄩ敊璇?| 楠岃瘉 UI 鏍戠粨鏋勶紝璋冩暣閫夋嫨鍣?|
| 瓒呮椂閿欒 | 鍏冪礌鍔犺浇鎱?| 澧炲姞 timeout 鎴?`defaultTimeoutMs` |
| 鍔ㄤ綔鎵ц澶辫触 | 鍏冪礌涓嶅彲瑙?绂佺敤 | 妫€鏌ュ墠缃潯浠讹紝娣诲姞 wait 姝ラ |
| 鎶ュ憡鏈敓鎴?| 璺緞鏉冮檺 | 妫€鏌?`reportPath` 鐩綍鏉冮檺 |

---

## 馃摎 鎵╁睍鍜岃嚜瀹氫箟

### 娣诲姞鑷畾涔夊姩浣滄楠?1. 鍒涘缓瀹炵幇 `IAction` 鎺ュ彛鐨?C# 绫?2. 鍦?`.unityuiflow.json` 涓敞鍐岀▼搴忛泦:
   ```json
   {
     "customActionAssemblies": ["YourCustomAssembly"]
   }
   ```
3. 鍦?YAML 涓娇鐢ㄨ嚜瀹氫箟鍔ㄤ綔:
   ```yaml
   steps:
     - action: your_custom_action
       param1: value1
       param2: value2
   ```

### 鍒涘缓鏂扮殑绀轰緥 EditorWindow
1. 缁ф壙 `EditorWindow`
2. 瀹炵幇 `OnGUI()` 鎴栦娇鐢?UIToolkit
3. 浣跨敤鍛藉悕瑙勮寖: `Example[YourWindow]Window`
4. 灏嗙被鏀惧湪 `Samples~/Editor/` 涓?5. 瀵瑰簲鐨?YAML 娴嬭瘯鏀惧湪 `Samples~/Yaml/` 涓?
---

## 鉁?Headed 妯″紡娴嬭瘯瀹屾暣鍑嗗娓呭崟

### 鐜妫€鏌?- [ ] Unity 鐗堟湰 6000.6.0a2 鎴栨洿楂?- [ ] 鎵€鏈変緷璧栧寘宸插畨瑁?(Test Framework銆両nputSystem銆乁I TestFramework 绛?
- [ ] MCP 鏈嶅姟鍣ㄥ凡杩炴帴 (鐘舵€? connected 鉁?
- [ ] 椤圭洰鏈瓨鍦ㄧ紪璇戦敊璇?
### 閰嶇疆妫€鏌?- [ ] `.unityuiflow.json` 鏂囦欢瀛樺湪涓斿彲璇?- [ ] `"headed": true` 宸插惎鐢?- [ ] `"reportPath": "./Reports"` 鐩綍鏈夊啓鏉冮檺
- [ ] `"screenshotOnFailure": true` 宸插惎鐢紙渚夸簬璋冭瘯锛?- [ ] `"defaultTimeoutMs": 10000` 璁剧疆鍚堢悊

### 娴嬭瘯鏂囦欢妫€鏌?- [ ] YAML 娴嬭瘯鏂囦欢瀛樺湪浜?`Samples~/Yaml/`
  - [ ] 01-basic-login.yaml 鉁?  - [ ] 02-selectors-and-assertions.yaml 鉁?  - [ ] 03-wait-for-element.yaml 鉁?  - [ ] 鍏朵粬娴嬭瘯鏂囦欢瀹屾暣 鉁?- [ ] 绀轰緥 EditorWindow 瀛樺湪浜?`Samples~/Editor/`
  - [ ] ExampleBasicLoginWindow
  - [ ] ExampleSelectorsWindow
  - [ ] ExampleWaitForElementWindow
  - [ ] 鍏朵粬绀轰緥绐楀彛

### Headed 妯″紡鐗规畩妫€鏌?- [ ] 缂栬緫鍣ㄦ樉绀哄櫒姝ｅ父杩炴帴锛堥渶瑕?UI 娓叉煋锛?- [ ] 缂栬緫鍣ㄦ湭鏈€灏忓寲锛堢獥鍙ｅ彲瑙侊級
- [ ] GPU 鍔犻€熷惎鐢紙鏀瑰杽娓叉煋鎬ц兘锛?- [ ] 瓒冲鐨?RAM锛圚eaded 浼氬鍔犲唴瀛樺崰鐢級
- [ ] 棰勭暀纾佺洏绌洪棿鐢ㄤ簬鎴浘 (鏈€灏?100MB)

### 绗竴娆?Headed 娴嬭瘯寤鸿
1. **浠庣畝鍗曟祴璇曞紑濮?*: 鍏堣繍琛?`01-basic-login.yaml`
2. **浣跨敤榛樿瓒呮椂**: 涓嶈杩囧害浼樺寲瓒呮椂鏃堕棿
3. **鍚敤鎴浘**: 渚夸簬鍚庣画鍒嗘瀽
4. **鐩戞帶缂栬緫鍣?*: 瑙傚療鍏冪礌楂樹寒鏁堟灉
5. **璁板綍闂**: 璁颁笅閬囧埌鐨勪换浣曢€夋嫨鍣ㄦ垨绛夊緟闂

---

## 馃搳 Headed 妯″紡 vs CLI 妯″紡閫夋嫨琛?
| 鍦烘櫙 | 寤鸿妯″紡 | 鐞嗙敱 |
|------|---------|------|
| **寮€鍙戣皟璇?* | 鉁?Headed | 鍙鍖栧揩閫熷畾浣嶉棶棰?|
| **閫夋嫨鍣ㄩ獙璇?* | 鉁?Headed | 鐪嬪埌楂樹寒鏁堟灉鏈€鐩磋 |
| **瓒呮椂闂璇婃柇** | 鉁?Headed | 鑳借瀵熺瓑寰呰繃绋?|
| **鏂版祴璇曠紪鍐?* | 鉁?Headed | 浜や簰寮忓紑鍙戞洿楂樻晥 |
| **CI/CD 娴佹按绾?* | 鉁?CLI | 鏃?GUI 鐜锛岄€熷害蹇?|
| **鎬ц兘鍩哄噯娴嬭瘯** | 鈿狅笍 CLI | Headed 鏈夌粯鍒跺紑閿€ |
| **鑷姩鍖栨祴璇曞浠?* | 鉁?CLI | 鍙潬绋冲畾锛屼笉渚濊禆鏄剧ず |
| **鍥㈤槦娴嬭瘯鎶ュ憡** | 馃搳 涓よ€?| 鍏堢敤 Headed 璋冭瘯锛屽啀鐢?CLI 鐢熶骇 |

---

## 馃搱 娴嬭瘯鎵ц鐨勫畬鏁村伐浣滄祦

```
鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹?                 寮€濮?Headed 妯″紡娴嬭瘯                     鈹?鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?                         鈹?                         鈻?      鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?      鈹?绗?1 姝ワ細鍚敤 Headed 閰嶇疆       鈹?      鈹?(.unityuiflow.json)            鈹?      鈹?- "headed": true               鈹?      鈹?- "defaultTimeoutMs": 10000    鈹?      鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?                   鈹?                   鈻?      鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?      鈹?绗?2 姝ワ細閫夋嫨娴嬭瘯鏂囦欢            鈹?      鈹?(Samples~/Yaml/...)      鈹?      鈹?鉁?01-basic-login.yaml          鈹?      鈹?鉁?02-selectors-and-assertions  鈹?      鈹?鉁?03-wait-for-element.yaml     鈹?      鈹?鉁?...鍏朵粬娴嬭瘯鏂囦欢              鈹?      鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?                   鈹?                   鈻?      鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?      鈹?绗?3 姝ワ細MCP 鏈嶅姟鍣ㄦ墽琛?         鈹?      鈹?Tool: mcp_unitypilot_...e2e_run 鈹?      鈹?- specPath: 娴嬭瘯鏂囦欢璺緞        鈹?      鈹?- artifactDir: 杈撳嚭鐩綍         鈹?      鈹?- exportZip: 瀵煎嚭鍘嬬缉鍖?        鈹?      鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?                   鈹?                   鈻?      鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?      鈹?绗?4 姝ワ細Headed 妯″紡鎵ц         鈹?      鈹?馃摵 缂栬緫鍣ㄥ彲瑙嗗寲鏄剧ず             鈹?      鈹?- 鍏冪礌楂樹寒                      鈹?      鈹?- 姝ラ鎵ц杩涘害                  鈹?      鈹?- 鎴浘淇濆瓨                     鈹?      鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?                   鈹?        鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹粹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?        鈹?                    鈹?        鈻?                    鈻?    鉁?鍏ㄩ儴閫氳繃          鉂?鍑虹幇澶辫触
    鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?       鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?    鈹?鐢熸垚鎶ュ憡 鈹?       鈹?鏌ョ湅闂    鈹?    鈹?result   鈹?       鈹?- 閫夋嫨鍣ㄩ敊璇攤
    鈹?PASS     鈹?       鈹?- 瓒呮椂      鈹?    鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?       鈹?- 鏂█澶辫触  鈹?                        鈹斺攢鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹?                              鈹?                              鈻?                        鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?                        鈹?璋冭瘯鍜屼慨澶?  鈹?                        鈹?- 鏇存柊 YAML  鈹?                        鈹?- 淇敼閰嶇疆   鈹?                        鈹斺攢鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹?                              鈹?                              鈻?                        銆愰噸鏂版墽琛屻€?                        
鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?```

---

## 馃幆 Headed 妯″紡鏈€浣冲疄璺?
### 鉁?鎺ㄨ崘鍋氭硶
```yaml
# 1. 鍛藉悕娓呮櫚鐨勬楠?- name: 鐐瑰嚮鐧诲綍鎸夐挳
  action: click
  selector: "#login-button"
  # 鏄庣‘鐨勬楠ゅ悕绉帮紝渚夸簬璋冭瘯鏃跺揩閫熷畾浣?
# 2. 鍚堢悊鐨勮秴鏃惰缃?action: wait_for_element
selector: "#result-message"
timeout: "3s"  # 缁欒冻鍐椾綑鏃堕棿锛孒eaded 涓嬭兘鐪嬪埌杞杩囩▼

# 3. 瀹屾暣鐨勬柇瑷€
- name: 楠岃瘉鏈€缁堢姸鎬?  action: assert_text
  selector: "#status-label"
  expected: "鐧诲綍鎴愬姛"  # 鍏蜂綋鐨勯鏈熷€?
# 4. 蹇呰鐨勬埅鍥?- name: 淇濆瓨娴嬭瘯璇佹嵁
  action: screenshot
  tag: "final-state"  # 鏈夋剰涔夌殑鏍囩
```

### 鉂?閬垮厤鍋氭硶
```yaml
# 1. 瓒呯煭瓒呮椂
timeout: "500ms"  # 鍙兘瀵艰嚧璇垽

# 2. 妯＄硦鐨勬楠ゅ悕绉?- action: click
  selector: "#btn"
  # 鏃犳硶蹇€熺悊瑙ｆ祴璇曟剰鍥?
# 3. 杩囧害鐨勭瓑寰?action: wait
duration: "5000ms"  # 澶暱娴垂鏃堕棿

# 4. 缂哄皯鏂█
- action: click
  selector: "#submit"
# 娌℃湁楠岃瘉鎿嶄綔缁撴灉鏄惁姝ｇ‘
```

---

## 馃摓 蹇€熷弬鑰冨懡浠?
| 闇€姹?| 鍛戒护/姝ラ | 璇存槑 |
|------|---------|------|
| **鍚敤 Headed** | 缂栬緫 `.unityuiflow.json` + `"headed": true` | 绔嬪嵆鍚敤鍙鍖栨ā寮?|
| **杩愯鍗曚釜娴嬭瘯** | MCP 宸ュ叿 + specPath + 鏂囦欢璺緞 | 鎵ц鎸囧畾鐨?YAML 娴嬭瘯 |
| **杩愯澶氫釜娴嬭瘯** | 寰幆璋冪敤 MCP 宸ュ叿 + 涓嶅悓鐨?specPath | 渚濇鎵ц涓嶅悓鐨勬祴璇曟枃浠?|
| **鏌ョ湅鎶ュ憡** | 鎵撳紑 `./Reports/report.md` | 鏌ョ湅瀹屾暣鐨勬祴璇曟姤鍛?|
| **鏌ョ湅鎴浘** | 鎵撳紑 `./Reports/screenshots/` | 鏌ョ湅澶辫触鏃剁殑璇佹嵁鎴浘 |
| **绂佺敤 Headed** | 缂栬緫 `.unityuiflow.json` + `"headed": false` | 鍒囨崲鍥炲揩閫熸ā寮?|
| **澧炲姞瓒呮椂** | 缂栬緫 `.unityuiflow.json` + `"defaultTimeoutMs": 15000` | 缁欐參閫?PC 鏇村鏃堕棿 |
| **鍑忓皯杩囩▼杈撳嚭** | 閰嶇疆鏃ュ織绾у埆锛堝鏈夛級 | 绠€鍖栨帶鍒跺彴杈撳嚭 |

---

**鏂囨。鐗堟湰**: 2.0 (Headed 妯″紡瀹屽叏鎸囧崡)  
**鏈€鍚庢洿鏂?*: 2026 骞?4 鏈?14 鏃? 
**閫傜敤椤圭洰**: UnityUIFlow 鑷姩鍖栨祴璇曟鏋? 
**閲嶇偣**: MCP 鏈嶅姟鍣?+ Headed 鍙鍖栬皟璇曞畬鏁村伐浣滄祦
