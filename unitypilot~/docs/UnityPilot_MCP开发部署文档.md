# UnityPilot MCP 鈥?寮€鍙戦儴缃叉枃妗?
## 鐩綍

> 绾﹀畾锛氭湰鏂囨墍鏈夊懡浠ょず渚嬮粯璁ゅ湪 `unitypilot_mcp/` 鐩綍鎵ц銆?
1. [鏋舵瀯姒傝堪](#1-鏋舵瀯姒傝堪)
2. [寮€鍙戞ā寮忚繍琛宂(#2-寮€鍙戞ā寮忚繍琛?
3. [淇敼 Python 鏂囦欢鍚庣殑澶勭悊](#3-淇敼-python-鏂囦欢鍚庣殑澶勭悊)
4. [閲嶅惎 MCP 鏈嶅姟鍣╙(#4-閲嶅惎-mcp-鏈嶅姟鍣?
5. [鍚?AI 宸ュ叿鐨勯噸杩炴搷浣淽(#5-鍚?ai-宸ュ叿鐨勯噸杩炴搷浣?
6. [涓€閿剼鏈鏄嶿(#6-涓€閿剼鏈鏄?
7. [VSCode Tasks 璇存槑](#7-vscode-tasks-璇存槑)
8. [甯歌寮€鍙戝満鏅€熸煡](#8-甯歌寮€鍙戝満鏅€熸煡)
9. [甯歌闂](#9-甯歌闂)

---

## 1. 鏋舵瀯姒傝堪

UnityPilot MCP 鐢?*涓や釜鏈嶅姟**缁勬垚锛岃繍琛屽湪鍚屼竴涓?Python 杩涚▼涓細

```text
鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?鈹?             Python 杩涚▼锛坲nitypilot-mcp锛?          鈹?鈹?                                                    鈹?鈹? 鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹? 鈹屸攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?  鈹?鈹? 鈹? MCP stdio 鏈嶅姟   鈹? 鈹? WS Orchestrator     鈹?  鈹?鈹? 鈹? 锛團astMCP锛?     鈹? 鈹? ws://127.0.0.1:8765  鈹?  鈹?鈹? 鈹? JSON-RPC/stdio   鈹? 鈹? WebSocket 鏈嶅姟绔?    鈹?  鈹?鈹? 鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹? 鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?  鈹?鈹?           鈹?                      鈹?              鈹?鈹斺攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹尖攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹尖攢鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹?             鈹?                      鈹?      AI 宸ュ叿璋冪敤                 Unity Editor
   锛圕laude/Cursor/VSCode锛?      锛圕# 瀹㈡埛绔級
```

**鍏抽敭鏈哄埗锛?*

- **MCP stdio 鏈嶅姟**锛氱敱 AI 宸ュ叿锛圕laude Code / Cursor / VSCode锛夊湪姣忔浼氳瘽鏃?*鑷姩鍚姩**锛岄€氳繃 stdin/stdout 閫氫俊锛屾棤闇€鎵嬪姩绠＄悊銆?- **WS 鏈嶅姟鍣?*锛氫綔涓?MCP 鏈嶅姟鐨勫瓙浠诲姟鍚姩锛孶nity Editor 鍦ㄦ墦寮€鏃惰嚜鍔ㄨ繛鎺ャ€?- **杩涚▼鐢熷懡鍛ㄦ湡**锛欰I 宸ュ叿鍚姩浼氳瘽 鈫?鍚姩 Python 杩涚▼ 鈫?Python 杩涚▼鍐?WS 鏈嶅姟鍣ㄥ惎鍔?鈫?Unity 杩炴帴銆?- **editable install**锛坄pip install -e ..`锛夛細Python 鐩存帴璇诲彇婧愮爜鐩綍锛?*淇敼 `.py` 鏂囦欢鏃犻渶閲嶆柊瀹夎**锛屼絾闇€瑕?*閲嶅惎杩涚▼**鎵嶈兘鐢熸晥銆?
---

## 2. 寮€鍙戞ā寮忚繍琛?
寮€鍙戞湡闂存湁涓ょ杩愯鏂瑰紡锛屾牴鎹満鏅€夋嫨锛?
### 鏂瑰紡 A锛氫粎鍚姩 WS 鏈嶅姟鍣紙鎺ㄨ崘鐢ㄤ簬寮€鍙戣皟璇曪級

```bash
# Windows
python -m unitypilot_mcp.main

# macOS / Linux
python3 -m unitypilot_mcp.main
```

杈撳嚭锛?```text
INFO     server listening on 127.0.0.1:8765
```

**鐗圭偣锛?*
- 缁堢涓彲鐩存帴鐪嬪埌 WS 鏈嶅姟鍣ㄦ棩蹇?- MCP 鍗忚閮ㄥ垎浠嶇敱 AI 宸ュ叿鑷姩鍚姩锛堟寚鍚戞簮鐮佺洰褰曪級
- 鎸?`Ctrl+C` 鍗冲彲鍋滄锛屼慨鏀逛唬鐮佸悗閲嶆柊杩愯

### 鏂瑰紡 B锛氬惎鍔ㄥ畬鏁?MCP+WS 杩涚▼

```bash
# Windows
python run_unitypilot_mcp.py

# macOS / Linux
python3 run_unitypilot_mcp.py
```

**鐗圭偣锛?*
- 涓?AI 宸ュ叿瀹為檯鍚姩鏂瑰紡瀹屽叏鐩稿悓
- stdin 澶勪簬闃诲绛夊緟鐘舵€侊紙姝ｅ父鐜拌薄锛岀瓑寰?MCP 娑堟伅杈撳叆锛?- 閫傚悎楠岃瘉瀹屾暣閾捐矾锛岃皟璇?MCP 鍗忚鏈韩

### 鏂瑰紡 C锛氫竴閿惎鍔紙鍚緷璧栨鏌?+ 鍐掔儫娴嬭瘯锛?
```bash
# Windows
deploy/dev.bat

# macOS / Linux
chmod +x deploy/dev.sh && ./deploy/dev.sh
```

**鐗圭偣锛?*
- 鑷姩妫€鏌?Python 鐗堟湰鍜屼緷璧?- 鑷姩鏉€鎺夋棫鐨?8765 绔彛杩涚▼
- 鍚姩鍚庤繍琛屽啋鐑熸祴璇曢獙璇佸崗璁?- 缁欏嚭鍚庣画鎿嶄綔鎻愮ず

### 棣栨寮€鍙戠幆澧冩惌寤?
```bash
# 1. 鍏嬮殕/杩涘叆椤圭洰鐩綍
cd d:/path/to/unitypilot           # Windows
cd /Users/name/path/to/unitypilot  # macOS

# 2. 瀹夎渚濊禆锛坋ditable 妯″紡锛屼慨鏀规簮鐮佹棤闇€閲嶈锛?pip install -e ..            # Windows
pip3 install -e ..           # macOS

# 3. 楠岃瘉瀹夎
python src/unitypilot_mcp/mcp_smoke_test.py
# 鏈熸湜锛歔OK] MCP stdio smoke test passed (11 tools)

# 4. 鍚姩寮€鍙戞湇鍔″櫒
python -m unitypilot_mcp.main
```

---

## 3. 淇敼 Python 鏂囦欢鍚庣殑澶勭悊

### 鏍稿績鍘熷垯

> **Python 鏄В閲婂瀷璇█**锛氳繘绋嬪惎鍔ㄦ椂鍔犺浇鎵€鏈夋ā鍧楀埌鍐呭瓨锛岃繍琛屾湡闂?*涓嶄細**鑷姩閲嶆柊鍔犺浇淇敼鐨勬枃浠躲€備慨鏀?`.py` 鏂囦欢鍚庯紝**蹇呴』閲嶅惎杩涚▼**鎵嶈兘鐢熸晥銆?
### 涓嶅悓鎯呭喌鐨勫鐞?
| 淇敼鍐呭 | 鏄惁闇€瑕侀噸鍚?| 璇存槑 |
| --- | --- | --- |
| `src/unitypilot_mcp/*.py`锛堜换鎰?Python 鏂囦欢锛?| **鏄?* | 杩涚▼閲嶅惎鍚庣珛鍗崇敓鏁?|
| `pyproject.toml`锛堜粎鏀圭増鏈彿/鎻忚堪锛?| 鍚?| 涓嶅奖鍝嶈繍琛屾椂 |
| `pyproject.toml`锛堟柊澧?淇敼渚濊禆锛?| **鏄?* | 杩橀渶鍏?`pip install -e ..` |
| `Assets/**/*.cs`锛圲nity C# 鏂囦欢锛?| 鍚︼紙Unity 渚э級 | Unity 浼氳嚜鍔ㄧ紪璇?|
| `.mcp.json`锛圡CP 閰嶇疆锛?| 闇€閲嶅惎 AI 宸ュ叿浼氳瘽 | AI 宸ュ叿璇诲彇閰嶇疆鏃舵墠鐢熸晥 |

### 蹇€熷垽鏂?
```text
鏀逛簡 .py 鏂囦欢锛?  鈹斺攢鈹€ 鏄?鈫?閲嶅惎 WS 鏈嶅姟鍣紙瑙佺 4 鑺傦級
       鈹斺攢鈹€ AI 宸ュ叿涓嬫璋冪敤宸ュ叿鏃惰嚜鍔ㄦ劅鐭ユ柊鐗堟湰

鏀逛簡 pyproject.toml 渚濊禆锛?  鈹斺攢鈹€ pip install -e ..  鈫?鍐嶉噸鍚湇鍔″櫒

鏀逛簡 .mcp.json 閰嶇疆锛?  鈹斺攢鈹€ 閲嶅惎 AI 宸ュ叿浼氳瘽锛堝叧闂啀鎵撳紑 Claude Code / Cursor锛?```

---

## 4. 閲嶅惎 MCP 鏈嶅姟鍣?
### 閲嶅惎鏈哄埗璇存槑

MCP 鏈嶅姟鍣ㄧ敱**涓ゅ眰**缁勬垚锛岄噸鍚瓥鐣ヤ笉鍚岋細

| 灞?| 杩涚▼ | 绠＄悊鏂?| 閲嶅惎鏂瑰紡 |
| --- | --- | --- | --- |
| **MCP stdio 灞?* | `python run_unitypilot_mcp.py` | AI 宸ュ叿鑷姩绠＄悊 | 鍏抽棴/閲嶅紑 AI 浼氳瘽 |
| **WS 鏈嶅姟灞?* | 鐩戝惉 `:8765` 鐨?Python 杩涚▼ | 寮€鍙戣€呮墜鍔ㄧ鐞?| 鏉€杩涚▼鍚庨噸鍚?|

**寮€鍙戞湡鏈€甯歌鐨勫満鏅?*锛氫慨鏀逛簡 Python 浠ｇ爜鍚庯紝鍙渶閲嶅惎 **WS 鏈嶅姟灞?*锛堢洃鍚?8765 鐨勮繘绋嬶級锛孉I 宸ュ叿浼氳瘽鍙互淇濇寔涓嶅姩锛屼笅娆¤皟鐢ㄥ伐鍏锋椂浼氳嚜鍔ㄧ敤鏂颁唬鐮佸鐞嗚姹傘€?
---

### 4.1 Windows 閲嶅惎

#### 鏂规硶 1锛氫竴閿剼鏈紙鎺ㄨ崘锛?
```bat
deploy/restart_mcp.bat
```

鍙屽嚮鎴栧湪鍛戒护鎻愮ず绗︿腑杩愯锛岃剼鏈細鑷姩鎵惧埌骞舵潃鎺?8765 绔彛杩涚▼锛岀劧鍚庨噸鍚€?
#### 鏂规硶 2锛氬懡浠ゆ彁绀虹鎵嬪姩鎿嶄綔

```bat
REM 姝ラ 1锛氭壘鍒板苟鏉€鎺?8765 绔彛杩涚▼
for /f "tokens=5" %p in ('netstat -ano ^| findstr ":8765 " ^| findstr LISTENING') do taskkill /PID %p /F

REM 姝ラ 2锛氶噸鍚湇鍔″櫒
python -m unitypilot_mcp.main
```

#### 鏂规硶 3锛氫换鍔＄鐞嗗櫒

1. 鎵撳紑浠诲姟绠＄悊鍣?鈫?璇︾粏淇℃伅
2. 鎵惧埌 `python.exe`锛堝彲閫氳繃"鍛戒护琛?鍒楃‘璁ゅ惈 `unitypilot`锛?3. 鍙抽敭 鈫?缁撴潫浠诲姟
4. 閲嶆柊杩愯 `python -m unitypilot_mcp.main`

#### 鏂规硶 4锛歏SCode Tasks锛堟帹鑽?VSCode 鐢ㄦ埛锛?
`Ctrl+Shift+P` 鈫?`Tasks: Run Task` 鈫?閫夋嫨 **`UnityPilot: 閲嶅惎 WS 鏈嶅姟鍣╜**

---

### 4.2 macOS 閲嶅惎

#### 鏂规硶 1锛氫竴閿剼鏈紙鎺ㄨ崘锛?
```bash
# 棣栨闇€瑕佽祴浜堟墽琛屾潈闄愶紙鍙渶涓€娆★級
chmod +x deploy/restart_mcp.sh

# 閲嶅惎
./deploy/restart_mcp.sh

# 浠呭仠姝紝涓嶉噸鍚?./deploy/restart_mcp.sh stop
```

#### 鏂规硶 2锛氱粓绔墜鍔ㄦ搷浣?
```bash
# 姝ラ 1锛氭壘鍒板苟鏉€鎺?8765 绔彛杩涚▼
kill -9 $(lsof -ti tcp:8765)

# 姝ラ 2锛氶噸鍚湇鍔″櫒
python3 -m unitypilot_mcp.main
```

#### 鏂规硶 3锛氬鏋滄湇鍔″櫒鍦ㄥ墠鍙扮粓绔繍琛?
鐩存帴鍦ㄧ粓绔寜 `Ctrl+C`锛岀劧鍚庯細

```bash
python3 -m unitypilot_mcp.main
```

#### 鏂规硶 4锛歏SCode Tasks

`Cmd+Shift+P` 鈫?`Tasks: Run Task` 鈫?閫夋嫨 **`UnityPilot: 閲嶅惎 WS 鏈嶅姟鍣╜**

---

### 4.3 纭閲嶅惎鎴愬姛

閲嶅惎鍚庣‘璁ゆ湇鍔″櫒宸插氨缁細

```bash
# Windows锛堝湪鏂扮殑鍛戒护鎻愮ず绗︾獥鍙ｏ級
netstat -ano | findstr ":8765 " | findstr LISTENING

# macOS / Linux
lsof -i tcp:8765 -sTCP:LISTEN

# 浠讳綍骞冲彴锛氬畬鏁村啋鐑熸祴璇?python src/unitypilot_mcp/mcp_smoke_test.py   # Windows
python3 src/unitypilot_mcp/mcp_smoke_test.py  # macOS
```

鏈熸湜杈撳嚭锛?```text
[OK] MCP stdio smoke test passed (11 tools)
```

---

## 5. 鍚?AI 宸ュ叿鐨勯噸杩炴搷浣?
### 鑳屾櫙

WS 鏈嶅姟鍣ㄩ噸鍚悗锛?
- **Unity Editor**锛氫細鑷姩妫€娴嬫柇绾垮苟鍦ㄧ害 2鈥? 绉掑唴閲嶆柊杩炴帴锛屾棤闇€鎵嬪姩鎿嶄綔銆?- **AI 宸ュ叿锛圡CP 灞傦級**锛歁CP stdio 杩涚▼閫氬父涓?WS 杩涚▼鍒嗙锛圓I 宸ュ叿姣忔璋冪敤宸ュ叿鏃堕噸鏂颁笌鏈嶅姟鍣ㄩ€氫俊锛夛紝涓€鑸?*鏃犻渶浠讳綍鎿嶄綔**锛屼笅娆¤皟鐢ㄥ伐鍏峰嵆鍙€?
浠ヤ笅鎯呭喌闇€瑕佹墜鍔ㄩ噸杩?AI 宸ュ叿锛堥€氬父鏄?`.mcp.json` 閰嶇疆鏀瑰姩锛屾垨 MCP stdio 杩涚▼寮傚父閫€鍑猴級锛?
### Claude Code

```bash
# 鏂规硶 1锛氶噸鏂版鏌ヨ繛鎺ョ姸鎬?claude mcp list
# 濡傛灉鏄剧ず 鉁?Connected锛屾棤闇€鎿嶄綔

# 鏂规硶 2锛氶噸鍚細璇濓紙娓呯┖涓婁笅鏂囷級
# 鍦?Claude Code 绐楀彛杈撳叆锛?reset 鎴栧叧闂綋鍓嶄細璇濋噸鏂版墦寮€

# 鏂规硶 3锛氬懡浠よ寮哄埗閲嶆柊鍔犺浇
claude mcp restart unitypilot  # 濡傛灉姝ゅ懡浠ゅ瓨鍦?```

### Cursor

1. `Ctrl+Shift+P`锛圵in锛? `Cmd+Shift+P`锛圡ac锛夆啋 鎼滅储 `MCP`
2. 鎵惧埌 `Restart MCP Server` 鎴栬繘鍏?**Settings 鈫?MCP**
3. 鐐瑰嚮 `unitypilot` 鏃佽竟鐨勫埛鏂?閲嶅惎鍥炬爣

鎴栫洿鎺ュ叧闂苟閲嶆柊鎵撳紑 Cursor 绐楀彛銆?
### VSCode Copilot

1. `Ctrl+Shift+P` / `Cmd+Shift+P` 鈫?鎼滅储 `MCP`
2. 閫夋嫨 `GitHub Copilot: Restart MCP Server`
3. 鎴栧叧闂?VSCode 閲嶆柊鎵撳紑椤圭洰

### OpenCode

閲嶅惎 OpenCode 搴旂敤鍗冲彲锛孧CP 鏈嶅姟鍣ㄤ細鍦ㄦ柊浼氳瘽鏃惰嚜鍔ㄥ惎鍔ㄣ€?
---

## 6. 涓€閿剼鏈鏄?
椤圭洰涓殑閮ㄧ讲鑴氭湰缁熶竴鏀惧湪 `deploy/`锛學indows 鐢?`.bat`锛宮acOS/Linux 鐢?`.sh`锛?
| 鑴氭湰 | 骞冲彴 | 鍔熻兘 |
| --- | --- | --- |
| `deploy/dev.bat` / `deploy/dev.sh` | Win / Mac | 寮€鍙戞ā寮忎竴閿惎鍔細妫€鏌ヤ緷璧?鈫?娓呯悊鏃ц繘绋?鈫?鍚姩鏈嶅姟鍣?鈫?鍐掔儫娴嬭瘯 |
| `deploy/restart_mcp.bat` / `deploy/restart_mcp.sh` | Win / Mac | 閲嶅惎 WS 鏈嶅姟鍣細鏉€鏃ц繘绋?鈫?鍚姩鏂拌繘绋?鈫?纭绔彛灏辩华 |
| `deploy/build_release.py` | 閫氱敤锛圥ython锛?| 鎵撳寘鍙戝竷锛氭瀯寤?wheel 鈫?涓嬭浇渚濊禆 鈫?鐢熸垚閰嶇疆妯℃澘 鈫?鎵?zip |

閮ㄧ讲璧勬簮缁熶竴鏀惧湪 `deploy/resources/`锛堜緥濡傚巻鍙插彂甯?zip銆佸畨瑁呯礌鏉愮瓑锛夈€?
### deploy/dev.bat / deploy/dev.sh 璇︾粏娴佺▼

```text
1. 妫€鏌?Python 鐗堟湰锛堥渶 3.11+锛?2. 妫€鏌?mcp / websockets 妯″潡鏄惁瀹夎锛岃嫢缂哄け鑷姩鎵ц pip install -e ..
3. 閰嶇疆 `PYTHONPATH=<椤圭洰鏍?/unitypilot_mcp/src`
4. 鏉€鎺夊崰鐢?8765 绔彛鐨勬棫杩涚▼
5. 鍚庡彴鍚姩 WS 鏈嶅姟鍣紙python -m unitypilot_mcp.main锛?6. 绛夊緟绔彛灏辩华锛堟渶闀?10 绉掞級
7. 杩愯 mcp_smoke_test.py 楠岃瘉鍗忚
8. 杈撳嚭鎿嶄綔鎻愮ず
```

### deploy/restart_mcp.bat 鍙傛暟

```bat
deploy/restart_mcp.bat         # 鍋滄鏃ц繘绋嬪苟閲嶅惎
```

### deploy/restart_mcp.sh 鍙傛暟

```bash
./deploy/restart_mcp.sh            # 鍋滄鏃ц繘绋嬪苟閲嶅惎锛堥粯璁わ級
./deploy/restart_mcp.sh stop       # 浠呭仠姝紝涓嶉噸鍚?./deploy/restart_mcp.sh restart    # 绛夊悓浜庨粯璁?```

---

## 7. VSCode Tasks 璇存槑

鍦?`.vscode/tasks.json` 涓畾涔変簡 7 涓换鍔★紝閫氳繃 `Ctrl+Shift+P` 鈫?`Tasks: Run Task` 璁块棶锛?
| 浠诲姟鍚?| 蹇嵎瑙﹀彂 | 璇存槑 |
| --- | --- | --- |
| **寮€鍙戞ā寮忓惎鍔?* | Run Task | 鍚姩 WS 鏈嶅姟鍣紝杈撳嚭淇濈暀鍦ㄤ笓鐢ㄧ粓绔潰鏉?|
| **鍋滄 WS 鏈嶅姟鍣?* | Run Task | 鏉€鎺?8765 绔彛杩涚▼锛堣法骞冲彴鍛戒护锛?|
| **閲嶅惎 WS 鏈嶅姟鍣?* | Run Task | 渚濇鎵ц鍋滄 + 鍚姩锛?*淇敼浠ｇ爜鍚庣敤姝や换鍔?*锛?|
| **MCP 鍐掔儫娴嬭瘯** | Run Task | 楠岃瘉 MCP 鍗忚锛屾棤闇€ Unity / WS 鏈嶅姟鍣ㄨ繍琛?|
| **瀹夎/鏇存柊渚濊禆** | Run Task | `pip install -e ..`锛岄娆℃垨渚濊禆鍙樻洿鍚庤繍琛?|
| **鎵撳寘鍙戝竷锛堝綋鍓嶅钩鍙帮級** | Run Task | 杩愯 `deploy/build_release.py` |
| **鍚姩 MCP+WS锛坰tdio 璋冭瘯锛?* | Run Task | 瀹屾暣杩涚▼璋冭瘯鐢紝stdio 闃诲灞炴甯?|

**鎺ㄨ崘缁戝畾蹇嵎閿?*锛堝湪 VSCode `keybindings.json` 涓坊鍔狅級锛?
```json
[
  {
    "key": "ctrl+shift+r",
    "command": "workbench.action.tasks.runTask",
    "args": "UnityPilot: 閲嶅惎 WS 鏈嶅姟鍣?
  }
]
```

---

## 8. 甯歌寮€鍙戝満鏅€熸煡

### 鍦烘櫙 1锛氫慨鏀逛簡宸ュ叿閫昏緫锛坄tool_facade.py` 绛夛級

```bash
# Windows
deploy/restart_mcp.bat

# macOS
./deploy/restart_mcp.sh

# VSCode锛堜换浣曞钩鍙帮級
Ctrl/Cmd+Shift+P 鈫?Tasks: Run Task 鈫?UnityPilot: 閲嶅惎 WS 鏈嶅姟鍣?```

淇敼绔嬪嵆鐢熸晥锛孶nity 浼氳嚜鍔ㄩ噸杩烇紝AI 宸ュ叿涓嬫璋冪敤宸ュ叿鍗崇敤鏂颁唬鐮併€?
### 鍦烘櫙 2锛氭柊澧炰簡渚濊禆鍖?
```bash
# 1. 鍦?pyproject.toml [project.dependencies] 涓坊鍔犱緷璧?# 2. 瀹夎
pip install -e ..       # Windows
pip3 install -e ..      # macOS

# 3. 閲嶅惎鏈嶅姟鍣?deploy/restart_mcp.bat        # Windows
./deploy/restart_mcp.sh       # macOS
```

### 鍦烘櫙 3锛氫慨鏀逛簡 MCP 宸ュ叿瀹氫箟锛坄mcp_stdio_server.py`锛?
```bash
# MCP stdio 杩涚▼鐢?AI 宸ュ叿绠＄悊锛岄渶瑕?AI 宸ュ叿閲嶆柊鍚姩杩涚▼
# 姝ラ 1锛氶噸鍚?WS 鏈嶅姟鍣紙纭繚浠ｇ爜鏇存柊锛?deploy/restart_mcp.bat / ./deploy/restart_mcp.sh

# 姝ラ 2锛氬湪 AI 宸ュ叿涓Е鍙?MCP 閲嶈繛
# Claude Code锛歝laude mcp list  锛堜細鑷姩妫€娴嬶級
# Cursor锛歋ettings 鈫?MCP 鈫?鍒锋柊
# VSCode锛欳md+Shift+P 鈫?Restart MCP Server
```

### 鍦烘櫙 4锛氫慨鏀逛簡 `.mcp.json` 閰嶇疆

```bash
# 鍙渶閲嶅惎 AI 宸ュ叿浼氳瘽锛屼笉闇€瑕侀噸鍚?Python 鏈嶅姟鍣?# Claude Code锛氬叧闂綋鍓嶇獥鍙ｉ噸鏂版墦寮€
# Cursor锛氶噸鍚?Cursor
# VSCode锛氶噸杞界獥鍙ｏ紙Ctrl+Shift+P 鈫?Reload Window锛?```

### 鍦烘櫙 5锛氬噯澶囨彁娴?/ 鍙戝竷鏂扮増鏈?
```bash
# 1. 鏇存柊鐗堟湰鍙?#    淇敼 pyproject.toml 涓殑 version = "x.y.z"

# 2. 鎵撳寘
python deploy/build_release.py --platform win   # Windows 鏈哄櫒涓?python3 deploy/build_release.py --platform mac  # macOS 鏈哄櫒涓?
# 3. 楠岃瘉浜х墿
ls dist/
# unitypilot-mcp-x.y.z-win64.zip
# unitypilot-mcp-x.y.z-macos.zip
```

---

## 9. 甯歌闂

### Q: 閲嶅惎鍚?Unity 涓嶈嚜鍔ㄩ噸杩?
妫€鏌?Unity 鎺у埗鍙版槸鍚︽湁閿欒銆俇nity 渚х殑閲嶈繛闂撮殧绾?2鈥? 绉掞紝鏈€澶氱瓑寰?10 绉掋€傝嫢瓒呮椂锛?
1. Unity 鑿滃崟 鈫?**upilot/upilot**锛堢姸鎬佺獥鍙ｏ級锛岀偣鍑?閲嶆柊杩炴帴"鎸夐挳
2. 鎴栭噸鍚?Unity Editor锛堜細鍦ㄥ惎鍔ㄦ椂鑷姩杩炴帴锛?
### Q: 鍋滄鏈嶅姟鍣ㄥ悗绔彛浠嶆樉绀哄崰鐢紙Windows锛?
TIME_WAIT 鐘舵€佺殑 TCP 杩炴帴锛?
```bat
REM 纭鏄惁浠嶆湁 LISTENING 鐘舵€侊紙LISTENING 鎵嶅奖鍝嶉噸鍚級
netstat -ano | findstr ":8765"
REM 鍙湁 LISTENING 琛屾墠闇€瑕佸鐞嗭紝TIME_WAIT 鍙拷鐣?```

### Q: macOS 涓?`lsof` 鎵句笉鍒拌繘绋嬩絾閲嶅惎澶辫触

```bash
# 浣跨敤 ss 鎴?netstat 鏇夸唬
netstat -an | grep 8765
# 鎴栫洿鎺ョ敤 fuser
fuser -k 8765/tcp
```

### Q: 淇敼浠ｇ爜鍚庨噸鍚紝AI 宸ュ叿浠嶆姤鏃ч敊璇?
AI 宸ュ叿鍙兘缂撳瓨浜嗕笂涓€娆＄殑宸ュ叿璋冪敤缁撴灉銆傚湪 AI 宸ュ叿瀵硅瘽妗嗕腑鏄庣‘璇存槑"閲嶆柊璋冪敤宸ュ叿"锛屾垨閲嶅惎 AI 宸ュ叿浼氳瘽銆?
### Q: `pip install -e ..` 姣忔閮藉緢鎱?
鍘熷洜锛氫緷璧栧凡瀹夎浣?pip 浠嶅湪妫€鏌ユ洿鏂般€傚姞 `--no-deps` 鍙烦杩囦緷璧栨鏌ワ細

```bash
pip install -e .. --no-deps -q
```

浠呭湪鏂板渚濊禆鏃舵墠闇€瑕佸畬鏁村畨瑁咃細

```bash
pip install -e .. -q
```

### Q: 涓や釜 AI 宸ュ叿鍚屾椂浣跨敤锛岀鍙ｅ啿绐?
鍚屼竴鍙版満鍣ㄥ悓鏃惰繍琛屼袱涓?AI 宸ュ叿浼氳瘽锛屽畠浠悇鑷細灏濊瘯鍚姩涓€涓?MCP 鏈嶅姟鍣紝閮戒細缁戝畾 `:8765`锛岀浜屼釜浼氬け璐ャ€傝В鍐虫柟妗堬細

- 鍙涓€涓?AI 宸ュ叿浼氳瘽澶勪簬娲诲姩鐘舵€?- 鎴栦慨鏀瑰叾涓竴涓殑绔彛锛堥渶鍚屾椂淇敼 Unity 渚ч厤缃級
