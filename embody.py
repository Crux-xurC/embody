"""embody.py -- evaluate one call inside a Python REPL and print one marker record.

Loaded into `python -i` by the caller (Xipe Totec's Python adapter) through an
init script:

    import sys; sys.path.insert(0, "<path-to-embody>"); import embody as _xt_embody; del sys.path[0]
    _xt_embody.install({"calc": "/project/calc.py", "pkg.mod": "/project/pkg/mod.py"})

and then, one line per evaluation:

    _xt_embody.run("<id>", "xt_<id>", <timeout ms>, "<module>", "<call>", <basis>)

Only the standard library is used. See ReadMe.md, "Python (`embody.py`)".
"""

import ast
import base64
import importlib
import importlib.abc
import importlib.util
import math
import pickle
import signal
import sys
import types

__all__ = ["install", "sanitize", "run"]


# --- loading without top-level side effects ------------------------------------

def _literal(node):
    try:
        ast.literal_eval(node)
        return True
    except Exception:
        return False


def _kept(node):
    """Whether a top-level statement is kept when a project module is loaded."""
    if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef, ast.Import, ast.ImportFrom)):
        return True
    # constants the functions read: NAME = <literal>, NAME: T = <literal>
    if isinstance(node, ast.Assign):
        return all(isinstance(t, ast.Name) for t in node.targets) and _literal(node.value)
    if isinstance(node, ast.AnnAssign):
        return isinstance(node.target, ast.Name) and node.value is not None and _literal(node.value)
    # try: import x / except ImportError: x = None
    if isinstance(node, ast.Try):
        stmts = node.body + node.orelse + node.finalbody + [s for h in node.handlers for s in h.body]
        return all(isinstance(s, ast.Pass) or _kept(s) for s in stmts)
    return False


def sanitize(source, path):
    """The module's code with only definitions, imports and literal constants
    kept, compiled with its own file name and line numbers."""
    tree = ast.parse(source, path)
    tree.body = [n for n in tree.body if _kept(n)]
    return compile(tree, path, "exec")


class _Loader(importlib.abc.Loader):
    def __init__(self, path):
        self.path = path

    def create_module(self, spec):
        return None

    def exec_module(self, module):
        if self.path is None:  # a package directory without __init__.py
            return
        with open(self.path, encoding="utf-8") as f:
            source = f.read()
        exec(sanitize(source, self.path), module.__dict__)


class _Finder(importlib.abc.MetaPathFinder):
    """The project's own modules, found by name and loaded sanitized."""

    def __init__(self, modules):
        self.modules = dict(modules)
        self.packages = set()
        for name, path in modules.items():
            if path.endswith("__init__.py"):
                self.packages.add(name)
            parts = name.split(".")
            for i in range(1, len(parts)):
                self.packages.add(".".join(parts[:i]))

    def find_spec(self, name, path=None, target=None):
        if name not in self.modules and name not in self.packages:
            return None
        file = self.modules.get(name)
        spec = importlib.util.spec_from_loader(name, _Loader(file), origin=file, is_package=name in self.packages)
        if file:
            spec.has_location = True
        if name in self.packages:
            spec.submodule_search_locations = []
        return spec


def install(modules):
    """Make the project's modules (name -> absolute path) importable, each
    loaded without its top-level side effects."""
    sys.meta_path.insert(0, _Finder(modules))


# --- one evaluation --------------------------------------------------------------

class _Timeout(BaseException):
    pass


def _alarm(_signum, _frame):
    raise _Timeout()


def _eq(x, y):
    """Equality for compare and the exact check: types must match, floats
    within 1e-9 (relative or absolute), containers element by element."""
    if isinstance(x, float) and isinstance(y, float):
        return x == y or math.isclose(x, y, rel_tol=1e-9, abs_tol=1e-9)
    if type(x) is not type(y):
        return False
    if isinstance(x, (list, tuple)):
        return len(x) == len(y) and all(_eq(a, b) for a, b in zip(x, y))
    if isinstance(x, dict):
        return x.keys() == y.keys() and all(_eq(x[k], y[k]) for k in x)
    try:
        return bool(x == y)
    except Exception:
        return False


def _equal(x, y):
    """_eq, or for values it can't walk (a cycle), the same pickle bytes."""
    try:
        return _eq(x, y)
    except RecursionError:
        try:
            return pickle.dumps(x) == pickle.dumps(y)
        except Exception:
            return False


def _one_line(text):
    return text.replace("\r", "\\r").replace("\n", "\\n")


def _type_name(v):
    t = type(v)
    return t.__qualname__ if t.__module__ == "builtins" else t.__module__ + "." + t.__qualname__


def _judge(v):
    """(repr, display, bin_base64)"""
    try:
        display = repr(v)
    except Exception as e:
        display = "<repr failed: %s>" % type(e).__name__
    try:
        if _equal(ast.literal_eval(display), v):
            return "exact", display, None
    except Exception:
        pass
    try:
        data = pickle.dumps(v)
        if _equal(pickle.loads(data), v):
            return "binary", display, base64.b64encode(data).decode("ascii")
    except Exception:
        pass
    return "unsupported", display, None


def _previous(basis):
    kind, text = basis
    if kind == "exact":
        return ast.literal_eval(text)
    return pickle.loads(base64.b64decode(text))


def run(call_id, var, timeout_ms, module, call, basis=None):
    """Evaluate `call` (text) with `module` imported, bind the value to `var`
    in __main__, and print one record: XT_BEGIN_<id> ... XT_END_<id>, or
    XT_ERROR_<id> crash|timeout <reason>. `basis`: None, ("exact", text) or
    ("binary", base64) -- the previous value to compare with."""
    out = sys.stdout
    main = sys.modules["__main__"].__dict__
    previous_handler = signal.signal(signal.SIGALRM, _alarm)
    signal.setitimer(signal.ITIMER_REAL, max(timeout_ms, 1) / 1000.0)
    try:
        try:
            importlib.import_module(module)
            top = module.split(".")[0]
            main[top] = sys.modules[top]
            value = eval(compile(call, "<xipe_totec>", "eval"), main)
        finally:
            signal.setitimer(signal.ITIMER_REAL, 0)
    except _Timeout:
        signal.signal(signal.SIGALRM, previous_handler)
        out.write("XT_ERROR_%s timeout\n" % call_id)
        out.flush()
        return
    except BaseException as e:
        signal.signal(signal.SIGALRM, previous_handler)
        out.write("XT_ERROR_%s crash %s\n" % (call_id, _one_line("%s: %s" % (type(e).__name__, e))))
        out.flush()
        return
    signal.signal(signal.SIGALRM, previous_handler)
    main[var] = value
    out.write("XT_CALLED_%s\n" % call_id)
    out.flush()
    try:
        r, display, data = _judge(value)
        if basis is None:
            c = "none"
        elif r == "unsupported":
            c = "skipped"
        else:
            try:
                c = "same" if _equal(_previous(basis), value) else "changed"
            except Exception:
                c = "changed"
        lines = ["XT_BEGIN_%s" % call_id, "repr=" + r, "type=" + _type_name(value), "compare=" + c, "display=" + _one_line(display)]
        if data is not None:
            lines.append("bin_base64=" + data)
        lines.append("XT_END_%s" % call_id)
        out.write("\n".join(lines) + "\n")
    except BaseException as e:
        out.write("XT_ERROR_%s crash %s\n" % (call_id, _one_line("judging the value: %s: %s" % (type(e).__name__, e))))
    out.flush()
