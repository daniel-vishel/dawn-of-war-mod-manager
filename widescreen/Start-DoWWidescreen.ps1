# ============================================================
#  Dawn of War (Anniversary Edition) - Widescreen Launcher
#  Запускает игру и меняет константу соотношения сторон (4:3 ->
#  ваше) ПРЯМО В ПАМЯТИ процесса. Файлы игры на диске не
#  изменяются вообще.
#
#  Откат: просто запустите игру обычным ярлыком Steam — без
#  лаунчера игра остаётся полностью оригинальной.
#  (Единственное, что правится на диске, — разрешение в конфиге
#  Local.ini; перед правкой создаётся копия Local.ini.wsbak,
#  откат: -RestoreIni)
#
#  Использование:
#      .\Start-DoWWidescreen.ps1                      # 3440x1440, exe не трогаем
#      .\Start-DoWWidescreen.ps1 -Width 2560 -Height 1080
#      .\Start-DoWWidescreen.ps1 -Game WA             # Winter Assault (W40kWA.exe)
#      .\Start-DoWWidescreen.ps1 -ExeMode compromise  # если сломана мини-карта
#      .\Start-DoWWidescreen.ps1 -Attach              # игра уже запущена — только патч
#      .\Start-DoWWidescreen.ps1 -RestoreIni          # вернуть оригинальный Local.ini
# ============================================================

param(
    [int]$Width  = 3440,
    [int]$Height = 1440,
    [ValidateSet('skip','compromise','full')]
    [string]$ExeMode = 'skip',
    [string]$GamePath = '',
    [ValidateSet('W40k','WA')]
    [string]$Game = 'W40k',
    [switch]$Attach,        # не запускать игру, патчить уже запущенную
    [switch]$NoIni,         # не трогать Local.ini
    [switch]$RestoreIni,    # восстановить Local.ini из копии и выйти
    [int]$WaitSec = 120     # сколько ждать загрузки игры и её модулей
)

$ErrorActionPreference = 'Stop'

# ---------- WinAPI: чтение/запись памяти чужого процесса ----------
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class DowMemPatch
{
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, uint newProt, out uint oldProt);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);

    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    struct MODULEENTRY32W {
        public uint dwSize; public uint th32ModuleID; public uint th32ProcessID;
        public uint GlblcntUsage; public uint ProccntUsage;
        public IntPtr modBaseAddr; public uint modBaseSize; public IntPtr hModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=256)] public string szModule;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string szExePath;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool Module32FirstW(IntPtr snap, ref MODULEENTRY32W me);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool Module32NextW(IntPtr snap, ref MODULEENTRY32W me);

    const uint TH32CS_SNAPMODULE   = 0x08;
    const uint TH32CS_SNAPMODULE32 = 0x10;
    // VM_OPERATION | VM_READ | VM_WRITE | QUERY_INFORMATION
    const uint PROC_ACCESS = 0x0008 | 0x0010 | 0x0020 | 0x0400;
    const uint PAGE_EXECUTE_READWRITE = 0x40;

    // Список модулей процесса: "имя|база|размер"
    public static string[] ListModules(int pid) {
        var result = new List<string>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snap == (IntPtr)(-1)) return result.ToArray();
        try {
            var me = new MODULEENTRY32W();
            me.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32W));
            if (Module32FirstW(snap, ref me)) {
                do {
                    result.Add(me.szModule + "|" + me.modBaseAddr.ToInt64() + "|" + me.modBaseSize);
                } while (Module32NextW(snap, ref me));
            }
        } finally { CloseHandle(snap); }
        return result.ToArray();
    }

    // Ищет find в памяти [base, base+size), пишет repl по всем вхождениям.
    // Возвращает [заменено, ужеСтоялоНовоеЗначение]
    public static int[] PatchRange(int pid, long baseAddr, long size, byte[] find, byte[] repl) {
        IntPtr h = OpenProcess(PROC_ACCESS, false, pid);
        if (h == IntPtr.Zero)
            throw new Exception("OpenProcess failed, err=" + Marshal.GetLastWin32Error());
        int patched = 0, present = 0;
        try {
            const int chunk = 0x10000;
            byte[] buf = new byte[chunk + 16];
            for (long off = 0; off < size; off += chunk) {
                int want = (int)Math.Min((long)chunk + find.Length - 1, size - off);
                if (want < find.Length) break;
                IntPtr got;
                if (!ReadProcessMemory(h, (IntPtr)(baseAddr + off), buf, (IntPtr)want, out got))
                    continue; // нечитаемый участок - пропускаем
                int len = (int)got;
                for (int i = 0; i <= len - find.Length; i++) {
                    bool mFind = true, mRepl = true;
                    for (int j = 0; j < find.Length; j++) {
                        if (buf[i + j] != find[j]) mFind = false;
                        if (buf[i + j] != repl[j]) mRepl = false;
                        if (!mFind && !mRepl) break;
                    }
                    if (mRepl) { present++; i += find.Length - 1; continue; }
                    if (!mFind) continue;
                    IntPtr addr = (IntPtr)(baseAddr + off + i);
                    uint oldProt;
                    if (VirtualProtectEx(h, addr, (IntPtr)repl.Length, PAGE_EXECUTE_READWRITE, out oldProt)) {
                        IntPtr written;
                        if (WriteProcessMemory(h, addr, repl, (IntPtr)repl.Length, out written)
                            && (long)written == repl.Length) patched++;
                        uint tmp;
                        VirtualProtectEx(h, addr, (IntPtr)repl.Length, oldProt, out tmp);
                    }
                    i += find.Length - 1;
                }
            }
        } finally { CloseHandle(h); }
        return new int[] { patched, present };
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

$ExeName  = if ($Game -eq 'WA') { 'W40kWA.exe' } else { 'W40k.exe' }
$ProcName = [IO.Path]::GetFileNameWithoutExtension($ExeName)
$IniPath  = Join-Path $GamePath 'Local.ini'
$IniBak   = "$IniPath.wsbak"

# ---------- Откат Local.ini ----------
if ($RestoreIni) {
    if (Test-Path $IniBak) {
        if (Test-Path $IniPath) { Set-ItemProperty $IniPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
        Copy-Item $IniBak $IniPath -Force
        Remove-Item $IniBak -Force
        Write-Host "Local.ini восстановлен из копии. Готово." -ForegroundColor Green
    } else {
        Write-Host "Копия $IniBak не найдена — Local.ini не изменялся этим лаунчером." -ForegroundColor Yellow
    }
    exit 0
}

# ---------- Вычисление байтов ----------
$OrigBytes = [byte[]](0xAB,0xAA,0xAA,0x3F)                 # float 1.3333 (4:3)
$AspectVal = [float]($Width / $Height)
$NewBytes  = [BitConverter]::GetBytes($AspectVal)
$CompBytes = [BitConverter]::GetBytes([float]1.25)         # 00 00 A0 3F

$hexNew = ($NewBytes | ForEach-Object { $_.ToString('X2') }) -join ' '
Write-Host ("Разрешение: {0}x{1}  |  соотношение = {2:N4}  |  hex: {3}" -f $Width,$Height,$AspectVal,$hexNew) -ForegroundColor Cyan
Write-Host "Режим exe: $ExeMode | игра: $ExeName`n"

# ---------- Local.ini: прописать разрешение (с копией) ----------
if (-not $NoIni) {
    if (Test-Path $IniPath) {
        if (-not (Test-Path $IniBak)) {
            Copy-Item $IniPath $IniBak
            Write-Host "Копия конфига: Local.ini.wsbak" -ForegroundColor DarkGray
        }
        Set-ItemProperty $IniPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
        $txt = Get-Content $IniPath -Raw
        $txt = $txt -replace '(?im)^\s*screenwidth\s*=\s*\d+',  "screenwidth=$Width"
        $txt = $txt -replace '(?im)^\s*screenheight\s*=\s*\d+', "screenheight=$Height"
        if ($txt -notmatch '(?im)^\s*screenwidth')  { $txt += "`r`nscreenwidth=$Width" }
        if ($txt -notmatch '(?im)^\s*screenheight') { $txt += "`r`nscreenheight=$Height" }
        Set-Content -Path $IniPath -Value $txt -Encoding ASCII
        Write-Host "Local.ini: разрешение ${Width}x${Height} прописано." -ForegroundColor Green
    } else {
        Write-Host "[!] Local.ini не найден — игра создаст его при первом запуске; разрешение выставьте повторным запуском лаунчера." -ForegroundColor Yellow
    }
}

# ---------- Цели патча ----------
$Targets = [ordered]@{
    'Platform.dll'      = $NewBytes
    'spDx9.dll'         = $NewBytes
    'UserInterface.dll' = $NewBytes
}
switch ($ExeMode) {
    'compromise' { $Targets[$ExeName] = $CompBytes }
    'full'       { $Targets[$ExeName] = $NewBytes }
}

# ---------- Запуск игры ----------
$proc = Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc -and -not $Attach) {
    Write-Host "Игра уже запущена (PID $($proc.Id)) — патчу её (как -Attach)." -ForegroundColor Yellow
} elseif (-not $proc) {
    if ($Attach) {
        Write-Host "Процесс $ProcName не найден — запустите игру и повторите." -ForegroundColor Red
        exit 1
    }
    Write-Host "Запускаю $ExeName..." -ForegroundColor Cyan
    Start-Process -FilePath (Join-Path $GamePath $ExeName) -WorkingDirectory $GamePath | Out-Null
}

# ---------- Ожидание процесса и патч модулей ----------
$deadline = (Get-Date).AddSeconds($WaitSec)
$done     = @{}   # "pid|module" -> $true
$lastPid  = 0
$allDone  = $false

Write-Host "Жду загрузки игры и патчу модули в памяти (до $WaitSec с)..." -ForegroundColor Cyan

while ((Get-Date) -lt $deadline -and -not $allDone) {
    Start-Sleep -Milliseconds 400
    $proc = Get-Process -Name $ProcName -ErrorAction SilentlyContinue |
            Sort-Object StartTime -Descending | Select-Object -First 1
    if (-not $proc) { continue }
    $procId = $proc.Id
    if ($procId -ne $lastPid -and $lastPid -ne 0) {
        Write-Host "  (игра перезапустилась: PID $lastPid -> $procId, патчу заново)" -ForegroundColor DarkYellow
    }
    $lastPid = $procId

    $mods = @()
    try { $mods = [DowMemPatch]::ListModules($procId) } catch { continue }
    if ($mods.Count -eq 0) { continue }

    foreach ($t in @($Targets.Keys)) {
        $key = "$procId|$t"
        if ($done[$key]) { continue }
        $m = $mods | Where-Object { $_.Split('|')[0] -ieq $t } | Select-Object -First 1
        if (-not $m) { continue }
        $parts = $m.Split('|')
        try {
            $r = [DowMemPatch]::PatchRange($procId, [long]$parts[1], [long]$parts[2], $OrigBytes, $Targets[$t])
        } catch { continue }
        if ($r[0] -gt 0) {
            $done[$key] = $true
            Write-Host "  [OK] $t — заменено в памяти: $($r[0]) вхожд." -ForegroundColor Green
        } elseif ($r[1] -gt 0) {
            $done[$key] = $true
            Write-Host "  [--] $t — уже содержит новое значение ($($r[1]) вхожд.)" -ForegroundColor DarkYellow
        }
        # если ни того ни другого - модуль ещё мог не расжаться/файл нестандартный; попробуем ещё
    }

    $doneCount = @($Targets.Keys | Where-Object { $done["$procId|$_"] }).Count
    if ($doneCount -eq $Targets.Count) { $allDone = $true }
}

Write-Host ""
if ($allDone) {
    Write-Host "================= ГОТОВО =================" -ForegroundColor Cyan
    Write-Host "Все модули пропатчены в памяти. Файлы игры на диске не изменялись." -ForegroundColor Green
    Write-Host @"
Проверьте в игре:
 1) Обзор шире (доп. область по бокам, не растяжение).
 2) Круги выделения юнитов остаются круглыми.
 3) Мини-карта: если искажена — перезапустите через
    .\Start-DoWWidescreen.ps1 -ExeMode compromise

Откат: просто запускайте игру обычным ярлыком Steam.
       Вернуть исходный Local.ini: .\Start-DoWWidescreen.ps1 -RestoreIni
==========================================
"@ -ForegroundColor Cyan
} else {
    $missing = @($Targets.Keys | Where-Object { -not $done["$lastPid|$_"] })
    Write-Host "[!] Не удалось пропатчить: $($missing -join ', ')" -ForegroundColor Red
    Write-Host @"
Возможные причины:
 - игра не успела загрузить модуль за $WaitSec с (увеличьте -WaitSec);
 - файлы уже изменены дисковым патчером (тогда всё уже работает);
 - нестандартная версия игры (шаблон 4:3 не найден).
Запасной вариант — дисковый патчер: ..\widescreen\DoW-Widescreen-Patcher.ps1
"@ -ForegroundColor Yellow
}
