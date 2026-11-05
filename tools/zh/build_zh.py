"""中文版一键出包：把汉化改动搬到原版的某个版本上，编译，替换进官方发布包，打出中文版 zip。

用法（在仓库根目录，需要 git、.NET 9 SDK、Python 3；发布还需要 gh 命令行）：
    python tools/zh/build_zh.py 0.7.45             # 跟进原版 v0.7.45，出 0.7.45.1 中文版
    python tools/zh/build_zh.py 0.7.44 --no-port   # 不搬提交，直接用当前源码出包
    python tools/zh/build_zh.py 0.7.44 --no-port --publish   # 出包后发布到本仓库的 GitHub Release

中文版版本号 = 原版版本号 + 第四段修订号（0.7.44.1、0.7.44.2…），软件内的自动更新按它判断新旧。
发布包名 TartarusSolver-<中文版版本>-zh.zip / -zh-full.zip，软件更新时按这个名字找。

步骤：
1. 取原版 tag v<版本>，在其上新建分支 zh-<版本>，把中文版提交（当前分支上、原版 tag 之后的提交）逐个搬过去。
   有冲突时停下来，说明哪些文件冲突，改好后 `git cherry-pick --continue` 再重跑本脚本加 --no-port。
2. 编译 src/Tartarus.App（Release，win-x64；非 Windows 上自动加 EnableWindowsTargeting）。
3. 下载官方 TartarusSolver-<版本>.zip 和 -full.zip，把 TartarusSolver.dll、Tartarus.*.dll 和各语言的
   TartarusSolver.resources.dll 换成新编译的，README.txt 换成中文说明，加上 LICENSE（MIT，保留原作者版权）。
4. 核对：文件清单与官方包一致（只多了 LICENSE）；带着中文界面文字，且不含原作者的错误上报地址。
5. 加 --publish 时，在本仓库建 tag v<中文版版本> 的 Release 并上传两个包。
"""
import argparse, os, platform, re, shutil, subprocess, sys, urllib.request, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
UPSTREAM = "https://github.com/glcoder/TartarusSolver"
RUNTIME_NEEDED = ("\n需要先安装 .NET 9 桌面运行时 (x64)：https://dotnet.microsoft.com/download/dotnet/9.0\n"
                  "和 Microsoft Visual C++ 运行库 (x64)：https://aka.ms/vs/17/release/vc_redist.x64.exe\n"
                  "（不想装的话，请下载 -full 完整版，里面已自带。）\n")
RUNTIME_BUNDLED = "\n完整版已自带 .NET 和 VC++ 运行库，无需额外安装。\n"
MARKERS = ["当前推荐", "窗口透明度"]  # zh-Hans 资源包里应有的中文版文字
REPO = "womeizui119/tartarus-solver-cn"


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
    return run("git", "describe", "--tags", "--abbrev=0", "--match", "v*", "--exclude", "v*.*.*.*").stdout.strip()


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
    set_version(f"{version}.1")


def set_version(v):
    props = ROOT / "Directory.Build.props"
    text = props.read_bytes().decode("utf-8")
    new = re.sub(r"<Version>[^<]+</Version>", f"<Version>{v}</Version>", text, count=1)
    if new != text:
        props.write_bytes(new.encode("utf-8"))
        run("git", "-c", "user.name=zh", "-c", "user.email=zh@local", "commit", "-qam", f"中文版 {v}")


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


def package(version, zhver, bin_dir, out):
    out.mkdir(parents=True, exist_ok=True)
    template = (ROOT / "tools/zh/README-zh.txt").read_text(encoding="utf-8")
    replace = {"TartarusSolver.dll", "TartarusSolver.exe"} | {p.name for p in bin_dir.glob("Tartarus.*.dll")}
    license = (ROOT / "LICENSE").read_bytes()
    made = []
    for suffix, note in [("", RUNTIME_NEEDED), ("-full", RUNTIME_BUNDLED)]:
        official = download(version, f"TartarusSolver-{version}{suffix}.zip", out / f"TartarusSolver-{version}{suffix}.zip")
        target = out / f"TartarusSolver-{zhver}-zh{suffix}.zip"
        readme = template.replace("{version}", zhver).replace("{upstream}", version).replace("{runtime}", note).replace("\n", "\r\n")
        with zipfile.ZipFile(official) as src, zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as dst:
            for info in src.infolist():
                if info.is_dir():
                    continue
                rel = info.filename.replace("\\", "/")
                name = rel.rsplit("/", 1)[-1]
                parts = rel.split("/")
                if len(parts) == 2 and name in replace:  # 程序文件夹根下的那几个 dll
                    data = (bin_dir / name).read_bytes()
                elif len(parts) == 3 and name == "TartarusSolver.resources.dll":  # 各语言界面文字
                    data = (bin_dir / parts[1] / name).read_bytes()
                elif rel == "TartarusSolver/README.txt":
                    data = b"\xef\xbb\xbf" + readme.encode("utf-8")
                else:
                    data = src.read(info)
                dst.writestr(zh_name(rel), data, zipfile.ZIP_DEFLATED)
            dst.writestr(f"{FOLDER}/LICENSE.txt", license, zipfile.ZIP_DEFLATED)
        check(official, target)
        made.append(target)
    return made


FOLDER, EXE = "塔罗斯监狱求解器", "塔罗斯监狱求解器.exe"


def zh_name(rel):
    """中文版包里的路径：程序文件夹和 exe 用中文名（exe 只是启动器，启动的仍是 TartarusSolver.dll）。"""
    parts = rel.split("/")
    parts[0] = FOLDER
    if len(parts) == 2 and parts[1] == "TartarusSolver.exe":
        parts[1] = EXE
    return "/".join(parts)


def check(official, target):
    with zipfile.ZipFile(official) as a, zipfile.ZipFile(target) as b:
        na = {zh_name(i.filename.replace("\\", "/")) for i in a.infolist() if not i.is_dir()}
        nb = {i.filename.replace("\\", "/") for i in b.infolist() if not i.is_dir()}
        na |= {f"{FOLDER}/LICENSE.txt"}
        if na != nb:
            sys.exit(f"{target.name}：文件清单和官方包不一致：{na ^ nb}")
        dll = b.read(f"{FOLDER}/TartarusSolver.dll")
        res = b.read(f"{FOLDER}/zh-Hans/TartarusSolver.resources.dll")
        if "塔罗斯监狱求解器".encode("utf-16-le") not in b.read(f"{FOLDER}/{EXE}"):
            sys.exit(f"{target.name}：exe 里没有中文名和图标")
        missing = [m for m in MARKERS if m.encode("utf-8") not in res and m.encode("utf-16-le") not in res]
        if missing:
            sys.exit(f"{target.name}：主程序里没有中文界面文字 {missing}")
        if b".sentry.io" in dll:
            sys.exit(f"{target.name}：主程序里带着原作者的错误上报地址")
        if b.testzip() is not None:
            sys.exit(f"{target.name}：压缩包损坏")
    print(f"核对通过：{target.name}（{target.stat().st_size / 1048576:.1f} MB）")


def publish(zhver, version, files):
    notes = (f"塔罗斯监狱求解器中文版 {zhver}，基于 glcoder/TartarusSolver v{version}（MIT 协议）修改。\n\n"
             f"- 一般电脑下载 TartarusSolver-{zhver}-zh-full.zip（自带运行库，解压即用）\n"
             f"- 已装 .NET 9 桌面运行时的下载 TartarusSolver-{zhver}-zh.zip（体积小）\n\n"
             "已安装的中文版会自动提示更新。原作者：https://github.com/glcoder/TartarusSolver")
    run("gh", "release", "create", f"v{zhver}", "-R", REPO, "--title", f"中文版 {zhver}", "--notes", notes,
        "--target", run("git", "rev-parse", "HEAD").stdout.strip(), *map(str, files))
    print("已发布：", f"https://github.com/{REPO}/releases/tag/v{zhver}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("version", help="原版版本号，如 0.7.40")
    ap.add_argument("--out", type=Path, default=ROOT / "dist-zh")
    ap.add_argument("--no-port", action="store_true", help="不搬提交（已手动解决冲突时）")
    ap.add_argument("--publish", action="store_true", help="出包后发布到 GitHub Release")
    a = ap.parse_args()
    if not a.no_port:
        port(a.version)
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    built = re.search(r"<Version>([^<]+)</Version>", props).group(1)
    if not built.startswith(a.version + "."):
        sys.exit(f"源码版本是 {built}，不是 {a.version}.x：先切到对应分支。")
    made = package(a.version, built, build(), a.out)
    for z in made:
        print("出包：", z)
    if a.publish:
        publish(built, a.version, made)


if __name__ == "__main__":
    main()
