# UnityPilot MCP 澧炲己鍔熻兘楠屾敹娴嬭瘯鐢ㄤ緥

> 浣跨敤 MCP 宸ュ叿閫愰」鎵ц浠ヤ笅娴嬭瘯銆傛瘡涓祴璇曟爣娉ㄩ鏈熺粨鏋滃拰鍒ゅ畾鏍囧噯銆?
---

## 楠屾敹娴佺▼绾﹀畾锛堝啓鐩樹笌鍚屾锛?
浠ヤ笅涓夋潯涓?*鏁存楠屾敹鐨勫浐瀹氭敹灏?鍓嶇疆姝ラ**锛屼笌鍏蜂綋鐢ㄤ緥缂栧彿鏃犲叧锛?
1. **鎵归噺鏀硅剼鏈師鍒欙紙Cursor / IDE 鐩存帴鏀瑰伐绋嬪唴鏂囦欢鏃讹級**  
   涓嶈鍦ㄦ瘡淇濆瓨涓€涓枃浠跺悗灏辫皟 MCP銆傚簲锛?*绛夋湰杞墍鏈夎剼鏈殑鏂板缓銆佷慨鏀瑰叏閮ㄥ畬鎴愬苟宸蹭繚瀛樺埌纾佺洏涔嬪悗**锛屽啀**寮哄埗璋冪敤涓€娆?*锛堟暣鎵逛竴娆″嵆鍙級锛?   - `unity_sync_after_disk_write(delayS=2, triggerCompile=true)`锛屾垨
   - `unity_sync_after_disk_write(delayS=2, triggerCompile=false)` 鍐嶆寜闇€ `unity_compile()`銆? 
   鐒跺悗鍐嶈皟鐢?`unity_compile_wait(timeoutS=120)` 鐩村埌 `status=ready`锛堣嫢涓婁竴姝ュ凡 `triggerCompile=true` 涓旂紪璇戝凡缁撴潫锛屼粛寤鸿 `compile_wait` 纭绋冲畾锛夈€? 
   鏈蛋 `script.update` 绛?Bridge 鍐欐枃浠惰矾寰勬椂锛?*渚濊禆缂栬瘧缁撴灉鐨勫悗缁?MCP 姝ラ**蹇呴』鏀惧湪涓婅堪鏁存壒鍚屾涔嬪悗銆?
2. **楠屾敹浼氳瘽杈圭晫**锛堟垨寮€濮嬩笅涓€鎵逛緷璧栫紪璇戠殑鐢ㄤ緥鍓嶏級锛氬悓鏍烽噰鐢ㄣ€屾湰鎵规枃浠舵敼鍔ㄥ叏閮ㄧ粨鏉?鈫?**涓€娆?* `unity_sync_after_disk_write` 鈫?`unity_compile_wait`銆嶏紱涔熷彲鐢?`unity_ensure_ready` 浣滀负鍚堝苟妫€鏌ャ€?
3. **鑷姩淇寰幆锛圓utoFix锛?*鍦ㄦ瘡娆¤ˉ涓佹垚鍔熻惤鐩樺悗锛屽凡鐢辨湇鍔＄鑷姩鎵ц锛氱瓑寰?**2 绉?* 鈫?`asset.refresh` 鈫?**寮哄埗缂栬瘧**锛坄compile.request`锛夛紝鏃犻渶鎵嬪伐閲嶅锛涜嫢鍚屾澶辫触浼氬湪鏃ュ織涓憡璀︼紝涓嬩竴杞粛浼氫互 `compile.request` 寮€澶淬€?
**宸ュ叿璇存槑锛?* `unity_sync_after_disk_write` 榛樿 `delayS=2` 鐢ㄤ簬缂撹В鎿嶄綔绯荤粺/纾佺洏鍐欏欢杩燂紱`triggerCompile=false` 鏃朵粎鍒锋柊璧勬簮鏁版嵁搴擄紱闇€瑕佽剼鏈噸缂栬瘧鏃惰 `triggerCompile=true`銆備笌銆屾瘡鏂囦欢涓€璋冦€嶇浉姣旓紝**鎸夋枃浠舵壒缁撴潫鍚庣粺涓€璋冪敤**鍙噺灏戦噸澶嶇紪璇戝苟鏇寸鍚堢鐩樿惤鐩樻椂搴忋€?
### T-SYNC-01: 鍐欑洏鍚庡悓姝?+ 绛夊緟缂栬瘧锛堟壒澶勭悊锛?```
姝ラ:
1. 鍦?IDE 涓畬鎴愭湰杞墍鏈?Assets 涓嬭剼鏈殑鏂板缓/淇敼锛屽苟鍏ㄩ儴淇濆瓨锛堜笉瑕佹瘡瀛樹竴涓枃浠跺氨鎵ц涓嬮潰涓ゆ锛?2. 璋冪敤 unity_sync_after_disk_write(delayS=2, triggerCompile=true)
3. 璋冪敤 unity_compile_wait(timeoutS=120)
```
**棰勬湡:** 姝ラ2 `ok=true`锛宲ayload 鍚?`refreshed=true`锛涜嫢缂栬瘧鎴愬姛鍚?`compiled=true` 涓?compile 鎽樿锛涙楠? `status=ready`
**鍒ゅ畾:** 鏃犻渶鍒囨崲 Unity 鐒︾偣鍗冲彲鍦ㄦ楠?鈥?鍐呭畬鎴愬鍏ヤ笌缂栬瘧锛涗笖姝ラ2鈥?涓?*鏈疆浠呮涓€娆?*鐨勫己鍒跺悓姝?
### 缂栬瘧鐘舵€佸悓姝ワ紙瀹炵幇璇存槑锛?
- **Bridge 鎺ㄩ€?*锛歚compile.started` / `compile.finished`锛坄unity_compile` / `compile.request` 鍙戣捣鏃讹級銆乣compile.pipeline.started` / `compile.pipeline.finished`锛堜换鎰忚剼鏈紪璇戯紝鍚湪缂栬緫鍣ㄥ唴瑙﹀彂鐨勭紪璇戯級锛涗粛淇濈暀鍘熸湁 `compile.status` / `compile.errors`銆?- **Python**锛氱淮鎶?`_compile_idle_event`锛屼笂杩颁簨浠朵細鏇存柊 `StateStore` 涓?idle 淇″彿锛沗unity_compile_wait` 榛樿鍏?`wait_for_compile_idle`锛堢煭绐楀彛锛夛紝鍐嶄互**鎸囨暟閫€閬?*杞 `resource.editorState`銆?- **鍗曞懡浠ら樆濉?*锛歚unity_compile_wait_editor` 鈫?Bridge `compile.wait`锛屽湪缂栬緫鍣ㄤ富绾跨▼杞鐩磋嚦 `EditorApplication.isCompiling == false`銆?
### T-COMPILE-01: unity_compile_wait锛堜簨浠?+ 閫€閬匡級
```
璋冪敤: unity_compile_wait(timeoutS=60, pollIntervalS=1, preferEvents=true)
锛堝彲鍦ㄨЕ鍙戠紪璇戝悗璋冪敤锛?```
**棰勬湡:** `status=ready`锛宍waitMode` 涓?`immediate`銆乣event`銆乣event+poll` 鎴?`poll` 涔嬩竴  
**鍒ゅ畾:** 闈?`timeout`锛沗preferEvents=false` 鏃朵粎杞锛屼粛搴?`ready`锛堣嫢缂栬瘧宸茬粨鏉燂級

### T-COMPILE-02: unity_compile_wait_editor
```
姝ラ:
1. unity_compile() 鎴栦繚瀛樿剼鏈Е鍙戠紪璇?2. unity_compile_wait_editor(timeoutMs=120000)
```
**棰勬湡:** `ok=true`锛宲ayload 鍚?`state=compile_idle` 鎴栫瓑浠锋垚鍔熷瓧娈? 
**鍒ゅ畾:** 涓?`EditorApplication.isCompiling` 涓€鑷?
### T-COMPILE-03: 浜嬩欢鍚嶇О锛堣皟璇曪級
鍦?Bridge / MCP 鏃ュ織涓彲瑙傚療鍒帮細`compile.pipeline.started` 鈫?鈥?鈫?`compile.pipeline.finished`锛屼互鍙?MCP 瑙﹀彂鐨?`compile.started` / `compile.finished`锛堥『搴忎互瀹為檯涓哄噯锛夈€?
---

## P2: unity_csharp_execute 寮曠敤淇

### T-P2-01: 鍩虹 return 璇彞
```
璋冪敤: unity_csharp_execute(code="return (1+2).ToString();")
```
**棰勬湡:** `ok=true`, `status="completed"`, `result="3"`
**鍒ゅ畾:** 涓嶅啀鍑虹幇 CS0012 / netstandard 鏈紩鐢ㄩ敊璇?
### T-P2-02: 浣跨敤 System.Linq
```
璋冪敤: unity_csharp_execute(code="var list = new System.Collections.Generic.List<int>{1,2,3}; return list.Where(x=>x>1).Count().ToString();")
```
**棰勬湡:** `ok=true`, `result="2"`
**鍒ゅ畾:** System.Core / System.Linq 寮曠敤姝ｅ父

### T-P2-03: 浣跨敤 UnityEngine API
```
璋冪敤: unity_csharp_execute(code="return UnityEngine.Application.unityVersion;")
```
**棰勬湡:** `ok=true`, `result` 鍖呭惈 Unity 鐗堟湰鍙峰瓧绗︿覆

### T-P2-04: 浣跨敤 UnityEditor API
```
璋冪敤: unity_csharp_execute(code="return UnityEditor.EditorApplication.isPlaying.ToString();")
```
**棰勬湡:** `ok=true`, `result="False"` (鍦?Edit 妯″紡涓?

### T-P2-05: 瀹夊叏娌欑浠嶆湁鏁?```
璋冪敤: unity_csharp_execute(code="System.Diagnostics.Process.Start(\"notepad\"); return \"done\";")
```
**棰勬湡:** `ok=false`, 閿欒鍖呭惈 `SECURITY_VIOLATION`

---

## P3: unity_console_get_logs 澧炲己

### T-P3-01: 鏈夋棩蹇楁椂鑳借繑鍥炲唴瀹?```
姝ラ:
1. 璋冪敤 unity_csharp_execute(code="UnityEngine.Debug.Log(\"MCP_TEST_LOG_12345\"); return \"ok\";")
2. 绛夊緟 1 绉?3. 璋冪敤 unity_console_get_logs(count=10)
```
**棰勬湡:** `ok=true`, `logs` 鏁扮粍闈炵┖, 鑷冲皯涓€鏉?message 鍖呭惈 `MCP_TEST_LOG_12345`

### T-P3-02: 鎸夌被鍨嬭繃婊?```
姝ラ:
1. 璋冪敤 unity_csharp_execute(code="UnityEngine.Debug.LogWarning(\"MCP_WARN_TEST\"); return \"ok\";")
2. 璋冪敤 unity_console_get_logs(logType="Warning", count=10)
```
**棰勬湡:** 杩斿洖鐨?logs 涓?logType 鍧囦负 "Warning"

### T-P3-03: 娓呯┖鍚庡啀璇诲彇
```
姝ラ:
1. 璋冪敤 unity_console_clear()
2. 璋冪敤 unity_console_get_logs(count=10)
```
**棰勬湡:** 娓呯┖鍚庨€氳繃 ring buffer 璇诲彇鐨勬棩蹇椾负娓呯┖鍓嶏紙ring buffer 涓嶅彈 console clear 褰卞搷锛夛紝
閫氳繃 reflection 璇诲彇鐨勬棩蹇椾负绌恒€倀otal >= 0锛屼笉鎶ラ敊

---

## P1: 绐楀彛瀹氫綅澧炲己

### T-P1-01: 鍒楀嚭鎵€鏈夌獥鍙?```
璋冪敤: unity_editor_windows_list()
```
**棰勬湡:** `ok=true`, `windows` 鏁扮粍鍖呭惈澶氫釜鏉＄洰锛屾瘡涓湁 `instanceId`, `typeName`, `fullTypeName`, `title`, `posX`, `posY`, `width`, `height`, `hasUIToolkit` 瀛楁

### T-P1-02: 鎸夌被鍨嬭繃婊?```
璋冪敤: unity_editor_windows_list(typeFilter="Inspector")
```
**棰勬湡:** 杩斿洖鐨?windows 涓?typeName 鎴?fullTypeName 鍖呭惈 "Inspector"

### T-P1-03: 鎸夋爣棰樿繃婊?```
璋冪敤: unity_editor_windows_list(titleFilter="Scene")
```
**棰勬湡:** 杩斿洖鐨?windows 鏍囬涓寘鍚?"Scene"

### T-P1-04: 閫氳繃绐楀彛鏍囬瀹氫綅 鈥?mouse_event
```
姝ラ:
1. 璋冪敤 unity_editor_windows_list() 鑾峰彇浠绘剰绐楀彛鏍囬锛堝 "Inspector"锛?2. 璋冪敤 unity_mouse_event(action="click", button="left", x=100, y=50, targetWindow="Inspector")
```
**棰勬湡:** `ok=true`, 涓嶅嚭鐜?`WINDOW_NOT_AVAILABLE`

### T-P1-05: 閫氳繃 instanceId 瀹氫綅
```
姝ラ:
1. 璋冪敤 unity_editor_windows_list() 鑾峰彇鏌愮獥鍙?instanceId锛堝亣璁句负 12345锛?2. 璋冪敤 unity_mouse_event(action="click", button="left", x=50, y=50, targetWindow="12345")
```
**棰勬湡:** `ok=true`

### T-P1-06: 閫氳繃瀹屽叏闄愬畾绫诲瀷鍚嶅畾浣?```
姝ラ:
1. 璋冪敤 unity_editor_windows_list() 鑾峰彇鏌愮獥鍙?fullTypeName
2. 鐢ㄨ fullTypeName 璋冪敤 unity_uitoolkit_dump(targetWindow=<fullTypeName>)
```
**棰勬湡:** `ok=true`锛堝鏋滅獥鍙ｆ湁 UIToolkit 鍐呭锛?
### T-P1-07: UIToolkit 绐楀彛鑷姩璺敱榧犳爣浜嬩欢
```
姝ラ:
1. 璋冪敤 unity_editor_windows_list() 鎵惧埌 hasUIToolkit=true 鐨勭獥鍙?2. 璋冪敤 unity_mouse_event(action="click", button="left", x=100, y=100, targetWindow=<璇ョ獥鍙ypeName>)
```
**棰勬湡:** `ok=true`, state 鍖呭惈 `:uitoolkit` 鍚庣紑锛堣〃绀鸿蛋浜?UIToolkit 鍚堟垚璺緞锛?
---

## P5: 閿洏浜嬩欢 UIToolkit 鍏煎

### T-P5-01: UIToolkit 绐楀彛閿洏浜嬩欢
```
姝ラ:
1. 璋冪敤 unity_editor_windows_list() 鎵惧埌 hasUIToolkit=true 鐨勭獥鍙?2. 璋冪敤 unity_keyboard_event(action="keypress", targetWindow=<绐楀彛鍚?, keyCode="Space")
```
**棰勬湡:** `ok=true`, state 鍖呭惈 `:uitoolkit` 鍚庣紑

### T-P5-02: IMGUI 绐楀彛閿洏浜嬩欢锛堝洖閫€璺緞锛?```
璋冪敤: unity_keyboard_event(action="keypress", targetWindow="game", keyCode="Space")
```
**棰勬湡:** `ok=true`, state 涓嶅寘鍚?`:uitoolkit`锛堣蛋 IMGUI 璺緞锛?
### T-P5-03: type 鍔ㄤ綔杈撳叆鏂囨湰
```
璋冪敤: unity_keyboard_event(action="type", targetWindow="inspector", text="hello")
```
**棰勬湡:** `ok=true`

---

## P4: ScrollView 婊氬姩 API

### T-P4-01: 缁濆婊氬姩
```
姝ラ:
1. 璋冪敤 unity_editor_windows_list() 鎵惧埌鏈?UIToolkit 鍐呭鐨勭獥鍙?2. 璋冪敤 unity_uitoolkit_scroll(targetWindow=<绐楀彛鍚?, scrollToX=0, scrollToY=200, mode="absolute")
```
**棰勬湡:** `ok=true`, `scrollOffsetY` 绾︾瓑浜?200

### T-P4-02: 澧為噺婊氬姩
```
璋冪敤: unity_uitoolkit_scroll(targetWindow="inspector", deltaX=0, deltaY=100, mode="delta")
```
**棰勬湡:** `ok=true`, 杩斿洖鏂扮殑 `scrollOffsetX` 鍜?`scrollOffsetY`

### T-P4-03: 鏃?ScrollView 鏃朵紭闆呭け璐?```
姝ラ:
1. 閫夋嫨涓€涓‘璁ゆ病鏈?ScrollView 鐨勭獥鍙?2. 璋冪敤 unity_uitoolkit_scroll(targetWindow=<绐楀彛鍚?, scrollToY=100)
```
**棰勬湡:** `ok=false`, state 鍖呭惈 `SCROLLVIEW_NOT_FOUND`

---

## P0: 鍩熼噸杞借繛鎺ユ仮澶?
### T-P0-01: 缂栬瘧鍚庝繚鎸佽繛鎺?```
姝ラ:
1. 璋冪敤 unity_mcp_status() 纭 connected=true
2. 璋冪敤 unity_compile()
3. 绛夊緟杩斿洖缁撴灉锛堝彲鑳介渶瑕?30-60 绉掞紝鍖呮嫭鍩熼噸杞斤級
4. 璋冪敤 unity_mcp_status() 鍐嶆纭
```
**棰勬湡:**
- unity_compile 杩斿洖 `ok=true` 鎴栧寘鍚?`reconnected=true` 鐨勬垚鍔熺粨鏋?- 涓嶅啀鍑虹幇 `CONNECTION_LOST` 閿欒
- 鏈€缁?unity_mcp_status 鏄剧ず `connected=true`

### T-P0-02: 缂栬瘧鍚庤兘绔嬪嵆鎵ц鍚庣画鍛戒护
```
姝ラ:
1. 璋冪敤 unity_compile()
2. 绛夊緟瀹屾垚
3. 绔嬪嵆璋冪敤 unity_editor_state()
```
**棰勬湡:** unity_editor_state 杩斿洖 `ok=true`, `connected=true`

### T-P0-03: PlayMode 杩涘叆鍚庝繚鎸佽繛鎺?```
姝ラ:
1. 璋冪敤 unity_playmode_start()
2. 绛夊緟杩斿洖
3. 璋冪敤 unity_mcp_status()
4. 璋冪敤 unity_playmode_stop()
```
**棰勬湡:**
- playmode_start 杩斿洖 `ok=true`锛堝彲鑳藉惈 `reconnected=true`锛?- mcp_status 鏄剧ず connected=true
- playmode_stop 杩斿洖 `ok=true`

### T-P0-04: 缂栬瘧閿欒鍚庝篃鑳芥仮澶?```
姝ラ:
1. 璋冪敤 unity_script_create(scriptPath="Assets/Editor/TempBrokenScript.cs", content="public class Broken {")
2. 璋冪敤 unity_compile()
3. 绛夊緟缁撴灉
4. 璋冪敤 unity_compile_errors()
5. 璋冪敤 unity_script_delete(scriptPath="Assets/Editor/TempBrokenScript.cs")
6. 璋冪敤 unity_compile()
```
**棰勬湡:**
- 绗竴娆?compile 鍚?compile_errors 杩斿洖閿欒鍒楄〃
- 鍒犻櫎鑴氭湰鍚庣浜屾 compile 鎴愬姛锛宔rrors=0
- 鍏ㄧ▼涓嶅嚭鐜?CONNECTION_LOST

---

## 缁煎悎娴嬭瘯

### T-INT-01: 瀹屾暣宸ヤ綔娴侊紙绐楀彛鍒楄〃 鈫?瀹氫綅 鈫?榧犳爣鐐瑰嚮 鈫?閿洏杈撳叆锛?```
姝ラ:
1. unity_editor_windows_list()
2. 浠庣粨鏋滀腑鎵惧埌 Inspector 绐楀彛
3. unity_mouse_event(action="click", button="left", x=200, y=100, targetWindow="inspector")
4. unity_keyboard_event(action="type", targetWindow="inspector", text="TestValue")
```
**棰勬湡:** 鍏ㄩ儴 ok=true锛屾棤 WINDOW_NOT_AVAILABLE

### T-INT-02: 缂栬瘧 鈫?鏃ュ織 鈫?C# 鎵ц 鍏ㄩ摼璺?```
姝ラ:
1. unity_compile()
2. unity_console_get_logs(count=5)
3. unity_csharp_execute(code="return \"post_compile_ok\";")
```
**棰勬湡:** 缂栬瘧鎴愬姛锛堝煙閲嶈浇鍚庢仮澶嶏級锛屾棩蹇楀彲璇诲彇锛孋# 鎵ц杩斿洖 `"post_compile_ok"`

### T-INT-03: ScrollView + 榧犳爣缁勫悎
```
姝ラ:
1. unity_editor_windows_list(typeFilter="Inspector")
2. unity_uitoolkit_scroll(targetWindow="inspector", scrollToY=500, mode="absolute")
3. unity_mouse_event(action="click", button="left", x=200, y=300, targetWindow="inspector")
```
**棰勬湡:** 婊氬姩鎴愬姛鍚庣偣鍑绘垚鍔?
---

## M26: 楠屾敹鑷姩鍖栧伐鍏?
鏈妭瀵瑰簲 P0鈥揚3 瀹炵幇鐨?MCP 宸ュ叿涓庤祫婧愶細`unity_compile_wait`銆乣unity_screenshot_editor_window`銆乣unity_batch_diagnostics`銆乣unity_verify_window`锛屼互鍙婅祫婧?`unity://diagnostics/window`銆乣unity://console/summary`锛堟部鐢?`unity://diagnostics/unitypilot-logs-tab` 浣滄棩蹇楀尯涓撻」蹇収锛夈€?
### M26 鍓嶇疆鏉′欢锛堣嚜鍔ㄥ寲鑴氭湰搴斿厛鏂█锛?
| 妫€鏌ラ」 | 璋冪敤鏂瑰紡 | 閫氳繃鏉′欢 |
|--------|----------|----------|
| MCP 涓?Unity 宸茶繛鎺?| `unity_mcp_status()` 鎴栫瓑浠风姸鎬佸伐鍏?| `connected=true`锛堜互椤圭洰瀹為檯宸ュ叿鍚嶄负鍑嗭級 |
| 缂栬緫鍣ㄥ彲鍝嶅簲 | `resource_editor_state` / `unity://editor/state` | 杩斿洖 `ok`锛屽惈 `unityVersion`銆乣projectPath` |
| 锛堝彲閫夛級宸叉墦寮€ UnityPilot 绐楀彛 | 鑿滃崟 `upilot/upilot` 鎴?`unity_menu_execute` | 绐楀彛瀛樺湪鏃跺啀璺戞埅鍥?绐楀彛璇婃柇鏇存湁鎰忎箟 |

### M26 鎺ㄨ崘鑷姩鍖栨祦姘寸嚎锛堝崟鑴氭湰椤哄簭锛?
浠ヤ笅椤哄簭鍙湪涓€娆?CI/鏈湴鑴氭湰涓覆鑱旀墽琛岋紝鍓嶄竴姝ュけ璐ュ垯鍚庣画璺宠繃鎴栬涓哄け璐ャ€?
```
1. unity_mcp_status()                    鈫?纭杩炴帴
2. unity_compile_wait(timeoutS=180)    鈫?纭繚褰撳墠鏃犵紪璇戞寕璧?3. unity_menu_execute("upilot/upilot")   鈫?鎵撳紑绐楀彛锛堣嫢椤圭洰鏆撮湶璇ュ伐鍏凤紱鍚﹀垯浜哄伐/姝ラ璇存槑锛?4. unity_batch_diagnostics()           鈫?蹇収锛氱獥鍙?+ 鎺у埗鍙?+ 缂栬緫鍣ㄧ姸鎬?5. unity_verify_window(includeScreenshot=true)   鈫?涓€閿細compileWait + 璇婃柇 + 鎴浘
6. 锛堝彲閫夛級璇诲彇 MCP resource unity://diagnostics/window銆乽nity://console/summary
7. 锛堝彲閫夛級鍒囨崲鍒般€岃瘖鏂棩蹇椼€嶆爣绛惧悗璇诲彇 unity://diagnostics/unitypilot-logs-tab
```

### M26 鑷姩鍖栨柇瑷€娓呭崟锛堣剼鏈唴鍙€愰」 assert锛?
| 姝ラ | 瀛楁/璺緞 | 鏂█ |
|------|-----------|------|
| compile_wait锛堢┖闂诧級 | `data.status` | `== "ready"` |
| compile_wait锛堢┖闂诧級 | `data.isCompiling` | `== false` |
| batch_diagnostics | `data.windowDiagnostics` | 瀛樺湪涓斾负 object |
| batch_diagnostics | `data.consoleSummary.total` | `>= 0` |
| batch_diagnostics | `data.editorState.unityVersion` | 闈炵┖瀛楃涓?|
| verify_window | `data.compileWait.status` | `== "ready"` |
| verify_window锛堝惈鎴浘锛?| `data.screenshot.imageData` | 闈炵┖ Base64 瀛楃涓?|
| verify_window锛堟棤鎴浘锛?| `data.screenshot` | 閿笉瀛樺湪鎴?`undefined` |
| resource window | `windowOpen` | 绐楀彛鎵撳紑鏃朵负 `true`锛涙湭鎵撳紑鍙负 `false`锛堜笉寮哄埗澶辫触锛?|
| resource window | `healthScore` | 甯冨眬鏃犳孩鍑烘椂涓?`"ok"`锛涙湁婧㈠嚭涓?`"fail"` |
| resource window | `codeVersion` | 闈炵┖ |
| resource console/summary | `total` | `== logCount + warningCount + errorCount + assertCount`锛堜笌瀹炵幇涓€鑷存椂锛?|

璇存槑锛歚consoleSummary` 鍚勮鏁板瓧娈佃嫢涓?`total` 姹傚拰瀛樺湪瀹炵幇宸紓锛岃嚜鍔ㄥ寲鍙彧鏂█ `total >= 0` 涓庡悇鍒嗛」 `>= 0`銆?
### T-M26-01: unity_compile_wait 鈥?绌洪棽鏃剁珛鍗宠繑鍥?```
璋冪敤: unity_compile_wait(timeoutS=10, pollIntervalS=0.5)
```
**棰勬湡:** `ok=true`, `status="ready"`, `isCompiling=false`, `pollCount=1`
**鍒ゅ畾:** 缂栬緫鍣ㄧ┖闂叉椂棣栨杞鍗抽€氳繃

### T-M26-02: unity_compile_wait 鈥?缂栬瘧涓瓑寰?```
姝ラ:
1. 瑙﹀彂缂栬瘧锛堝淇敼鑴氭湰鍚庝繚瀛橈級
2. 绔嬪嵆璋冪敤 unity_compile_wait(timeoutS=120, pollIntervalS=1.0)
```
**棰勬湡:** `status="ready"`, `pollCount > 1`, `elapsedS > 0`
**鍒ゅ畾:** 缂栬瘧缁撴潫鍚庤嚜鍔ㄨ繑鍥?
### T-M26-03: unity_screenshot_editor_window
```
璋冪敤: unity_screenshot_editor_window(windowTitle="UnityPilot")
```
**棰勬湡:** `ok=true`, `imageData` 闈炵┖锛圔ase64 PNG锛夛紝`format="png"`
**鍒ゅ畾:** Windows 骞冲彴鑳芥埅鍙栧埌缂栬緫鍣ㄧ獥鍙ｅ儚绱?
### T-M26-04: unity_screenshot_editor_window 鈥?绐楀彛涓嶅瓨鍦?```
璋冪敤: unity_screenshot_editor_window(windowTitle="涓嶅瓨鍦ㄧ殑绐楀彛")
```
**棰勬湡:** `ok=false`, 閿欒鐮?`WINDOW_NOT_FOUND`

### T-M26-05: unity_batch_diagnostics
```
璋冪敤: unity_batch_diagnostics()
```
**棰勬湡:** `ok=true`, 杩斿洖鍖呭惈涓変釜瀛愬璞?
- `windowDiagnostics`: 鍚?`windowOpen`, `healthScore`, `codeVersion`, `sections`
- `consoleSummary`: 鍚?`total`, `logCount`, `warningCount`, `errorCount`
- `editorState`: 鍚?`isCompiling`, `unityVersion`

### T-M26-06: unity_verify_window 鈥?鍏ㄨ嚜鍔ㄩ獙鏀?```
璋冪敤: unity_verify_window(windowTitle="UnityPilot", includeScreenshot=true)
```
**棰勬湡:** `ok=true`, 杩斿洖鍖呭惈:
- `compileWait.status="ready"`
- `windowDiagnostics.healthScore="ok"`
- `consoleSummary.errorCount >= 0`
- `screenshot.imageData` 闈炵┖锛圔ase64 PNG锛?
### T-M26-07: unity_verify_window 鈥?鏃犳埅鍥炬ā寮?```
璋冪敤: unity_verify_window(windowTitle="UnityPilot", includeScreenshot=false)
```
**棰勬湡:** `ok=true`, 杩斿洖涓嶅惈 `screenshot` 瀛楁

### T-M26-08: Resource 鈥?unity://diagnostics/window
```
璇诲彇: unity://diagnostics/window
```
**棰勬湡:** 杩斿洖 JSON 鍚?`windowOpen`, `windowWidth`, `windowHeight`, `healthScore`,
`codeVersion`, `domainReloadEpoch`, `isCompiling`, `sections[]`

### T-M26-09: Resource 鈥?unity://console/summary
```
璇诲彇: unity://console/summary
```
**棰勬湡:** 杩斿洖 JSON 鍚?`total`, `logCount`, `warningCount`, `errorCount`, `assertCount`

### T-M26-10: 鏍囩椤垫寔涔呭寲
```
姝ラ:
1. 鍦?UnityPilot 绐楀彛鍒囨崲鍒般€岃瘖鏂棩蹇椼€嶆爣绛?2. 鍏抽棴绐楀彛
3. 閲嶆柊鎵撳紑绐楀彛锛堣彍鍗?upilot/upilot锛?```
**棰勬湡:** 绐楀彛閲嶆柊鎵撳紑鍚庝粛鍋滅暀鍦ㄣ€岃瘖鏂棩蹇椼€嶆爣绛撅紙activeTab=1锛?
### T-M26-11: Domain Reload 绾厓
```
姝ラ:
1. 璋冪敤 unity_batch_diagnostics() 璁板綍 domainReloadEpoch 鍊?2. 瑙﹀彂缂栬瘧锛堝煙閲嶈浇锛?3. 璋冪敤 unity_compile_wait() 绛夊緟缂栬瘧瀹屾垚
4. 鍐嶆璋冪敤 unity_batch_diagnostics()
```
**棰勬湡:** 绗簩娆＄殑 domainReloadEpoch > 绗竴娆＄殑鍊硷紙鍩熼噸杞藉悗鑷姩鏇存柊锛?
### T-M26-12: 娴佹按绾夸覆鑱?鈥?status 鈫?compile_wait 鈫?batch 鈫?verify
```
姝ラ锛堣嚜鍔ㄥ寲鑴氭湰椤哄簭鎵ц锛屼腑闂?sleep 0 鎴栨瀬鐭級:
1. unity_mcp_status() 鎴栭€氳繃 facade 纭浼氳瘽宸茶繛鎺?2. unity_compile_wait(timeoutS=60, pollIntervalS=0.5)
3. unity_batch_diagnostics()
4. unity_verify_window(windowTitle="UnityPilot", includeScreenshot=true)
```
**棰勬湡:**
- 姝ラ 2锛歚ok=true`, `status="ready"`
- 姝ラ 3锛歚ok=true`锛屼笖 `windowDiagnostics`銆乣consoleSummary`銆乣editorState` 涓夐敭鍧囧瓨鍦?- 姝ラ 4锛歚ok=true`锛屼笖 `compileWait`銆乣windowDiagnostics`銆乣consoleSummary` 瀛樺湪锛宍screenshot.imageData` 闈炵┖
**鍒ゅ畾:** 鍗曟浼氳瘽鍐呮棤 `UNITY_NOT_CONNECTED`锛屾棤寮傚父鏍?
### T-M26-13: unity_compile_wait 鈥?瓒呮椂璺緞锛堥渶浜轰负鎷夐暱缂栬瘧鎴?mock锛?```
鍓嶇疆: 浠呭湪鍙鐜般€岄暱鏃堕棿缂栬瘧銆嶆垨娴嬭瘯鐜鍙敞鍏ュ欢杩熸椂浣跨敤锛涘惁鍒欐爣涓恒€屽彲閫?鎵嬪伐銆嶃€?
姝ラ:
1. 瑙﹀彂涓€娆￠暱缂栬瘧锛堝澶ч噺鑴氭湰閲嶅鍏ワ級
2. 璋冪敤 unity_compile_wait(timeoutS=2, pollIntervalS=0.2)
```
**棰勬湡:** `ok=true`, `status="timeout"`, `isCompiling=true`, `elapsedS` 鎺ヨ繎 `timeoutS`
**鍒ゅ畾:** 璇佹槑杞鍦ㄨ秴鏃跺悗閫€鍑猴紝鑰岄潪鏃犻檺闃诲

### T-M26-14: Resource 涓?Tool 鏁版嵁涓€鑷存€?```
姝ラ:
1. 璋冪敤 unity_batch_diagnostics()锛岃褰?windowDiagnostics.healthScore銆乧onsoleSummary
2. 鍒嗗埆閫氳繃 MCP resource 璇诲彇 unity://diagnostics/window銆乽nity://console/summary锛堣嫢 Cursor/瀹㈡埛绔敮鎸?resource 涓?tool 骞惰锛?```
**棰勬湡:** 鍚屼竴鏃跺埢锛堟棤缂栬瘧銆佹棤绐楀彛灏哄鍙樺寲锛変笅锛宍healthScore` 涓庢帶鍒跺彴璁℃暟涓?batch 鍐呭祵鏁版嵁涓€鑷存垨绛変环
**鍒ゅ畾:** 鑷姩鍖栧彲鏀惧涓恒€屽悓涓€鍒嗛挓鍐呬袱娆¤鍙栵紝宸€煎湪鍙帴鍙楄寖鍥淬€?
### T-M26-15: unity://diagnostics/unitypilot-logs-tab 鈥?涓庡叏绐楄瘖鏂厤鍚?```
姝ラ:
1. 鎵撳紑 UnityPilot 绐楀彛骞跺垏鎹㈠埌銆岃瘖鏂棩蹇椼€嶆爣绛?2. 璇诲彇 unity://diagnostics/unitypilot-logs-tab
3. 璇诲彇 unity://diagnostics/window
```
**棰勬湡:**
- 鏃ュ織璧勬簮锛歚snapshotValid=true`, `horizontalBarRisk` 涓庡竷灞€棰勬湡涓€鑷达紙姝ｅ父甯冨眬涓嬩负 `false`锛?- 绐楀彛璧勬簮锛歚sections` 涓惈 `logToolbar`銆乣logScroll` 绛夎褰曪紙绐楀彛鎵撳紑涓旀浘缁樺埗杩囨椂锛?**鍒ゅ畾:** 涓撻」鏃ュ織鍖轰笌鍏ㄧ獥 sections 鍙悓鏃剁敤浜庡洖褰掋€屾í鍚戞潯銆嶇被闂

### T-M26-16: 绐楀彛鏈墦寮€鏃剁殑琛屼负
```
姝ラ:
1. 鍏抽棴 UnityPilot 缂栬緫鍣ㄧ獥鍙ｏ紙鑻ュ凡鎵撳紑锛?2. 璋冪敤 unity_batch_diagnostics() 鎴栬鍙?unity://diagnostics/window
```
**棰勬湡:** `ok=true`锛堣繛鎺ユ甯告椂锛夛紝`windowDiagnostics.windowOpen=false`锛宍healthScore` 鍙负 `unknown`锛堝疄鐜颁互浠ｇ爜涓哄噯锛?**鍒ゅ畾:** 鑷姩鍖栦笉搴斿洜銆屾湭寮€绐椼€嶈€屽垽鏁存澶辫触锛涙埅鍥剧被鐢ㄤ緥闇€鍗曠嫭瑕佹眰鍏堝紑绐?
### T-M26-17: verify_window 鈥?鎴浘澶辫触闄嶇骇锛堟爣棰橀敊璇級
```
璋冪敤: unity_verify_window(windowTitle="__涓嶅瓨鍦ㄧ殑鏍囬__", includeScreenshot=true)
```
**棰勬湡:** 鏁村寘浠嶅彲鑳?`ok=true`锛坈ompileWait銆佽瘖鏂垚鍔燂級锛宍screenshot` 鍐呭惈 `error` 鎴栭敊璇爜锛堜笌 `screenshot_editor_window` 琛屼负涓€鑷达級
**鍒ゅ畾:** 鑴氭湰搴斿 `screenshot` 鍒嗘敮妫€鏌?`imageData` 涓?`error`锛岄伩鍏嶄粎鏂█椤跺眰 `ok`

### T-M26-18: 鏍囩鎸佷箙鍖?鈥?涓?resource 浜ゅ弶楠岃瘉锛堝彲閫夛級
```
姝ラ:
1. 鍒囨崲鍒般€岃瘖鏂棩蹇椼€嶏紝鍏抽棴绐楀彛鍐嶆墦寮€锛堝悓 T-M26-10锛?2. 璋冪敤 unity_batch_diagnostics()锛屾煡鐪?windowDiagnostics.activeTab锛堣嫢瀹炵幇鏆撮湶锛?   鎴栦汉宸ョ‘璁?UI 浠嶄负璇婃柇鏃ュ織
3. 璇诲彇 unity://diagnostics/unitypilot-logs-tab锛岀‘璁?snapshotValid
```
**棰勬湡:** 鎸佷箙鍖栧悗棣栨鎵撳紑鑻ュ湪璇婃柇鏃ュ織锛宍snapshotValid=true` 涓?`activeTab` 涓?UI 涓€鑷达紙浠ュ疄闄?payload 瀛楁涓哄噯锛?**鍒ゅ畾:** 瀛楁鍚嶈嫢涓庢枃妗ｇ暐鏈夊嚭鍏ワ紝浠?`UnityPilotLogsTabResourcePayload` / `WindowDiagnosticsPayload` 涓哄噯璋冩暣鏂█

---

## M27: 缂栬緫鍣ㄧ獥鍙?/ UIToolkit / E2E 鎵╁睍

**鍓嶇疆:** 涓?M26 鐩稿悓锛涢粯璁ゅ竷灞€闇€鍖呭惈 **Game** 瑙嗗浘锛圱-M27-03锛夛紱缂栬緫鍣?UI 璇█寤鸿 **鑻辨枃**锛堢獥鍙ｆ爣棰?`Inspector` / `Game`锛夈€?
浠ヤ笅鐢ㄤ緥鐢?`scripts/run_acceptance_suite.py` 浠?**T-M27-01** 鈥?**T-M27-08** 椤哄簭鎵ц锛堥渶 Unity 宸茶繛鎺ワ級銆?
### T-M27-01: editor.windows.list 鈥?closable 瀛楁
**棰勬湡:** 姣忎釜绐楀彛椤瑰惈 `closable`銆乣closeDeniedReason`銆?
### T-M27-02: editor.window.close 鈥?鏈壘鍒?**璋冪敤:** `unity_editor_window_close(windowTitle="__m27_missing_window__")`  
**棰勬湡:** 閿欒鐮?`WINDOW_NOT_FOUND`銆?
### T-M27-03: editor.window.close 鈥?榛戝悕鍗?**璋冪敤:** `unity_editor_window_close(windowTitle="Game")`锛圙ame 瑙嗗浘鎵撳紑鏃讹級  
**棰勬湡:** `WINDOW_CLOSE_DENIED`銆?
### T-M27-04: uitoolkit.event 鈥?wheel
**棰勬湡:** 瀵瑰甫 `name` 鐨?`Label` 娲惧彂 `wheel` 鎴愬姛锛坄wheelDeltaY` 闈為浂锛夈€?
### T-M27-05: editor.window.setRect 鈥?鍋滈潬绐楀彛
**璋冪敤:** `unity_editor_window_set_rect` 鍖归厤鏍囬鍚?`Inspector`  
**棰勬湡:** `WINDOW_DOCKED`锛堥粯璁ゅ仠闈犲竷灞€锛夈€?
### T-M27-06: uitoolkit.scroll 鈥?宓屽璺緞澶辫触
**璋冪敤:** `scrollViewNamePath` 涓轰笉瀛樺湪鐨?`outer|inner`  
**棰勬湡:** 缁撴灉 payload `ok=false`锛宍state` 鍚?`SCROLLVIEW_NOT_FOUND`銆?
### T-M27-07: editor_e2e_run 鈥?exportZip
**姝ラ:** 瀵?`e2e-specs/examples/smoke_editor_state.yaml` 鎵ц `exportZip=true`  
**棰勬湡:** 杩斿洖 `zipPath` 涓旂鐩樺瓨鍦?`e2e-bundle.zip`銆?
### T-M27-08: capturePointer / releasePointer
**棰勬湡:** 瀵瑰懡鍚嶆帶浠朵緷娆?`capturePointer`銆乣releasePointer` 鍧?`ok=true`銆?
---

## S绯诲垪锛氭祴璇曞崗鍚屽寮哄伐鍏?
### T-S1-01: UIToolkit 鍏冪礌鍊艰鍙?鈥?dump 鍖呭惈 value/valueType
```
璋冪敤: unity_uitoolkit_dump(targetWindow="inspector", maxDepth=8)
```
**棰勬湡:** 杩斿洖鐨?`elements` 涓紝TextField 绫诲瀷鐨勫厓绱?`value` 瀛楁鍖呭惈褰撳墠杈撳叆妗嗘枃瀛楋紝`valueType="string"`锛?Toggle 绫诲瀷 `value="True"/"False"`锛宍valueType="bool"`锛汢utton 绫诲瀷 `valueType="button"`锛宍interactable=true`
**鍒ゅ畾:** 妫€鏌ヨ嚦灏戝瓨鍦?1 涓?`valueType` 闈炵┖鐨勫厓绱狅紱`interactable` 瀵?TextField/Toggle/Button 鍧囦负 `true`

### T-S1-02: UIToolkit 鍏冪礌鍊艰鍙?鈥?query 杩斿洖 value
```
璋冪敤: unity_uitoolkit_query(targetWindow="inspector", typeFilter="TextField")
```
**棰勬湡:** 杩斿洖鐨?`matches` 涓瘡涓厓绱犻兘鏈?`value`锛堝彲涓虹┖瀛楃涓诧級鍜?`valueType="string"`
**鍒ゅ畾:** `matchCount >= 1`锛屼笖鎵€鏈?match 鐨?`valueType == "string"`

### T-S1-03: 鍏冪礌鐒︾偣鐘舵€?```
姝ラ:
1. unity_uitoolkit_interact(targetWindow="inspector", action="focus", elementName="<鏌愪釜TextField鍚?")
2. unity_uitoolkit_query(targetWindow="inspector", nameFilter="<鍚屼笂>")
```
**棰勬湡:** 姝ラ2杩斿洖鐨勫厓绱?`isFocused=true`
**鍒ゅ畾:** `matches[0].isFocused == true`

### T-S2-01: UIToolkit 璁剧疆 TextField 鍊?```
姝ラ:
1. unity_uitoolkit_set_value(targetWindow="inspector", elementName="<TextField鍚?", value="TestValue123")
2. unity_uitoolkit_query(targetWindow="inspector", nameFilter="<鍚屼笂>")
```
**棰勬湡:** 姝ラ1杩斿洖 `ok=true, state="set:TextField:TestValue123"`锛涙楠?鏌ヨ鍒?`value="TestValue123"`
**鍒ゅ畾:** 璁剧疆鍓嶅悗鍊煎彉鏇翠竴鑷?
### T-S2-02: UIToolkit 璁剧疆 Toggle 鍊?```
姝ラ:
1. unity_uitoolkit_query(targetWindow="<绐楀彛>", typeFilter="Toggle") 鈥?璁板綍褰撳墠鍊?2. unity_uitoolkit_set_value(targetWindow="<绐楀彛>", elementName="<Toggle鍚?", value="true")
3. unity_uitoolkit_query(targetWindow="<绐楀彛>", nameFilter="<鍚屼笂>")
```
**棰勬湡:** 姝ラ2 `ok=true`锛屾楠? `value="True"`
**鍒ゅ畾:** Toggle 鍊艰鎴愬姛鍒囨崲

### T-S2-03: UIToolkit interact 鈥?鎸夊悕绉扮偣鍑?Button
```
璋冪敤: unity_uitoolkit_interact(targetWindow="<绐楀彛>", action="click", elementName="<Button鍚?")
```
**棰勬湡:** `ok=true, state="clicked:<Button鍚?:Button"`
**鍒ゅ畾:** 杩斿洖 ok=true 涓?state 鍖呭惈 "clicked"

### T-S2-04: UIToolkit setValue 鈥?涓嶆敮鎸佺殑绫诲瀷杩斿洖閿欒
```
璋冪敤: unity_uitoolkit_set_value(targetWindow="<绐楀彛>", elementName="<Label鍚?", value="abc")
```
**棰勬湡:** `ok=false, state="UNSUPPORTED_ELEMENT_TYPE:Label"`
**鍒ゅ畾:** 涓嶅彲鍐欑殑鍏冪礌绫诲瀷杩斿洖鏄庣‘閿欒鐮?
### T-S3-01: compile_wait 璺ㄥ煙閲嶈浇瀛樻椿
```
姝ラ:
1. 淇敼涓€涓?C# 鑴氭湰锛堣Е鍙戠紪璇戝拰鍩熼噸杞斤級
2. 绔嬪嵆璋冪敤 unity_compile_wait(timeoutS=120, pollIntervalS=1)
```
**棰勬湡:** 鍗充娇鏈熼棿 Unity 鏂繛骞堕噸杩烇紝`compile_wait` 涓嶄細宕╂簝锛屾渶缁堣繑鍥?`status="ready"` 涓?`reconnectedDuringWait=true`
**鍒ゅ畾:** status=="ready"锛屾棤寮傚父

### T-S3-02: compile_wait 瓒呮椂
```
璋冪敤: unity_compile_wait(timeoutS=2, pollIntervalS=0.5)
锛堝湪缂栬瘧涓皟鐢紝涓旂紪璇戣€楁椂 >2 绉掞級
```
**棰勬湡:** 杩斿洖 `status="timeout", isCompiling=true`
**鍒ゅ畾:** 瓒呮椂涓嶅穿婧冿紝姝ｇ‘鎶ョ姸鎬?
### T-S4-01: mouse_event 鎸?elementName 鑷姩鍧愭爣
```
姝ラ:
1. unity_uitoolkit_dump(targetWindow="<绐楀彛>") 鈥?鎵惧埌涓€涓湁鍚嶇О鐨勫彲鐐瑰嚮鍏冪礌
2. unity_mouse_event(action="click", button="left", targetWindow="<绐楀彛>", elementName="<鍏冪礌鍚?")
```
**棰勬湡:** `ok=true`锛岄紶鏍囩偣鍑昏嚜鍔ㄥ懡涓鍏冪礌涓績鍧愭爣
**鍒ゅ畾:** ok=true 涓旀棤 WINDOW_NOT_AVAILABLE

### T-S4-02: mouse_event elementName 涓嶅瓨鍦ㄦ椂闄嶇骇
```
璋冪敤: unity_mouse_event(action="click", button="left", targetWindow="<绐楀彛>", elementName="__涓嶅瓨鍦╛_")
```
**棰勬湡:** `ok=true`锛堥檷绾у埌 x=0,y=0 鍧愭爣鍙戦€佷簨浠讹級锛屾垨琛屼负鍚堢悊
**鍒ゅ畾:** 涓嶅穿婧?
### T-S5-01: wait_condition 鈥?绛夊緟鍏冪礌鍑虹幇
```
姝ラ:
1. 纭繚鏌愪釜绐楀彛宸叉墦寮€涓旀湁宸茬煡鍏冪礌
2. unity_wait_condition(targetWindow="<绐楀彛>", conditionType="element_exists", elementName="<宸茬煡鍏冪礌>", timeoutS=5)
```
**棰勬湡:** `met=true`锛宍pollCount` 杈冨皬锛堝厓绱犲凡瀛樺湪锛岄娆″嵆鍛戒腑锛?**鍒ゅ畾:** met==true

### T-S5-02: wait_condition 鈥?鍏冪礌涓嶅瓨鍦ㄦ椂瓒呮椂
```
璋冪敤: unity_wait_condition(targetWindow="<绐楀彛>", conditionType="element_exists", elementName="__缁濆涓嶅瓨鍦╛_", timeoutS=3)
```
**棰勬湡:** `met=false`锛宍elapsedS` 鎺ヨ繎 3
**鍒ゅ畾:** 瓒呮椂杩斿洖 met=false锛屼笉宕╂簝

### T-S5-03: wait_condition 鈥?绛夊緟鍊煎尮閰?```
姝ラ:
1. unity_uitoolkit_set_value(targetWindow="<绐楀彛>", elementName="<TextField>", value="WaitForMe")
2. unity_wait_condition(targetWindow="<绐楀彛>", conditionType="element_value", elementName="<TextField>", valueEquals="WaitForMe", timeoutS=5)
```
**棰勬湡:** `met=true`锛堝€煎凡璁剧疆锛岄娆″嵆鍛戒腑锛?**鍒ゅ畾:** met==true 涓?matchedElement.value=="WaitForMe"

### T-S5-04: wait_condition 鈥?绛夊緟鍏冪礌娑堝け
```
璋冪敤: unity_wait_condition(targetWindow="<绐楀彛>", conditionType="element_not_exists", elementName="__缁濆涓嶅瓨鍦╛_", timeoutS=3)
```
**棰勬湡:** `met=true`锛堝厓绱犳湰灏变笉瀛樺湪锛?**鍒ゅ畾:** met==true

### T-S6-01: screenshot_editor_window 浣跨敤 FindTargetWindow
```
姝ラ:
1. unity_editor_windows_list() 鈥?鑾峰彇绐楀彛鍒楄〃锛岃褰曚竴涓獥鍙ｇ殑 typeName
2. unity_screenshot_editor_window(windowTitle="<typeName>")
```
**棰勬湡:** 杩斿洖 `imageData` Base64锛堢幇鍦ㄦ敮鎸佹寜绫诲瀷鍚?鍒悕绛夊绛栫暐鍖归厤锛?**鍒ゅ畾:** imageData 闈炵┖

### T-S6-02: screenshot_editor_window 鍒悕
```
璋冪敤: unity_screenshot_editor_window(windowTitle="inspector")
```
**棰勬湡:** 鎴愬姛鎴彇 Inspector 绐楀彛鎴浘
**鍒ゅ畾:** 杩斿洖 imageData 闈炵┖

### T-S7-01: ensure_ready 鈥?姝ｅ父鐜
```
璋冪敤: unity_ensure_ready(timeoutS=30)
```
**棰勬湡:** `ready=true, connected=true, compileStatus="ready", inEditMode=true`
**鍒ゅ畾:** 鎵€鏈夊瓙椤瑰潎涓烘甯哥姸鎬?
### T-S7-02: ensure_ready 鈥?缂栬瘧涓?```
姝ラ:
1. 淇敼鑴氭湰瑙﹀彂缂栬瘧
2. 绔嬪嵆璋冪敤 unity_ensure_ready(timeoutS=120)
```
**棰勬湡:** 绛夊緟缂栬瘧瀹屾垚鍚庤繑鍥?`ready=true`
**鍒ゅ畾:** ready==true锛宑ompileStatus=="ready"

### T-S8-01: task_execute 鈥?姝ｅ父鎵ц
```
璋冪敤: unity_task_execute(taskName="test_ping", toolName="resource_editor_state", timeoutS=10)
```
**棰勬湡:** `status="completed", attempt=1, result` 鍖呭惈 editorState 鏁版嵁
**鍒ゅ畾:** status=="completed"

### T-S8-02: task_execute 鈥?瓒呮椂閲嶈瘯
```
璋冪敤: unity_task_execute(taskName="test_timeout", toolName="compile_wait", toolArgs={"timeout_s": 1, "poll_interval_s": 0.1}, timeoutS=2, retryCount=1, maxTotalS=10)
锛堝湪闈炵紪璇戠姸鎬佹墽琛岋紝compile_wait 浼氬緢蹇繑鍥烇級
```
**棰勬湡:** `status="completed"`锛坈ompile_wait 寰堝揩杩斿洖 ready锛?**鍒ゅ畾:** status=="completed"锛宔vents 鍒楄〃涓虹┖鎴栧彧鏈夋垚鍔熻褰?
### T-S8-03: task_execute 鈥?鎬昏秴鏃惰烦杩?```
璋冪敤: unity_task_execute(taskName="test_skip", toolName="wait_condition",
    toolArgs={"target_window": "inspector", "condition_type": "element_exists", "element_name": "__涓嶅瓨鍦╛_", "timeout_s": 5},
    timeoutS=6, maxTotalS=8, retryCount=1)
```
**棰勬湡:** `status="skipped"`锛坵ait_condition 涓ゆ鍧囧洜鍏冪礌涓嶅瓨鍦ㄨ€岃繑鍥?met=false 鈫?tool error 鈫?璺宠繃锛?**鍒ゅ畾:** status=="skipped"锛宔vents 鍖呭惈 retry 璁板綍

### T-INTEGRATED-01: 瀹屾暣鑷姩鍖栨祦姘寸嚎妯℃嫙
```
姝ラ:
1. unity_ensure_ready(timeoutS=60) 鈥?棰勬
2. unity_editor_windows_list() 鈥?鍒楀嚭绐楀彛
3. unity_uitoolkit_dump(targetWindow="inspector") 鈥?鏌ョ湅 Inspector 鍏冪礌鏍?4. unity_uitoolkit_query(targetWindow="inspector", typeFilter="TextField") 鈥?鎵?TextField
5. unity_uitoolkit_set_value(targetWindow="inspector", elementName="<鎵惧埌鐨勫悕>", value="PipelineTest")
6. unity_wait_condition(targetWindow="inspector", conditionType="element_value", elementName="<鍚屼笂>", valueEquals="PipelineTest", timeoutS=5)
7. unity_screenshot_editor_window(windowTitle="inspector")
```
**棰勬湡:** 姣忎竴姝ラ兘鎴愬姛锛屾渶缁堟埅鍥惧寘鍚慨鏀瑰悗鐨勫€?**鍒ゅ畾:** 鍏ㄩ摼璺?ok=true / met=true锛屾埅鍥?imageData 闈炵┖
