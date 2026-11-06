#!/usr/bin/env python3
"""Static audit: XAML StaticResource keys must exist (global theme OR local view resources);
x:Class must match files; module view ctors must take their VM."""
import re, sys, os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "StrykerOSS")
dark = open(os.path.join(ROOT, "Themes", "Dark.xaml")).read()
global_keys = set(re.findall(r'x:Key="([\w]+)"', dark))
implicit = {"ToolTip", "ScrollBar"}

all_xaml = []
for base, _, files in os.walk(ROOT):
    for f in files:
        if f.endswith(".xaml"):
            all_xaml.append(os.path.join(base, f))

problems = []
local_keys_by_file = {}
for path in all_xaml:
    text = open(path).read()
    local = set(re.findall(r'x:Key="([\w]+)"', text))
    local_keys_by_file[path] = local
    for m in re.finditer(r'\{StaticResource\s+([\w]+)\s*\}', text):
        k = m.group(1)
        if k not in global_keys and k not in local and k not in implicit:
            problems.append(f"{os.path.relpath(path, ROOT)}: unknown StaticResource key '{k}'")
    mc = re.search(r'x:Class="([\w.]+)"', text)
    if mc:
        cls = mc.group(1)
        rel = cls.replace("StrykerOSS.", "").replace(".", "/")
        cs = os.path.join(ROOT, rel + ".xaml.cs")
        if not os.path.exists(cs):
            problems.append(f"{os.path.relpath(path, ROOT)}: missing code-behind {cs}")

views_dir = os.path.join(ROOT, "Views")
# SetupWindow : assistant de première configuration, reçoit IServiceProvider (pas un VM de module)
EXEMPT = {"MainWindow", "PlaceholderView", "ConsentWindow", "SetupWindow"}
for f in os.listdir(views_dir):
    if f.endswith(".xaml.cs"):
        name = f[:-8]
        if name in EXEMPT:
            continue
        p = os.path.join(views_dir, f)
        t = open(p).read()
        if "InitializeComponent" not in t:
            problems.append(f"Views/{f}: no InitializeComponent call")
        if not re.search(r'public\s+\w+View\s*\(\s*ViewModels?\.', t):
            problems.append(f"Views/{f}: ctor must take its VM (ViewModels.XxxVm)")

if problems:
    print("VIOLATIONS:")
    print("\n".join(problems))
    sys.exit(1)
print(f"OK - {len(all_xaml)} XAML files checked, all resource keys valid, all code-behinds present.")
