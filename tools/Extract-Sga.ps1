# ============================================================
#  Extract-Sga.ps1 — распаковщик архивов Relic SGA v2 (Dawn of War 1)
#
#  Использование:
#      .\Extract-Sga.ps1 -Archive W40kData.sga -List
#      .\Extract-Sga.ps1 -Archive W40kData.sga -List -Mask "*ui*"
#      .\Extract-Sga.ps1 -Archive W40kData.sga -Mask "art/ui/screens/*" -OutDir .\out
#
#  Формат SGA v2 (little-endian):
#    0x00  "_ARCHIVE"                     8 байт
#    0x08  версия u16 major=2, u16 minor  4 байта
#    0x0C  md5 A                          16 байт
#    0x1C  имя архива (utf-16-le)         128 байт
#    0x9C  md5 B                          16 байт
#    0xAC  размер TOC u32
#    0xB0  смещение данных u32
#    0xB4  TOC (все смещения внутри TOC — от 0xB4):
#          u32 vdOff, u16 vdCnt, u32 dirOff, u16 dirCnt,
#          u32 fileOff, u16 fileCnt, u32 nameOff, u16 nameCnt
#    vdrive: 64s path, 64s name, 4*u16 диапазоны, 2 байта     (138)
#    dir:    u32 nameOff(отн. секции имён), 4*u16 диапазоны   (12)
#    file:   u32 nameOff, u32 флагСжатия(0/16/32), u32 dataOff,
#            u32 размерСжат, u32 размерРаспак                 (20)
#    имена:  ASCIIZ-строки; nameOff — от начала секции имён
#    данные: dataOff — от "смещения данных"; флаг 16/32 = zlib
# ============================================================

param(
    [Parameter(Mandatory=$true)]
    [string]$Archive,
    [string]$Mask = '*',
    [string]$OutDir = '',
    [switch]$List
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

public class SgaEntry {
    public string Path;
    public uint CompFlag;
    public long DataOffset;   // абсолютное смещение в файле архива
    public int  CompSize;
    public int  DecompSize;
}

public static class SgaV2 {
    static ushort U16(byte[] b, long o) { return BitConverter.ToUInt16(b, (int)o); }
    static uint   U32(byte[] b, long o) { return BitConverter.ToUInt32(b, (int)o); }

    static string CStr(byte[] b, long start) {
        long end = start;
        while (end < b.Length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, (int)start, (int)(end - start));
    }

    public static List<SgaEntry> ReadToc(string path) {
        byte[] b = File.ReadAllBytes(path);
        if (Encoding.ASCII.GetString(b, 0, 8) != "_ARCHIVE")
            throw new Exception("not an SGA archive (bad magic)");
        int verMajor = U16(b, 8);
        if (verMajor != 2)
            throw new Exception("unsupported SGA version " + verMajor + " (expected 2 for DoW1)");
        long dataOffset = U32(b, 0xB0);
        const long TOC = 0xB4;

        long vdOff   = TOC + U32(b, TOC + 0);  int vdCnt   = U16(b, TOC + 4);
        long dirOff  = TOC + U32(b, TOC + 6);  int dirCnt  = U16(b, TOC + 10);
        long fileOff = TOC + U32(b, TOC + 12); int fileCnt = U16(b, TOC + 16);
        long nameOff = TOC + U32(b, TOC + 18); // nameCnt (кол-во строк) не нужен

        // Папки: имя (полный путь вида art\ui\screens) + диапазон файлов
        var dirNames  = new string[dirCnt];
        var dirFileStart = new int[dirCnt];
        var dirFileEnd   = new int[dirCnt];
        for (int i = 0; i < dirCnt; i++) {
            long e = dirOff + i * 12;
            dirNames[i]     = CStr(b, nameOff + U32(b, e));
            dirFileStart[i] = U16(b, e + 8);
            dirFileEnd[i]   = U16(b, e + 10);
        }

        // Диск (vdrive) обычно один: path="data"
        string drivePath = vdCnt > 0 ? CStr(b, vdOff) : "";

        var result = new List<SgaEntry>();
        for (int d = 0; d < dirCnt; d++) {
            string dir = dirNames[d].Replace('\\', '/');
            for (int f = dirFileStart[d]; f < dirFileEnd[d]; f++) {
                long e = fileOff + f * 20;
                var it = new SgaEntry();
                string nm = CStr(b, nameOff + U32(b, e));
                it.Path       = (dir.Length > 0 ? dir + "/" : "") + nm;
                if (drivePath.Length > 0) it.Path = drivePath.ToLowerInvariant() + "/" + it.Path;
                it.CompFlag   = U32(b, e + 4);
                it.DataOffset = dataOffset + U32(b, e + 8);
                it.CompSize   = (int)U32(b, e + 12);
                it.DecompSize = (int)U32(b, e + 16);
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
                if (r <= 0) throw new Exception("unexpected EOF in archive");
                got += r;
            }
            if (e.CompFlag == 0) return raw;
            // zlib: пропускаем 2-байтовый заголовок (78 xx), остальное — deflate
            using (var ms = new MemoryStream(raw, 2, raw.Length - 2))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream(e.DecompSize)) {
                ds.CopyTo(outMs);
                return outMs.ToArray();
            }
        }
    }
}
"@

$Archive = (Resolve-Path $Archive).Path
$entries = [SgaV2]::ReadToc($Archive)
Write-Host ("Архив: {0} | файлов в TOC: {1}" -f (Split-Path $Archive -Leaf), $entries.Count) -ForegroundColor Cyan

$matched = @($entries | Where-Object { $_.Path -like $Mask })
Write-Host ("По маске '{0}': {1}" -f $Mask, $matched.Count) -ForegroundColor Cyan

if ($List -or -not $OutDir) {
    $matched | ForEach-Object {
        "{0,10}  {1}" -f $_.DecompSize, $_.Path
    }
    if (-not $List -and -not $OutDir) {
        Write-Host "`nУкажите -OutDir для распаковки." -ForegroundColor Yellow
    }
    exit 0
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$n = 0
foreach ($e in $matched) {
    $dest = Join-Path $OutDir ($e.Path -replace '/', '\')
    $destDir = Split-Path $dest -Parent
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
    [IO.File]::WriteAllBytes($dest, [SgaV2]::ReadFileData($Archive, $e))
    $n++
}
Write-Host "Распаковано файлов: $n -> $OutDir" -ForegroundColor Green
