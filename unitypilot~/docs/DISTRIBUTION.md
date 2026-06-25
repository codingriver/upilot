# UnityPilot MCP 鈥?鎵撳寘銆佸彂甯冧笌瀹夎鎸囧崡

## 鐩綍

> 绾﹀畾锛氭湰鏂囨墍鏈夊懡浠ょず渚嬮粯璁ゅ湪 `unitypilot_mcp/` 鐩綍鎵ц銆?
1. [椤圭洰缁撴瀯](#1-椤圭洰缁撴瀯)
2. [涓€閿墦鍖呰剼鏈琞(#2-涓€閿墦鍖呰剼鏈?
3. [鎵嬪姩鎵撳寘鍙戝竷](#3-鎵嬪姩鎵撳寘鍙戝竷)
4. [瀹夎鏂瑰紡瀵规瘮](#4-瀹夎鏂瑰紡瀵规瘮)
5. [Claude Code 閰嶇疆](#5-claude-code-閰嶇疆)
6. [Cursor 閰嶇疆](#6-cursor-閰嶇疆)
7. [VSCode 閰嶇疆](#7-vscode-閰嶇疆)
8. [Unity 渚ч厤缃甝(#8-unity-渚ч厤缃?
9. [楠岃瘉杩為€氭€(#9-楠岃瘉杩為€氭€?
10. [甯歌闂](#10-甯歌闂)

---

## 1. 椤圭洰缁撴瀯

```text
<浠撳簱鏍?/
  unitypilot-editor/      # Unity UPM 鍖咃紙package.json + Editor/ C# 鎻掍欢锛?  src/unitypilot_mcp/     # Python 婧愮爜
    __init__.py
    mcp_main.py           # CLI 鍏ュ彛锛坃cli 鍑芥暟锛?    mcp_stdio_server.py   # FastMCP 宸ュ叿瀹氫箟 + WS 鏈嶅姟鍣ㄧ敓鍛藉懆鏈?    server.py             # WebSocket Orchestrator锛堢洃鍚?8765锛?    tool_facade.py        # 宸ュ叿瀹炵幇
    dispatcher.py / ...   # 鍐呴儴妯″潡
  deploy/                 # 閮ㄧ讲鑴氭湰涓庨儴缃茶祫婧?    build_release.py      # 涓€閿墦鍖呰剼鏈?    dev.bat / dev.sh
    restart_mcp.bat / restart_mcp.sh
    resources/            # 閮ㄧ讲璧勬簮锛堝鍘嗗彶鍙戝竷鍖呯瓑锛?  run_unitypilot_mcp.py   # 寮€鍙戞湡鐩存帴杩愯鑴氭湰
  pyproject.toml          # Python 鍖呭厓鏁版嵁
  UnityPilot/             # 鍙€夛細绀轰緥 Unity 宸ョ▼锛堟湰鍦?file: 寮曠敤 unitypilot-editor锛?```

**閫氫俊鏋舵瀯锛?*

```text
AI 宸ュ叿 鈹€鈹€JSON-RPC/stdio鈹€鈹€鈻?Python MCP 鏈嶅姟鍣紙unitypilot-mcp锛?                                    鈹?                            WebSocket :8765
                                    鈹?                           Unity Editor锛圕# 瀹㈡埛绔級
```

**涓轰粈涔堥渶瑕佸垎骞冲彴鎵撳寘锛?*

MCP 鍗忚锛圝SON-RPC/stdio锛夊鎵€鏈?AI 宸ュ叿瀹屽叏鐩稿悓锛圕laude Code / Cursor / VSCode Copilot / OpenCode锛夛紝鏈嶅姟绔簩杩涘埗鏃犻渶鍖哄垎銆備絾 Python 浜岃繘鍒朵緷璧栵紙`pydantic-core`銆乣cryptography`銆乣pywin32`銆乣websockets` 绛夛級鍚钩鍙颁笓灞炵殑 `.whl` 鏂囦欢锛屽洜姝ら渶瑕佸湪鍚勫钩鍙板垎鍒墦鍖呫€?
---

## 2. 涓€閿墦鍖呰剼鏈?
`deploy/build_release.py` 鏄?`deploy/` 鐩綍涓嬬殑鎵撳寘鑴氭湰锛屽畬鎴愪互涓嬪伐浣滐細

1. 鏋勫缓鏈寘鐨?wheel
2. 涓嬭浇鍏ㄩ儴杩愯鏃朵緷璧?wheel锛堥拡瀵瑰綋鍓嶅钩鍙帮紝鍏辩害 32 涓級
3. 鐢熸垚鍚?AI 宸ュ叿鐨?MCP 閰嶇疆鏂囦欢妯℃澘
4. 鐢熸垚 `install.bat`锛圵indows锛夊拰 `install.sh`锛坢acOS锛夊畨瑁呰剼鏈?5. 灏嗘墍鏈夊唴瀹规墦鍖呬负涓€涓?zip 绂荤嚎瀹夎鍖?
### 2.1 鍓嶇疆瑕佹眰

- Python 3.11 鎴栦互涓?- 缃戠粶杩炴帴锛堢敤浜庝笅杞戒緷璧栵級
- `build` 宸ュ叿锛堣剼鏈細鑷姩瀹夎锛屼篃鍙墜鍔ㄦ墽琛?`pip install build`锛?
### 2.2 鍛戒护鏍煎紡

```text
python deploy/build_release.py [--platform {win,mac,auto}]
```

### 2.3 鍙傛暟璇存槑

| 鍙傛暟         | 绠€鍐?| 鍙€夊€?                | 榛樿鍊?| 璇存槑                              |
| ------------ | ---- | ---------------------- | ------ | --------------------------------- |
| `--platform` | `-p` | `win` / `mac` / `auto` | `auto` | 鐩爣骞冲彴锛宍auto` 鑷姩妫€娴嬪綋鍓嶇郴缁?|

**`--platform` 璇︾粏璇存槑锛?*

- `auto`锛堥粯璁わ級锛氳嚜鍔ㄦ娴嬪綋鍓嶆搷浣滅郴缁熴€俉indows 涓婁骇鍑?`win64` 鍖咃紝macOS 涓婁骇鍑?`macos` 鍖呫€?- `win`锛氬己鍒惰緭鍑?Windows 鍖咃紙`win64`锛夈€?*蹇呴』鍦?Windows 涓婅繍琛?*锛屽洜涓洪渶瑕佷笅杞?Windows 涓撳睘浜岃繘鍒?wheel锛坄pywin32`銆乣cryptography-win_amd64` 绛夛級銆?- `mac`锛氬己鍒惰緭鍑?macOS 鍖咃紙`macos`锛夈€?*蹇呴』鍦?macOS 涓婅繍琛?*锛屽師鍥犲悓涓娿€?
> 涓嶆敮鎸佽法骞冲彴浜ゅ弶缂栬瘧銆傞渶瑕佸嚭鍝釜骞冲彴鐨勫寘锛屽氨鍦ㄥ搴斿钩鍙扮殑鏈哄櫒涓婅繍琛岃剼鏈€?
### 2.4 浣跨敤绀轰緥

```bash
# 绀轰緥 1锛氬綋鍓嶅钩鍙版墦鍖咃紙鏈€甯哥敤锛?python deploy/build_release.py

# 绀轰緥 2锛氭槑纭寚瀹?Windows 鍖咃紙鍦?Windows 鏈哄櫒涓婏級
python deploy/build_release.py --platform win

# 绀轰緥 3锛氭槑纭寚瀹?macOS 鍖咃紙鍦?macOS 鏈哄櫒涓婏級
python deploy/build_release.py -p mac

# 绀轰緥 4锛氭煡鐪嬪府鍔?python deploy/build_release.py --help
```

### 2.5 杈撳嚭浜х墿

浜х墿缁熶竴鏀惧湪 `dist/` 鐩綍锛屽懡鍚嶆牸寮忎负 `unitypilot-mcp-<鐗堟湰鍙?-<骞冲彴>.zip`锛?
```text
dist/
  unitypilot-mcp-0.1.0-win64.zip    # Windows 绂荤嚎鍖咃紙绾?17 MB锛?  unitypilot-mcp-0.1.0-macos.zip    # macOS 绂荤嚎鍖咃紙绾?13 MB锛?```

### 2.6 zip 鍖呭唴缁撴瀯

```text
unitypilot-mcp-0.1.0-win64.zip
  wheels/
    unitypilot_mcp-0.1.0-py3-none-any.whl    # 鏈寘
    mcp-1.26.0-py3-none-any.whl
    websockets-16.0-cp311-cp311-win_amd64.whl
    pydantic_core-2.41.5-cp311-cp311-win_amd64.whl
    cryptography-46.0.6-cp311-abi3-win_amd64.whl
    pywin32-311-cp311-cp311-win_amd64.whl
    ...锛堝叡绾?33 涓?wheel锛屽惈鍏ㄩ儴渚濊禆锛?  install.bat        # Windows 涓€閿畨瑁咃紙鍙屽嚮杩愯锛?  install.sh         # macOS/Linux 涓€閿畨瑁?  mcp-configs/
    claude-code.mcp.json    # Claude Code / OpenCode / Claude VSCode 鎵╁睍
    cursor.mcp.json         # Cursor
    vscode.mcp.json         # VSCode Copilot
  README.txt         # 蹇€熶笂鎵嬭鏄?```

### 2.7 鎵撳寘杩囩▼杈撳嚭绀轰緥

```text
============================================================
  鎵撳寘骞冲彴: win64  (鐗堟湰 0.1.0)
============================================================

[1/4] 鏋勫缓 wheel...
  $ python -m build --wheel --outdir ...
  wheel: unitypilot_mcp-0.1.0-py3-none-any.whl

[2/4] 涓嬭浇渚濊禆 wheel...
  $ python -m pip download ... --dest ...
  鍏变笅杞?32 涓緷璧栧寘

[3/4] 鐢熸垚鑴氭湰鍜岄厤缃枃浠?..
  閰嶇疆鏂囦欢: ['claude-code.mcp.json', 'cursor.mcp.json', 'vscode.mcp.json']

[4/4] 鎵撳寘 zip...
  + install.bat
  + install.sh
  + mcp-configs/claude-code.mcp.json
  ...
  + wheels/unitypilot_mcp-0.1.0-py3-none-any.whl
  ...

  浜х墿: dist/unitypilot-mcp-0.1.0-win64.zip  (16.6 MB)
```

### 2.8 鐗堟湰鍙锋洿鏂?
淇敼 `../pyproject.toml` 涓殑 `version` 瀛楁鍚庨噸鏂拌繍琛岃剼鏈嵆鍙細

```toml
[project]
version = "0.2.0"   # 鏀硅繖閲?```

---

## 3. 鎵嬪姩鎵撳寘鍙戝竷

浠呭湪闇€瑕佸彂甯冨埌 PyPI 鎴栧崟鐙瀯寤?wheel 鏃朵娇鐢ㄦ鑺傦紝鏃ュ父鍑虹绾垮寘璇风洿鎺ョ敤 `deploy/build_release.py`銆?
### 3.1 鏋勫缓 wheel

```bash
pip install build
python -m build --wheel --outdir dist ..
# 浜х墿锛歞ist/unitypilot_mcp-0.1.0-py3-none-any.whl
```

### 3.2 鍙戝竷鍒?PyPI

```bash
pip install twine

# 鍏堟祴璇?twine upload --repository testpypi dist/*

# 姝ｅ紡鍙戝竷
twine upload dist/*
```

> 闇€瑕?PyPI 璐﹀彿骞剁敓鎴?API Token銆傚彂甯冨悗鐢ㄦ埛鍙€氳繃 `pip install unitypilot-mcp` 鐩存帴瀹夎銆?
### 3.3 鍙戝竷鍒?GitHub Releases

灏?`dist/*.whl` 涓婁紶鍒?GitHub Release Assets锛岀敤鎴烽€氳繃浠ヤ笅鍛戒护瀹夎锛?
```bash
pip install https://github.com/your-org/your-repo/releases/download/v0.1.0/unitypilot_mcp-0.1.0-py3-none-any.whl
```

---

## 4. 瀹夎鏂瑰紡瀵规瘮

鐢ㄦ埛鏀跺埌绂荤嚎鍖呭悗鐨勫畨瑁呮柟寮忥細

| 鏂瑰紡 | 閫傜敤鍦烘櫙 | 鎿嶄綔 |
| --- | --- | --- |
| **绂荤嚎鍖咃紙鎺ㄨ崘锛?* | 宸叉敹鍒?`deploy/build_release.py` 浜у嚭鐨?zip | 瑙ｅ帇鍚庤繍琛?`install.bat` / `install.sh` |
| **pip + PyPI** | 宸插彂甯冨埌 PyPI | `pip install unitypilot-mcp` |
| **uvx** | 宸插彂甯冨埌 PyPI锛岃拷姹傞浂渚濊禆绠＄悊 | 鏃犻渶瀹夎锛岀洿鎺ュ湪閰嶇疆涓娇鐢?`uvx unitypilot-mcp` |
| **婧愮爜** | 寮€鍙戣皟璇?| `pip install -e ..` |

### 绂荤嚎鍖呭畨瑁呮楠?
**Windows锛?*

```bat
1. 瑙ｅ帇 unitypilot-mcp-0.1.0-win64.zip
2. 鍙屽嚮 install.bat锛堟垨鍦ㄥ懡浠ゆ彁绀虹涓繍琛岋級
3. 绛夊緟鎻愮ず"瀹夎瀹屾垚锛?
```

**macOS锛?*

```bash
unzip unitypilot-mcp-0.1.0-macos.zip
cd unitypilot-mcp-0.1.0-macos
chmod +x install.sh
./install.sh
```

瀹夎瀹屾垚鍚庣郴缁熶腑鍑虹幇 `unitypilot-mcp` 鍛戒护锛屽彲鍦?MCP 閰嶇疆涓洿鎺ュ紩鐢ㄣ€?
### uvx 瀹夎锛圥yPI 鍙戝竷鍚庯級

```bash
# 瀹夎 uv锛堜竴娆℃€ф搷浣滐級
# Windows PowerShell锛?powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
# macOS锛?curl -LsSf https://astral.sh/uv/install.sh | sh
```

瀹夎鍚庢棤闇€鎵嬪姩瀹夎鍖咃紝鐩存帴鍦?MCP 閰嶇疆鏂囦欢涓娇鐢?`uvx unitypilot-mcp` 鍗冲彲銆?
---

## 5. Claude Code 閰嶇疆

Claude Code 閫氳繃椤圭洰鏍圭洰褰曠殑 `.mcp.json` 鑷姩鍔犺浇鏈嶅姟鍣ㄣ€?
**绂荤嚎鍖呭畨瑁呭悗锛?* 灏?`mcp-configs/claude-code.mcp.json` 澶嶅埗鍒?Unity 椤圭洰鏍圭洰褰曞苟閲嶅懡鍚嶄负 `.mcp.json`銆?
**鎵嬪姩鍒涘缓 `.mcp.json`锛?*

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "unitypilot-mcp",
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

> macOS 涓嶉渶瑕?`PYTHONUTF8`锛屼繚鐣欐棤瀹炽€?
**uvx 鏂瑰紡锛圥yPI 鍙戝竷鍚庯級锛?*

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "uvx",
      "args": ["unitypilot-mcp"],
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**鏈湴婧愮爜鏂瑰紡锛堝紑鍙戞湡锛夛細**

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "python",
      "args": ["d:/path/to/unitypilot/run_unitypilot_mcp.py"],
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

> macOS锛歚"command": "python3"`锛岃矾寰勬敼涓?`/Users/name/path/to/unitypilot/run_unitypilot_mcp.py`

**楠岃瘉锛?*

```bash
claude mcp list
# 鏈熸湜锛歶nitypilot: ... - 鉁?Connected
```

---

## 6. Cursor 閰嶇疆

| 閰嶇疆鑼冨洿         | 鏂囦欢璺緞                       |
| ---------------- | ------------------------------ |
| 鍏ㄥ眬锛堟墍鏈夐」鐩級 | `~/.cursor/mcp.json`           |
| 椤圭洰绾?          | `<椤圭洰鏍?/.cursor/mcp.json`    |

**绂荤嚎鍖呭畨瑁呭悗锛?* 灏?`mcp-configs/cursor.mcp.json` 澶嶅埗鍒?`<椤圭洰鏍?/.cursor/mcp.json`锛堢洰褰曚笉瀛樺湪鍒欐柊寤猴級銆?
**鎵嬪姩鍒涘缓閰嶇疆锛堟牸寮忎笌 Claude Code 鐩稿悓锛夛細**

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "unitypilot-mcp",
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**macOS 鏈湴婧愮爜鏂瑰紡锛?*

```json
{
  "mcpServers": {
    "unitypilot": {
      "command": "python3",
      "args": ["/Users/name/path/to/unitypilot/run_unitypilot_mcp.py"]
    }
  }
}
```

**楠岃瘉锛?* Cursor 鈫?Settings 鈫?MCP锛宍unitypilot` 鏄剧ず缁胯壊鍦嗙偣鍗充负宸茶繛鎺ャ€?
---

## 7. VSCode 閰嶇疆

VSCode 閫氳繃 GitHub Copilot 鎵╁睍锛堥渶 1.99+锛夋敮鎸?MCP锛岄厤缃牸寮忎笌 Claude Code / Cursor **鏈夋墍涓嶅悓**锛氫娇鐢?`"servers"` 閿紙鑰岄潪 `"mcpServers"`锛夛紝涓旈渶瑕?`"type": "stdio"` 瀛楁銆?
| 閰嶇疆鑼冨洿 | 鏂囦欢璺緞 |
| --- | --- |
| 椤圭洰绾?| `<椤圭洰鏍?/.vscode/mcp.json` |
| 鍏ㄥ眬 | `~/.vscode/settings.json`锛堝啓鍏?`github.copilot.chat.mcp.servers`锛?|

**绂荤嚎鍖呭畨瑁呭悗锛?* 灏?`mcp-configs/vscode.mcp.json` 澶嶅埗鍒?`<椤圭洰鏍?/.vscode/mcp.json`锛堢洰褰曚笉瀛樺湪鍒欐柊寤猴級銆?
**鎵嬪姩鍒涘缓 `.vscode/mcp.json`锛?*

```json
{
  "servers": {
    "unitypilot": {
      "type": "stdio",
      "command": "unitypilot-mcp",
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**uvx 鏂瑰紡锛?*

```json
{
  "servers": {
    "unitypilot": {
      "type": "stdio",
      "command": "uvx",
      "args": ["unitypilot-mcp"],
      "env": { "PYTHONUTF8": "1" }
    }
  }
}
```

**Claude VSCode 鎵╁睍锛?* 閰嶇疆鏂瑰紡涓?Claude Code CLI 鐩稿悓锛屼娇鐢?`"mcpServers"` 閿紝鏀剧疆 `.mcp.json` 鏂囦欢銆?
---

## 8. Unity 渚ч厤缃?
### 8.1 瀹夎鎻掍欢锛圲PM锛屾帹鑽愶級

鏈粨搴撳嵆 UPM 鍖呮潵婧愶細鍖呮牴鐩綍涓?`unitypilot-editor/`锛堝唴鍚?`package.json`锛夛紝涓?GitHub 杩滅▼涓€鑷淬€?
鍦ㄧ洰鏍?Unity 宸ョ▼鐨?`Packages/manifest.json` 鐨?`dependencies` 涓姞鍏ワ細

```json
"io.github.codingriver.unitypilot-editor": "https://github.com/codingriver/unitypilot.git?path=/unitypilot-editor"
```

鍥哄畾鐗堟湰鏃朵娇鐢?Git 鏍囩锛堢ず渚嬶級锛?
```json
"io.github.codingriver.unitypilot-editor": "https://github.com/codingriver/unitypilot.git?path=/unitypilot-editor#v0.1.0"
```

鍦ㄦ湰 monorepo 鍐呭紑鍙?`UnityPilot` 绀轰緥宸ョ▼鏃讹紝浣跨敤鏈湴宓屽叆璺緞锛堜笌涓婂紡涓?*鍚屼竴浠芥簮鐮?*锛夛細

```json
"io.github.codingriver.unitypilot-editor": "file:../../unitypilot-editor"
```

淇濆瓨鍚庣敱 Unity Package Manager 瑙ｆ瀽锛涙棤闇€鍐嶅鍒?`Assets/.../UnityPilot` 鐩綍銆?
### 8.2 鍚敤

Unity 鑿滃崟 鈫?**UnityPilot** 鐩稿叧椤逛腑鍚敤 UnityPilot锛堝嬀閫夛級

### 8.3 杩炴帴椤哄簭

```text
1. 鍚姩 AI 宸ュ叿 鈫?MCP 鏈嶅姟鍣ㄨ嚜鍔ㄥ惎鍔紝鐩戝惉 ws://127.0.0.1:8765
2. 鎵撳紑 Unity 鈫?UnityPilot 鎻掍欢鑷姩杩炴帴
3. 鑿滃崟 **upilot/upilot**锛堢姸鎬佺獥鍙ｏ級鈫?鏄剧ず"宸茶繛鎺?
```

> MCP 鏈嶅姟鍣ㄥ繀椤诲厛浜?Unity 鍚姩銆俇nity 鏄?WebSocket 瀹㈡埛绔紝Python 鏄湇鍔＄銆?
---

## 9. 楠岃瘉杩為€氭€?
### 9.1 鍐掔儫娴嬭瘯锛堟棤闇€ Unity锛?
```bash
cd /path/to/unitypilot
python src/unitypilot_mcp/mcp_smoke_test.py

# 鏈熸湜杈撳嚭锛?# [OK] MCP stdio smoke test passed (11 tools)
```

### 9.2 Claude Code 杩炴帴娴嬭瘯

```bash
claude mcp list
# 鏈熸湜锛歶nitypilot: ... - 鉁?Connected
```

### 9.3 鍦?AI 宸ュ叿涓皟鐢ㄥ伐鍏?
鍦?Claude Code / Cursor 瀵硅瘽妗嗕腑杈撳叆锛?
```text
璋冪敤 unity_editor_state 宸ュ叿锛屽憡璇夋垜褰撳墠缂栬緫鍣ㄧ姸鎬?```

---

## 10. 甯歌闂

### Q: `install.bat` 鎶ラ敊"鎵句笉鍒?python"

Python 鏈姞鍏ョ郴缁?PATH銆傝В鍐虫柟妗堬細

- 閲嶆柊瀹夎 Python锛屽嬀閫?**"Add Python to PATH"**
- 鎴栨墜鍔ㄥ湪鍛戒护鎻愮ず绗︿腑杩愯锛歚where python` 鎵惧埌璺緞鍚庣洿鎺ヨ皟鐢?
### Q: Windows 涓?`unitypilot-mcp` 鍛戒护鎵句笉鍒?
pip `--user` 瀹夎鐨勮剼鏈彲鑳戒笉鍦?PATH 涓€傝В鍐虫柟妗堬細

```bash
# 鎵惧埌鐢ㄦ埛 Scripts 鐩綍
python -c "import site; print(site.getusersitepackages())"
# 灏嗗搴旂殑 Scripts/ 鐩綍鍔犲叆 PATH
# 鎴栧湪 .mcp.json 涓娇鐢ㄥ畬鏁磋矾寰勶細
# "command": "C:/Users/name/AppData/Roaming/Python/Python311/Scripts/unitypilot-mcp.exe"
```

### Q: macOS 涓?`python` 鎵句笉鍒?
macOS 榛樿鏃?`python` 鍛戒护锛屼娇鐢?`python3`銆傚湪 MCP 閰嶇疆鍜?`install.sh` 涓潎宸蹭娇鐢?`python3`銆傝嫢浠嶆姤閿欙紝鍏堝畨瑁?Python锛?
```bash
brew install python
```

### Q: 绔彛 8765 琚崰鐢?
淇敼 `src/unitypilot_mcp/server.py` 涓?`WsOrchestratorServer` 鐨勯粯璁ょ鍙ｏ紝骞跺悓姝ヤ慨鏀?Unity 渚?`UnityPilotBridge.cs` 涓殑杩炴帴鍦板潃銆?
### Q: `claude mcp list` 鏄剧ず Failed to connect

- Python 鐗堟湰浣庝簬 3.11 鈫?鍗囩骇
- `mcp` 鍖呮湭瀹夎 鈫?`pip install mcp>=1.26.0`
- 浣跨敤浜嗙绾垮寘瀹夎浣?`install.bat` 鏈墽琛屾垚鍔?鈫?妫€鏌?Python 鏄惁鍦?PATH 涓?
### Q: Cursor 涓湅涓嶅埌 MCP 宸ュ叿

Cursor 鐗堟湰闇€ 鈮?0.46锛屽湪 **Settings 鈫?Features 鈫?MCP** 涓‘璁ゅ凡鍚敤 MCP 鍔熻兘銆?