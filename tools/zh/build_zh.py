"""中文版一键出包：把汉化改动搬到原版的某个版本上，编译，替换进官方发布包，打出中文版 zip。

用法（在仓库根目录，需要 git、.NET 9 SDK、Python 3）：
    python tools/zh/build_zh.py 0.7.40            # 跟进原版 v0.7.40
    python tools/zh/build_zh.py 0.7.40 --out dist  # 输出目录（默认 dist-zh）

步骤：
1. 取原版 tag v<版本>，在其上新建分支 zh-<版本>，把中文版提交（当前分支上、原版 tag 之后的提交）逐个搬过去。
   有冲突时停下来，说明哪些文件冲突，改好后 `git cherry-pick --continue` 再重跑本脚本加 --no-port。
2. 编译 src/Tartarus.App（Release，win-x64；非 Windows 上自动加 EnableWindowsTargeting）。
3. 下载官方 TartarusSolver-<版本>.zip 和 -full.zip，把 TartarusSolver.dll 和 Tartarus.*.dll 换成新编译的，
   README.txt 换成中文说明，打成 TartarusSolver-<版本>-zh.zip / -zh-full.zip。
4. 核对：文件清单与官方包一致，只有换掉的文件不同；dll 中确实带着中文界面文字，且不含原作者的错误上报地址。
"""
import argparse, os, platform, re, shutil, subprocess, sys, urllib.request, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
UPSTREAM = "https://github.com/glcoder/TartarusSolver"
RUNTIME_NEEDED = ("\n需要先安装 .NET 9 桌面运行时 (x64)：https://dotnet.microsoft.com/download/dotnet/9.0\n"
                  "和 Microsoft Visual C++ 运行库 (x64)：https://aka.ms/vs/17/release/vc_redist.x64.exe\n"
                  "（不想装的话，请下载 -full 完整版，里面已自带。）\n")
RUNTIME_BUNDLED = "\n完整版已自带 .NET 和 VC++ 运行库，无需额外安装。\n"
MARKERS = ["审讯室", "未在捕获", "保存问题报告"]


def run(*cmd, cwd=ROOT, check=True):
    print("$", " ".join(cmd))
    r = subprocess.run(cmd, cwd=cwd, text=True, capture_output=True, encoding="utf-8", errors="replace")
    if check and r.returncode != 0:
        sys.exit(f"失败：{' '.join(cmd)}\n{r.stdout}\n{r.stderr}")
    return r


def zh_commits(base_tag):
    """当前分支上、原版 base_tag 之后的提交（就是汉化改动），从旧到新。"""
    out = run("git", "rev-list", "--reverse", f"{base_tag}..HEAD").stdout.split()
    if not out:
        sys.exit(f"当前分支在 {base_tag} 之后没有提交：请在中文版分支上运行。")
    return out


def current_base():
    """当前分支所基于的原版 tag（离 HEAD 最近的 vX.Y.Z）。"""
    return run("git", "describe", "--tags", "--abbrev=0", "--match", "v*").stdout.strip()


def port(version):
    tag = f"v{version}"
    run("git", "fetch", "--tags", "--depth", "50", UPSTREAM, check=False)
    run("git", "fetch", UPSTREAM, f"refs/tags/{tag}:refs/tags/{tag}", check=False)
    if run("git", "rev-parse", "-q", "--verify", f"{tag}^{{commit}}", check=False).returncode != 0:
        sys.exit(f"原版没有 {tag} 这个版本。")
    base = current_base()
    if base == tag:
        print(f"当前分支已基于 {tag}，不用搬。")
        return
    commits = zh_commits(base)
    branch = f"zh-{version}"
    run("git", "checkout", "-B", branch, tag)
    for c in commits:
        r = run("git", "cherry-pick", "-x", c, check=False)
        if r.returncode != 0:
            conflicts = run("git", "diff", "--name-only", "--diff-filter=U").stdout.split()
            sys.exit("搬汉化改动时有冲突（原版改了同一处代码）：\n  " + "\n  ".join(conflicts)
                     + "\n改好后执行 git add 这些文件、git cherry-pick --continue，再加 --no-port 重跑本脚本。")
    print(f"已在 {tag} 上建好分支 {branch}，搬了 {len(commits)} 个汉化提交。")


def build():
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet"))
    cmd = [dotnet, "build", "src/Tartarus.App/Tartarus.App.csproj", "-c", "Release", "-p:TARTARUS_SENTRY_DSN="]
    if platform.system() != "Windows":
        cmd.append("-p:EnableWindowsTargeting=true")
    r = run(*cmd)
    if re.search(r"[1-9]\d* Error\(s\)", r.stdout):
        sys.exit(r.stdout)
    bins = list((ROOT / "src/Tartarus.App/bin/Release").glob("net*-windows*/win-x64/TartarusSolver.dll"))
    if not bins:
        sys.exit("没找到编译出的 TartarusSolver.dll")
    return bins[0].parent


def download(version, name, dest):
    url = f"{UPSTREAM}/releases/download/v{version}/{name}"
    if dest.exists():
        return dest
    print("下载", url)
    with urllib.request.urlopen(url) as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f)
    return dest


def package(version, bin_dir, out):
    out.mkdir(parents=True, exist_ok=True)
    template = (ROOT / "tools/zh/README-zh.txt").read_text(encoding="utf-8")
    replace = ["TartarusSolver.dll"] + [p.name for p in bin_dir.glob("Tartarus.*.dll")]
    made = []
    for suffix, note in [("", RUNTIME_NEEDED), ("-full", RUNTIME_BUNDLED)]:
        official = download(version, f"TartarusSolver-{version}{suffix}.zip", out / f"TartarusSolver-{version}{suffix}.zip")
        target = out / f"TartarusSolver-{version}-zh{suffix}.zip"
        readme = template.replace("{version}", version).replace("{runtime}", note).replace("\n", "\r\n")
        with zipfile.ZipFile(official) as src, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as dst:
            for info in src.infolist():
                if info.is_dir():
                    continue
                rel = info.filename.replace("\\", "/")
                name = rel.rsplit("/", 1)[-1]
                if rel.count("/") == 1 and name in replace:  # 程序文件夹根下的那几个 dll
                    data = (bin_dir / name).read_bytes()
                elif rel == "TartarusSolver/README.txt":
                    data = b"\xef\xbb\xbf" + readme.encode("utf-8")
                else:
                    data = src.read(info)
                dst.writestr(rel, data, zipfile.ZIP_DEFLATED)
        check(official, target, replace)
        made.append(target)
    return made


def check(official, target, replaced):
    with zipfile.ZipFile(official) as a, zipfile.ZipFile(target) as b:
        na = {i.filename.replace("\\", "/") for i in a.infolist() if not i.is_dir()}
        nb = {i.filename.replace("\\", "/") for i in b.infolist() if not i.is_dir()}
        if na != nb:
            sys.exit(f"{target.name}：文件清单和官方包不一致：{na ^ nb}")
        dll = b.read("TartarusSolver/TartarusSolver.dll")
        missing = [m for m in MARKERS if m.encode("utf-16-le") not in dll]
        if missing:
            sys.exit(f"{target.name}：主程序里没有中文界面文字 {missing}")
        if b".sentry.io" in dll:
            sys.exit(f"{target.name}：主程序里带着原作者的错误上报地址")
        if b.testzip() is not None:
            sys.exit(f"{target.name}：压缩包损坏")
    print(f"核对通过：{target.name}（{target.stat().st_size / 1048576:.1f} MB）")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("version", help="原版版本号，如 0.7.40")
    ap.add_argument("--out", type=Path, default=ROOT / "dist-zh")
    ap.add_argument("--no-port", action="store_true", help="不搬提交（已手动解决冲突时）")
    a = ap.parse_args()
    if not a.no_port:
        port(a.version)
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    built = re.search(r"<Version>([^<]+)</Version>", props).group(1)
    if built != a.version:
        sys.exit(f"源码版本是 {built}，不是 {a.version}：先切到对应分支。")
    for z in package(a.version, build(), a.out):
        print("出包：", z)


if __name__ == "__main__":
    main()
