# IMGUI 鎺т欢鑷姩鍖栬鐩栦笌闄愬埗璇存槑

鐗堟湰锛?.0.0
鏃ユ湡锛?026-04-23
鐘舵€侊細宸插叏閲忛獙璇侊紙8 浠?IMGUI YAML 鍥炲綊鐢ㄤ緥鍏ㄩ儴鎵ц閫氳繃锛屽惈璐熷悜娴嬭瘯锛?
---

## 1. 鏂囨。鐩爣

鏈枃妗ｅ垪鍑?UnityUIFlow V1 瀵?IMGUI锛圛mmediate Mode GUI锛夋帶浠剁殑鑷姩鍖栬鐩栬寖鍥达紝鏄庣‘浠ヤ笅鍒嗙被锛?
- **鍏ㄩ潰鏀寔**锛氬彲閫氳繃 YAML `imgui_*` 澹版槑寮忔楠ゅ畬鎴愬畾浣嶃€佷氦浜掋€佽祴鍊笺€佹柇瑷€銆?- **灞€閮ㄦ敮鎸?*锛氬彲瀹氫綅鍜岄儴鍒嗕氦浜掞紝浣嗛儴鍒嗛珮绾у姛鑳介渶 C# 缂栫▼鎴栧弽灏勮緟鍔┿€?- **浠呭畾浣?鏂█**锛氬彲閫氳繃閫夋嫨鍣ㄥ畾浣嶅拰璇诲彇灞炴€э紝浣嗕氦浜掑彈闄愭垨闇€闂存帴鎵嬫銆?- **涓嶅彲鑷姩鍖?*锛氱敱浜?IMGUI 鏋舵瀯鐗规€ф垨 Unity API 闄愬埗锛屾棤娉曢€氳繃褰撳墠鑷姩鍖栨墜娈垫搷浣溿€?
IMGUI 涓?UIToolkit 鏄袱濂楃嫭绔嬬殑娓叉煋浣撶郴锛?
- UIToolkit 淇濈暀 `VisualElement` 鎸佷箙鏍戯紝鍙€氳繃 CSS-like 閫夋嫨鍣ㄩ亶鍘嗗畾浣嶃€?- IMGUI 姣忓抚閫氳繃 `OnGUI()` 鍥炶皟鍗虫椂缁樺埗锛屾棤鎸佷箙鏍戠粨鏋勶紝鎺т欢浠呭湪涓€甯х殑 `GUILayoutUtility` 甯冨眬鎵规涓煭鏆傚瓨鍦ㄣ€?
鍥犳锛孖MGUI 鑷姩鍖栦笉璧?`ElementFinder` + `VisualElement` 璺緞锛岃€屾槸寤虹珛浜嗕竴濂楀钩琛屽瓙绯荤粺锛堣 搂7 璁捐鍘熺悊锛夈€?
褰撳墠 IMGUI 楠屾敹鍩虹嚎锛?
- `Samples~/Yaml/99-imgui-example.yaml`锛氫富璺緞鍐掔儫娴嬭瘯锛堢偣鍑汇€佽緭鍏ャ€侀€夋嫨銆佹柇瑷€銆佹埅鍥撅級銆?- `Samples~/Yaml/98-imgui-advanced.yaml`锛氶珮绾у満鏅紙绛夊緟瓒呮椂銆佹粴鍔ㄣ€佺劍鐐归摼銆佺粍鍚堥敭銆佸€兼柇瑷€锛夈€?- `Samples~/Yaml/97-imgui-negative-assert.yaml`锛氳礋鍚戞柇瑷€楠岃瘉銆?- `Samples~/Yaml/_96-imgui-negative-wait.yaml`锛氳礋鍚戠瓑寰呰秴鏃堕獙璇併€?- `Samples~/Yaml/_97-imgui-negative-assert.yaml`锛氳礋鍚戞柇瑷€鍓湰楠岃瘉銆?- `Samples~/Yaml/69-imgui-alternative-params.yaml`锛氭浛浠ｅ弬鏁伴獙璇侊紙`option`銆乣selector`锛夈€?- `Samples~/Yaml/_78-negative-type-text-non-input.yaml`锛歵ype_text 搴旂敤浜庨潪杈撳叆鍏冪礌璐熷悜楠岃瘉銆?- `Samples~/Yaml/_79-negative-press-key-combination.yaml`锛氭棤鏁堢粍鍚堥敭璐熷悜楠岃瘉銆?
---

## 2. 浣曟椂鐢?YAML 娴嬭瘯鐢ㄤ緥锛屼綍鏃剁敤 C# 浠ｇ爜娴嬭瘯鐢ㄤ緥

### 2.1 浼樺厛閫夋嫨 YAML 娴嬭瘯鐢ㄤ緥鐨勫満鏅?
| 鍦烘櫙 | 鍘熷洜 |
|------|------|
| IMGUI EditorWindow 鍩虹鎺т欢浜や簰锛堢偣鍑汇€佽緭鍏ャ€佹柇瑷€锛?| `imgui_click`銆乣imgui_type`銆乣imgui_assert_text` 鐩存帴鏀寔 |
| 鏂囨湰瀛楁杈撳叆涓庤鍙?| `imgui_type`锛堥€愬瓧绗﹁緭鍏ワ級+ `imgui_read_value`锛堝瓨鍏?SharedBag锛夎鐩栧畬鏁磋矾寰?|
| 鎸夐挳鐐瑰嚮涓庡彲瑙佹€ф柇瑷€ | `imgui_click` + `imgui_assert_visible` 鏄渶绠€鍗曠殑楠岃瘉褰㈠紡 |
| 涓嬫媺閫夋嫨锛堝凡鐭ラ€夐」锛?| `imgui_select_option` 鏀寔 `field_name` 鍙嶅皠鐩村啓锛堟渶绋冲畾锛夋垨浜嬩欢妯℃嫙鍥為€€ |
| 鐒︾偣涓庨敭鐩樺揩鎹烽敭 | `imgui_focus` + `imgui_press_key_combination`锛堝 `Ctrl+A`銆乣Delete`锛?|
| 婊氬姩瑙嗗浘 | `imgui_scroll` 鐩存帴鏀寔 |
| 绛夊緟鎺т欢鍑虹幇锛堣疆璇級 | `imgui_wait`锛堟敮鎸?`timeout` 鍙傛暟锛?|
| 涓?UIToolkit 鍔ㄤ綔娣风敤锛堝悓涓€ YAML 鏂囦欢锛?| IMGUI 鍔ㄤ綔鍙笌 UIToolkit 鍔ㄤ綔鏃犵紳娣锋帓锛岄€傚悎娣峰悎 UI 绐楀彛 |
| 璐熷悜娴嬭瘯锛堟柇瑷€鎺т欢涓嶅瓨鍦ㄣ€佽秴鏃跺け璐ワ級 | `imgui_wait` + `timeout` 閰嶅悎 `expected_failure` 娴嬭瘯 |

### 2.2 闇€瑕?C# 浠ｇ爜娴嬭瘯鐢ㄤ緥鐨勫満鏅?
| 鍦烘櫙 | 鍘熷洜 |
|------|------|
| `GenericMenu` / `EditorUtility.DisplayDialog` 娴獥鍐呴儴鎿嶄綔 | 寮瑰嚭鑿滃崟鐢?Editor 鍏ㄥ眬绠＄悊锛屼笉鍦ㄧ洰鏍囩獥鍙?`GUILayoutUtility` 甯冨眬鎵规鍐?|
| 鐙珛 `EditorWindow` 寮圭獥锛圕olor Picker銆丆urve Editor锛?| 鎷ユ湁鐙珛 `OnGUI` 涓婁笅鏂囷紝褰撳墠 IMGUI 妗ユ帴鎸夊崟绐楀彛缁勭粐 |
| Toggle 杩愯鏃?bool 鍊肩簿纭柇瑷€ | `GUILayoutEntry` 涓嶅瓨鍌ㄦ帶浠朵笟鍔＄姸鎬佸€硷紱闇€ C# 鍙嶅皠璇诲彇琚祴绐楀彛瀛楁 |
| Slider 褰撳墠 float 鍊肩簿纭柇瑷€ | 鍚屼笂锛沗imgui_assert_value` 鍙兘灏藉姏鎺ㄦ柇 |
| 绾粯鍒舵帶浠讹紙`GUI.DrawTexture`銆乣Handles`锛?| 涓嶇粡杩?`GUILayoutUtility.GetRect`锛屾棤 `GUILayoutEntry` |
| IME/杈撳叆娉曠粍鍚堣緭鍏ラ獙璇?| `UnityEngine.Event` 涓嶈鐩?IME 缁勫悎鎬佽涔?|
| 绯荤粺鍓创鏉跨湡瀹炵矘璐撮獙璇?| 闇€瑕佸钩鍙扮骇鍓创鏉?API |
| 澶氱獥鍙?IMGUI 鍗忓悓鎿嶄綔 | 褰撳墠 `ImguiBridgeRegistry` 鎸夊崟瀹夸富绐楀彛缁勭粐 |
| 鎷栨斁鏂囦欢/瀵硅薄鍒?IMGUI 鎺т欢 | 渚濊禆 Editor `DragAndDrop` 鐢熷懡鍛ㄦ湡锛屼笌鏅€?IMGUI 鎸囬拡浜嬩欢涓嶇瓑浠?|
| 鍍忕礌绾ц瑙夊姣?| 闇€瑕佸浘鍍忓鐞嗗簱锛屾鏋跺綋鍓嶆棤鍐呭缓鍍忕礌 diff |
| 琚祴浠ｇ爜鏃?`GUI.SetNextControlName` 涓旂粨鏋勪笉绋冲畾 | 姝ゆ椂 `index` 閫夋嫨鍣ㄤ細婕傜Щ锛涢渶 C# 鐩存帴璋冪敤琚祴绐楀彛鏂规硶 |

### 2.3 YAML + C# 娣峰悎鍦烘櫙

- 鍦?C# Fixture 瀛愮被鐨?`SetUp` 涓垵濮嬪寲琚祴绐楀彛鍜屾祴璇曟暟鎹紝鐒跺悗閫氳繃 `ExecuteYamlStepsAsync` 椹卞姩 YAML 娴佺▼銆?- IMGUI `field_name` 鍙嶅皠鐩村啓锛坄imgui_select_option` 鐨?`field_name` 鍙傛暟锛夋槸鏈€绋冲畾鐨勪笅鎷夎祴鍊兼柟寮忥細鏃犻渶寮瑰嚭 OS 鍘熺敓鑿滃崟锛岀洿鎺ヤ慨鏀硅娴嬬獥鍙ｅ瓧娈靛€笺€?- 瀵归渶瑕佺簿纭€兼柇瑷€鐨?Toggle/Slider 鍦烘櫙锛岀敤 C# 鍦?`TearDown` 涓弽灏勮鍙栬娴嬬獥鍙ｅ瓧娈垫柇瑷€锛孻AML 鍙礋璐?UI 浜や簰娴佺▼銆?
---

## 3. V1 鍏ㄩ潰鏀寔鐨勬帶浠?
浠ヤ笅鎺т欢鍙€氳繃 YAML `imgui_*` 鍐呯疆鍔ㄤ綔瀹屾垚瀹屾暣鐨勫畾浣嶃€佷氦浜掑拰鏂█锛?
| 鎺т欢 | IMGUI API | 閫夋嫨鍣ㄧず渚?| 鍙敤鍔ㄤ綔 |
|------|-----------|-----------|---------|
| `Button` | `GUILayout.Button` | `gui(button)` / `gui(button, text="Save")` | `imgui_click`銆乣imgui_double_click`銆乣imgui_right_click`銆乣imgui_hover`銆乣imgui_assert_text`銆乣imgui_assert_visible`銆乣imgui_assert_value` |
| `TextField` | `GUILayout.TextField` / `EditorGUILayout.TextField` | `gui(textfield, index=0)` / `gui(textfield, control_name="username")` | `imgui_type`銆乣imgui_focus`銆乣imgui_click`銆乣imgui_assert_visible`銆乣imgui_assert_text`銆乣imgui_assert_value`銆乣imgui_read_value`銆乣imgui_press_key`銆乣imgui_press_key_combination` |
| `Label` | `GUILayout.Label` | `gui(label, index=2)` / `gui(label, text="Status")` | `imgui_assert_text`銆乣imgui_assert_visible`銆乣imgui_assert_value`銆乣imgui_hover` |
| `Toggle` | `GUILayout.Toggle` / `EditorGUILayout.Toggle` | `gui(toggle, text="Enabled")` / `gui(toggle, control_name="feature-toggle")` | `imgui_click`銆乣imgui_assert_visible`銆乣imgui_assert_value`锛堝敖鍔涙帹鏂紝瑙?搂6锛?|
| `Popup/Dropdown` | `EditorGUILayout.Popup` | `gui(dropdown, index=0)` / `gui(dropdown, control_name="quality-popup")` | `imgui_click`銆乣imgui_select_option`銆乣imgui_assert_visible`銆乣imgui_assert_value`銆乣imgui_read_value` |
| `Toolbar` | `GUILayout.Toolbar` | `gui(toolbar, index=0)` | `imgui_click`銆乣imgui_assert_visible` |
| `Slider` | `GUILayout.HorizontalSlider` / `EditorGUILayout.Slider` | `gui(slider, index=0)` / `gui(slider, control_name="scale-slider")` | `imgui_click`銆乣imgui_assert_visible`銆乣imgui_press_key`锛堟柟鍚戦敭锛?|
| `ScrollView` | `GUILayout.BeginScrollView` | `gui(scroller)` | `imgui_scroll`銆乣imgui_assert_visible` |
| `Group` | `GUILayout.BeginVertical/Horizontal` | `gui(group="Settings")` | 浣滀负璺緞瀹瑰櫒閰嶅悎瀛愭帶浠堕€夋嫨鍣ㄤ娇鐢?|

**璇存槑**锛?
- `imgui_assert_value` 瀵?IMGUI 鎺т欢涓?*灏藉姏鎺ㄦ柇**锛坆est-effort锛夈€侷MGUI 鐨?`GUILayoutEntry` 涓嶅瓨鍌ㄦ帶浠剁殑杩愯鏃跺€硷紙濡?Toggle 鐨?bool銆丼lider 鐨?float锛夛紝鏂█渚濊禆 `text` 鍐呭鎴?`style` 鍚嶇О鎺ㄦ柇锛岃 搂6 闄愬埗銆?- `imgui_select_option` 瀵?Dropdown 鎻愪緵涓ゆ潯璺緞锛氫紭鍏堥€氳繃 `field_name` 鍙嶅皠鐩村啓瀛楁鍊硷紙鏈€绋冲畾锛夛紱鑻ユ湭鎻愪緵 `field_name`锛屽垯鍥為€€鍒颁簨浠舵ā鎷燂紙鐐瑰嚮灞曞紑 鈫?鏂瑰悜閿鑸?鈫?鍥炶溅纭锛夛紝鍚庤€呭鍔ㄦ€佸脊鍑鸿彍鍗曚负灏藉姏鑰屼负銆傛敮鎸?`option`锛堟寜鏂囨湰鎴栫储寮曪級鍜?`index` 鍙傛暟銆?- `imgui_press_key` / `imgui_press_key_combination` 鍙戦€?`KeyDown`/`KeyUp` 浜嬩欢鍒板綋鍓嶈幏鐒︽帶浠讹紝閫傜敤浜庢枃鏈瓧娈电殑蹇嵎閿搷浣滐紙濡?`Ctrl+A`銆乣Delete`锛夈€?
---

## 4. V1 灞€閮ㄦ敮鎸佺殑鎺т欢

浠ヤ笅鎺т欢鍙畾浣嶅拰杩涜閮ㄥ垎浜や簰锛屼絾閮ㄥ垎鍔熻兘鍦?V1 涓彈闄愶細

| 鎺т欢 | 鍙敤 YAML 鍔ㄤ綔 | 涓嶆敮鎸佺殑浜や簰 | 鍘熷洜涓庢浛浠ｆ柟妗?|
|------|--------------|------------|--------------|
| `Foldout` | `imgui_click`銆乣imgui_assert_visible` | 鐘舵€佹柇瑷€锛堝睍寮€/鎶樺彔锛?| `GUILayoutEntry` 涓嶆毚闇?foldout 鐨勫睍寮€鐘舵€侊紱闇€閫氳繃琚祴浠ｇ爜鐨?`field_name` 鍙嶅皠璇诲彇 |
| `MinMaxSlider` | `imgui_click`銆乣imgui_assert_visible`銆乣imgui_press_key` | 绮剧‘鎷栨嫿鍒扮洰鏍囪寖鍥?| 褰撳墠浠呮敮鎸佺偣鍑诲拰鏂瑰悜閿井璋冿紱绮剧‘鑼冨洿璁剧疆闇€ C# 鍙嶅皠鐩村啓 |
| `TextArea` | `imgui_type`銆乣imgui_focus`銆乣imgui_click`銆乣imgui_assert_text` | 澶氳鏂囨湰鐨勯€愯鏂█ | 涓?`TextField` 鍏辩敤鍚屼竴浜嬩欢璺緞锛屼絾琛屾暟璁＄畻鍜屾粴鍔ㄥ畾浣嶆棤鍐呭缓鏀寔 |
| `IntField/FloatField`锛圗ditorGUILayout锛?| `imgui_type`銆乣imgui_focus`銆乣imgui_assert_text` | 鏁板€艰秺鐣屾牎楠屾柇瑷€ | 鍊间互瀛楃涓插舰寮忓瓨鍦ㄤ簬 `GUILayoutEntry` 涓紝绫诲瀷绾ф柇瑷€闇€棰濆瑙ｆ瀽 |

---

## 5. V1 涓嶅彲鑷姩鍖栫殑鎺т欢涓庡姛鑳?
浠ヤ笅鎺т欢鎴栧姛鑳界敱浜?IMGUI 鏋舵瀯鐗规€ф垨 Unity API 闄愬埗锛屽湪 V1 涓畬鍏ㄤ笉鍙嚜鍔ㄥ寲锛?
| 鎺т欢/鍔熻兘 | 鍘熷洜 | 寤鸿 |
|---------|------|------|
| `GenericMenu` / `EditorUtility.DisplayDialog` 娴獥 | 寮瑰嚭鑿滃崟/瀵硅瘽妗嗙敱 Editor 鍏ㄥ眬绠＄悊锛屼笉鍦ㄧ洰鏍囩獥鍙ｇ殑 `GUILayoutUtility` 甯冨眬鎵规鍐咃紝蹇収鏃犳硶鎹曡幏 | 浣跨敤 `imgui_select_option` 鐨?`field_name` 鍙嶅皠璺緞缁曡繃寮瑰嚭鑿滃崟锛涙垨灏嗚娴嬩唬鐮佹敼涓?UIToolkit 瀹炵幇 |
| 鐙珛 `EditorWindow` 寮圭獥锛圕olor Picker銆丆urve Editor锛?| 寮圭獥鎷ユ湁鐙珛鐨?`OnGUI` 涓婁笅鏂囧拰甯冨眬鐘舵€侊紝褰撳墠 IMGUI 妗ユ帴鎸夊崟绐楀彛缁勭粐 | C# 缂栫▼鐩存帴鎿嶄綔寮圭獥鏁版嵁锛涙垨灏嗚娴嬮€昏緫鏀逛负 UIToolkit 娴獥 |
| 绾粯鍒舵帶浠讹紙`GUI.DrawTexture`銆乣Handles` 绛夛級 | 涓嶇粡杩?`GUILayoutUtility.GetRect`锛屼笉浜х敓 `GUILayoutEntry`锛屾棤娉曡繘鍏ュ揩鐓?| 鏃犲彲琛屾浛浠ｆ柟妗堬紱闇€灏嗚娴?UI 杩佺Щ鍒?GUILayout 鎴?UIToolkit |
| 杩愯鏃跺€肩簿纭柇瑷€锛圱oggle bool銆丼lider float 绛夛級 | `GUILayoutEntry` 浠呭瓨鍌ㄥ竷灞€淇℃伅锛坮ect銆乻tyle銆乼ext锛夛紝涓嶅瓨鍌ㄦ帶浠剁殑涓氬姟鐘舵€佸€?| 浣跨敤 `field_name` 鍙傛暟閫氳繃鍙嶅皠璇诲彇琚祴绐楀彛鐨勫瓧娈?灞炴€у€艰繘琛屾柇瑷€ |
| Tooltip 鍙娓叉煋 | Tooltip 鐢?Editor 鍏ㄥ眬绠＄悊锛屼笉鍦ㄧ洰鏍囩獥鍙ｅ竷灞€鏍戝唴 | 鍙€氳繃琚祴浠ｇ爜鐨勫瓧娈垫柇瑷€ Tooltip 鏂囨湰锛屼絾鏃犳硶楠岃瘉娓叉煋 |
| 鎷栨斁鏂囦欢/瀵硅薄鍒?IMGUI 鎺т欢 | 渚濊禆 Editor `DragAndDrop` 鐢熷懡鍛ㄦ湡锛屼笉绛変环浜庢櫘閫?IMGUI 鎸囬拡浜嬩欢 | C# 缂栫▼妯℃嫙 `DragAndDrop` |
| 绯荤粺鍓创鏉挎搷浣滐紙Copy/Paste锛?| 闇€瑕佺郴缁熺骇鍓创鏉?API + 閿洏蹇嵎閿粍鍚?| 浣跨敤 `imgui_type` 蹇€熻矾寰勬浛浠ｇ矘璐达紱鎴栦娇鐢?`imgui_press_key_combination` 鍙戦€?`Ctrl+C/V`锛堥儴鍒嗗満鏅湁鏁堬級 |
| IME / 杈撳叆娉曠粍鍚堣緭鍏?| `UnityEngine.Event` 涓嶈鐩?IME 缁勫悎鎬佽涔?| 浣跨敤 `imgui_type` 鐩存帴鍐欏叆鏈€缁堟枃鏈?|

---

## 6. 鍏ㄩ儴 IMGUI 鍐呯疆鍔ㄤ綔璇︾粏璇存槑

### 6.1 榧犳爣/鎸囬拡鍔ㄤ綔

| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 搴曞眰鏈哄埗 | 浜嬩欢搴忓垪 | YAML 绀轰緥 |
|--------|---------|---------|---------|-----------|
| `imgui_click` | `selector` | `EditorWindow.SendEvent` | `Layout` 鈫?`MouseDown` 鈫?`MouseUp` | `- imgui_click: { selector: "gui(button, text=\"Save\")" }` |
| `imgui_double_click` | `selector` | `EditorWindow.SendEvent` | `Layout` 鈫?`MouseDown` 鈫?`MouseUp` 鈫?`MouseDown` 鈫?`MouseUp` | `- imgui_double_click: { selector: "gui(button)" }` |
| `imgui_right_click` | `selector` | `EditorWindow.SendEvent`锛宍button=1` | `Layout` 鈫?`MouseDown(button=1)` 鈫?`MouseUp(button=1)` | `- imgui_right_click: { selector: "gui(button, text=\"Options\")" }` |
| `imgui_hover` | `selector` | `EditorWindow.SendEvent` | `MouseMove` 鍒板厓绱犱腑蹇?| `- imgui_hover: { selector: "gui(label)" }` |
| `imgui_scroll` | `selector`銆乣delta`锛堟鏁?鍚戜笂锛?| `EditorWindow.SendEvent` | `ScrollWheel` 鍒板厓绱犱腑蹇?| `- imgui_scroll: { selector: "gui(scroller)", delta: -3 }` |

**鍧愭爣杞崲璇存槑**锛氭墍鏈夐紶鏍囦簨浠跺潗鏍囧潎涓虹獥鍙ｅ眬閮ㄥ潗鏍囥€傛鏋堕€氳繃 `MonoHook` 鐨?`OnGUIReplacement` 鑷姩璁＄畻 `WindowToContentOffset`锛岀‘淇濇敞鍏ュ潗鏍囦笌 IMGUI 娓叉煋鍧愭爣绯讳竴鑷淬€?
### 6.2 閿洏/杈撳叆鍔ㄤ綔

| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 搴曞眰鏈哄埗 | 浜嬩欢搴忓垪 | YAML 绀轰緥 |
|--------|---------|---------|---------|-----------|
| `imgui_type` | `selector`銆乣text` | `EditorWindow.SendEvent`锛岄€愬瓧绗?| `MouseDown` 鈫?`MouseUp`锛堣仛鐒︼級鈫?姣忓瓧绗︿竴娆?`KeyDown`锛坄character` 瀛楁锛宍keyCode=None`锛?| `- imgui_type: { selector: "gui(textfield, index=0)", text: "admin" }` |
| `imgui_focus` | `selector` | `EditorWindow.SendEvent` | `Layout` 鈫?`MouseDown` 鈫?`MouseUp`锛堜互鑱氱劍涓虹洰鐨勶級 | `- imgui_focus: { selector: "gui(textfield, control_name=\"username\")" }` |
| `imgui_press_key` | `selector`锛堝彲閫夛級銆乣key`锛圞eyCode 鏋氫妇鍚嶏級 | `EditorWindow.SendEvent` | `KeyDown`锛坄keyCode` 鐢?`key` 鍙傛暟鏄犲皠锛夆啋 `KeyUp` | `- imgui_press_key: { key: "Return" }` |
| `imgui_press_key_combination` | `selector`锛堝彲閫夛級銆乣keys`锛堝 `"Ctrl+A"`锛?| `EditorWindow.SendEvent`锛岃В鏋愪慨楗伴敭 | 淇グ閿?`KeyDown` 鈫?涓婚敭 `KeyDown`锛堝甫 `EventModifiers`锛夆啋 涓婚敭 `KeyUp` 鈫?淇グ閿?`KeyUp` | `- imgui_press_key_combination: { keys: "Ctrl+A" }` |

**娉ㄦ剰**锛歚imgui_press_key` 鍜?`imgui_press_key_combination` 灏嗕簨浠跺彂閫佸埌**褰撳墠鑾风劍鎺т欢**銆傝嫢鐪佺暐 `selector`锛岄粯璁や綔鐢ㄤ簬 `GUIUtility.keyboardControl` 瀵瑰簲鐨勬帶浠躲€?
### 6.3 鍊艰祴鍊煎姩浣?
| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 鏀寔鐨勬帶浠?| 璇存槑 | YAML 绀轰緥 |
|--------|---------|-----------|------|-----------|
| `imgui_select_option` | `selector`銆乣field_name`锛堝彲閫夛紝鍙嶅皠瀛楁鍚嶏級銆乣option`锛堟枃鏈?绱㈠紩锛屽彲鐢?`index` 鍒悕锛?| `Popup`/`Dropdown` | 涓昏矾寰勶細`field_name` 鍙嶅皠鐩村啓瀛楁鍊硷紙璺宠繃 OS 寮瑰嚭鑿滃崟锛夛紱鍥為€€璺緞锛氱偣鍑诲睍寮€ 鈫?DownArrow 瀵艰埅 鈫?Return 纭 | `- imgui_select_option: { selector: "gui(dropdown, index=0)", field_name: "_qualityLevel", option: "High" }` |

### 6.4 鏂█鍔ㄤ綔

| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 璇存槑 | YAML 绀轰緥 |
|--------|---------|------|-----------|
| `imgui_assert_visible` | `selector` | 鏂█鍏冪礌蹇収 Rect 鐨勫楂樺潎闈為浂 | `- imgui_assert_visible: { selector: "gui(button, text=\"OK\")" }` |
| `imgui_assert_text` | `selector`銆乣text` | 鏂█蹇収鏉＄洰鐨?`.Text` 瀛楁涓?`text` 鍙傛暟鍖归厤锛堢簿纭垨鍖呭惈锛孫rdinalIgnoreCase锛?| `- imgui_assert_text: { selector: "gui(label, index=0)", text: "Status: Ready" }` |
| `imgui_assert_value` | `selector`銆乣value` | 灏藉姏鎺ㄦ柇锛歀abel/Button 杩斿洖 `text`锛汿oggle/Slider 鏃犺繍琛屾椂鍊硷紝杩斿洖 "unknown" 鎴?style 鍚嶇О锛涘缓璁敼鐢?`field_name` 鍙嶅皠鏂█ | `- imgui_assert_value: { selector: "gui(label, text=\"Score\")", value: "100" }` |

### 6.5 璇诲彇/绛夊緟鍔ㄤ綔

| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 璇存槑 | YAML 绀轰緥 |
|--------|---------|------|-----------|
| `imgui_read_value` | `selector`銆乣bag_key` | 璇诲彇蹇収鏉＄洰鐨?`text` 鍊煎苟瀛樺叆 `SharedBag[bag_key]`锛屼緵鍚庣画姝ラ閫氳繃 `{{ bag_key }}` 妯℃澘寮曠敤 | `- imgui_read_value: { selector: "gui(label, index=1)", bag_key: "status_text" }` |
| `imgui_wait` | `selector`銆乣timeout`锛堝 `"3s"`锛屼笂闄?600s锛?| 杞鐩村埌鎺т欢鍑虹幇鍦ㄥ揩鐓т腑锛涜秴杩?`timeout` 鍒欏け璐?| `- imgui_wait: { selector: "gui(button, text=\"Done\")", timeout: "5s" }` |

---

## 7. 杈撳叆妯℃嫙鏀寔璇︽儏

### 7.1 榧犳爣/鎸囬拡杈撳叆

| 鎿嶄綔 | 鏀寔鎯呭喌 | 鎶€鏈満鍒?|
|------|---------|---------|
| 宸﹂敭鍗曞嚮 | 鉁?瀹屾暣鏀寔 | `SendEvent(MouseDown)` + `SendEvent(MouseUp)`锛屽潗鏍囦负鎺т欢 Rect 涓績 |
| 宸﹂敭鍙屽嚮 | 鉁?瀹屾暣鏀寔 | 涓ゆ杩炵画 `MouseDown`+`MouseUp` |
| 鍙抽敭鍗曞嚮 | 鉁?瀹屾暣鏀寔 | `MouseDown(button=1)` + `MouseUp(button=1)` |
| 鎮仠锛圡ouseMove锛?| 鉁?瀹屾暣鏀寔 | `SendEvent(MouseMove)` 鍒版帶浠朵腑蹇?|
| 婊氬姩 | 鉁?瀹屾暣鏀寔 | `SendEvent(ScrollWheel)`锛宍delta` 鍙傛暟姝ｄ负鍚戜笂銆佽礋涓哄悜涓?|
| 淇グ閿?鐐瑰嚮 | 鉂?涓嶆敮鎸?| IMGUI 榧犳爣浜嬩欢鏈皝瑁?`EventModifiers` 鍙傛暟锛涢渶 C# 鐩存帴娉ㄥ叆 |
| 鎷栨嫿 | 鉂?涓嶆敮鎸?| V1 鏃?`imgui_drag` 鍔ㄤ綔锛涚簿纭嫋鎷介渶 C# 鏋勯€?`MouseDrag` 浜嬩欢搴忓垪 |
| 璺ㄧ獥鍙ｆ搷浣?| 鉂?涓嶆敮鎸?| 褰撳墠姣忎釜娴嬭瘯浠呮敮鎸佷竴涓涓荤獥鍙ｇ殑 IMGUI 妗ユ帴 |
| 鏂囦欢/瀵硅薄鎷栨斁 | 鉂?涓嶆敮鎸?| 渚濊禆 Editor `DragAndDrop` 鐢熷懡鍛ㄦ湡 |

### 7.2 閿洏杈撳叆

| 鎿嶄綔 | 鏀寔鎯呭喌 | 鎶€鏈満鍒?|
|------|---------|---------|
| 鍗曞瓧绗︽枃鏈緭鍏?| 鉁?瀹屾暣鏀寔 | `imgui_type`锛氭瘡瀛楃涓€娆?`KeyDown`锛岃缃?`Event.character` 瀛楁锛堜笉璁?keyCode锛?|
| 鍔熻兘閿紙Enter/Escape/Delete/Tab/鏂瑰悜閿瓑锛?| 鉁?瀹屾暣鏀寔 | `imgui_press_key`锛歚Event.keyCode` 鏄犲皠 KeyCode 鏋氫妇鍚?|
| 缁勫悎閿紙Ctrl+A/Ctrl+C 绛夛級 | 鉁?瀹屾暣鏀寔 | `imgui_press_key_combination`锛氫慨楗伴敭 `KeyDown`鈫掍富閿紙甯?`EventModifiers`锛夆啋涓婚敭 `KeyUp`鈫掍慨楗伴敭 `KeyUp` |
| 鎵归噺鏂囨湰蹇€熷啓鍏?| 鉂?涓嶆敮鎸?| 鏃犵瓑浠?`imgui_type_fast`锛圥2 鎵╁睍椤癸級锛涘綋鍓嶅彧鑳介€愬瓧绗?`imgui_type` |
| 鐒︾偣閾?Tab 鍒囨崲 | 鉂?涓嶆敮鎸?| 鍙敤 `imgui_press_key: { key: "Tab" }` 妯℃嫙锛屼絾鏃犱笓鐢ㄧ劍鐐归摼瀵艰埅鍔ㄤ綔 |
| IME/杈撳叆娉曠粍鍚堣緭鍏?| 鉂?涓嶆敮鎸?| `UnityEngine.Event` 涓嶈鐩?IME 缁勫悎鎬佽涔?|
| 绯荤粺鍓创鏉跨湡瀹炵矘璐?| 鉂?涓嶆敮鎸?| 闇€瑕佸钩鍙扮骇鍓创鏉?API锛沗imgui_type` 鍙浛浠?|

---

## 8. 璁捐鍘熺悊涓庢灦鏋?
### 8.1 鏍稿績鏋舵瀯

```
YAML imgui_* 鍔ㄤ綔
    鈫?ImguiSelectorCompiler锛堢紪璇?gui(button, text="OK") 璇硶锛?    鈫?ImguiExecutionBridge锛堥€氳繃 MonoHook 娉ㄥ叆 OnGUI 閽╁瓙锛屾垨鍙嶅皠 GUILayoutUtility.current锛?    鈫?OnGUI 鎵ц 鈫?ImguiSnapshotCapture锛堝弽灏?GUILayoutUtility.current.topLevel.entries锛?    鈫?ImguiElementLocator锛堝熀浜庡揩鐓у尮閰嶉€夋嫨鍣細type / text / index / group / control_name / focused锛?    鈫?Event 娉ㄥ叆锛圡ouseDown/Up/Move/ScrollWheel/KeyDown/Up锛夋垨鏂█
```

### 8.2 蹇収鎹曡幏鍙岄摼璺?
Unity 6000.6.0a2 涓?`GUILayoutUtility.current` 鍦?`OnGUI` 杩斿洖鍚庝細琚竻绌猴紝鍥犳妗嗘灦瀹炵幇浜嗕袱鏉℃崟鑾烽摼璺細

| 閾捐矾 | 鏈哄埗 | 閫傜敤鍦烘櫙 |
|------|------|---------|
| **鍙嶅皠涓婚摼璺?* | 鍦?`OnGUI` 鎵ц鏈熼棿鍙嶅皠璇诲彇 `GUILayoutUtility.current.topLevel.entries`锛岄€掑綊閬嶅巻 `GUILayoutGroup`/`GUILayoutEntry` | 鏍囧噯 GUILayout 鎺т欢锛圔utton銆丩abel銆乀oggle 绛夛級 |
| **MonoHook Fallback** | 閫氳繃 `MonoHook` 搴?hook `GUILayoutUtility.DoGetRect`銆乣EditorGUILayout.Popup`銆乣GUIStyle.Draw`锛屽湪璋冪敤鏃跺疄鏃惰褰曟帶浠?rect/style/text | Unity 6000+ 鍙嶅皠澶辨晥鏃讹紱缁曡繃 GUILayout 鐨勮嚜瀹氫箟缁樺埗鎺т欢 |

涓ゆ潯閾捐矾鐨勬暟鎹細鍦?`OnGUI` 缁撴潫鍚庡悎骞讹細鑻ュ弽灏勯摼璺棤鏁版嵁涓?hook 閾捐矾鏈夎褰曪紝鍒欒嚜鍔ㄤ娇鐢?hook 鏁版嵁浣滀负蹇収銆?
### 8.3 浜嬩欢娉ㄥ叆鏈哄埗

鎵€鏈変氦浜掗€氳繃鏋勯€?`UnityEngine.Event` 骞惰皟鐢?`EditorWindow.SendEvent()` 瀹炵幇锛?
| 鍔ㄤ綔 | 浜嬩欢搴忓垪 | 寤惰繜 |
|------|---------|------|
| `imgui_click` | `Layout` 鈫?`MouseDown` 鈫?`MouseUp` | 50ms post-delay |
| `imgui_double_click` | `Layout` 鈫?`MouseDown` 鈫?`MouseUp` 鈫?`MouseDown` 鈫?`MouseUp` | 80ms post-delay |
| `imgui_right_click` | `Layout` 鈫?`MouseDown(button=1)` 鈫?`MouseUp(button=1)` | 50ms post-delay |
| `imgui_hover` | `MouseMove` | 鏃?|
| `imgui_type` | `MouseDown` 鈫?`MouseUp`锛堣仛鐒︼級鈫?閫愬瓧绗?`KeyDown` | 姣忓瓧绗﹂棿闅?|
| `imgui_scroll` | `ScrollWheel` | 鏃?|
| `imgui_press_key` | `KeyDown` 鈫?`KeyUp` | 鏃?|
| `imgui_press_key_combination` | 淇グ閿?`KeyDown` 鈫?涓婚敭 `KeyDown` 鈫?涓婚敭 `KeyUp` 鈫?淇グ閿?`KeyUp` | 鏃?|

**鍧愭爣杞崲**锛欼MGUI 鎺т欢鐨?`Rect` 涓虹獥鍙ｅ眬閮ㄥ潗鏍囥€俙SendEvent` 闇€瑕佺獥鍙ｇ┖闂村潗鏍囷紙鍚爣棰樻爮鍋忕Щ锛夈€傛鏋堕€氳繃 `MonoHook` 鐨?`OnGUIReplacement` 鑷姩璁＄畻 `WindowToContentOffset`锛岀‘淇濅簨浠舵敞鍏ュ潗鏍囧噯纭€?
### 8.4 ImguiBridgeRegistry 涓庣獥鍙ｇ鐞?
- 姣忎釜 `EditorWindow` 瀹炰緥瀵瑰簲涓€涓?`ImguiBridgeRegistry` 鏉＄洰銆?- Registry 绠＄悊 MonoHook 鐨勫畨瑁呭拰鍗歌浇锛歚OnGUI` hook銆乣GUILayoutUtility.DoGetRect` hook銆乣EditorGUILayout.Popup` hook銆乣GUIStyle.Draw` hook銆?- 褰撶獥鍙ｅ叧闂椂鑷姩鍗歌浇鎵€鏈?hook锛岄槻姝㈠唴瀛樻硠婕忋€?
---

## 9. 閫夋嫨鍣ㄨ娉曞畬鏁村弬鑰?
```yaml
# 鍩烘湰绫诲瀷鍖归厤锛堟寜鎺ㄦ柇鐨勬帶浠剁被鍨嬶級
gui(button)
gui(textfield, index=2)
gui(toggle, text="Enabled")
gui(label)
gui(dropdown)
gui(slider)
gui(toolbar)
gui(scroller)

# ControlName 鍖归厤锛堥渶瑕佽娴嬩唬鐮侀厤鍚?GUI.SetNextControlName锛?gui(textfield, control_name="username-field")
gui(button, control_name="save-button")

# 鏂囨湰鍖归厤锛圤rdinalIgnoreCase 瀛愪覆鍖归厤锛?gui(button, text="Save")
gui(label, text="Status")

# 绱㈠紩鍖归厤锛堝湪鎸?type 杩囨护鍚庣殑鍊欓€夊垪琛ㄤ腑鎸夌储寮曢€夊彇锛?gui(textfield, index=0)
gui(button, index=2)

# 缁勮矾寰勯檺瀹氾紙缂╁皬鍖归厤鑼冨洿锛?gui(group="Settings" > button, text="Apply")
gui(group="Panel" > textfield, index=0)

# 鐒︾偣鍖归厤锛堣繑鍥炲綋鍓嶈幏鐒︽帶浠讹級
gui(focused)
```

**閫夋嫨鍣ㄤ紭鍏堢骇**锛?
1. `focused` 鈥?鏈€楂樹紭鍏堢骇鐗规畩鍖归厤锛岄€氳繃 `GUIUtility.keyboardControl` 鍒ゆ柇銆?2. `control_name` 鈥?鏈€绮剧‘锛屾帹鑽愬湪鍏抽敭鎺т欢涓婁娇鐢紙闇€ `GUI.SetNextControlName`锛夈€?3. `text` 鈥?OrdinalIgnoreCase 瀛愪覆鍖归厤銆?4. `group` 鈥?鍏堥檺瀹氱埗瀹瑰櫒鑼冨洿锛屽啀鍦ㄨ寖鍥村唴鍖归厤瀛愭帶浠躲€?5. `index` 鈥?鍦ㄦ寜 type 杩囨护鍚庣殑鍊欓€夊垪琛ㄤ腑鎸変綅缃储寮曢€夊彇銆?
---

## 10. 琚祴浠ｇ爜鐨勫彲娴嬭瘯鎬ф敼閫狅紙鎺ㄨ崘锛?
涓轰簡璁╅€夋嫨鍣ㄦ洿绋冲畾锛屽缓璁湪 IMGUI 浠ｇ爜涓负鍏抽敭鎺т欢璁剧疆 `ControlName`锛?
```csharp
GUI.SetNextControlName("save-button");
if (GUILayout.Button("Save")) { /* ... */ }

GUI.SetNextControlName("username-field");
_username = GUILayout.TextField(_username);

// 涓虹粍鍛藉悕锛屼究浜?group 璺緞闄愬畾
GUILayout.BeginVertical("box");
{
    GUI.SetNextControlName("quality-popup");
    _qualityLevel = EditorGUILayout.Popup(_qualityLevel, _options);
}
GUILayout.EndVertical();
```

YAML 涓嵆鍙簿纭尮閰嶏細

```yaml
- imgui_click: { selector: "gui(button, control_name=\"save-button\")" }
- imgui_type:
    selector: "gui(textfield, control_name=\"username-field\")"
    text: "admin"
- imgui_select_option:
    selector: "gui(dropdown, control_name=\"quality-popup\")"
    field_name: "_qualityLevel"
    option: "High"
```

**鏀归€犲缓璁?*锛?
- 涓烘墍鏈夐渶瑕佽嚜鍔ㄥ寲鐨勫叧閿氦浜掓帶浠惰缃?`ControlName`銆?- 閬垮厤鍦ㄥ悓涓€ `OnGUI` 甯у唴鍔ㄦ€佹敼鍙樻帶浠舵暟閲忔垨椤哄簭锛堜細瀵艰嚧 `index` 閫夋嫨鍣ㄦ紓绉伙級銆?- 浣跨敤 `GUILayout.BeginVertical("box")` 鎴?`GUILayout.BeginHorizontal("box")` 涓洪€昏緫鍒嗙粍鍛藉悕锛屼究浜?`group` 璺緞闄愬畾銆?
---

## 11. 鎸変氦浜掔被鍨嬬殑瑕嗙洊鎬荤粨

| 浜や簰绫诲瀷 | V1 瑕嗙洊鑼冨洿 | 鏈鐩栬寖鍥?|
|---------|-----------|-----------|
| 鐐瑰嚮 | 鎵€鏈夊彲浜х敓 `GUILayoutEntry` 鐨勬帶浠?| 涓嶈兘鐢ㄩ€夋嫨鍣ㄧ洿鎺ョ偣鍑?`GenericMenu` 娴獥涓殑鑿滃崟椤?|
| 鍙屽嚮 | 鎵€鏈夊彲鐐瑰嚮鎺т欢 | 鍙屽嚮妫€娴嬩緷璧栬娴嬩唬鐮佽嚜韬疄鐜版椂闂撮槇鍊硷紙濡?`ImguiExampleWindow` 鐨?`DoubleClickThreshold`锛?|
| 鍙抽敭鐐瑰嚮 | 鎵€鏈夊彲鐐瑰嚮鎺т欢 | `GenericMenu` 娴獥鍐呴」 |
| 鎮仠 | 鎵€鏈夊彲瑙佹帶浠?| Tooltip 鍙娓叉煋楠岃瘉 |
| 鏂囨湰杈撳叆 | `TextField`銆乣TextArea` | IME 缁勫悎杈撳叆 |
| 蹇€熷啓鍊?| `imgui_type` 閫愬瓧绗﹁緭鍏ワ紙鏃?`imgui_type_fast` 绛夋晥鍔ㄤ綔锛孭2 鎵╁睍椤癸級 | 鏃?|
| 閫夋嫨锛堜笅鎷夛級 | `Popup` / `Dropdown`锛坄imgui_select_option`锛宍field_name` 鍙嶅皠璺緞鏈€绋冲畾锛?| 寮瑰嚭鑿滃崟鍐呴儴閫愰」鐐瑰嚮 |
| 婊戝潡璋冩暣 | `Slider`锛堢偣鍑?+ 鏂瑰悜閿級 | 绮剧‘鎷栨嫿鍒扮洰鏍囧€?|
| 婊氬姩 | `ScrollView`锛坄imgui_scroll`锛?| 鑷畾涔夊惛闄勬粴鍔ㄨ涓?|
| 鎸夐敭 | 浠绘剰鑾风劍鍏冪礌锛坄imgui_press_key`銆乣imgui_press_key_combination`锛?| IME銆佺郴缁熺骇鍓创鏉?|
| 鐒︾偣 | `imgui_focus`锛堢偣鍑昏仛鐒︼級 | 鐒︾偣閾惧鑸紙Tab 鍒囨崲鐒︾偣锛?|
| 鏂█ | 鍙鎬с€佹枃鏈€佸敖鍔涘€兼柇瑷€ | 瑙嗚鍍忕礌瀵规瘮銆佽繍琛屾椂绫诲瀷鍖栧€肩簿纭姣?|
| 璇诲彇鍊?| `imgui_read_value`锛堣鍙?text 瀛樺叆 SharedBag锛?| 缁撴瀯鍖栧€艰鍙栵紙濡?Vector3銆丆olor锛?|
| 鎴浘 | 澶嶇敤 UIToolkit 鎴浘鍔ㄤ綔锛堝綋鍓嶇獥鍙ｏ級 | 澶氱獥鍙ｆ埅鍥?|
| 绛夊緟 | `imgui_wait`锛堣疆璇㈢瓑寰呮帶浠跺嚭鐜帮紝鏀寔 timeout锛?| 鏃?|

---

## 12. 涓嶈兘瀹炵幇鎴栧綋鍓嶅彈 Unity 鎺ュ彛杈圭晫闃绘柇

浠ヤ笅鑳藉姏褰撳墠涓嶆槸"椤圭洰杩樻病鍋?锛岃€屾槸鏄庢樉鍙?Unity / IMGUI 鏋舵瀯杈圭晫褰卞搷锛?
| 椤圭洰 | 褰撳墠闃绘柇鍘熷洜 | 缁撹 |
|------|-----------|------|
| `GenericMenu` / 鐙珛娴獥鍐呴儴鎺т欢绾ц嚜鍔ㄥ寲 | 寮瑰嚭鑿滃崟鐢?Editor 鍏ㄥ眬绠＄悊锛屼笉鍦ㄧ洰鏍囩獥鍙?`GUILayoutUtility` 甯冨眬鎵规鍐?| 灞炰簬 Unity IMGUI 鏋舵瀯杈圭晫 |
| IMGUI 鎺т欢杩愯鏃剁簿纭€兼柇瑷€ | `GUILayoutEntry` 涓嶅瓨鍌ㄤ笟鍔＄姸鎬佸€硷紙濡?bool銆乫loat銆乮nt锛?| 灞炰簬 IMGUI 鏋舵瀯璁捐锛涢渶閫氳繃 `field_name` 鍙嶅皠缁曡繃 |
| 绾粯鍒舵帶浠讹紙`GUI.DrawTexture`銆乣Handles`锛?| 涓嶇粡杩?`GUILayoutUtility.GetRect`锛屾棤 `GUILayoutEntry` | 灞炰簬 IMGUI 鏋舵瀯杈圭晫 |
| IME 缁勫悎杈撳叆 | `UnityEngine.Event` 涓嶈鐩?IME 缁勫悎鎬佽涔?| 灞炰簬 Unity 鑳藉姏杈圭晫 |
| 绯荤粺鍓创鏉跨骇鐪熷疄绮樿创 | 闇€瑕佸钩鍙扮骇鍓创鏉?API 鎴栫郴缁熷揩鎹烽敭閾捐矾 | 灞炰簬绯荤粺/骞冲彴杈圭晫 |
| 澶氱獥鍙ｅ崗鍚屾嫋鎷?鏂█ | 褰撳墠娴嬭瘯妯″瀷鎸夊崟瀹夸富绐楀彛缁勭粐 | 褰撳墠鍙楁鏋惰璁￠檺鍒?|
| 鍍忕礌绾ц瑙?diff | 褰撳墠椤圭洰鍙湁鐪熷疄鎴浘锛屾病鏈夊唴寤鸿瑙夊熀绾夸笌宸紓鍒嗘瀽閾捐矾 | 褰撳墠鏈帴鍏ワ紝涓嶅睘浜?IMGUI 鎺т欢鍔ㄤ綔灞傞棶棰?|

---

## 13. 宸ョ▼涓婂彲瀹炵幇浣嗗綋鍓嶅皻鏈惤鍦扮殑鍔熻兘

浠ヤ笅鍔熻兘鏄?Unity API 鎴栫郴缁熻兘鍔涘凡缁忓紑鏀俱€佷唬鐮佹鏋朵篃鑳芥壙杞斤紝浣嗗綋鍓嶉」鐩噷**杩樻病鏈夊啓瀵瑰簲瀹炵幇**鐨勮兘鍔涖€?
### 13.1 鎸夊紑鍙戦毦搴︽帓搴忥紙浠庢槗鍒伴毦锛?
| 浼樺厛绾?| 鍔熻兘 | 闅惧害 | 璇存槑 |
|--------|------|------|------|
| P2 | `imgui_assert_property`锛坰tyle/rect 鏂█锛?| 浣?| `ImguiSnapshotEntry` 宸插寘鍚?`StyleName` 鍜?`Rect`锛屽彧闇€灏佽鏂█鍔ㄤ綔銆?|
| P2 | `imgui_type_text_fast`锛堝弽灏勫瓧娈电洿鍐欙級 | 浣?| `ImguiActionHelper.TrySetFieldValue` 宸插疄鐜板弽灏勫瓧娈佃祴鍊硷紝鍙渶灏佽涓虹嫭绔嬪姩浣溿€?|
| P2 | `imgui_drag`锛堟粦鍧?婊氬姩鏉＄簿纭嫋鎷斤級 | 涓?| 闇€瑕佹瀯閫?MouseDrag 浜嬩欢搴忓垪骞惰绠楃洰鏍囧潗鏍囷紝涓庣幇鏈?`SendMouseEvent` 缁撳悎鍗冲彲銆?|
| P3 | `imgui_wait_for_element` 璇箟涓?UIToolkit 瀵归綈 | 浣?| 褰撳墠 `imgui_wait` 鍔熻兘宸插畬鏁达紝涓昏鏄姩浣滃懡鍚嶅拰鍙傛暟瀵归綈銆?|
| P4 | 澶嶆潅鑷畾涔?IMGUI 鎺т欢鐨?`GUILayoutEntry` 鎹曡幏鎵╁睍 | 涓瓇楂?| 闇€瑕佷负鏇村 EditorGUILayout 鏂规硶瀹夎 `MonoHook`锛屾垨鎵╁睍 `InferControlType` 鐨?style 鏄犲皠琛ㄣ€?|
| P5 | 澶氱獥鍙?IMGUI 鍗忓悓娴嬭瘯 | 楂?| 闇€瑕侀噸鏋?`ImguiBridgeRegistry` 鍜岀獥鍙ｇ鐞嗛€昏緫锛屼负姣忎釜绐楀彛缁存姢鐙珛鐨勬ˉ鎺ュ拰蹇収銆?|

### 13.2 鎸変娇鐢ㄩ绻佺▼搴︽帓搴忥紙浠庨珮鍒颁綆锛?
| 浼樺厛绾?| 鍔熻兘 | 棰戠巼 | 璇存槑 |
|--------|------|------|------|
| P2 | `imgui_drag`锛堟粦鍧楃簿纭嫋鎷斤級 | 涓?| 鏉愯川銆佸悗澶勭悊鍙傛暟娴嬭瘯涓緝甯歌锛屼絾 `field_name` 鍙嶅皠鐩村啓宸茶兘婊¤冻澶氭暟鍊艰瀹氶渶姹傘€?|
| P2 | `imgui_type_text_fast` | 涓?| 闀挎枃鏈緭鍏ュ満鏅笅姣旈€愬瓧绗?`imgui_type` 蹇緱澶氥€?|
| P3 | `imgui_assert_property` | 浣?| 涓昏鐢ㄤ簬楠岃瘉鎺т欢瀛樺湪鎬у拰鍩虹甯冨眬灞炴€э紝`imgui_assert_visible` 宸茶鐩栧ぇ閮ㄥ垎鍦烘櫙銆?|
| P4 | 澶嶆潅鑷畾涔夋帶浠舵崟鑾锋墿灞?| 浣?| 绗笁鏂?IMGUI 鎻掍欢澶氭牱鍖栵紝浼樺厛绾у彇鍐充簬鍏蜂綋椤圭洰闇€姹傘€?|
| P5 | 澶氱獥鍙ｅ崗鍚?| 浣?| 鍗曠獥鍙?IMGUI 娴嬭瘯宸茶鐩?95% 浠ヤ笂鐨?Editor IMGUI 鍦烘櫙銆?|

---

## 14. 宸茬煡闂涓庣ǔ瀹氭€у娉?
### 14.1 鍙嶅皠涓庣増鏈吋瀹规€?
- `ImguiSnapshotCapture` 鍙嶅皠 `GUILayoutUtility.current`銆乣GUILayoutGroup`銆乣GUILayoutEntry` 绛夊唴閮ㄥ瓧娈点€俇nity 鐗堟湰鍗囩骇锛堝挨鍏舵槸 6000 绯诲垪锛夊凡瀵艰嚧 `GUILayoutUtility.current` 鍦?`OnGUI` 杩斿洖鍚庤娓呯┖銆?- **缂撹В鎺柦**锛氭鏋跺凡寮曞叆 `MonoHook` 鍥涢噸 fallback锛坄OnGUI` hook + `DoGetRect` hook + `Popup` hook + `GUIStyle.Draw` hook锛夛紝鍦ㄥ弽灏勫け鏁堟椂鑷姩鍒囨崲鍒?hook 閾捐矾銆?- **椋庨櫓**锛氳嫢 Unity 鏈潵鏇存敼 `GUILayoutUtility.DoGetRect` 绛惧悕鎴?`GUIStyle.Draw` 鍙傛暟锛宧ook 閾捐矾浠嶉渶鏇存柊銆?
### 14.2 甯冨眬绋冲畾鎬?
- 绐楀彛 resize 鍚?`index` 閫夋嫨鍣ㄥ彲鑳藉け鏁堬紝鍥犱负 `GUILayoutEntry` 鐨勫垪琛ㄩ『搴忓彲鑳介殢鍙敤绌洪棿閲嶆帓銆?- **寤鸿**锛氬缁堜紭鍏堜娇鐢?`control_name` 鎴?`text` 閫夋嫨鍣紱`index` 浠呬綔涓烘渶鍚庢墜娈点€?
### 14.3 鍊兼柇瑷€鐨勫眬闄愭€?
- `imgui_assert_value` 瀵?IMGUI 鎺т欢鏄敖鍔涙帹鏂€備緥濡傦細
  - `Toggle` 鐨?`GUILayoutEntry` 鍙寘鍚?`text`锛堟爣绛炬枃鏈級锛屼笉鍖呭惈鍕鹃€夌姸鎬併€?  - `Slider` 鐨?`GUILayoutEntry` 鍙寘鍚?`rect` 鍜?`style`锛屼笉鍖呭惈褰撳墠鏁板€笺€?- **寤鸿**锛氬闇€瑕佺簿纭€兼柇瑷€鐨勫満鏅紝浣跨敤 `field_name` 鍙傛暟閫氳繃鍙嶅皠璇诲彇琚祴绐楀彛瀛楁銆?
### 14.4 璐熷悜娴嬭瘯鐨勭ǔ瀹氭€?
- `IMGUI Negative - Wait Timeout`锛坄_96-imgui-negative-wait.yaml`锛夊伓灏斾細鍥?IMGUI 閲嶇粯鏃舵満鎻愬墠閫氳繃锛堟帶浠跺湪瓒呮椂鍓嶆剰澶栧嚭鐜帮級锛屽睘浜庨潪鍏抽敭 flaky 娴嬭瘯銆?- **寤鸿**锛氳礋鍚戞祴璇曠殑 timeout 涓嶅簲璁剧疆杩囩煭锛堝缓璁?鈮?s锛夛紝閬垮厤涓庢甯搁噸鎺?閲嶇粯绔炰簤銆?
### 14.5 涓?UIToolkit 娣风敤

IMGUI 鍔ㄤ綔涓?UIToolkit 鍔ㄤ綔鍙湪鍚屼竴 YAML 涓贩鐢細

```yaml
steps:
  # UIToolkit 閮ㄥ垎
  - click: { selector: "#open-settings" }

  # IMGUI 閮ㄥ垎锛堣缃潰鏉挎槸 IMGUI 鐨勶級
  - imgui_type:
      selector: "gui(textfield, control_name=\"project-name\")"
      text: "TestProject"
  - imgui_click:
      selector: "gui(button, text=\"Save\")"

  # 鍥炲埌 UIToolkit
  - assert_text:
      selector: "#status-label"
      expected: "Saved"
```

娣风敤鏃堕渶娉ㄦ剰锛?
- `fixture.host_window` 蹇呴』鎸囧悜鍖呭惈 IMGUI 鍐呭鐨?`EditorWindow`銆?- IMGUI 鍔ㄤ綔涓嶄緷璧?`rootVisualElement`锛屽洜姝ゅ彲鍦?UIToolkit 鍔ㄤ綔澶辫触鏃朵綔涓洪檷绾ц矾寰勪娇鐢紙濡?`IMGUIContainer` 鍐呴儴鍐呭锛夈€?
---

## 15. 淇鍘嗗彶

### 2026-04-23 2.0.0

- 鍩轰簬浠ｇ爜搴撳畬鏁村鏍革紙`UnityUIFlow.ImguiActions.cs`銆乣ImguiBridgeRegistry.cs`銆乣UnityUIFlow.ImguiLocators.cs`銆乣UnityUIFlow.ImguiParsing.cs`锛夐噸鍐欐枃妗ｃ€?- 鏂板 搂2銆屼綍鏃剁敤 YAML 娴嬭瘯鐢ㄤ緥锛屼綍鏃剁敤 C# 浠ｇ爜娴嬭瘯鐢ㄤ緥銆嶁€斺€斿寘鍚紭鍏堥€夋嫨 YAML 鐨勫満鏅竻鍗曘€佸繀椤荤敤 C# 鐨勫満鏅竻鍗曘€佷互鍙?YAML+C# 娣峰悎妯″紡璇存槑銆?- 鏂板 搂6銆屽叏閮?IMGUI 鍐呯疆鍔ㄤ綔璇︾粏璇存槑銆嶁€斺€旀寜榧犳爣銆侀敭鐩樸€佸€艰祴鍊笺€佹柇瑷€銆佽鍙?绛夊緟浜旂被锛岄€愬姩浣滃垪鍑哄叧閿弬鏁般€佸簳灞傛満鍒躲€佷簨浠跺簭鍒椼€乊AML 绀轰緥銆?- 鏂板 搂7銆岃緭鍏ユā鎷熸敮鎸佽鎯呫€嶁€斺€旈€愭潯鍒楀嚭榧犳爣/鎸囬拡鍜岄敭鐩樿緭鍏ョ殑鏀寔鎯呭喌鍙婂簳灞傛妧鏈満鍒讹紙鍚?鉁?鉂?鏍囪锛夈€?- 閲嶅啓 搂8銆岃璁″師鐞嗕笌鏋舵瀯銆嶏細琛ュ厖 `ImguiBridgeRegistry` 绐楀彛绠＄悊缁嗚妭锛涙槑纭洓閲?MonoHook fallback锛堝師鏂囦负涓夐噸锛夈€?- 鏇存柊 搂3锛堝叏闈㈡敮鎸佹帶浠讹級锛氭槑纭?`imgui_select_option` 鏀寔 `option` 鍜?`index` 鍙傛暟鍒悕銆?- 鏇存柊 搂11锛堣鐩栨€荤粨锛夛細鏄庣‘ `imgui_type_fast` 涓?P2 鏈惤鍦版墿灞曢」銆?
### 2026-04-21 1.2.0 绗笁杞墿灞?
- 鏂板 `87-type-text-vs-fast.yaml`锛氬寘鍚?`type_text_fast` 瀵圭┖瀛楃涓?`""` 鐨勮竟鐣岄獙璇併€?- 鏂板 IMGUI 璐熷悜娴嬭瘯锛歚_91-negative-set-value-invalid.yaml`銆?- YAML 鍩虹嚎浠?102 浠芥墿灞曞埌 115 浠斤紝IMGUI 鐩稿叧鐢ㄤ緥浠?8 浠芥墿灞曞埌 10 浠姐€?
### 2026-04-21 1.1.0 鎵╁睍楠岃瘉

- 鏂板 `69-imgui-alternative-params.yaml`锛氶獙璇?`imgui_select_option` 鐨?`option` 鍙傛暟銆乣imgui_press_key` 鍜?`imgui_press_key_combination` 鐨?`selector` 鍙€夊弬鏁般€?- 鏂板 IMGUI 璐熷悜娴嬭瘯锛歚_78-negative-type-text-non-input.yaml`銆乣_79-negative-press-key-combination.yaml`銆?- YAML 鍩虹嚎浠?71 浠芥墿灞曞埌 102 浠斤紝IMGUI 鐩稿叧鐢ㄤ緥浠?5 浠芥墿灞曞埌 8 浠姐€?
### 2026-04-21 1.0.0 鍒濆鐗堟湰

- 鍩轰簬鍏ㄩ噺浠ｇ爜澶嶆牳鍜?5 浠?YAML 鍥炲綊鐢ㄤ緥楠岃瘉缁撴灉鍒涘缓鏈枃妗ｃ€?- 纭 15 涓?`imgui_*` 鍔ㄤ綔鍏ㄩ儴瀹炵幇骞堕€氳繃鍥炲綊楠岃瘉銆?- 纭蹇収鎹曡幏鍙岄摼璺紙鍙嶅皠 + MonoHook锛夊湪 Unity 6000.6.0a2 涓嬪伐浣滄甯搞€?- 鏄庣‘鍒楀嚭 IMGUI 鏋舵瀯瀵艰嚧鐨勭‖鎬ц竟鐣岋紙鍊兼柇瑷€銆佹诞绐椼€佺函缁樺埗鎺т欢锛夈€?