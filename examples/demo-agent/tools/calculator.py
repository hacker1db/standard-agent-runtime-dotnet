import ast, json, operator, sys
OPS = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv,
       ast.Pow: operator.pow, ast.Mod: operator.mod, ast.USub: operator.neg, ast.UAdd: operator.pos}
def ev(n):
    if isinstance(n, ast.Constant) and isinstance(n.value, (int, float)): return n.value
    if isinstance(n, ast.BinOp) and type(n.op) in OPS: return OPS[type(n.op)](ev(n.left), ev(n.right))
    if isinstance(n, ast.UnaryOp) and type(n.op) in OPS: return OPS[type(n.op)](ev(n.operand))
    raise ValueError("unsupported expression")
try:
    req = json.load(sys.stdin)
    expr = str(req["args"]["expression"])
    print(json.dumps({"status": "success", "result": str(ev(ast.parse(expr, mode="eval").body))}))
except Exception as e:
    print(json.dumps({"status": "error", "error": str(e)}))
