# UIToolkit 鎺т欢鑷姩鍖栬鐩栦笌闄愬埗璇存槑

鐗堟湰锛?.0.0
鏃ユ湡锛?026-04-23
鐘舵€侊細宸插叏閲忛獙璇侊紙115 浠?YAML 鍏ㄩ儴鎵ц閫氳繃锛屽惈璐熷悜娴嬭瘯锛?
---

## 1. 鏂囨。鐩爣

鏈枃妗ｅ垪鍑?UnityUIFlow V1 瀵?UIToolkit 鎵€鏈夋帶浠剁被鍨嬬殑鑷姩鍖栬鐩栬寖鍥达紝鏄庣‘浠ヤ笅鍒嗙被锛?- **鍏ㄩ潰鏀寔**锛氬彲閫氳繃 YAML 澹版槑寮忔楠ゅ畬鎴愬畾浣嶃€佷氦浜掋€佽祴鍊笺€佹柇瑷€銆?- **灞€閮ㄦ敮鎸?*锛氬彲瀹氫綅鍜岄儴鍒嗕氦浜掞紝浣嗛儴鍒嗛珮绾у姛鑳介渶 C# Page Object 缂栫▼銆?- **浠呭畾浣?鏂█**锛氬彲閫氳繃閫夋嫨鍣ㄥ畾浣嶅拰璇诲彇灞炴€э紝浣嗕氦浜掑繀椤婚€氳繃 C# 缂栫▼銆?- **涓嶅彲鑷姩鍖?*锛氬畼鏂?API 涓嶅紑鏀炬垨鎺т欢鐗规€у鑷存棤娉曢€氳繃浠讳綍鑷姩鍖栨墜娈垫搷浣溿€?
褰撳墠绀轰緥楠屾敹鍩虹嚎锛?- `Samples~/Yaml` 宸叉墿灞曞埌 **115 浠?* YAML锛堝惈绀轰緥鐢ㄤ緥 `01-35`銆乣41-44`銆佸満鏅墿灞?`50-57`銆乣66-90`銆両MGUI 鐢ㄤ緥 `97-99`銆丠ost Window 鐢ㄤ緥銆佹潯浠?寰幆鐢ㄤ緥銆侀珮绾у弬鏁板埆鍚嶃€佽礋鍚戞祴璇曠瓑锛夈€?- **鍏ㄩ噺楠岃瘉缁撴灉**锛氭鍚戞祴璇曞叏閮ㄩ€氳繃 / 璐熷悜娴嬭瘯鎸夎璁″け璐ワ紙棰勬湡琛屼负锛? 0 浠介敊璇€?
---

## 2. 浣曟椂鐢?YAML 娴嬭瘯鐢ㄤ緥锛屼綍鏃剁敤 C# 浠ｇ爜娴嬭瘯鐢ㄤ緥

### 2.1 浼樺厛閫夋嫨 YAML 娴嬭瘯鐢ㄤ緥鐨勫満鏅?
| 鍦烘櫙 | 鍘熷洜 |
|------|------|
| 绔埌绔敤鎴锋祦绋嬶紙鐐瑰嚮 鈫?杈撳叆 鈫?鏂█锛?| YAML 姝ラ涓庣敤鎴锋搷浣滀竴涓€瀵瑰簲锛屽彲璇绘€ч珮 |
| 鎺т欢鍙鎬с€佹枃鏈€佸€兼柇瑷€ | `assert_visible`銆乣assert_text`銆乣assert_value` 瑕嗙洊 90% 浠ヤ笂鐨勯獙璇侀渶姹?|
| 琛ㄥ崟濉啓锛圱extField銆乀oggle銆丏ropdown銆丼lider锛?| `set_value`銆乣type_text_fast`銆乣select_option` 鐩存帴鏀寔 |
| 鏁版嵁椹卞姩娴嬭瘯锛堝缁勮緭鍏ュ弬鏁帮級 | 鍐呭缓 `data.rows` / `from_csv` / `from_json` 鏁版嵁婧愭敮鎸?|
| 鎴浘鍥炲綊娴嬭瘯 | `screenshot` 鍔ㄤ綔鍐呭缓锛屾棤闇€ C# 浠ｇ爜 |
| 鏈夋潯浠舵楠わ紙濡?鑻ュ厓绱犲瓨鍦ㄥ垯鐐瑰嚮"锛?| `if: exists/not_exists` + `repeat_while` 鎺у埗娴?|
| 鑿滃崟鎿嶄綔锛堜笂涓嬫枃鑿滃崟銆佸脊鍑鸿彍鍗曪級 | `open_context_menu`銆乣select_context_menu_item`銆乣menu_item` 涓撶敤鍔ㄤ綔 |
| 閿洏蹇嵎閿紙Ctrl+S銆丆trl+A 绛夛級 | `press_key_combination` 鍐呭缓 |
| 璺ㄥ绐楀彛鏃犲叧鐨勫崟绐楀彛 EditorWindow 娴嬭瘯 | Fixture 浠呴渶澹版槑 `host_window.type` |

### 2.2 闇€瑕?C# 浠ｇ爜娴嬭瘯鐢ㄤ緥鐨勫満鏅?
| 鍦烘櫙 | 鍘熷洜 |
|------|------|
| 鎿嶄綔绯荤粺寮圭獥锛圕olor Picker銆丆urve Editor銆丱bject Picker 娴獥锛?| 鐙珛 `EditorWindow`锛屼笉鍦ㄨ娴嬬獥鍙?UI 鏍戝唴锛孻AML 鏃犳硶瀹氫綅 |
| 闇€瑕佽闂?`SerializedObject`/`SerializedProperty` 鐨勫鏉傞獙璇?| YAML 鍔ㄤ綔灞備笉鏆撮湶搴忓垪鍖栧璞?API |
| 鑷畾涔?`PropertyDrawer` 鍐呴儴甯冨眬鐨勭簿缁嗘柇瑷€ | 鍔ㄦ€佺敓鎴愮殑瀛愬厓绱犺矾寰勪笉绋冲畾锛岄渶 C# 杩愯鏃堕亶鍘?|
| 鐪熷疄鏂囦欢鎷栨斁锛坄DragAndDrop` 鐢熷懡鍛ㄦ湡锛?| 渚濊禆 Editor `DragAndDrop` API锛屼笉绛変环浜?UIToolkit 鎸囬拡浜嬩欢 |
| 澶氱獥鍙ｅ崗鍚岋紙璺ㄧ獥鍙ｆ嫋鎷姐€佽法绐楀彛鏂█锛?| V1 娴嬭瘯妯″瀷鎸夊崟瀹夸富绐楀彛缁勭粐 |
| 楠岃瘉涓氬姟閫昏緫鍓綔鐢紙C# 鐘舵€佸彉鍖栥€丼criptableObject 淇敼锛?| 闇€瑕佺洿鎺ヨ闂娴嬪璞″疄渚?|
| IME/杈撳叆娉曠粍鍚堣緭鍏ョ殑绮剧‘楠岃瘉 | InputSystem 娴嬭瘯 API 涓嶈鐩?IME 缁勫悎鎬?|
| 鎬ц兘鍩哄噯娴嬭瘯锛堝抚鐜囥€丟C 鍒嗛厤锛?| 闇€瑕?`ProfilerRecorder` 绛?C# API |
| 鍍忕礌绾ц瑙夊姣?| 闇€瑕佸紩鍏ュ浘鍍忓鐞嗗簱锛屾鏋跺綋鍓嶆棤鍐呭缓鍍忕礌 diff |
| 鑷畾涔?`EditorWindow` 鐨勯潪鏍囧噯浜嬩欢澶勭悊 | 闇€瑕佺洿鎺ュ弽灏勬垨璋冪敤琚祴绐楀彛鐨勫唴閮ㄦ柟娉?|

### 2.3 YAML + C# 娣峰悎鍦烘櫙

- **鎺ㄨ崘妯″紡**锛欳# Fixture 瀛愮被瑕嗙洊 `SetUp`/`TearDown` 鍋氭暟鎹噯澶囧拰娓呯悊锛孻AML 璐熻矗 UI 浜や簰姝ラ銆?- 鍦?C# Fixture 涓皟鐢?`await ExecuteYamlStepsAsync(yamlContent)` 娉ㄥ叆 YAML 娴佺▼銆?- 鍦?C# Fixture 涓皟鐢?`await ExecuteActionAsync(action, parameters)` 娉ㄥ叆鍗曟楠ゅ姩浣溿€?
---

## 3. V1 鍏ㄩ潰鏀寔鐨勬帶浠?
浠ヤ笅鎺т欢鍙€氳繃 YAML 鍐呯疆鍔ㄤ綔瀹屾垚瀹屾暣鐨勫畾浣嶃€佷氦浜掋€佽祴鍊煎拰鏂█锛?
| 鎺т欢 | 鍙敤 YAML 鍔ㄤ綔 | 璧嬪€兼柟寮?| 鏂█鏂瑰紡 |
| --- | --- | --- | --- |
| `Button` | `click`銆乣double_click`銆乣hover` | 涓嶉€傜敤锛堟棤 value锛?| `assert_visible`銆乣assert_text`銆乣assert_enabled`銆乣assert_disabled` |
| `RepeatButton` | `click`銆乣hover` | 涓嶉€傜敤 | `assert_visible`銆乣assert_text`銆乣assert_enabled` |
| `Label` | `hover` | 涓嶉€傜敤锛堢函鏄剧ず锛?| `assert_visible`銆乣assert_text`銆乣assert_text_contains`銆乣assert_property` |
| `Toggle` | `click`銆乣set_value` | `set_value`锛坄true`/`false`锛?| `assert_value`銆乣assert_property` |
| `TextField` | `type_text`銆乣type_text_fast`銆乣set_value`銆乣click`銆乣focus` | `type_text`銆乣type_text_fast`銆乣set_value` | `assert_text`銆乣assert_value`銆乣assert_property` |
| `IntegerField` | `type_text_fast`銆乣set_value`銆乣click`銆乣focus` | `set_value`锛堟暣鏁版枃鏈級 | `assert_value`銆乣assert_property` |
| `LongField` | 鍚?`IntegerField` | `set_value`锛堥暱鏁存暟鏂囨湰锛?| 鍚屼笂 |
| `FloatField` | 鍚?`IntegerField` | `set_value`锛堟诞鐐规枃鏈級 | 鍚屼笂 |
| `DoubleField` | 鍚?`IntegerField` | `set_value`锛堟诞鐐规枃鏈級 | 鍚屼笂 |
| `UnsignedIntegerField` | 鍚?`IntegerField` | `set_value`锛堟棤绗﹀彿鏁存暟鏂囨湰锛?| 鍚屼笂 |
| `UnsignedLongField` | 鍚?`IntegerField` | `set_value`锛堟棤绗﹀彿闀挎暣鏁版枃鏈級 | 鍚屼笂 |
| `Slider` | `set_slider`銆乣set_value`銆乣drag`銆乣click` | `set_slider`锛坒loat value锛夈€乣set_value` | `assert_value`銆乣assert_property` |
| `SliderInt` | 鍚?`Slider` | 鍚?`Slider` | 鍚屼笂 |
| `DropdownField` | `select_option`銆乣click` | `select_option`锛坆y text/index锛夈€乣set_value` | `assert_value`銆乣assert_property` |
| `PopupField<string>` | `select_option`銆乣click_popup_item`銆乣click` | `select_option`銆乣set_value` | `assert_value`銆乣assert_property` |
| `EnumField` | `select_option`銆乣click` | `select_option`锛坆y enum name/index锛夈€乣set_value` | `assert_value` |
| `RadioButton` | `click`銆乣set_value` | `set_value`锛坄true`/`false`锛?| `assert_value` |
| `RadioButtonGroup` | `select_option`銆乣set_value` | `select_option`锛堟寜绱㈠紩锛?| `assert_value` |
| `Foldout` | `toggle_foldout`銆乣click`銆乣set_value` | `toggle_foldout`銆乣set_value`锛坄true`/`false`锛?| `assert_value`銆乣assert_property` |
| `ScrollView` | `scroll`銆乣drag` | 涓嶉€傜敤 | `assert_visible`銆乣assert_property` |
| `ListView` | `select_list_item`锛坄index`/`indices`锛夈€乣drag_reorder`銆乣scroll`銆乣click` | `select_list_item` | `assert_property`锛坄selectedIndex`/`selectedIndices`锛?|
| `TreeView` | `select_tree_item`锛坄id`/`index`锛夈€乣scroll`銆乣click` | `select_tree_item` | `assert_property`锛坄selectedIndex`锛?|
| `Vector2Field` | `set_value`銆乣focus` | `set_value`锛坄"x,y"`锛?| `assert_value` |
| `Vector3Field` | `set_value`銆乣focus` | `set_value`锛坄"x,y,z"`锛?| `assert_value` |
| `Vector4Field` | `set_value`銆乣focus` | `set_value`锛坄"x,y,z,w"`锛?| `assert_value` |
| `Vector2IntField` | `set_value`銆乣focus` | `set_value`锛坄"x,y"`锛?| `assert_value` |
| `Vector3IntField` | `set_value`銆乣focus` | `set_value`锛坄"x,y,z"`锛?| `assert_value` |
| `RectField` | `set_value`銆乣focus` | `set_value`锛坄"x,y,w,h"`锛?| `assert_value` |
| `RectIntField` | `set_value`銆乣focus` | `set_value`锛坄"x,y,w,h"`锛?| `assert_value` |
| `BoundsField` | `set_value`銆乣focus` | `set_value`锛坄"cx,cy,cz,ex,ey,ez"`锛?| `assert_value` |
| `BoundsIntField` | `set_value`銆乣focus` | `set_value`锛坄"px,py,pz,sx,sy,sz"`锛?| `assert_value` |
| `MinMaxSlider` | `set_slider`锛坄min_value`/`max_value`锛夈€乣set_value`銆乣drag` | `set_slider`銆乣set_value` | `assert_value` |
| `Hash128Field` | `set_value`銆乣focus` | `set_value`锛?2 浣嶅崄鍏繘鍒跺瓧绗︿覆锛?| `assert_value` |
| `ProgressBar` | 鏃狅紙绾樉绀猴級 | 涓嶉€傜敤 | `assert_property`锛坄value`銆乣title`锛?|
| `Image` | `click`銆乣hover` | 涓嶉€傜敤锛堢函鏄剧ず锛?| `assert_visible`銆乣assert_property` |
| `HelpBox` | 鏃狅紙绾樉绀猴級 | 涓嶉€傜敤 | `assert_visible`銆乣assert_text` |
| `Box` | `click`銆乣hover` | 涓嶉€傜敤锛堢函瀹瑰櫒锛?| `assert_visible` |
| `GroupBox` | `click`銆乣hover` | 涓嶉€傜敤锛堢函瀹瑰櫒锛?| `assert_visible` |
| `VisualElement` | `click`銆乣hover`銆乣drag` | 涓嶉€傜敤锛堝熀纭€瀹瑰櫒锛?| `assert_visible`銆乣assert_property` |
| `TabView` | `select_tab`銆乣close_tab` | 涓嶉€傜敤锛堝鍣?瀵艰埅锛?| `assert_visible`銆乣assert_property` |
| `Toolbar` | `hover` | 涓嶉€傜敤锛堢函瀹瑰櫒锛?| `assert_visible` |
| `ToolbarButton` | `click`銆乣double_click`銆乣hover` | 涓嶉€傜敤锛堢户鎵胯嚜 `Button`锛?| `assert_visible`銆乣assert_text`銆乣assert_enabled` |
| `ToolbarToggle` | `click`銆乣set_value` | `set_value`锛坄true`/`false`锛岀户鎵胯嚜 `Toggle`锛?| `assert_value`銆乣assert_property` |
| `ToolbarSearchField` | `type_text`銆乣type_text_fast`銆乣set_value`銆乣click`銆乣focus` | `type_text`銆乣type_text_fast`銆乣set_value` | `assert_text`銆乣assert_value`銆乣assert_property` |

---

## 4. V1 灞€閮ㄦ敮鎸佺殑鎺т欢

| 鎺т欢 | 鍙敤 YAML 鍔ㄤ綔 | 涓嶆敮鎸佺殑浜や簰 | 鍘熷洜涓庢浛浠ｆ柟妗?|
| --- | --- | --- | --- |
| `EnumFlagsField` | `select_option`锛坄value`/`index`/`indices`锛夈€乣toggle_mask_option`銆乣click_popup_item`銆乣set_value` | 寮瑰嚭闈㈡澘鐪熷疄閫愰」鐐瑰嚮 | 寮瑰嚭闈㈡澘涓虹嫭绔嬫诞绐楋紱`select_option` 鏁村€肩洿鍐?/ `toggle_mask_option` 鍗曚綅 toggle / `click_popup_item` 瀛楁鍊兼搷浣?涓烘浛浠ｆ柟妗?|
| `MaskField` | `select_option`锛坄value`/`index`/`indices`锛夈€乣toggle_mask_option`銆乣click_popup_item`銆乣set_value` | 寮瑰嚭闈㈡澘鐪熷疄閫愰」鐐瑰嚮 | 鍚屼笂 |
| `LayerMaskField` | `select_option`锛坄value`/`index`/`indices`锛夈€乣toggle_mask_option`銆乣click_popup_item`銆乣set_value` | 寮瑰嚭闈㈡澘閫愰」鎿嶄綔 | 鍚屼笂 |
| `ColorField` | `set_value`銆乣assert_value`銆乣click` | 鎷捐壊鍣ㄩ潰鏉夸氦浜掋€丒ye Dropper | 鎷捐壊鍣ㄤ负鐙珛 `EditorWindow`锛涗娇鐢?`set_value`/`assert_value` 鐩村啓 `Color` 鍊硷紙鏀寔 `#RRGGBB`/`#RRGGBBAA` 鍗佸叚杩涘埗鏍煎紡锛?|
| `MultiColumnListView` | `select_list_item`锛坄index`/`indices`锛夈€乣sort_column`銆乣resize_column`銆乣scroll`銆乣click` | 鐪熷疄 UI 鎷栨嫿鎺掑簭/鍒楀 | `sort_column` 鐩存帴鍐?`sortColumnDescriptions`锛沗resize_column` 鐩存帴璁剧疆 `Column.width` |
| `MultiColumnTreeView` | `select_tree_item`锛坄id`/`index`锛夈€乣sort_column`銆乣resize_column`銆乣scroll`銆乣click` | 鐪熷疄 UI 鎷栨嫿鎺掑簭/鍒楀 | 鍚屼笂 |
| `TwoPaneSplitView` | `drag`锛堟嫋鎷藉垎鍓叉潯锛夈€乣set_split_view_size` | 璺ㄧ獥鍙ｆ嫋鍒嗗壊鏉¤仈鍔?| `set_split_view_size` 鎸?`pane`锛?/1锛夋寚瀹氱洰鏍囧昂瀵革紙鍍忕礌锛?|
| `Scroller` | `set_value`銆乣click`銆乣drag`銆乣page_scroller`銆乣drag_scroller` | 鏃?| `drag_scroller` 鏀寔鎸?`ratio` 鎴栨柟鍚?璺濈鎷栨嫿 thumb锛沗page_scroller` 鏀寔鍒嗛〉鏂瑰悜涓庢鏁?|
| `TagField` | `select_option`锛坄value`/`index`锛夈€乣click_popup_item`銆乣assert_value` | 寮瑰嚭鑿滃崟鍐呴儴閫愰」鐐瑰嚮 | 缁ф壙鑷?`PopupField<string>`锛宍select_option` / `click_popup_item` 鐩村啓璁惧畾鍊?|
| `LayerField` | `select_option`锛坄value`/`index`锛夈€乣click_popup_item`銆乣assert_value` | 寮瑰嚭鑿滃崟鍐呴儴閫愰」鐐瑰嚮 | 鍚屼笂 |
| `ObjectField` | `set_value`锛坄guid:`/`path:`/`name:`/`asset-name:`/`search:`/`search:TypeName:Needle`锛夈€乣assert_value`銆乣assert_property` | Object Picker 瀵硅瘽妗嗐€佺湡瀹炴嫋鏀捐祴鍊?| 鐩存帴鍔犺浇璧勪骇骞惰祴鍊硷紱瀵硅薄閫夋嫨娴獥涓庣湡瀹?DragAndDrop 涓嶅彲鑷姩鍖?|
| `CurveField` | `set_value`锛堥敭甯?DSL锛歚time:value:inTangent:outTangent;...`锛夈€乣assert_value`銆乣assert_property` | 鏇茬嚎缂栬緫鍣ㄧ獥鍙ｄ氦浜?| 閿抚 DSL 鍙洿鍐欏畬鏁?`AnimationCurve`锛涙洸绾跨紪杈戝櫒娴獥涓嶅湪琚祴鏍戝唴 |
| `GradientField` | `set_value`锛堟笎鍙?DSL锛歚time:#RRGGBBAA;...|time:alpha;...`锛夈€乣assert_value`銆乣assert_property` | 娓愬彉缂栬緫鍣ㄧ獥鍙ｄ氦浜?| 娓愬彉 DSL 鍙洿鍐欏畬鏁?`Gradient`锛涙笎鍙樼紪杈戝櫒娴獥涓嶅湪琚祴鏍戝唴 |
| `ToolbarMenu` | `click`銆乣open_popup_menu`銆乣select_popup_menu_item`銆乣assert_menu_item`銆乣assert_menu_item_disabled`銆乣menu_item` | 鐩存帴瀹氫綅寮瑰嚭娴獥鍐呴儴 VisualTree | 瀹樻柟 `PopupMenuSimulator` + `DropdownMenu` reflection fallback 鏀寔鑿滃崟椤归€夋嫨涓庢柇瑷€ |
| `ToolbarPopupSearchField` | `type_text`銆乣type_text_fast`銆乣set_value`銆乣assert_value`銆乣click`銆乣focus` | 寮瑰嚭鑿滃崟鍐呴儴椤圭偣鍑?| 鎼滅储鏂囨湰杈撳叆鏈綋鍙嚜鍔ㄥ寲锛涘脊鍑虹粨鏋滆彍鍗曚粛涓虹嫭绔嬫诞绐?|
| `ToolbarBreadcrumbs` | `click`銆乣navigate_breadcrumb`銆乣read_breadcrumbs` | 寮瑰嚭寮忓鑸彍鍗?| 宸叉敮鎸佹寜 `label`/`index` 瀵艰埅鍙婅嚜鍔ㄦ灇涓撅紙`read_breadcrumbs`锛夛紱寮瑰嚭寮忓巻鍙茶矾寰勮彍鍗曚粛灞炵嫭绔嬫诞绐楄竟鐣?|
| `PropertyField` | `set_bound_value`銆乣assert_bound_value`锛屼互鍙婇€氳繃鍚庝唬鎺т欢鎵ц `click`/`set_value`/`assert_value` | 鐩存帴瀵规湭缁戝畾鎴栨棤绋冲畾 `bindingPath` 鐨勫鏉傚瓙缁撴瀯鍋氱粺涓€璧嬪€?| 鎸?`binding_path` 缁熶竴璇箟璧嬪€硷紱鑻ュ瓙鏍戞棤绋冲畾缁戝畾璺緞浠嶉渶鍥為€€鍒板悗浠ｆ帶浠跺畾浣?|
| `InspectorElement` | `set_bound_value`銆乣assert_bound_value`锛屼互鍙婇€氳繃鍚庝唬鎺т欢鎵ц `click`/`set_value`/`assert_value` | 瀹屽叏鑴辩 Inspector 鐢熸垚缁撴瀯鍋氫换鎰忓瓧娈垫帹鏂?| 鎸?`binding_path` 绌块€忓埌 Inspector 缁戝畾瀛楁 |

---

## 5. V1 涓嶅彲鑷姩鍖栫殑鎺т欢涓庡姛鑳?
| 鎺т欢/鍔熻兘 | 鍘熷洜 | 寤鸿 |
| --- | --- | --- |
| `IMGUIContainer` 鍐呴儴鎺т欢 | IMGUI 鍐呭涓嶈繘鍏?UIToolkit VisualTree锛岄€夋嫨鍣ㄦ棤娉曟嬁鍒板唴閮ㄥ厓绱?| 灏嗚娴?UI 杩佺Щ鍒扮函 UIToolkit 瀹炵幇锛涙垨浣跨敤 `imgui_*` IMGUI 涓撳睘鍔ㄤ綔 |
| `ProjectSettingsProvider`锛堥」鐩缃潰鏉匡級 | 褰撳墠瀹炵幇浣跨敤 `EditorGUILayout`锛圛MGUI锛夛紝涓嶇敓鎴?UIToolkit 瀛愬厓绱?| 灞炰簬 Unity 鎶€鏈竟鐣岋紱闇€閲嶅啓涓?UIToolkit 鎵嶈兘瑕嗙洊 |
| `ColorField` 鐨?Color Picker / Eye Dropper | 寮瑰嚭鐙珛缂栬緫鍣ㄧ獥鍙?| 浣跨敤 `set_value` 鐩村啓棰滆壊鍊?|
| `CurveField` 鏇茬嚎缂栬緫鍣ㄦ诞绐?| 鐙珛缂栬緫鍣ㄧ獥鍙?| 浣跨敤閿抚 DSL 鐩村啓 |
| `GradientField` 娓愬彉缂栬緫鍣ㄦ诞绐?| 鐙珛缂栬緫鍣ㄧ獥鍙?| 浣跨敤娓愬彉 DSL 鐩村啓 |
| Object Picker 瀵硅瘽妗?| Unity 璧勪骇閫夋嫨瀵硅瘽妗嗕笉鍦ㄥ綋鍓嶇獥鍙ｆ爲鍐?| 浣跨敤 `set_value` 鐨?`guid:`/`path:` 绛栫暐鐩存帴璧嬪€?|
| Tooltip 娴獥 | 鐢?Editor 鍏ㄥ眬绠＄悊锛屼笉鍦ㄧ洰鏍囩獥鍙?UI 鏍戝唴 | 鍙€氳繃 `assert_property` 鏂█ `tooltip` 灞炴€у€硷紝浣嗘棤娉曢獙璇佸彲瑙嗘覆鏌?|
| `ToolbarPopupSearchField` 寮瑰嚭缁撴灉鑿滃崟 | 缁撴灉闈㈡澘涓嶅湪琚祴绐楀彛鏍戝唴锛屽畼鏂规湭鏆撮湶绋冲畾缁撴灉椤归亶鍘?API | 鎼滅储杈撳叆鏈綋鍙嚜鍔ㄥ寲锛涚粨鏋滈」閫夋嫨闇€ C# 缂栫▼ |
| 澶氱獥鍙ｅ崗鍚?| V1 姣忎釜娴嬭瘯浠呮敮鎸佷竴涓涓荤獥鍙?| 鍗曠獥鍙ｈ璁★紱璺ㄧ獥鍙ｆ祴璇曢渶鎷嗕负澶氫釜鐙珛鐢ㄤ緥 |
| 鍓创鏉挎搷浣滐紙Copy/Paste锛?| 闇€瑕佺郴缁熺骇鍓创鏉?API + 閿洏蹇嵎閿粍鍚?| 浣跨敤 `set_value` 蹇€熻矾寰勬浛浠ｇ矘璐?|
| IME / 杈撳叆娉曠粍鍚堣緭鍏?| InputSystem 娴嬭瘯 API 涓嶈鐩?IME 缁勫悎鎬?| 浣跨敤 `type_text_fast` 鐩存帴鍐欏叆鏈€缁堟枃鏈?|
| 鎷栨斁鏂囦欢鍒版帶浠?| 渚濊禆 Editor `DragAndDrop` 鐢熷懡鍛ㄦ湡锛屼笉绛変环浜?UIToolkit 浜嬩欢 | C# 缂栫▼妯℃嫙 `DragAndDrop` |
| 鍔ㄦ€佺敓鎴愮殑娴獥寮?UI | `ShowAsDropdown`銆乣GenericMenu.ShowAsContext` 绛夊垱寤虹殑娴獥涓嶅湪琚祴鏍戝唴 | 鑻ュ彲缁曡繃寮瑰嚭鐩存帴 `set_value` 鍒欎娇鐢ㄧ洿鎺ュ啓鍊兼柟妗?|
| 鍍忕礌绾ц瑙夊姣?| 鏃犲唴寤鸿瑙夊熀绾夸笌宸紓鍒嗘瀽閾捐矾 | P5 鎵╁睍 |

---

## 6. 鍏ㄩ儴鍐呯疆鍔ㄤ綔璇︾粏璇存槑

### 6.1 榧犳爣/鎸囬拡鍔ㄤ綔

| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 搴曞眰鏈哄埗 | YAML 绀轰緥 |
|--------|---------|---------|-----------|
| `click` | `selector`銆乣button`锛坙eft/right/middle锛夈€乣modifiers`锛坰hift/ctrl/alt/cmd锛?| `DispatchClick` 鈫?`PointerDownEvent`+`PointerUpEvent`锛沗Button` 瀛愮被棰濆浜旂骇 fallback | `- click: { selector: "#ok-btn" }` |
| `double_click` | 鍚?`click`锛宍clickCount=2` | 鍚?`click` | `- double_click: { selector: "#item" }` |
| `hover` | `selector`銆乣duration`锛堟绉掑瓧绗︿覆锛?| `MouseMoveEvent` 鍒板厓绱犱腑蹇?| `- hover: { selector: "#tooltip-target", duration: "200ms" }` |
| `drag` | `from`锛堥€夋嫨鍣ㄦ垨 `"x,y"`锛夈€乣to`锛堝悓涓婏級銆乣duration`銆乣button`銆乣modifiers` | `MouseDown` 鈫?澶氬抚 `MouseMove`锛堢嚎鎬ф彃鍊硷級鈫?`MouseUp`锛涘悓鏃舵淳鍙?`PointerDown/Up` | `- drag: { from: "#thumb", to: "#target" }` |
| `scroll` | `selector`銆乣delta_x`銆乣delta_y` | `WheelEvent` 鍒板厓绱犱腑蹇?| `- scroll: { selector: "#scroll-view", delta_y: -100 }` |
| `open_context_menu` | `selector`銆乣modifiers` | 鍙抽敭鐐瑰嚮鍏冪礌锛坄button=1`锛?| `- open_context_menu: { selector: "#item" }` |
| `select_context_menu_item` | `value`锛堣彍鍗曢」鏂囨湰锛屽彲鐢?`item` 鍒悕锛?| 瀹樻柟 `ContextMenuSimulator` + `FloatingPanelLocator` fallback | `- select_context_menu_item: { value: "Delete" }` |
| `open_popup_menu` | `selector`銆乣modifiers` | 宸﹂敭鐐瑰嚮鍏冪礌鎵撳紑寮瑰嚭鑿滃崟 | `- open_popup_menu: { selector: "#toolbar-menu" }` |
| `select_popup_menu_item` | `value`锛堣彍鍗曢」鏂囨湰锛屽彲鐢?`item` 鍒悕锛?| `FloatingPanelLocator` 閬嶅巻娴眰闈㈡澘 | `- select_popup_menu_item: { value: "New Item" }` |
| `assert_menu_item` | `value`/`item`锛堣彍鍗曢」鏂囨湰锛?| 鏂█鑿滃崟椤瑰瓨鍦ㄤ笖宸插惎鐢?| `- assert_menu_item: { value: "Copy" }` |
| `assert_menu_item_disabled` | `value`/`item` | 鏂█鑿滃崟椤瑰瓨鍦ㄤ笖宸茬鐢?| `- assert_menu_item_disabled: { value: "Paste" }` |
| `menu_item` | `item`锛圲nity 鑿滃崟璺緞锛夈€乣type`锛坰elect/validate锛?| `EditorApplication.ExecuteMenuItem`锛沗kind=popup/context/auto` | `- menu_item: { item: "Edit/Undo" }` |
| `click_popup_item` | `selector`銆乣value`/`index` | 鐩存帴鎿嶄綔瀛楁鍊硷紙`MaskField`/`EnumFlagsField`/`PopupField`锛夛紱涓嶄緷璧栨诞灞傞潰鏉跨偣鍑?| `- click_popup_item: { selector: "#mask", value: "Layer 1" }` |

### 6.2 閿洏鍔ㄤ綔

| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 搴曞眰鏈哄埗 | YAML 绀轰緥 |
|--------|---------|---------|-----------|
| `type_text` | `selector`銆乣value`锛堣杈撳叆鐨勬枃鏈級 | 閫愬瓧绗︼細瀹樻柟 UIToolkit 椹卞姩 鈫?InputSystem `SendText` 鈫?`KeyDownEvent`+`KeyUpEvent` 鈫?`set_value` 鐩村啓鍏滃簳 | `- type_text: { selector: "#search", value: "hello" }` |
| `type_text_fast` | `selector`銆乣value` | 鐩存帴璧嬪€?`.value` 灞炴€э紙涓嶆ā鎷熼敭鐩樹簨浠讹級锛涙敮鎸佺┖瀛楃涓?`""` 娓呯┖ | `- type_text_fast: { selector: "#name", value: "Alice" }` |
| `press_key` | `selector`锛堝彲閫夛級銆乣key`锛圞eyCode 鏋氫妇鍚嶏紝濡?`Return`銆乣Escape`銆乣Delete`銆乣Tab`锛?| 瀹樻柟椹卞姩 鈫?InputSystem `PressKey` 鈫?`KeyDownEvent`+`KeyUpEvent` | `- press_key: { key: "Return" }` |
| `press_key_combination` | `keys`锛堝 `"Ctrl+A"`銆乣"Ctrl+Shift+Z"`锛?| 瑙ｆ瀽淇グ閿紱渚濇 `KeyDown` 鈫?涓婚敭锛堝甫淇グ绗︼級鈫?`ExecuteCommandEvent`/`ValidateCommandEvent`锛堝宸茬煡鍛戒护锛夆啋 涓婚敭 `KeyUp` 鈫?淇グ閿?`KeyUp` | `- press_key_combination: { keys: "Ctrl+Z" }` |
| `execute_command` | `command`锛圲IToolkit 鍛戒护鍚嶏紝濡?`Copy`銆乣Paste`銆乣SelectAll`銆乣Delete`銆乣Undo`銆乣Redo`锛?| `ExecuteCommandEvent` 娲惧彂鍒板綋鍓嶈仛鐒﹀厓绱?| `- execute_command: { command: "SelectAll" }` |
| `validate_command` | `command` | `ValidateCommandEvent` 娲惧彂鍒板綋鍓嶈仛鐒﹀厓绱?| `- validate_command: { command: "Copy" }` |
| `focus` | `selector` | `element.Focus()` | `- focus: { selector: "#input" }` |

### 6.3 鍊艰祴鍊煎姩浣?
| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 鏀寔鐨勬帶浠剁被鍨?| YAML 绀轰緥 |
|--------|---------|--------------|-----------|
| `set_value` | `selector`銆乣value`锛堝瓧绗︿覆锛?| 鎵€鏈?`BaseField<T>` 瀛愮被锛圱extField銆乀oggle銆両ntegerField銆丗loatField銆丏ropdownField銆丼lider銆乂ector*銆丷ect*銆丅ounds*銆丆olorField銆丆urveField銆丟radientField銆丱bjectField銆丠ash128Field 绛夛級 | `- set_value: { selector: "#slider", value: "0.75" }` |
| `set_slider` | `selector`銆乣value`锛堝崟鍊硷級鎴?`min_value`+`max_value`锛堝弻鍊硷紝MinMaxSlider锛?| `Slider`銆乣SliderInt`銆乣MinMaxSlider` | `- set_slider: { selector: "#volume", value: "0.5" }` |
| `select_option` | `selector`銆乣value`/`index`/`indices`锛堥€楀彿鍒嗛殧澶氱储寮曪級 | `DropdownField`銆乣EnumField`銆乣EnumFlagsField`銆乣RadioButtonGroup`銆乣MaskField`銆乣LayerMaskField`銆乣TagField`銆乣LayerField`銆乣PopupField<string>` | `- select_option: { selector: "#quality", value: "High" }` |
| `toggle_mask_option` | `selector`銆乣value`/`index`锛堝崟椤癸級 | `EnumFlagsField`銆乣MaskField`銆乣LayerMaskField` | `- toggle_mask_option: { selector: "#flags", value: "OptionA" }` |
| `toggle_foldout` | `selector`銆乣expand`锛堝彲閫夛紝`true`/`false`锛?| `Foldout` | `- toggle_foldout: { selector: "#settings" }` |
| `select_tab` | `selector`锛圱abView锛夈€乣label`/`index` | `TabView` | `- select_tab: { selector: "#tabs", label: "Settings" }` |
| `close_tab` | `selector`锛圱abView锛夈€乣label`/`index` | `TabView` | `- close_tab: { selector: "#tabs", index: 0 }` |
| `select_list_item` | `selector`銆乣index`锛堝崟閫夛級鎴?`indices`锛堝閫夛紝閫楀彿鍒嗛殧锛?| `ListView`銆乣MultiColumnListView` | `- select_list_item: { selector: "#list", index: 2 }` |
| `drag_reorder` | `selector`銆乣from_index`銆乣to_index` | `ListView` | `- drag_reorder: { selector: "#list", from_index: 0, to_index: 3 }` |
| `select_tree_item` | `selector`銆乣id`/`index` | `TreeView`銆乣MultiColumnTreeView` | `- select_tree_item: { selector: "#tree", id: "node-1" }` |
| `set_bound_value` | `selector`銆乣binding_path`銆乣value` | `PropertyField`銆乣InspectorElement` 浠ュ強浠讳綍鏈夌粦瀹氳矾寰勭殑鍚庝唬鎺т欢 | `- set_bound_value: { selector: "#inspector", binding_path: "speed", value: "10.5" }` |
| `navigate_breadcrumb` | `selector`锛圔readcrumbBar锛夈€乣label`/`index` | `ToolbarBreadcrumbs` | `- navigate_breadcrumb: { selector: "#breadcrumbs", label: "Root" }` |
| `read_breadcrumbs` | `selector`锛圔readcrumbBar锛夈€乣bag_key`锛堝瓨鍏?SharedBag锛?| `ToolbarBreadcrumbs` | `- read_breadcrumbs: { selector: "#breadcrumbs", bag_key: "crumbs" }` |
| `set_split_view_size` | `selector`锛圱woPaneSplitView锛夈€乣pane`锛?/1锛夈€乣size`锛堝儚绱狅級 | `TwoPaneSplitView` | `- set_split_view_size: { selector: "#split", pane: 0, size: 300 }` |
| `page_scroller` | `selector`锛圫crollView/Scroller锛夈€乣direction`锛坲p/down/left/right锛夈€乣count`锛堥〉鏁帮級銆乣page_size`锛堝彲閫夛紝鍍忕礌锛?| `ScrollView`銆乣Scroller` | `- page_scroller: { selector: "#scroll", direction: "down", count: 2 }` |
| `drag_scroller` | `selector`锛圫croller锛夈€乣ratio`锛?~1锛夋垨 `direction`+`distance` | `Scroller` | `- drag_scroller: { selector: "#vscroll", ratio: 0.5 }` |
| `sort_column` | `selector`锛圡ultiColumn*锛夈€乣column`锛堝悕绉?鏍囬锛夋垨 `index`銆乣direction`锛坅scending/descending锛?| `MultiColumnListView`銆乣MultiColumnTreeView` | `- sort_column: { selector: "#table", column: "Name", direction: "ascending" }` |
| `resize_column` | `selector`锛圡ultiColumn*锛夈€乣column`/`index`銆乣width`锛堝儚绱狅級 | `MultiColumnListView`銆乣MultiColumnTreeView` | `- resize_column: { selector: "#table", column: "Size", width: 120 }` |

### 6.4 鏂█鍔ㄤ綔

| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 璇存槑 |
|--------|---------|------|
| `assert_visible` | `selector`銆乣timeout` | 鏂█鍏冪礌鍙锛坉isplay鈮爊one銆乿isibility鈮爃idden銆乷pacity>0锛夛紝鏀寔瓒呮椂杞 |
| `assert_not_visible` | `selector`銆乣timeout` | 鏂█鍏冪礌涓嶅彲瑙?|
| `wait_for_element` | `selector`銆乣timeout` | 绛夊緟鍏冪礌鍑虹幇锛堜笌 `assert_visible` 鐩稿悓锛屼絾璇箟寮鸿皟绛夊緟锛?|
| `assert_text` | `selector`銆乣expected` | 鏂█ `.text` / `.value`锛堝瓧绗︿覆锛夌簿纭瓑浜?`expected`锛堝拷鐣ュぇ灏忓啓锛?|
| `assert_text_contains` | `selector`銆乣expected` | 鏂█鏂囨湰鍖呭惈 `expected`锛堝拷鐣ュぇ灏忓啓锛?|
| `assert_value` | `selector`銆乣expected` | 鏂█鍏冪礌 `.value` 鐨勫瓧绗︿覆琛ㄧず銆傛敮鎸佺被鍨嬬骇姣旇緝锛歚Color`锛?RRGGBBAA锛夈€乣Vector*`銆乣Rect*`銆乣Bounds*`銆乣Hash128`銆佹灇涓惧悕绉扮瓑 |
| `assert_bound_value` | `selector`銆乣binding_path`銆乣expected` | 鏂█缁戝畾瀛楁鐨勫€?|
| `assert_property` | `selector`銆乣property`锛堝睘鎬у悕锛屽 `display`銆乣value`銆乣name`銆乣visible`銆乣tabIndex`銆乣lowValue`銆乣highValue`锛夈€乣expected` | 鏂█ USS 鏍峰紡灞炴€ф垨 UIElement 鍙嶅皠灞炴€?|
| `assert_enabled` | `selector` | 鏂█鍏冪礌鏈 `SetEnabled(false)` 绂佺敤 |
| `assert_disabled` | `selector` | 鏂█鍏冪礌宸茶 `SetEnabled(false)` 绂佺敤 |

### 6.5 宸ュ叿/鎺у埗娴佸姩浣?
| 鍔ㄤ綔鍚?| 鍏抽敭鍙傛暟 | 璇存槑 |
|--------|---------|------|
| `screenshot` | `tag`锛堟枃浠跺悕鏍囩锛?| 鎴彇褰撳墠瀹夸富绐楀彛鎴浘锛涜矾寰勫啓鍏?`CurrentAttachments` |
| `wait` | `duration`锛堝 `"500ms"`銆乣"2s"`锛涗笂闄?600s锛?| 绛夊緟鍥哄畾鏃堕暱 |

---

## 7. 杈撳叆妯℃嫙鏀寔璇︽儏

### 7.1 榧犳爣/鎸囬拡杈撳叆

| 鎿嶄綔 | 鏀寔鎯呭喌 | 鎶€鏈満鍒?|
|------|---------|---------|
| 宸﹂敭鍗曞嚮 | 鉁?瀹屾暣鏀寔 | `PointerDownEvent`+`PointerUpEvent`锛沗Button` 瀛愮被棰濆 `SendClickEvent`鈫抈Button.clicked`鈫抈Clickable.clicked`鈫抈SimulateSingleClick`鈫抈ClickEvent` 浜旂骇 fallback |
| 宸﹂敭鍙屽嚮 | 鉁?瀹屾暣鏀寔 | `clickCount=2` 鐨?`PointerDownEvent`+`PointerUpEvent` |
| 鍙抽敭鍗曞嚮 | 鉁?瀹屾暣鏀寔 | `button=1` 鐨?`PointerDownEvent`+`PointerUpEvent` |
| 涓敭鍗曞嚮 | 鉁?鏀寔 | `button=2` 鐨?`PointerDownEvent`+`PointerUpEvent` |
| 淇グ閿?鐐瑰嚮锛圫hift/Ctrl/Alt/Cmd锛?| 鉁?瀹屾暣鏀寔 | `EventModifiers` 鍙傛暟浼犻€掑埌 `PointerDownEvent` |
| 鎮仠锛圡ouseMove锛?| 鉁?瀹屾暣鏀寔 | `MouseMoveEvent` 鍒板厓绱犱腑蹇冿紱鏀寔鎸佺画鏃堕暱 |
| 鎷栨嫿锛堜袱鐐归棿锛?| 鉁?瀹屾暣鏀寔 | 鍒嗗抚绾挎€ф彃鍊?`MouseMove`锛涙敮鎸?`button`锛?/1/2锛夊拰 `modifiers` |
| 婊氳疆 | 鉁?瀹屾暣鏀寔 | `WheelEvent` 鐨?`delta.x`/`delta.y` |
| 璺ㄧ獥鍙ｆ嫋鎷?| 鉂?涓嶆敮鎸?| V1 鍗曠獥鍙ｉ檺鍒?|
| 鏂囦欢/瀵硅薄鎷栨斁 | 鉂?涓嶆敮鎸?| 渚濊禆 Editor `DragAndDrop` 鐢熷懡鍛ㄦ湡 |
| 榧犳爣鍧愭爣绮剧‘瀹氫綅 | 鉁?鏀寔锛坄from`/`to` 鍙傛暟鍙洿鎺ヤ紶 `"x,y"` 绐楀彛鍧愭爣锛?| 鍏冪礌涓績鑷姩璁＄畻锛涙垨鏄惧紡鍧愭爣杈撳叆 |

### 7.2 閿洏杈撳叆

| 鎿嶄綔 | 鏀寔鎯呭喌 | 鎶€鏈満鍒?|
|------|---------|---------|
| 鍗曞瓧绗︽枃鏈緭鍏?| 鉁?瀹屾暣鏀寔 | `type_text`锛氶€愬瓧绗?`KeyDownEvent`+`KeyUpEvent`锛涘畼鏂归┍鍔ㄤ紭鍏堬紝InputSystem 娆′箣锛孶IToolkit 浜嬩欢鍏滃簳 |
| 鎵归噺鏂囨湰蹇€熷啓鍏?| 鉁?瀹屾暣鏀寔 | `type_text_fast`锛氱洿鎺ヨ祴鍊?`.value`锛涙敮鎸佺┖瀛楃涓?`""` |
| 鍔熻兘閿紙Enter/Escape/Delete/Tab/鏂瑰悜閿瓑锛?| 鉁?瀹屾暣鏀寔 | `press_key`锛氬畼鏂归┍鍔?鈫?InputSystem `PressKey` 鈫?`KeyDownEvent`+`KeyUpEvent` |
| 缁勫悎閿紙Ctrl+A/Ctrl+C/Ctrl+S/Ctrl+Z 绛夛級 | 鉁?瀹屾暣鏀寔 | `press_key_combination`锛氫緷娆″彂閫佷慨楗伴敭 `KeyDown`鈫掍富閿紙甯?`modifiers`锛夆啋 宸茬煡鍛戒护鐨?`ExecuteCommandEvent`鈫掍富閿?`KeyUp`鈫掍慨楗伴敭 `KeyUp` |
| UIToolkit 鍛戒护锛圕opy/Paste/SelectAll/Undo/Redo/Delete锛?| 鉁?瀹屾暣鏀寔 | `execute_command`锛歚ExecuteCommandEvent` 娲惧彂 |
| UIToolkit 鍛戒护楠岃瘉 | 鉁?瀹屾暣鏀寔 | `validate_command`锛歚ValidateCommandEvent` 娲惧彂 |
| 鐒︾偣鍒囨崲锛圱ab 瀵艰埅锛?| 鉂?涓嶆敮鎸?| V1 涓嶆敮鎸?Tab 鐒︾偣閾鹃亶鍘嗭紱鍙敤 `focus` 鍔ㄤ綔鐩存帴鑱氱劍鎸囧畾鍏冪礌 |
| IME/杈撳叆娉曠粍鍚堣緭鍏?| 鉂?涓嶆敮鎸?| InputSystem 娴嬭瘯 API 涓嶈鐩?IME 缁勫悎鎬?|
| 绯荤粺鍓创鏉跨湡瀹?Ctrl+V | 鉂?涓嶆敮鎸?| 闇€瑕佸钩鍙扮骇鍓创鏉?API锛涗娇鐢?`set_value` 鏇夸唬 |

---

## 8. 閫夋嫨鍣ㄨ娉曞畬鏁村弬鑰?
```yaml
# 鍩烘湰閫夋嫨鍣?"#button-id"               # 鎸?name/id锛堢瓑浠?VisualElement.name锛?".my-class"                # 鎸?USS 绫诲悕
"Button"                   # 鎸?UIToolkit 鎺т欢绫诲瀷鍚?"VisualElement"            # 鍩虹绫诲瀷锛堝尮閰嶆墍鏈夛級

# 缁勫悎閫夋嫨鍣?"#panel Label"             # 鍚庝唬锛堢┖鏍硷級
"#panel > Label"           # 鐩存帴瀛愬厓绱?"#panel > .item > Button"  # 澶氱骇 child

# 灞炴€ч€夋嫨鍣?"[name=my-button]"         # 灞炴€х瓑鍊?"[data-role=primary]"      # userData Dictionary 妗ユ帴
"[data-testid=foo]"        # 鑷畾涔夋祴璇?ID

# 浼被
"Button:hover"             # 浼被锛堟敮鎸佽寖鍥存湁闄愶級
```

**閫夋嫨鍣ㄤ紭鍏堢骇**锛堟鏋跺唴閮級锛?1. 蹇€熻矾寰勶細`#id`锛圲Query `.Q<T>(name)`锛夈€乣.class`锛圲Query `.Q<T>(className:`...`)`锛?2. 鍏ㄦ爲閬嶅巻锛氱被鍨?+ 灞炴€х粍鍚?3. 娴眰闈㈡澘鎼滅储锛歚FloatingPanelLocator` 鏋氫妇鎵€鏈?panel锛堢敤浜庤彍鍗?popup 鍐呭厓绱狅級

---

## 9. IMGUI 鑷姩鍖栨敮鎸?
> IMGUI锛圛mmediate Mode GUI锛夎嚜鍔ㄥ寲宸蹭綔涓虹嫭绔嬪瓙绯荤粺瀹炵幇锛岃缁嗚鐩栬寖鍥淬€佸姩浣滃垪琛ㄣ€侀€夋嫨鍣ㄨ娉曘€佽璁″師鐞嗕笌闄愬埗璇存槑锛岃鍙傝銆奍MGUI 鎺т欢鑷姩鍖栬鐩栦笌闄愬埗璇存槑.md銆嬨€?
IMGUI 鍔ㄤ綔锛坄imgui_*`锛変笌 UIToolkit 鍔ㄤ綔鍙湪鍚屼竴 YAML 涓贩鐢ㄣ€傜畝瑕佷俊鎭細

- **15 涓?IMGUI 鍔ㄤ綔**锛歚imgui_click`銆乣imgui_double_click`銆乣imgui_right_click`銆乣imgui_hover`銆乣imgui_type`銆乣imgui_focus`銆乣imgui_scroll`銆乣imgui_select_option`銆乣imgui_press_key`銆乣imgui_press_key_combination`銆乣imgui_read_value`銆乣imgui_assert_text`銆乣imgui_assert_visible`銆乣imgui_assert_value`銆乣imgui_wait`銆?- **閫夋嫨鍣ㄨ娉?*锛歚gui(button)`銆乣gui(textfield, control_name="xxx")`銆乣gui(group="Settings" > button, text="Apply")`銆乣gui(focused)`銆?- **楠屾敹鍩虹嚎**锛歚Samples~/Yaml/99-imgui-example.yaml`銆乣98-imgui-advanced.yaml`銆乣97-imgui-negative-assert.yaml` 绛?8 浠界敤渚嬪凡鍏ㄩ儴閫氳繃楠岃瘉銆?
---

## 10. 鎸変氦浜掔被鍨嬬殑瑕嗙洊鎬荤粨

| 浜や簰绫诲瀷 | V1 瑕嗙洊鑼冨洿 | 鏈鐩栬寖鍥?|
| --- | --- | --- |
| 鍗曞嚮 | 鎵€鏈夊彲鐐瑰嚮鎺т欢锛涜彍鍗曢」閫氳繃 `select_context_menu_item`/`select_popup_menu_item`/`menu_item` 涓撶敤鍔ㄤ綔锛沗com.unity.ui 2.0.0` 涓嬭嚜鍔ㄩ檷绾у埌 `DispatchClick`+浜旂骇 fallback | 鐙珛娴獥涓殑閫氱敤閫夋嫨鍣ㄧ洿鎺ョ偣鍑?|
| 鍙屽嚮 | 鎵€鏈夊彲鐐瑰嚮鎺т欢 | 鏃?|
| 鎮仠 | 鎵€鏈夊彲瑙佹帶浠?| Tooltip 鍙娓叉煋楠岃瘉 |
| 鎷栨嫿 | 浠绘剰涓ゅ厓绱犻棿锛沗TwoPaneSplitView` 鍒嗗壊鏉★紱`Scroller` thumb锛沗drag_reorder` 鍒楄〃閲嶆帓 | 璺ㄧ獥鍙ｆ嫋鎷姐€佹枃浠舵嫋鏀?|
| 婊氬姩 | `ScrollView`銆乣ListView`銆乣TreeView`銆乣Scroller` | 鑷畾涔夊惛闄勬粴鍔ㄩ珮绾ц涓?|
| 鏂囨湰杈撳叆锛堢湡瀹為敭鐩橈級 | `TextField` 鍙婃墍鏈夊瓙绫?| IME 缁勫悎杈撳叆 |
| 蹇€熷啓鍊?| 鎵€鏈?`BaseField<T>` 瀛愮被鐨勫瓧绗︿覆/鏁板€?鏋氫妇/鍚戦噺/鐭╁舰/杈圭晫/棰滆壊/鍝堝笇/CurveField/GradientField/ObjectField | 瀵硅薄閫夋嫨鍣ㄦ诞绐椼€佹洸绾?娓愬彉缂栬緫鍣ㄦ诞绐?|
| 閫夋嫨锛堜笅鎷?鍗曢€夛級 | `DropdownField`銆乣PopupField<string>`銆乣EnumField`銆乣EnumFlagsField`銆乣RadioButtonGroup`銆乣MaskField`銆乣LayerMaskField`銆乣TagField`銆乣LayerField` | 寮瑰嚭闈㈡澘閫愰」浜や簰 |
| 鍒楄〃閫夋嫨 | `ListView`銆乣MultiColumnListView`锛堝崟閫?澶氶€夛級 | 鏃?|
| 鏍戦€夋嫨 | `TreeView`銆乣MultiColumnTreeView` | 灞曞紑/鎶樺彔鍔ㄧ敾鍐呴儴鐘舵€?|
| 鍒楁帓搴?| `MultiColumnListView`銆乣MultiColumnTreeView`锛坄sort_column`锛?| `ColumnSortingMode.Custom` 鐨勮嚜瀹氫箟鎺掑簭鍥炶皟 |
| 鍒楀璋冩暣 | `MultiColumnListView`銆乣MultiColumnTreeView`锛坄resize_column`锛?| 鏃?|
| Split 鎷栨嫿 | `TwoPaneSplitView`锛堝垎鍓叉潯閿氱偣 `drag` + `set_split_view_size`锛?| 澶嶆潅璺ㄧ獥鍙?split 鑱斿姩 |
| Scroller 璧嬪€?| `Scroller`锛坄set_value`銆乣page_scroller`銆乣drag_scroller`锛?| 鏃?|
| 鎶樺彔/灞曞紑 | `Foldout` | 鏃?|
| 婊戝潡 | `Slider`銆乣SliderInt`銆乣MinMaxSlider` | 鏃?|
| Tab 鍒囨崲 | `TabView`锛坄select_tab`銆乣close_tab`锛?| 鏃?|
| Toolbar 鎺т欢 | `ToolbarButton`銆乣ToolbarToggle`銆乣ToolbarSearchField`銆乣ToolbarMenu`锛堝畼鏂?`PopupMenuSimulator`+`DropdownMenu` fallback锛夈€乣ToolbarPopupSearchField`锛堣緭鍏ユ湰浣擄級銆乣ToolbarBreadcrumbs`锛坄navigate_breadcrumb`銆乣read_breadcrumbs`锛?| `ToolbarPopupSearchField` 寮瑰嚭缁撴灉鍒楄〃 |
| 鎸夐敭 | 浠绘剰鑾风劍鍏冪礌锛沗press_key`銆乣press_key_combination`銆乣type_text`銆乣type_text_fast`銆佹寚閽堢被 `modifiers` 缁勫悎 | IME銆佺郴缁熷壀璐存澘鐪熷疄绮樿创 |
| 鐒︾偣 | 鎵€鏈夊彲鑱氱劍鍏冪礌锛坄focus` 鍔ㄤ綔锛?| 鐒︾偣閾惧鑸紙Tab 鍒囨崲鐒︾偣锛?|
| 鏂█ | 鍙鎬с€佹枃鏈€乿alue銆乸roperty銆乪nabled/disabled銆乥ound value | 瑙嗚鍍忕礌瀵规瘮 |
| 鎴浘 | 褰撳墠绐楀彛 | 澶氱獥鍙ｆ埅鍥?|
| 绛夊緟 | `wait`锛堝浐瀹氭椂闀匡級銆乣wait_for_element`锛堣疆璇㈢瓑寰咃紝鏈€闀?600s锛?| 鏃?|

---

## 11. 涓嶈兘瀹炵幇鎴栧綋鍓嶅彈 Unity 鎺ュ彛杈圭晫闃绘柇

| 椤圭洰 | 褰撳墠闃绘柇鍘熷洜 | 缁撹 |
| --- | --- | --- |
| `IMGUIContainer` 鍐呴儴鎺т欢绾ц嚜鍔ㄥ寲 | IMGUI 鍐呭涓嶈繘鍏?UIToolkit VisualTree | 灞炰簬 Unity 鎶€鏈竟鐣?|
| `ProjectSettingsProvider`锛堥」鐩缃潰鏉匡級 | 褰撳墠瀹炵幇浣跨敤 `EditorGUILayout`锛圛MGUI锛?| 灞炰簬 Unity 鎶€鏈竟鐣?|
| `ColorField` Color Picker / Eye Dropper | 鐙珛缂栬緫鍣ㄧ獥鍙?| 灞炰簬 Unity 绐楀彛杈圭晫 |
| `CurveField` 鏇茬嚎缂栬緫鍣ㄦ诞绐?| 鐙珛缂栬緫鍣ㄧ獥鍙?| 灞炰簬 Unity 绐楀彛杈圭晫 |
| `GradientField` 娓愬彉缂栬緫鍣ㄦ诞绐?| 鐙珛缂栬緫鍣ㄧ獥鍙?| 灞炰簬 Unity 绐楀彛杈圭晫 |
| `ObjectField` Object Picker | 璧勪骇閫夋嫨瀵硅瘽妗嗕笉鍦ㄥ綋鍓嶇獥鍙ｆ爲鍐?| 灞炰簬 Unity 绐楀彛杈圭晫 |
| `ObjectField` 鐪熷疄 DragAndDrop | 渚濊禆 Editor `DragAndDrop` 鐢熷懡鍛ㄦ湡 | 灞炰簬 Unity DragAndDrop 杈圭晫 |
| `ToolbarPopupSearchField` 缁撴灉鍒楄〃 | 缁撴灉闈㈡澘涓嶅湪琚祴绐楀彛鏍戝唴 | 褰撳墠鍙?Unity/鍖呮帴鍙ｈ竟鐣岄樆鏂?|
| Tooltip 鍙娓叉煋鏂█ | Tooltip 鐢?Editor 鍏ㄥ眬绠＄悊 | 灞炰簬 Unity 鍏ㄥ眬 UI 杈圭晫 |
| IME 缁勫悎杈撳叆 | InputSystem 娴嬭瘯 API 涓嶈鐩?IME 缁勫悎鎬?| 灞炰簬 Unity/InputSystem 鑳藉姏杈圭晫 |
| 绯荤粺鍓创鏉跨湡瀹炵矘璐?| 闇€瑕佺郴缁熷壀璐存澘+骞冲彴绾у揩鎹烽敭閾捐矾 | 灞炰簬绯荤粺/骞冲彴杈圭晫 |
| 澶氱獥鍙ｅ崗鍚屾嫋鎷?鏂█ | 褰撳墠娴嬭瘯妯″瀷鎸夊崟瀹夸富绐楀彛缁勭粐 | 褰撳墠鍙楁鏋惰璁′笌 Unity 澶氱獥鍙ｈ竟鐣屽叡鍚岄檺鍒?|
| 鍍忕礌绾ц瑙?diff | 鏃犲唴寤鸿瑙夊熀绾夸笌宸紓鍒嗘瀽閾捐矾 | 褰撳墠鏈帴鍏?|

---

## 12. 宸ョ▼涓婂彲瀹炵幇浣嗗綋鍓嶅皻鏈惤鍦扮殑鍔熻兘

| 浼樺厛绾?| 鍔熻兘 | 闅惧害 | 璇存槑 |
| --- | --- | --- | --- |
| P2 | `ToolbarMenu` 閫氱敤閫夋嫨鍣ㄦ敮鎸佹诞绐楄彍鍗曢」 | 涓?| 闇€瑕佽妗嗘灦鎰熺煡骞堕亶鍘嗘诞绐?VisualTree |
| P4 | `ColorField`/`CurveField`/`GradientField` 缂栬緫鍣ㄦ诞绐椾氦浜?| 涓瓇楂?| 鐙珛 `EditorWindow`锛沗set_value`/`assert_value` 鐩村啓宸茶鐩栦富璺緞 |
| P4 | `ToolbarPopupSearchField` 寮瑰嚭缁撴灉鍒楄〃椤归€夋嫨 | 楂?| 瀹樻柟鏈毚闇茬ǔ瀹氱粨鏋滈」閬嶅巻/閫夋嫨 API |
| P5 | `ObjectField` 鐪熷疄 DragAndDrop | 楂?| 闇€妯℃嫙瀹屾暣 `DragAndDrop` 鐢熷懡鍛ㄦ湡 |
| P5 | 绯荤粺鍓创鏉挎搷浣滐紙鐪熷疄 Ctrl+C/V锛?| 楂?| 闇€瑕佸钩鍙扮骇鍓创鏉?API锛岃法骞冲彴鍏煎鎬у鏉?|
| P5 | 澶氱獥鍙ｅ崗鍚屾祴璇?| 楂?| 闇€閲嶆瀯 `UnityUIFlowSimulationSession` 鍜岀獥鍙ｇ鐞嗛€昏緫 |
| P5 | 瑙嗚鍍忕礌瀵规瘮鏂█ | 楂?| 闇€瑕佸紩鍏ュ浘鍍忓鐞嗗簱骞跺缓绔嬪熀绾垮浘鐗囩鐞嗘祦绋?|
| P6 | IME 缁勫悎杈撳叆 | 鏋侀珮 | 闇€瑕佹搷浣滅郴缁熺骇杈撳叆娉曟ā鎷燂紝瓒呭嚭 Unity 鑼冨洿 |

---

## 13. 淇鍘嗗彶

### 2026-04-23 2.0.0

- 鍩轰簬浠ｇ爜搴撳畬鏁村鏍革紙鎵€鏈?Action 绫汇€丗ixture 鍩虹被銆丄ctionHelpers銆両mguiActions锛夐噸鍐欐枃妗ｃ€?- 鏂板 搂2銆屼綍鏃剁敤 YAML 娴嬭瘯鐢ㄤ緥锛屼綍鏃剁敤 C# 浠ｇ爜娴嬭瘯鐢ㄤ緥銆嶁€斺€斿寘鍚紭鍏堥€夋嫨 YAML 鐨勫満鏅竻鍗曘€佸繀椤荤敤 C# 鐨勫満鏅竻鍗曘€佷互鍙?YAML+C# 娣峰悎妯″紡璇存槑銆?- 鏂板 搂6銆屽叏閮ㄥ唴缃姩浣滆缁嗚鏄庛€嶁€斺€旀寜榧犳爣銆侀敭鐩樸€佸€艰祴鍊笺€佹柇瑷€銆佸伐鍏?鎺у埗娴佷簲绫伙紝閫愬姩浣滃垪鍑哄叧閿弬鏁般€佸簳灞傛満鍒躲€乊AML 绀轰緥銆?- 鏂板 搂7銆岃緭鍏ユā鎷熸敮鎸佽鎯呫€嶁€斺€旈€愭潯鍒楀嚭榧犳爣/鎸囬拡鍜岄敭鐩樿緭鍏ョ殑鏀寔鎯呭喌鍙婂簳灞傛妧鏈満鍒躲€?- 鏂板 搂8銆岄€夋嫨鍣ㄨ娉曞畬鏁村弬鑰冦€嶁€斺€旇ˉ鍏呭睘鎬ч€夋嫨鍣ㄣ€乣[data-*]` 鐢ㄦ硶銆?- 鏇存柊 搂3锛堝叏闈㈡敮鎸佹帶浠讹級锛氳ˉ鍏?`assert_disabled` 鍔ㄤ綔瑕嗙洊锛涚‘璁?YAML 鍩虹嚎 115 浠姐€?- 鏇存柊 搂4锛堝眬閮ㄦ敮鎸佹帶浠讹級锛氫慨姝?`ColorField` `set_value` 鏀寔 `#RRGGBBAA` 鍗佸叚杩涘埗鏍煎紡鐨勮鏄庛€?
### 2026-04-21 1.8.0 绗笁杞墿灞曚慨璁?
- 鏂板 13 浠?YAML锛屽叏閲忓浠朵粠 102 浠芥墿灞曞埌 115 浠姐€?- 鏂板 `imgui_wait` 鍔ㄤ綔锛堝搴?IMGUI 鏂囨。鍚屾鏇存柊锛夈€?- 鏂板 `_91-negative-set-value-invalid.yaml` 绛夎礋鍚戞祴璇曘€?
### 2026-04-21 1.7.0 澶ц妯℃墿灞曚慨璁?
- 鏂板 31 浠?YAML锛屽叏閲忓浠朵粠 71 浠芥墿灞曞埌 102 浠姐€?- 鏇夸唬鍙傛暟瑕嗙洊锛? 浠斤級銆佸姛鑳藉寮鸿鐩栥€佽礋鍚戞祴璇曟墿灞曪紙23 浠斤級銆?
### 2026-04-21 1.6.0 鍏ㄩ噺楠岃瘉涓庝慨澶嶄慨璁?
- 鍏ㄩ噺楠岃瘉 71 浠?YAML锛涗慨澶?Empty String 鍙傛暟銆乣AssertionFailed` 鐘舵€佹槧灏勩€佺紪璇戠洃鎺х瓑 4 涓棶棰樸€?- IMGUI 鏂囨。鐙珛涓恒€奍MGUI 鎺т欢鑷姩鍖栬鐩栦笌闄愬埗璇存槑.md銆嬨€?
### 2026-04-16 1.5.0

- 閫傞厤 `com.unity.ui@2.0.0`锛歚DispatchClick`銆佷簲绾?Button fallback銆乣DropdownMenu` reflection fallback銆?- 鎵€鏈夊巻鍙插緟寮€鍙戦」锛圥1/P2锛夋爣璁颁负宸插畬鎴愩€?
### 2026-04-13 / 2026-04-12

- `PropertyField`/`InspectorElement` 鎻愬崌涓哄眬閮ㄦ敮鎸併€?- `sort_column`/`resize_column`/`close_tab`/`press_key_combination`/`read_breadcrumbs`/`drag_scroller`/`menu_item` 绛夊姩浣滃叏閮ㄦ爣璁颁负宸插畬鎴愩€?