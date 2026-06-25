# UnityUIFlow UXML/USS 鑷姩鍖栧弸濂藉紑鍙戣鑼?
## 1. 鐩爣

鏈枃妗ｇ敤浜庣害鏉熷熀浜?UIToolkit 鐨?`EditorWindow` 椤甸潰寮€鍙戞柟寮忥紝璁╅〉闈粠涓€寮€濮嬪氨閫傚悎琚?`UnityUIFlow` 鐨?YAML 鐢ㄤ緥绋冲畾椹卞姩锛屽噺灏戜互涓嬮棶棰橈細

- YAML 閫夋嫨鍣ㄧ粡甯稿け鏁?- 鎸夐挳鑳芥墜鐐逛絾鑷姩鍖栫偣涓嶅姩
- 鎻愮ず銆佸脊灞傘€佺姸鎬佹枃妗堥毦浠ユ柇瑷€
- 椤甸潰缁撴瀯涓€鏀癸紝鎵归噺鐢ㄤ緥澶ч噺鎶ラ敊

鏈枃妗ｅ彧閽堝褰撳墠浠撳簱閲屽凡缁忓疄鐜扮殑 `UnityUIFlow` 鑳藉姏锛屼笉璁ㄨ灏氭湭鏀寔鐨勯€氱敤 Web/CSS 娴嬭瘯瑙勮寖銆?
## 2. 褰撳墠椤圭洰閲岀湡姝ｆ敮鎸佺殑閫夋嫨鍣ㄨ兘鍔?
`UnityUIFlow` 褰撳墠鏀寔鐨勯€夋嫨鍣ㄨ娉曟潵鑷互涓嬪疄鐜帮細

- `#id` 瀵瑰簲 `VisualElement.name`
- `.class` 瀵瑰簲 USS class
- `Button`銆乣Label`銆乣TextField` 杩欑被绫诲瀷閫夋嫨鍣?- `[name=xxx]`
- `[tooltip=xxx]`
- `[data-xxx=yyy]`锛屽€兼潵鑷?`element.userData`
- 鍚庝唬閫夋嫨鍣細`#panel Button`
- 鐩存帴瀛愬厓绱犻€夋嫨鍣細`#panel > .item`
- 浼被锛歚:first-child`

褰撳墠涓嶅缓璁緷璧栫殑鑳藉姏锛?
- 涓嶈鍋囪鏀寔瀹屾暣 CSS
- 涓嶈鍋囪鏀寔澶嶆潅灞炴€ф瘮杈冦€佹ā绯婂尮閰嶃€佹鍒欏尮閰?- 涓嶈浣跨敤 `.yml`锛屽綋鍓嶆枃浠跺叆鍙ｆ寜 `.yaml` 鏍￠獙

椤圭洰鍐呯殑楂樼ǔ瀹氭€т紭鍏堢骇寤鸿濡備笅锛?
1. `#name`
2. `[data-xxx=yyy]`
3. `[tooltip=xxx]`
4. `.class`
5. 绫诲瀷閫夋嫨鍣ㄥ拰灞傜骇缁勫悎
6. `:first-child`

鍘熷洜锛?
- `#name` 鍦ㄥ綋鍓嶅疄鐜颁腑鏈夊揩閫熻矾寰勶紝鏌ユ壘鏈€绋冲畾銆佹渶蹇?- `.class` 鏇撮€傚悎鏍峰紡璇箟锛屼笉閫傚悎闀挎湡鎵挎媴鑷姩鍖栦富閫夋嫨鍣?- `:first-child` 瀵圭粨鏋勬敼鍔ㄦ渶鏁忔劅锛屽簲灏介噺灏戠敤

## 3. UXML 鍛藉悕瑙勮寖

### 3.1 蹇呴』缁欏叧閿氦浜掑厓绱犺缃?`name`

浠ヤ笅鍏冪礌蹇呴』璁剧疆鍞竴 `name`锛?
- 杈撳叆妗?- 鎸夐挳
- 鏍囩椤靛垏鎹㈠叆鍙?- 鐘舵€佹爣绛?- toast銆乨ialog銆乴oading銆乪mpty state 鐨勬牴鑺傜偣
- 婊氬姩瀹瑰櫒
- 鍒楄〃鏍硅妭鐐?- 闇€瑕佹柇瑷€鏂囨湰鐨?`Label`

鎺ㄨ崘鍛藉悕椋庢牸锛?
- 椤甸潰鏍硅妭鐐癸細`xxx-root`
- 闈㈡澘瀹瑰櫒锛歚xxx-panel`
- 杈撳叆妗嗭細`username-input`銆乣search-input`
- 鎸夐挳锛歚login-button`銆乣save-button`
- 鐘舵€佹枃鏈細`status-label`
- 寮瑰眰鎴?toast锛歚toast-message`銆乣confirm-dialog`
- 鍒楄〃锛歚order-list`
- 鍒楄〃椤癸細`order-item-1`銆乣order-item-2`

瑕佹眰锛?
- 鍏ㄩ儴浣跨敤灏忓啓鐭í绾垮懡鍚?- 涓€涓獥鍙ｅ唴 `name` 蹇呴』鍞竴
- 涓嶈鎶婅嚜鍔ㄧ敓鎴愮紪鍙蜂綔涓轰富閫夋嫨鍣紝闄ら潪瀹冨ぉ鐒剁ǔ瀹?
### 3.2 `class` 璐熻矗鏍峰紡锛屼笉璐熻矗涓诲畾浣?
`class` 鎺ㄨ崘鎵挎媴瑙嗚鑱岃矗锛屼緥濡傦細

- `page-root`
- `page-panel`
- `primary-button`
- `danger-button`
- `form-row`

涓嶆帹鑽愭妸 YAML 涓婚€夋嫨鍣ㄥ啓鎴愶細

```yaml
selector: ".primary-button"
```

鎺ㄨ崘鍐欐垚锛?
```yaml
selector: "#save-button"
```

### 3.3 闇€瑕佷笟鍔¤涔夋椂锛岀敤 `userData` 鎻愪緵 `data-*`

褰撳墠 `UnityUIFlow` 鏀寔 `[data-xxx=yyy]`锛屼絾鏁版嵁鏉ユ簮涓嶆槸 UXML 鍘熺敓灞炴€э紝鑰屾槸杩愯鏃跺湪 C# 涓啓鍏?`element.userData`銆?
鎺ㄨ崘鐢ㄦ硶锛?
```csharp
saveButton.userData = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["data-role"] = "primary",
};
```

鐒跺悗 YAML 鍙啓锛?
```yaml
selector: "[data-role=primary]"
```

閫傜敤鍦烘櫙锛?
- 鍚岀被鍏冪礌寰堝锛屼絾涓氬姟瑙掕壊鏄庣‘
- 鍚屼竴涓?`UXML` 浼氬鐢ㄥ埌澶氫釜椤甸潰
- 闇€瑕佺粰 agent 鏇村己鐨勮涔夐敋鐐?
## 4. UXML 缁撴瀯瑙勮寖

### 4.1 椤甸潰鏍圭粨鏋勪繚鎸佹竻鏅?
鎺ㄨ崘鏈€灏忕粨鏋勶細

```xml
<ui:VisualElement name="feature-root" class="page-root">
    <ui:VisualElement name="feature-panel" class="page-panel">
        <!-- 琛ㄥ崟 -->
        <!-- 鎿嶄綔鎸夐挳 -->
        <!-- 鐘舵€佹爣绛?-->
        <!-- 鍔ㄦ€佸尯鍩?-->
    </ui:VisualElement>
</ui:VisualElement>
```

寤鸿锛?
- 姣忎釜鍔熻兘鍧楁湁鐙珛瀹瑰櫒
- 鍒楄〃銆佸脊灞傘€乼oast 瀹瑰櫒鍗曠嫭鍛藉悕
- 涓嶈涓轰簡鎺掔増鎶婅涔夋棤鍏崇殑绌哄鍣ㄥ爢澶繁

### 4.2 瀵瑰姩鎬佸唴瀹逛繚鐣欑ǔ瀹氬涓昏妭鐐?
鎺ㄨ崘锛?
- 鍥哄畾涓€涓?`toast-host`
- 鍥哄畾涓€涓?`dialog-host`
- 鍥哄畾涓€涓?`result-panel`

杩欐牱鍗充娇鍔ㄦ€佸厓绱犱細鍒涘缓/閿€姣侊紝鑷姩鍖栦粛鐒惰兘鍥寸粫绋冲畾瀹瑰櫒璁捐 YAML銆?
渚嬪锛?
```xml
<ui:VisualElement name="toast-host" />
```

鑰屼笉鏄 toast 闅忔満鎻掑埌椤甸潰浠绘剰浣嶇疆銆?
### 4.3 鏂█鏂囨湰蹇呴』钀藉湪绋冲畾鍏冪礌涓?
鎺ㄨ崘锛?
- 鐧诲綍缁撴灉鏀惧湪 `#status-label`
- 鏍￠獙淇℃伅鏀惧湪 `#form-error-label`
- 淇濆瓨缁撴灉鏀惧湪 `#save-result-label`

涓嶆帹鑽愶細

- 鍙湪鎺у埗鍙版墦鍗扮粨鏋?- 鏂囨鍙嚭鐜板湪涓存椂銆佹棤鍚嶅厓绱犻噷
- 涓€涓爣绛炬棦鎵挎媴鏍囬鍙堟壙鎷呯姸鎬佽緭鍑?
## 5. USS 瑙勮寖

### 5.1 USS 涓嶅簲褰卞搷鍏冪礌鍙祴璇曟€?
褰撳墠 `UnityUIFlow` 瀵瑰彲瑙佹€х殑鍒ゆ柇渚濊禆锛?
- `display != None`
- `visibility != Hidden`
- `opacity > 0`
- `panel != null`

鍥犳瑕佹敞鎰忥細

- `display: none` 鐨勫厓绱犱細琚涓轰笉鍙
- `visibility: hidden` 鐨勫厓绱犱細琚涓轰笉鍙
- `opacity: 0` 鐨勫厓绱犱細琚涓轰笉鍙

濡傛灉 YAML 瑕佺敤 `assert_visible` 鎴?`wait_for_element`锛屽搴斿厓绱犲繀椤荤湡鐨勫彲瑙併€?
### 5.2 涓嶈涓轰簡鍔ㄧ敾闀挎湡鎶婂叧閿厓绱犱繚鎸佸湪鈥滀笉鍙浣嗗瓨鍦ㄢ€?
涓嶆帹鑽愶細

- 鐘舵€佹爣绛鹃暱鏈?`opacity: 0`
- toast 姘歌繙鍦ㄦ爲閲屼絾榛樿 `visibility: hidden`

鎺ㄨ崘锛?
- 瑕佷箞鏄惧紡鍒囨崲涓哄彲瑙?- 瑕佷箞鍒涘缓鍚庢樉绀猴紝鐢ㄥ畬鍚庣Щ闄ゆ垨 `display: none`

### 5.3 甯冨眬瑕佷繚璇佸彲鐐瑰嚮鍖哄煙绋冲畾

寤鸿锛?
- 鎸夐挳涓嶈琚€忔槑閬僵瑕嗙洊
- 涓嶈鎶婂彲鐐瑰嚮鍏冪礌缂╁埌鏋佸皬
- 涓嶈渚濊禆鐗规畩灞傚彔鍏崇郴璁╂寜閽€滅湅璧锋潵鑳界偣锛屽疄闄呭懡涓埆鐨勫厓绱犫€?
杩欐槸鍥犱负 `click` 鍔ㄤ綔浼氬熀浜庡厓绱犲疄闄呬綅缃彂閫佹寚閽?榧犳爣浜嬩欢銆?
## 6. C# 浜や簰鎺ュ叆瑙勮寖

### 6.1 椤甸潰搴斿疄鐜扮ǔ瀹氱殑鑷姩鍖栨瀯寤哄叆鍙?
濡傛灉椤甸潰閫氳繃 YAML `fixture.host_window` 鎵撳紑锛屾帹鑽愬疄鐜帮細

- `EditorWindow`
- `IUnityUIFlowTestHostWindow`
- `PrepareForAutomatedTest()`

杩欐牱 `UnityUIFlow` 鑳藉湪杩愯鍓嶇粺涓€鏋勫缓椤甸潰骞舵竻鐞嗚剰鐘舵€併€?
### 6.2 缁熶竴鍦?`BuildUi()` 涓畬鎴愯繖鍑犱欢浜?
鎺ㄨ崘椤哄簭锛?
1. 鍔犺浇 `VisualTreeAsset`
2. 鍔犺浇 `StyleSheet`
3. `rootVisualElement.Clear()`
4. 鎸傝浇鏍峰紡
5. `CloneTree`
6. `Q<T>()` 鑾峰彇鍏抽敭鎺т欢
7. 鏍￠獙鍏抽敭鎺т欢涓嶄负绌?8. 娉ㄥ唽浜嬩欢
9. 璁剧疆鍒濆鐘舵€?
### 6.3 鎸夐挳浼樺厛娉ㄥ唽 `MouseUpEvent`

褰撳墠 `UnityUIFlow` 鐨?`click` 鍔ㄤ綔浼氭淳鍙戞寚閽堝拰榧犳爣浜嬩欢锛屽苟鍦ㄦ牱渚嬬獥鍙ｄ腑浼樺厛閫氳繃 `MouseUpEvent` 椹卞姩閫昏緫銆?
鎺ㄨ崘锛?
```csharp
loginButton.RegisterCallback<MouseUpEvent>(_ => HandleLogin());
```

杩欐牱鍜屽綋鍓嶈嚜鍔ㄥ寲鐐瑰嚮璺緞鍏煎鎬ф渶濂姐€?
璇存槑锛?
- 濡傛灉浣犱娇鐢?`Button.clicked`锛岀悊璁轰笂涔熷彲鑳藉伐浣?- 浣嗕粠褰撳墠椤圭洰瀹炶返鐪嬶紝`MouseUpEvent` 鏄洿鐩存帴銆佸彲鎺с€佸凡琚牱渚嬮獙璇佺殑鎺ユ硶

### 6.4 杈撳叆妗嗚浣跨敤鏍囧噯鍙啓瀛楁

褰撳墠妗嗘灦瀵?`type_text_fast` / `type_text` 鐨勬敮鎸侀噸鐐规槸锛?
- `TextField`
- 甯歌鏁板€煎瓧娈?- 甯?`value` 鍙啓灞炴€х殑鎺т欢

鎺ㄨ崘锛?
- 琛ㄥ崟杈撳叆浼樺厛浣跨敤 `TextField`
- 涓嶈鎶婁笟鍔″叧閿緭鍏ュ仛鎴愬彧璇昏瑙夊３锛屽啀闈犻澶栭€昏緫鍚屾

### 6.5 椤甸潰鍒濆鍖栧繀椤诲彲閲嶅

閲嶅鎵撳紑绐楀彛鏃讹紝椤甸潰蹇呴』鍥炲埌绋冲畾鍒濆鐘舵€併€?
渚嬪锛?
- `status-label` 鍒濆鍖栦负 `Idle`
- toast 榛樿闅愯棌鎴栦笉瀛樺湪
- 杈撳叆妗嗛粯璁ゆ竻绌?- 鍒楄〃鎭㈠榛樿椤?
鍚﹀垯鎵归噺鎵ц鍜岄噸澶嶆墽琛屾椂瀹规槗鐩镐簰姹℃煋銆?
## 7. 鍔ㄦ€佸厓绱犺鑼?
### 7.1 Toast

鎺ㄨ崘鍋氭硶锛?
- 鏈夌嫭绔嬪涓?`toast-host`
- toast 鍏冪礌鍛藉悕涓?`toast-message`
- 鍑虹幇鏃跺垱寤烘垨鏄剧ず
- 娑堝け鏃剁Щ闄ゆ垨 `display: none`

鎺ㄨ崘 YAML锛?
```yaml
- action: assert_visible
  selector: "#toast-message"
  timeout: "1s"

- repeat_while:
    condition:
      exists: "#toast-message"
    max_iterations: 20
    steps:
      - action: wait
        duration: "50ms"

- action: assert_not_visible
  selector: "#toast-message"
  timeout: "500ms"
```

### 7.2 Loading

鎺ㄨ崘锛?
- 浣跨敤鏄庣‘鍛藉悕鐨?`loading-indicator`
- 鏄剧ず鍜岄殣钘忛€昏緫鍙娴?- 涓嶈鍙潬閬僵 alpha 鍙樺寲浣嗘案杩滀笉绉婚櫎

### 7.3 鍒楄〃

鎺ㄨ崘锛?
- 鍒楄〃鏍规湁 `name`
- 鍒楄〃椤瑰叿澶囩ǔ瀹氱被鍚嶏紝渚嬪 `.item`
- 鑻ヨ鏂█绗竴椤癸紝鍙厤鍚?`.item:first-child`

涓嶆帹鑽愶細

- 鍒楄〃椤瑰畬鍏ㄥ尶鍚?- 渚濊禆闅忔満鐢熸垚椤哄簭

## 8. YAML 璁捐瑙勮寖

### 8.1 浼樺厛浣跨敤 `#name`

鎺ㄨ崘锛?
```yaml
selector: "#status-label"
```

涓嶆帹鑽愶細

```yaml
selector: "Label"
selector: ".status"
```

### 8.2 瀵瑰姩鎬佸満鏅厛绛夊緟锛屽啀鏂█

鎺ㄨ崘锛?
- 鐐瑰嚮鍚庢湁寤惰繜鍑虹幇鐨勫厓绱狅紝鍏?`wait_for_element` 鎴?`assert_visible`
- 浼氭秷澶辩殑鍏冪礌锛岀敤 `repeat_while + wait`

### 8.3 杈撳叆娴嬭瘯浼樺厛浣跨敤 `type_text_fast`

寤鸿锛?
- 鍐掔儫銆佸洖褰掔敤渚嬩紭鍏?`type_text_fast`
- 闇€瑕佽瀵熼€愬瓧杈撳叆銆佽皟璇曡妭濂忔椂鍐嶇敤 `type_text`

### 8.4 鏂█钀藉埌绋冲畾缁撴灉涓?
鎺ㄨ崘锛?
- 鏂█ `#status-label`
- 鏂█ `#error-label`
- 鏂█ `#toast-message`

涓嶆帹鑽愶細

- 鏂█涓存椂甯冨眬鏂囨湰
- 鏂█瀹规槗琚瑙夋敼鐗堝奖鍝嶇殑瑁呴グ鏂囨

## 9. 椤甸潰浜や粯娓呭崟

鏂伴〉闈㈠噯澶囨帴鍏?`UnityUIFlow` 鏃讹紝鑷冲皯妫€鏌ヤ互涓嬮」鐩細

1. 鍏抽敭杈撳叆銆佹寜閽€佺姸鎬佸厓绱犻兘璁剧疆浜嗗敮涓€ `name`
2. 椤甸潰鏍硅妭鐐广€佷富闈㈡澘銆佸姩鎬佸涓昏妭鐐规湁娓呮櫚鍛藉悕
3. 鍏抽敭閫昏緫閫氳繃 `Q<T>(name)` 鑾峰彇骞剁粦瀹?4. 椤甸潰鏀寔閲嶅鎵撳紑鍚庢仮澶嶅垵濮嬬姸鎬?5. 鍔ㄦ€佸厓绱犳湁绋冲畾瀹夸富鍜岀ǔ瀹氬悕绉?6. YAML 閫夋嫨鍣ㄤ紭鍏堜娇鐢?`#name`
7. 鐢ㄤ緥鏂囦欢浣跨敤 `.yaml`
8. 鑷冲皯鏈変竴涓渶灏忓啋鐑熺敤渚嬭鐩栦富娴佺▼

## 10. 鎺ㄨ崘鐩綍绾﹀畾

寤鸿涓€涓〉闈㈣嚦灏戝寘鍚細

- `Assets/YourFeature/Uxml/YourPageWindow.uxml`
- `Assets/YourFeature/Uss/YourPageWindow.uss`
- `Assets/YourFeature/Editor/YourPageWindow.cs`
- `Assets/YourFeature/Yaml/your-page-smoke.yaml`

濡傛灉椤甸潰瑕佷綔涓烘爣鍑嗙ず渚嬶紝鍙弬鑰冨綋鍓嶄粨搴擄細

- `Samples~/Uxml`
- `Samples~/Uss`
- `Samples~/Yaml`
- `Samples~/Editor`

## 11. 鏈€閲嶈鐨勪笁鏉?
濡傛灉浣犲彧璁颁笁鏉★紝璇疯杩欎笁鏉★細

1. 鍏抽敭鍏冪礌涓€瀹氳鏈夌ǔ瀹?`name`
2. 鐐瑰嚮閫昏緫浼樺厛缁戝畾 `MouseUpEvent`
3. 鎵€鏈夊彲鏂█缁撴灉閮借钀藉湪绋冲畾銆佸懡鍚嶆槑纭殑鍏冪礌涓?