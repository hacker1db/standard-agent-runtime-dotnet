import datetime, json, sys
sys.stdin.read()
print(json.dumps({"status": "success", "result": datetime.datetime.now(datetime.timezone.utc).isoformat()}))
