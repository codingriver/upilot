import json
import urllib.request
import re

url = 'http://127.0.0.1:8011/mcp'

def call_tool(name, args):
    req = urllib.request.Request(url, method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Accept", "application/json, text/event-stream")
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": name, "arguments": args}}).encode()
    resp = urllib.request.urlopen(req, body, timeout=120)
    data = resp.read().decode()
    events = re.findall(r"data: (.+)", data)
    return json.loads(events[0]) if events else {}

def extract_text(result):
    try:
        for c in result["result"]["content"]:
            if c.get("type") == "text":
                return c["text"]
    except Exception:
        pass
    return ""

def check_file(f):
    result = call_tool('unity_uiflow_run_file', {'yamlPath': f})
    text = extract_text(result)
    data = json.loads(text)
    result_data = data.get('data', {}).get('result', {}) or {}
    raw = result_data.get('raw', {}) or {}
    cases = raw.get('cases', [])
    if not cases:
        print(f + ': no cases, error=' + str(result_data.get('errorMessage', 'unknown')))
        return
    case = cases[0]
    print(f + ':')
    print('  case_status:', case.get('status'))
    if case.get('failedStep'):
        print('  error:', case['failedStep']['errorMessage'])
    elif case.get('stepResults'):
        for s in case['stepResults']:
            if s['status'] != 'Passed':
                print('  step_error:', s['errorMessage'])
                break
    else:
        print('  error:', case.get('errorMessage', 'unknown'))

check_file('Assets/Examples/Yaml/111-imgui-empty-string.yaml')
