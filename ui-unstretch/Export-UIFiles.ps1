# ============================================================
#  Export-UIFiles.ps1 — выгрузка файлов разметки интерфейса
#  из архивов игры для анализа/доработки UI-мода.
#
#  Достаёт из W40kData*.sga (и WXP*.sga, если есть) все файлы
#  art/ui/screens/* (.screen, .lua taskbar-разметка) в папку
#  .\extracted\<имя-архива>\ рядом со скриптом.
#
#  Ничего в игре не изменяет — только читает архивы.
#
#  Использование:
#      .\Export-UIFiles.ps1
#      .\Export-UIFiles.ps1 -GamePath "D:\...\Dawn of War Gold"
# ============================================================

param(
    [string]$GamePath = '',
    [string]$Mask = '*ui/screens*'
)

$ErrorActionPreference = 'Stop'

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

$Extractor = Join-Path $PSScriptRoot '..\tools\Extract-Sga.ps1'
if (-not (Test-Path $Extractor)) {
    Write-Host "Не найден $Extractor — запускайте из папки ui-unstretch репозитория." -ForegroundColor Red
    exit 1
}

# Все data-архивы модулей (UI-разметка лежит в W40kData.sga)
$archives = Get-ChildItem -Path $GamePath -Recurse -Filter '*.sga' |
            Where-Object { $_.Name -match '(?i)Data' -and $_.Name -notmatch '(?i)sound|music|movies|SharedTextures|FullRes' }

if (-not $archives) {
    Write-Host "Не нашёл data-архивов .sga в папке игры." -ForegroundColor Red
    exit 1
}

$OutRoot = Join-Path $PSScriptRoot 'extracted'
$total = 0
foreach ($a in $archives) {
    Write-Host "`n=== $($a.Name) ===" -ForegroundColor Cyan
    $out = Join-Path $OutRoot ($a.BaseName)
    try {
        & $Extractor -Archive $a.FullName -Mask $Mask -OutDir $out
        $files = @(Get-ChildItem $out -Recurse -File -ErrorAction SilentlyContinue)
        $total += $files.Count
    } catch {
        Write-Host "  [!] $($a.Name): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host "`nИтого выгружено файлов: $total -> $OutRoot" -ForegroundColor Green
Write-Host "Эта папка нужна для доработки UI-мода (пересчёт разметки под 21:9)." -ForegroundColor Cyan
