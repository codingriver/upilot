# UnityUIFlow Agent MCP 娴嬭瘯寮哄埗瑙勮寖

## 1. 鐩爣

鏈枃妗ｆ槸缁?Agent 浣跨敤鐨勫己鍒舵墽琛岃鑼冦€?
鐩殑鍙湁涓€涓細Agent 鍦ㄦ湰椤圭洰涓墽琛?YAML 鑷姩鍖栨祴璇曟椂锛屽繀椤婚€氳繃 MCP 鏈嶅姟鍣紝骞朵笖蹇呴』浣跨敤 Headed 妯″紡銆?
杩欎笉鏄缓璁紝鑰屾槸纭€ц姹傘€?
## 2. 閫傜敤鑼冨洿

鏈枃妗ｉ€傜敤浜庝互涓嬪満鏅細

- Agent 楠岃瘉 YAML 鐢ㄤ緥
- Agent 璋冭瘯 YAML 鐢ㄤ緥澶辫触
- Agent 寮€鍙戞柊椤甸潰鍚庤ˉ鍏呮垨淇敼 YAML 娴嬭瘯
- Agent 淇鑷姩鍖栨祴璇曠浉鍏?Bug
- Agent 鎵ц鍩轰簬 UnityUIFlow 鐨?E2E 楠屾敹

鏈枃妗ｄ笉闄愬埗 Agent 淇敼浠ｇ爜銆佷慨澶?Bug銆佸紑鍙戝姛鑳芥湰韬€?
鏈枃妗ｅ彧寮哄埗绾︽潫鈥滄祴璇曟墽琛屾柟寮忊€濄€?
## 3. 纭€ц鍒?
Agent 蹇呴』閬靛畧浠ヤ笅瑙勫垯锛?
1. **YAML 娴嬭瘯鍙兘閫氳繃 MCP 鏈嶅姟鍣ㄦ墽琛屻€?*
2. **YAML 娴嬭瘯蹇呴』浣跨敤 Headed 妯″紡銆?*
3. **娌℃湁鍙敤 MCP 鏈嶅姟鍣ㄦ椂锛岀姝㈣繍琛?YAML 娴嬭瘯銆?*
4. **涓嶈兘鎺ョ褰撳墠 MCP 鏈嶅姟鍣ㄦ椂锛岀姝㈠亣瑁呮祴璇曞凡鎵ц銆?*
5. **绂佹鐢?CLI銆乁nity Test Runner銆佷复鏃惰剼鏈€佹墜宸ョ偣鍑绘垨鍏朵粬鏇夸唬鏂瑰紡鍐掑厖 YAML MCP 娴嬭瘯缁撴灉銆?*
6. **Agent 鍙互鍦ㄦ病鏈?MCP 鐨勬儏鍐典笅淇敼浠ｇ爜銆佷慨澶?Bug銆佸疄鐜伴渶姹傦紝浣嗕笉鑳藉０绉板凡瀹屾垚 YAML 娴嬭瘯楠岃瘉銆?*
7. **鍑℃槸杈撳嚭鈥滃凡楠岃瘉 YAML 娴嬭瘯閫氳繃鈥濊繖绫荤粨璁猴紝鍓嶆彁蹇呴』鏄?MCP 宸ュ叿鐪熷疄鎵ц鎴愬姛銆?*

## 4. Headed 妯″紡瑕佹眰

鎵ц YAML 娴嬭瘯鍓嶏紝Agent 蹇呴』纭椤圭洰鏍圭洰褰曞瓨鍦?[`.unityuiflow.json`](d:/UnityUIFlow/.unityuiflow.json)锛屼笖鑷冲皯婊¤冻浠ヤ笅瑕佹眰锛?
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

鍏朵腑锛?
- `"headed": true` 鏄己鍒堕」
- 鑻?`headed` 涓嶆槸 `true`锛孉gent 涓嶅緱鎵ц YAML 娴嬭瘯
- Agent 鍙戠幇閰嶇疆涓嶆弧瓒宠姹傛椂锛屽簲鍏堜慨姝ｉ厤缃紝鍐嶇户缁悗缁祴璇曟祦绋?
## 5. MCP 鏈嶅姟鍣ㄥ己鍒剁瓥鐣?
鏈」鐩殑 MCP 鏈嶅姟鍣ㄤ互 [`.vscode/mcp.json`](d:/UnityUIFlow/.vscode/mcp.json) 涓哄噯銆?
褰撳墠绾﹀畾鐨勬湇鍔″櫒涓?`unitypilot`锛屽惎鍔ㄦ柟寮忎负 `stdio`銆?
Agent 蹇呴』鎸変互涓嬮『搴忓鐞嗭細

1. 鍏堟鏌ュ綋鍓嶆槸鍚﹀凡缁忓瓨鍦ㄥ彲鐢?MCP 鏈嶅姟鍣ㄣ€?2. 瀵?`stdio` 鍨?MCP锛屼笉鑳藉彧妫€鏌ヨ繘绋嬪瓨鍦ㄤ笌鍚︼紝蹇呴』纭鈥滃綋鍓嶆墽琛岀幆澧冨凡缁忔帴绠★紝骞朵笖鍙互鐩存帴璋冪敤 MCP tool鈥濄€?3. 濡傛灉褰撳墠鐜宸茬粡鎺ョ璇?MCP 鏈嶅姟鍣紝涓?MCP tool 鍙互鐩存帴浣跨敤锛屽垯鐩存帴澶嶇敤锛屼笉寰楅噸澶嶅惎鍔ㄣ€?4. 濡傛灉 MCP 鏈嶅姟鍣ㄦ湭鍚姩锛屾垨鑰呭凡鏈夎繘绋嬩絾褰撳墠鐜涓嶈兘鎺ョ銆佷笉鑳界洿鎺ヨ皟鐢紝鍒欏繀椤诲叧闂棫 MCP 杩涚▼銆?5. 鍏抽棴鏃ц繘绋嬪悗锛屾寜 [`.vscode/mcp.json`](d:/UnityUIFlow/.vscode/mcp.json) 閲嶆柊鍚姩 `unitypilot` MCP 鏈嶅姟鍣ㄣ€?6. 鏂板惎鍔ㄧ殑 MCP 鏈嶅姟鍣ㄥ簲淇濇寔鍚庡彴甯搁┗杩愯锛岄櫎闈炵敤鎴锋槑纭姹傚叧闂紝鍚﹀垯涓嶈鍦ㄦ祴璇曠粨鏉熷悗鑷姩鍋滄銆?
## 6. MCP 宸ュ叿璋冪敤涓庡彲鐢ㄦ€ф娴?
Agent 蹇呴』鏄惧紡妫€娴嬧€滃伐鍏锋槸鍚﹀彲璋冪敤鈥濓紝涓嶈兘鍙湅杩涚▼銆佺鍙ｃ€佹棩蹇楁垨閰嶇疆鏂囦欢銆?
鎺ㄨ崘浼樺厛妫€鏌ヤ互涓?MCP 宸ュ叿鏄惁宸插湪褰撳墠鐜涓湡瀹炴毚闇插苟鍙皟鐢細

- `unity_mcp_status`
- `unity_editor_e2e_run`

鍦ㄩ儴鍒嗗涓荤幆澧冧腑锛屽伐鍏峰悕鍙兘甯︽湁鍓嶇紑锛屼緥濡傦細

- `mcp_unitypilot_unity_mcp_status`
- `mcp_unitypilot_unity_editor_e2e_run`

鍒ゅ畾瑙勫垯濡備笅锛?
1. 鑻ュ綋鍓嶇幆澧冧腑鏈毚闇?`unitypilot` 鐩稿叧 MCP tool锛屽垯瑙嗕负褰撳墠鐜**涓嶅彲鐢?MCP**銆?2. 鑻ュ伐鍏峰悕瀛樺湪锛屼絾璋冪敤澶辫触銆佽秴鏃舵垨鏃犳硶杩炴帴 Unity锛屼篃瑙嗕负褰撳墠鐜**涓嶅彲鐢?MCP**銆?3. 鍙湁鍦ㄥ綋鍓嶇幆澧冧腑鐪熷疄璋冪敤鎴愬姛鍚庯紝鎵嶅彲浠ヨ瀹氣€滃凡鎺ョ MCP 涓斿彲鎵ц YAML 娴嬭瘯鈥濄€?4. 鏂囨。涓殑宸ュ叿鍚嶇ず渚嬬敤浜庡府鍔╄瘑鍒拰璋冪敤锛屼笉瑕佹眰鎵€鏈夊涓婚兘浣跨敤瀹屽叏鐩稿悓鐨勬渶缁堝墠缂€锛涗互褰撳墠浼氳瘽瀹為檯鏆撮湶鐨勫伐鍏峰悕涓哄噯銆?
鎺ㄨ崘妫€娴嬮『搴忥細

1. 鍏堟鏌?`unity_mcp_status` 鎴栧叾瀹夸富鏄犲皠鍚庣殑绛変环宸ュ叿鏄惁瀛樺湪銆?2. 璋冪敤璇ュ伐鍏凤紝纭 MCP 杩涚▼銆佸綋鍓嶅伐浣滅洰褰曘€乁nity 杩炴帴鐘舵€併€佺紪璇戠姸鎬佹甯搞€?3. 鍐嶈皟鐢?`unity_editor_e2e_run` 鎴栧叾瀹夸富鏄犲皠鍚庣殑绛変环宸ュ叿鎵ц YAML 娴嬭瘯銆?
## 7. 鏍囧噯 YAML E2E 宸ュ叿绀轰緥

鎺ㄨ崘浣跨敤鐨?YAML 鎵ц宸ュ叿璋冪敤绀轰緥濡備笅锛?
```text
宸ュ叿璋冪敤: mcp_unitypilot_unity_editor_e2e_run
鍙傛暟:
  - specPath: Samples~/Yaml/01-basic-login.yaml
  - artifactDir: D:\UnityUIFlow\artifacts
  - exportZip: true
  - stopOnFirstFailure: true
  - webhookOnFailure: true
```

鑻ュ綋鍓嶅涓讳笉甯?`mcp_unitypilot_` 鍓嶇紑锛屽垯绛変环宸ュ叿閫氬父涓猴細

```text
宸ュ叿璋冪敤: unity_editor_e2e_run
鍙傛暟:
  - specPath: Samples~/Yaml/01-basic-login.yaml
  - artifactDir: D:\UnityUIFlow\artifacts
  - exportZip: true
  - stopOnFirstFailure: true
  - webhookOnFailure: true
```

鎺ㄨ崘鍏堟墽琛岀殑鐘舵€佹鏌ュ伐鍏风ず渚嬪涓嬶細

```text
宸ュ叿璋冪敤: mcp_unitypilot_unity_mcp_status
```

鎴栵細

```text
宸ュ叿璋冪敤: unity_mcp_status
```

Agent 搴斿熀浜庡綋鍓嶄細璇濆疄闄呮毚闇茬殑宸ュ叿鍚嶉€夋嫨绛変环璋冪敤鏂瑰紡銆?
## 8. 鏃?MCP 鏃剁殑琛屼负杈圭晫

褰撳嚭鐜颁互涓嬩换涓€鎯呭喌鏃讹紝瑙嗕负鈥滄病鏈夊彲鐢?MCP 鏈嶅姟鍣ㄢ€濓細

- MCP 鏈嶅姟鍣ㄨ繘绋嬩笉瀛樺湪
- MCP 鏈嶅姟鍣ㄨ櫧鐒跺瓨鍦紝浣嗗綋鍓嶇幆澧冩棤娉曟帴绠?- MCP tool 鏃犳硶鐩存帴璋冪敤
- MCP 涓?Unity Editor 鏈缓绔嬫湁鏁堣繛鎺?- Headed 妯″紡涓嶆弧瓒宠姹?
姝ゆ椂 Agent 鐨勫厑璁歌涓哄彧鏈夛細

- 闃呰浠ｇ爜
- 淇敼浠ｇ爜
- 淇 Bug
- 寮€鍙戦渶姹?- 璋冩暣 YAML銆乁XML銆乁SS銆丆# 瀹炵幇
- 鍑嗗鍚庣画娴嬭瘯鎵€闇€閰嶇疆

姝ゆ椂 Agent 鐨勭姝㈣涓哄寘鎷細

- 杩愯 YAML 娴嬭瘯骞跺皢缁撴灉浣滀负姝ｅ紡楠岃瘉缁撹
- 澹扮О鈥滄祴璇曞凡閫氳繃鈥?- 鐢ㄥ叾浠栨柟寮忔浛浠?MCP 娴嬭瘯鍚庣粰鍑虹瓑浠风粨璁?
姝ｇ‘琛ㄨ堪搴斾负锛?
- 鈥滀唬鐮佸凡淇敼锛屼絾 YAML 娴嬭瘯灏氭湭鎵ц锛屽洜涓哄綋鍓嶆病鏈夊彲鐢?MCP 鏈嶅姟鍣ㄣ€傗€?- 鈥滃綋鍓嶄粎瀹屾垚瀹炵幇锛屾湭瀹屾垚 MCP 楠岃瘉銆傗€?
## 9. 鏍囧噯鎵ц娴佺▼

Agent 鎵ц YAML 娴嬭瘯鏃讹紝蹇呴』閬靛惊浠ヤ笅娴佺▼锛?
1. 纭 [`.unityuiflow.json`](d:/UnityUIFlow/.unityuiflow.json) 涓?`headed` 涓?`true`銆?2. 妫€鏌ュ綋鍓?MCP 鏈嶅姟鍣ㄦ槸鍚﹀瓨鍦ㄤ笖褰撳墠鐜鍙洿鎺ユ帴绠°€?3. 妫€娴?`unity_mcp_status` / `mcp_unitypilot_unity_mcp_status` 绛夌姸鎬佸伐鍏锋槸鍚︾湡瀹炲彲璋冪敤銆?4. 鑻ュ彲鎺ョ锛岀洿鎺ュ鐢ㄧ幇鏈?MCP 鏈嶅姟鍣ㄣ€?5. 鑻ヤ笉鍙帴绠★紝鍏堝叧闂棫 MCP 杩涚▼銆?6. 鎸?[`.vscode/mcp.json`](d:/UnityUIFlow/.vscode/mcp.json) 鍦ㄥ悗鍙板惎鍔ㄦ柊鐨?`unitypilot` MCP 鏈嶅姟鍣ㄣ€?7. 鍐嶆纭 MCP tool 宸插彲璋冪敤锛屼笖 Unity Editor 宸茶繛鎺ャ€?8. 閫氳繃 `unity_editor_e2e_run` / `mcp_unitypilot_unity_editor_e2e_run` 鎵ц鐩爣 YAML 鐢ㄤ緥銆?9. 鍩轰簬 MCP 杩斿洖缁撴灉銆佷骇鐗┿€佹埅鍥俱€佹棩蹇楃粰鍑烘祴璇曠粨璁恒€?
## 10. 娴嬭瘯缁撹杈撳嚭瑙勫垯

Agent 杈撳嚭娴嬭瘯缁撹鏃讹紝蹇呴』鍖哄垎浠ヤ笅涓夌鐘舵€侊細

### 10.1 宸查€氳繃 MCP 瀹屾垚娴嬭瘯

鍙湁鍦?MCP 宸ュ叿鐪熷疄鎵ц鎴愬姛鍚庯紝鎵嶈兘杈撳嚭锛?
- 鈥滃凡閫氳繃 MCP 鎵ц YAML 娴嬭瘯鈥?- 鈥滆 YAML 鐢ㄤ緥宸查獙璇侀€氳繃鈥?- 鈥滆闂宸查€氳繃 MCP 鍥炲綊楠岃瘉鈥?
### 10.2 宸插畬鎴愬疄鐜帮紝浣嗘湭瀹屾垚娴嬭瘯

褰撲唬鐮佸凡鏀瑰畬锛屼絾 MCP 鏉′欢涓嶆弧瓒虫椂锛屽簲杈撳嚭锛?
- 鈥滀唬鐮佷慨鏀瑰凡瀹屾垚鈥?- 鈥滃皻鏈€氳繃 MCP 鎵ц YAML 娴嬭瘯鈥?- 鈥滃綋鍓嶇己灏戝彲鐢?MCP 鏈嶅姟鍣ㄦ垨褰撳墠鐜鏃犳硶鎺ョ锛屽洜姝や笉鑳界粰鍑烘寮?YAML 楠岃瘉缁撹鈥?
### 10.3 MCP 鍚姩鎴栨帴绠″け璐?
褰?MCP 鏈嶅姟鍣ㄦ棤娉曟帴绠℃垨鍚姩澶辫触鏃讹紝搴旀槑纭鏄庡け璐ョ偣锛屼緥濡傦細

- 鏃犳硶鎺ョ宸叉湁 `stdio` MCP
- 鏃ц繘绋嬪叧闂け璐?- 鏂?MCP 杩涚▼鍚姩澶辫触
- Unity 鏈繛鎺ュ埌 MCP

涓嶅緱鎶婅繖绫诲け璐ュ啓鎴愨€滄祴璇曞け璐モ€濅笌鈥滀唬鐮佸け璐モ€濇贩涓轰竴璋堛€?
## 11. 鏄庣‘绂佹鐨勯敊璇仛娉?
浠ヤ笅鍋氭硶涓€寰嬬姝細

- 鐪嬪埌 MCP 杩涚▼瀛樺湪锛屽氨榛樿褰撳墠鐜涓€瀹氬彲鐢?- 娌℃湁鍏堟鏌?`unity_mcp_status` 鎴栫瓑浠风姸鎬佸伐鍏凤紝灏辩洿鎺ヨ瀹?MCP 鍙敤
- 鍙仛浠ｇ爜闈欐€佹鏌ワ紝灏卞绉?YAML 鐢ㄤ緥宸查獙璇?- 鏀瑰畬浠ｇ爜鍚庣洿鎺ョ敤 CLI 璺?YAML锛屽啀鎶婄粨鏋滃綋浣?MCP 娴嬭瘯
- 鍦?`headed=false` 鐨勬儏鍐典笅缁х画璺?YAML 楠屾敹
- 鍚姩浜嗘柊鐨?MCP 鍚庯紝鍦ㄤ换鍔＄粨鏉熸椂绉佽嚜鍏抽棴鍚庡彴 MCP
- 娌℃湁鐪熷疄璋冪敤 MCP tool锛屽嵈鍦ㄧ粨璁洪噷鍐欌€滃凡娴嬭瘯閫氳繃鈥?
## 12. 瀵?Agent 鐨勬渶缁堟墽琛岃姹?
Agent 鍦ㄦ湰椤圭洰涓殑宸ヤ綔鍘熷垯濡備笅锛?
- 寮€鍙戝彲浠ュ厛琛?- 淇?Bug 鍙互鍏堣
- 鏀?YAML 鍙互鍏堣
- 浣嗗彧瑕佽繘鍏モ€滄祴璇曢獙璇佲€濋樁娈碉紝灏卞繀椤讳娇鐢?MCP
- 鍙鎵ц YAML 娴嬭瘯锛屽氨蹇呴』浣跨敤 Headed 妯″紡
- 娌℃湁 MCP锛屽氨鍋滄娴嬭瘯锛屼笉寰椾吉閫犳祴璇曢棴鐜?
涓€鍙ヨ瘽鎬荤粨锛?
**鏈」鐩腑锛孻AML 娴嬭瘯 = MCP 鏈嶅姟鍣?+ Headed 妯″紡銆傜己涓€涓嶅彲銆?*
