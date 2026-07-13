# ============================================================
#  Dawn of War (Anniversary Edition) - Unstretched UI
#  Нерастянутый интерфейс для широких экранов (21:9 и др.).
#
#  Принцип: разметка интерфейса лежит в Engine.sga ->
#  data/art/ui/screens/*.screen (Lua-таблицы, координаты в долях
#  экрана). Скрипт извлекает разметку ИЗ ВАШЕГО архива, сжимает
#  все панели HUD по горизонтали к центру с коэффициентом
#  k = (4/3) / (ширина/высота), после чего интерфейс имеет те же
#  пропорции, что на 4:3 (мини-карта снова квадратная), а 3D-мир
#  (ctmSimVis) остаётся во всю ширину экрана.
#
#  Оригинальные файлы игры НЕ изменяются: результат кладётся
#  loose-файлом в <игра>\Engine\Data поверх архива.
#  Откат: -Restore (удаляет установленные файлы по манифесту).
#
#  Использование:
#      .\Install-UnstretchedUI.ps1                    # 3440x1440, только игровой HUD
#      .\Install-UnstretchedUI.ps1 -Width 2560 -Height 1080
#      .\Install-UnstretchedUI.ps1 -AllScreens        # + все меню (экспериментально)
#      .\Install-UnstretchedUI.ps1 -Restore           # откат
# ============================================================

param(
    [int]$Width  = 3440,
    [int]$Height = 1440,
    [string]$GamePath = '',
    [switch]$AllScreens,   # преобразовать все *.screen (меню), а не только игровой HUD
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture

# ---------- C#: чтение SGA v2 + парсер/трансформер .screen ----------
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

public class SgaEntry {
    public string Path;
    public uint CompFlag;
    public long DataOffset;
    public int  CompSize;
    public int  DecompSize;
}

public static class SgaV2R {
    static ushort U16(byte[] b, long o) { return BitConverter.ToUInt16(b, (int)o); }
    static uint   U32(byte[] b, long o) { return BitConverter.ToUInt32(b, (int)o); }
    static string CStr(byte[] b, long start) {
        long end = start;
        while (end < b.Length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, (int)start, (int)(end - start));
    }
    public static List<SgaEntry> ReadToc(string path) {
        byte[] b = File.ReadAllBytes(path);
        if (Encoding.ASCII.GetString(b, 0, 8) != "_ARCHIVE") throw new Exception("bad magic");
        if (U16(b, 8) != 2) throw new Exception("unsupported SGA version");
        long dataOffset = U32(b, 0xB0);
        const long TOC = 0xB4;
        long vdOff   = TOC + U32(b, TOC + 0);
        long dirOff  = TOC + U32(b, TOC + 6);  int dirCnt  = U16(b, TOC + 10);
        long fileOff = TOC + U32(b, TOC + 12);
        long nameOff = TOC + U32(b, TOC + 18);
        string drivePath = CStr(b, vdOff);
        var result = new List<SgaEntry>();
        for (int d = 0; d < dirCnt; d++) {
            long e = dirOff + d * 12;
            string dir = CStr(b, nameOff + U32(b, e)).Replace('\\', '/');
            int fs = U16(b, e + 8), fe = U16(b, e + 10);
            for (int f = fs; f < fe; f++) {
                long fe2 = fileOff + f * 20;
                var it = new SgaEntry();
                string nm = CStr(b, nameOff + U32(b, fe2));
                it.Path = (dir.Length > 0 ? dir + "/" : "") + nm;
                if (drivePath.Length > 0) it.Path = drivePath.ToLowerInvariant() + "/" + it.Path;
                it.CompFlag   = U32(b, fe2 + 4);
                it.DataOffset = dataOffset + U32(b, fe2 + 8);
                it.CompSize   = (int)U32(b, fe2 + 12);
                it.DecompSize = (int)U32(b, fe2 + 16);
                result.Add(it);
            }
        }
        return result;
    }
    public static byte[] ReadFileData(string archivePath, SgaEntry e) {
        using (var fs = File.OpenRead(archivePath)) {
            fs.Seek(e.DataOffset, SeekOrigin.Begin);
            byte[] raw = new byte[e.CompSize];
            int got = 0;
            while (got < raw.Length) {
                int r = fs.Read(raw, got, raw.Length - got);
                if (r <= 0) throw new Exception("unexpected EOF");
                got += r;
            }
            if (e.CompFlag == 0) return raw;
            using (var ms = new MemoryStream(raw, 2, raw.Length - 2))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream(e.DecompSize)) {
                ds.CopyTo(outMs);
                return outMs.ToArray();
            }
        }
    }
}

// ----- Парсер Lua-таблиц формата .screen -----
public class LNode {
    public string Key;                 // null для безымянных элементов
    public bool IsTable;
    public string Scalar;              // сырой скаляр: "строка", число, true/false
    public List<LNode> Items = new List<LNode>();
    public LNode Get(string key) {
        foreach (var n in Items) if (n.Key == key) return n;
        return null;
    }
    public string GetStr(string key) {
        var n = Get(key);
        if (n == null || n.Scalar == null) return null;
        return n.Scalar.Trim('"');
    }
}

public static class ScreenFile {
    static string _s; static int _p;
    public static int Patched;

    public static LNode Parse(string text) {
        _s = text; _p = 0;
        var root = new LNode { IsTable = true };
        SkipWs();
        while (_p < _s.Length) {
            string id = ReadIdent();
            SkipWs(); Expect('='); SkipWs();
            var v = ReadValue(); v.Key = id;
            root.Items.Add(v);
            SkipWs();
        }
        return root;
    }

    static void SkipWs() {
        while (_p < _s.Length) {
            char c = _s[_p];
            if (char.IsWhiteSpace(c)) { _p++; continue; }
            if (c == '-' && _p + 1 < _s.Length && _s[_p + 1] == '-') {
                while (_p < _s.Length && _s[_p] != '\n') _p++;
                continue;
            }
            break;
        }
    }
    static string ReadIdent() {
        int st = _p;
        while (_p < _s.Length && (char.IsLetterOrDigit(_s[_p]) || _s[_p] == '_')) _p++;
        if (st == _p) throw new Exception("identifier expected at offset " + _p);
        return _s.Substring(st, _p - st);
    }
    static void Expect(char c) {
        if (_p >= _s.Length || _s[_p] != c) throw new Exception(c + " expected at offset " + _p);
        _p++;
    }
    static LNode ReadValue() {
        SkipWs();
        char c = _s[_p];
        if (c == '{') {
            _p++;
            var t = new LNode { IsTable = true };
            SkipWs();
            while (_s[_p] != '}') {
                LNode item;
                int save = _p;
                if (char.IsLetter(_s[_p]) || _s[_p] == '_') {
                    string id = ReadIdent(); SkipWs();
                    if (_p < _s.Length && _s[_p] == '=') {
                        _p++;
                        item = ReadValue();
                        item.Key = id;
                    } else { _p = save; item = ReadValue(); }
                } else item = ReadValue();
                t.Items.Add(item);
                SkipWs();
                if (_p < _s.Length && _s[_p] == ',') { _p++; SkipWs(); }
            }
            _p++;
            return t;
        }
        if (c == '"') {
            int st = _p; _p++;
            while (_s[_p] != '"') { if (_s[_p] == '\\') _p++; _p++; }
            _p++;
            return new LNode { Scalar = _s.Substring(st, _p - st) };
        }
        {
            int st = _p;
            while (_p < _s.Length && !char.IsWhiteSpace(_s[_p]) && _s[_p] != ',' && _s[_p] != '}') _p++;
            return new LNode { Scalar = _s.Substring(st, _p - st) };
        }
    }

    public static string Serialize(LNode root) {
        var sb = new StringBuilder();
        foreach (var n in root.Items) {
            sb.Append(n.Key).Append(" = ");
            WriteVal(sb, n, 0);
            sb.Append("\n");
        }
        return sb.ToString();
    }
    static void WriteVal(StringBuilder sb, LNode n, int ind) {
        if (!n.IsTable) { sb.Append(n.Scalar); return; }
        sb.Append("\n");
        Indent(sb, ind); sb.Append("{\n");
        foreach (var it in n.Items) {
            Indent(sb, ind + 1);
            if (it.Key != null) sb.Append(it.Key).Append(" = ");
            WriteVal(sb, it, ind + 1);
            sb.Append(",\n");
        }
        Indent(sb, ind); sb.Append("}");
    }
    static void Indent(StringBuilder sb, int n) { for (int i = 0; i < n; i++) sb.Append('\t'); }

    // ----- Преобразование под широкий экран -----
    // Боксы виджетов заданы в долях экрана, позиция — смещение от
    // родителя. Примитивы (Graphic/Text/HitArea) — в долях родителя,
    // их не трогаем. Корневые панели прижимаются к центру
    // (x' = x*k + (1-k)/2), вложенные — чистое масштабирование x*k.
    public static void Transform(LNode fileRoot, double k, string[] keepNames) {
        Patched = 0;
        foreach (var asg in fileRoot.Items) {
            if (!asg.IsTable) continue;
            foreach (var sub in asg.Items) {
                if (sub.IsTable && (sub.Key == "Widgets" || sub.Key == "TooltipWidgets")) {
                    TransformChildren(sub, k, keepNames, true);
                }
            }
        }
    }
    static void TransformChildren(LNode widget, double k, string[] keep, bool anchorMode) {
        var children = widget.Get("Children");
        if (children == null || !children.IsTable) return;
        foreach (var ch in children.Items) {
            if (!ch.IsTable) continue;
            TransformWidget(ch, k, keep, anchorMode);
        }
    }
    static void TransformWidget(LNode w, double k, string[] keep, bool anchorMode) {
        string name = w.GetStr("name");
        if (name != null) {
            foreach (var kn in keep) {
                if (string.Equals(kn, name, StringComparison.OrdinalIgnoreCase)) {
                    TransformChildren(w, k, keep, anchorMode); // бокс не трогаем
                    return;
                }
            }
        }
        ScaleX(w.Get("position"), k, anchorMode ? (1.0 - k) * 0.5 : 0.0);
        ScaleX(w.Get("size"), k, 0.0);
        Patched++;
        TransformChildren(w, k, keep, false);
    }
    static void ScaleX(LNode t, double k, double add) {
        if (t == null || !t.IsTable || t.Items.Count < 1) return;
        var x = t.Items[0];
        double v;
        if (x.Scalar != null &&
            double.TryParse(x.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) {
            x.Scalar = (v * k + add).ToString("0.#####", CultureInfo.InvariantCulture);
        }
    }
}
"@

# ---------- Поиск папки с игрой ----------
function Find-GamePath {
    $candidates = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Dawn of War Gold",
        "C:\Program Files (x86)\Steam\steamapps\common\Dawn of War Anniversary Edition",
        "C:\Program Files\Steam\steamapps\common\Dawn of War Gold",
        "D:\Steam\steamapps\common\Dawn of War Gold",
        "D:\SteamLibrary\steamapps\common\Dawn of War Gold",
        "E:\Steam\steamapps\common\Dawn of War Gold",
        "E:\SteamLibrary\steamapps\common\Dawn of War Gold"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'W40k.exe')) { return $c }
    }
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        if ($steam) {
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path $vdf) {
                $libs = Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches |
                        ForEach-Object { $_.Matches } |
                        ForEach-Object { $_.Groups[1].Value -replace '\\\\','\' }
                foreach ($lib in $libs) {
                    foreach ($name in @('Dawn of War Gold','Dawn of War Anniversary Edition','Dawn of War')) {
                        $p = Join-Path $lib "steamapps\common\$name"
                        if (Test-Path (Join-Path $p 'W40k.exe')) { return $p }
                    }
                }
            }
        }
    } catch {}
    return $null
}

if (-not $GamePath) { $GamePath = Find-GamePath }
if (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'W40k.exe'))) {
    Write-Host "Не нашёл папку с игрой автоматически." -ForegroundColor Yellow
    $GamePath = Read-Host "Вставьте полный путь к папке игры (где лежит W40k.exe)"
    if (-not (Test-Path (Join-Path $GamePath 'W40k.exe'))) {
        Write-Host "W40k.exe не найден по этому пути. Выход." -ForegroundColor Red
        exit 1
    }
}
Write-Host "Папка игры: $GamePath" -ForegroundColor Cyan

# ---------- Engine.sga и папка установки ----------
$engineSga = Get-ChildItem -Path $GamePath -Recurse -Filter 'Engine.sga' -ErrorAction SilentlyContinue |
             Select-Object -First 1
if (-not $engineSga) {
    Write-Host "Engine.sga не найден в папке игры." -ForegroundColor Red
    exit 1
}
$engineDir  = Split-Path $engineSga.FullName -Parent
$installDir = Join-Path $engineDir 'Data\art\ui\screens'
$manifest   = Join-Path $engineDir 'Data\ui-unstretch-manifest.txt'

# ---------- Откат ----------
if ($Restore) {
    if (Test-Path $manifest) {
        foreach ($f in Get-Content $manifest) {
            if ($f -and (Test-Path $f)) {
                Remove-Item $f -Force
                Write-Host "Удалён: $f" -ForegroundColor Green
            }
        }
        Remove-Item $manifest -Force
        Write-Host "UI вернулся к оригиналу (разметка снова читается из Engine.sga). Готово." -ForegroundColor Green
    } else {
        Write-Host "Манифест не найден — мод не установлен, откатывать нечего." -ForegroundColor Yellow
    }
    exit 0
}

# ---------- Преобразование ----------
$aspect = $Width / $Height
$k = (4.0 / 3.0) / $aspect
if ($k -ge 0.999) {
    Write-Host "Соотношение $Width x $Height не шире 4:3 — преобразование не требуется." -ForegroundColor Yellow
    exit 0
}
Write-Host ("Разрешение: {0}x{1} | k = {2}" -f $Width, $Height, $k.ToString('0.####', $inv)) -ForegroundColor Cyan

$entries = [SgaV2R]::ReadToc($engineSga.FullName)
$screens = @($entries | Where-Object { $_.Path -like '*art/ui/screens/*.screen' })
if (-not $AllScreens) {
    $screens = @($screens | Where-Object { $_.Path -like '*gamescreen.screen' })
}
if ($screens.Count -eq 0) {
    Write-Host "В Engine.sga не найдено файлов разметки (*.screen) — нестандартная версия игры?" -ForegroundColor Red
    exit 1
}
Write-Host "Файлов разметки к преобразованию: $($screens.Count)"

# Виджеты, которые НЕ сжимаем (3D-мир и фоновая заглушка)
$keep = @('ctmSimVis', 'testBackground')

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$installed = @()
$failed = 0
foreach ($e in $screens) {
    $name = Split-Path ($e.Path -replace '/', '\') -Leaf
    try {
        $text = [Text.Encoding]::ASCII.GetString([SgaV2R]::ReadFileData($engineSga.FullName, $e))
        $tree = [ScreenFile]::Parse($text)
        [ScreenFile]::Transform($tree, $k, $keep)
        $out  = [ScreenFile]::Serialize($tree)
        [ScreenFile]::Parse($out) | Out-Null   # самопроверка: результат снова парсится
        $dest = Join-Path $installDir $name
        [IO.File]::WriteAllText($dest, $out, [Text.Encoding]::ASCII)
        $installed += $dest
        Write-Host ("  [OK] {0} — виджетов пересчитано: {1}" -f $name, [ScreenFile]::Patched) -ForegroundColor Green
    } catch {
        $failed++
        Write-Host "  [!!] $name — $($_.Exception.Message)" -ForegroundColor Red
    }
}

if ($installed.Count -gt 0) {
    $installed | Set-Content -Path $manifest -Encoding ASCII
}

Write-Host @"

================= ГОТОВО =================
Установлено файлов: $($installed.Count) (ошибок: $failed)
Куда: $installDir
Оригинальные архивы игры не изменялись.

Проверьте в игре (скирмиш):
 1) Панели HUD собраны по центру с пропорциями 4:3, не растянуты.
 2) Мини-карта квадратная.
 3) 3D-мир — во всю ширину экрана.
 4) Клики по кнопкам попадают точно.

Откат:      .\Install-UnstretchedUI.ps1 -Restore
Все меню:   .\Install-UnstretchedUI.ps1 -AllScreens
==========================================
"@ -ForegroundColor Cyan
