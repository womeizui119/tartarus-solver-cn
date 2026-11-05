"""中文版一键出包：编译中文版，替换进原版的官方发布包，打出中文版 zip；也能把中文版改动搬到原版的新版本上。

用法（在仓库根目录，需要 git、.NET 9 SDK、Python 3；发布还需要 gh 命令行）：
    python tools/zh/build_zh.py                    # 用当前源码出包
    python tools/zh/build_zh.py --publish          # 出包后发布到本仓库的 GitHub Release
    python tools/zh/build_zh.py --port 0.7.45      # 把中文版改动搬到原版 v0.7.45 上（中文版版本号次位 +1），再出包

版本号：中文版有自己的版本号（Directory.Build.props 的 <Version>，从 1.0.0 起只往上加，自动更新按它判断新旧），
所基于的原版版本记在 <TartarusUpstream>。发布包名 TartarusSolver-<中文版版本>-zh.zip / -zh-full.zip（软件更新按这个名字找）。

原版官方包（里面有识别模型和运行库）先从原作者的发布页下载；下载不到时，用本仓库 Release 里的备份
（tag upstream-v<原版版本>，由“备份原版”流程建）。

步骤：
1. （--port）取原版 tag，在其上新建分支 zh-<原版版本>，把中文版提交逐个搬过去。有冲突时停下来，说明哪些文件冲突。
2. 编译 src/Tartarus.App（Release，win-x64；非 Windows 上自动加 EnableWindowsTargeting）。
   环境变量 TARTARUS_SENTRY_DSN 有值时，问题报告会发到这个 Sentry 项目；没有就只存在玩家本机。
3. 把官方包里的 exe、TartarusSolver.dll、Tartarus.*.dll 和各语言 TartarusSolver.resources.dll 换成新编译的，
   程序文件夹和 exe 改中文名，README.txt 换成中文说明，加上 LICENSE（MIT，保留原作者版权）。
4. 核对：文件清单与官方包一致（只多了 LICENSE）；带着中文界面文字和中文图标。
5. （--publish）在本仓库建 tag v<中文版版本> 的 Release 并上传两个包。
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
PROPS = ROOT / "Directory.Build.props"


def prop(name):
    return re.search(rf"<{name}>([^<]+)</{name}>", PROPS.read_text(encoding="utf-8")).group(1)


def set_prop(name, value):
    text = PROPS.read_bytes().decode("utf-8")
    PROPS.write_bytes(re.sub(rf"<{name}>[^<]+</{name}>", f"<{name}>{value}</{name}>", text, count=1).encode("utf-8"))


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
    """当前源码所基于的原版 tag。"""
    return "v" + prop("TartarusUpstream")


def port(version):
    tag = f"v{version}"
    run("git", "fetch", "--tags", "--depth", "50", UPSTREAM, check=False)
    run("git", "fetch", UPSTREAM, f"refs/tags/{tag}:refs/tags/{tag}", check=False)
    # 原版仓库没了时，用本仓库的备份（“自动备份原版”流程存的 upstream/<tag>）
    run("git", "fetch", "origin", f"refs/tags/upstream/{tag}:refs/tags/{tag}", check=False)
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
    major, minor, *_ = (int(x) for x in prop("Version").split("."))
    zhver = f"{major}.{minor + 1}.0"
    set_prop("TartarusUpstream", version)
    set_prop("Version", zhver)
    run("git", "-c", "user.name=zh", "-c", "user.email=zh@local", "commit", "-qam", f"中文版 {zhver}（基于原版 {version}）")


def build():
    dotnet = shutil.which("dotnet") or str(Path.home() / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet"))
    cmd = [dotnet, "build", "src/Tartarus.App/Tartarus.App.csproj", "-c", "Release", "-p:TARTARUS_SENTRY_DSN=" + os.environ.get("TARTARUS_SENTRY_DSN", "")]
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
    if dest.exists():
        return dest
    for url in (f"{UPSTREAM}/releases/download/v{version}/{name}",
                f"https://github.com/{REPO}/releases/download/upstream-v{version}/{name}"):  # 本仓库的备份
        print("下载", url)
        try:
            with urllib.request.urlopen(url) as r, open(dest, "wb") as f:
                shutil.copyfileobj(r, f)
            return dest
        except Exception as e:
            print("  下载不到：", e)
            dest.unlink(missing_ok=True)
    sys.exit(f"原版和备份里都没有 {name}")


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
        dsn = os.environ.get("TARTARUS_SENTRY_DSN", "")
        if b".sentry.io" in dll and (not dsn or dsn.encode() not in dll):
            sys.exit(f"{target.name}：主程序里带着别人的错误上报地址")
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


def ocr_from_official(version, out):
    """编译要用的 OCR 模型从原版官方包里取（原本从第三方网站下载）：官方包有备份，就不再依赖那些网站。"""
    out.mkdir(parents=True, exist_ok=True)
    dest = ROOT / "models" / "ocr"
    dest.mkdir(parents=True, exist_ok=True)
    official = download(version, f"TartarusSolver-{version}.zip", out / f"TartarusSolver-{version}.zip")
    with zipfile.ZipFile(official) as z:
        for info in z.infolist():
            rel = info.filename.replace("\\", "/")
            if rel.startswith("TartarusSolver/models/ocr/") and not info.is_dir():
                target = dest / rel.rsplit("/", 1)[-1]
                if not target.exists():
                    target.write_bytes(z.read(info))
                    print("OCR 模型取自官方包：", target.name)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", metavar="原版版本", help="把中文版改动搬到原版这个版本上，如 0.7.45")
    ap.add_argument("--out", type=Path, default=ROOT / "dist-zh")
    ap.add_argument("--publish", action="store_true", help="出包后发布到 GitHub Release")
    a = ap.parse_args()
    if a.port:
        port(a.port)
    upstream, zhver = prop("TartarusUpstream"), prop("Version")
    print(f"中文版 {zhver}，基于原版 {upstream}")
    ocr_from_official(upstream, a.out)
    made = package(upstream, zhver, build(), a.out)
    for z in made:
        print("出包：", z)
    if a.publish:
        publish(zhver, upstream, made)


if __name__ == "__main__":
    main()
