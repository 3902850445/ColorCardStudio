# Convert Pantone CSV (Lab D50 values) to JSON with sRGB HEX
# Source: unofficial-pantone-solid-coated-2024-v5 (3219 colors)
# RED/GREEN/BLUE columns are actually L*, a*, b* Lab components

param(
    [string]$CsvPath = (Join-Path $env:TEMP "pantone_colors.csv"),
    [string]$OutPath = ""
)

if (-not $OutPath) {
    $OutPath = Join-Path (Split-Path $MyInvocation.MyCommand.Path "..") "wwwroot\data\pantone.json"
}

# --- Lab (D50) -> sRGB conversion (all inline, no nested function calls) ---
function ConvertFrom-LabD50 {
    param([double]$L, [double]$a, [double]$b)
    
    # CIELab -> XYZ (D50)
    $fx = ($L + 16.0) / 116.0 + $a / 500.0
    $fy = ($L + 16.0) / 116.0
    $fz = ($L + 16.0) / 116.0 - $b / 200.0
    
    $t = 6.0 / 29.0
    $fourOver116 = 4.0 / 116.0
    $threeTsq = 3.0 * $t * $t
    
    # Cube function inline
    if ($fx -gt $t) { $x = $fx * $fx * $fx } else { $x = $threeTsq * ($fx - $fourOver116) }
    if ($fy -gt $t) { $y = $fy * $fy * $fy } else { $y = $threeTsq * ($fy - $fourOver116) }
    if ($fz -gt $t) { $z = $fz * $fz * $fz } else { $z = $threeTsq * ($fz - $fourOver116) }
    
    # D50 -> D65 Bradford chromatic adaptation
    $X = 1.0479 * $x + 0.0229 * $y - 0.0501 * $z
    $Y = 0.0296 * $x + 0.9904 * $y - 0.0170 * $z
    $Z = -0.0092 * $x + 0.0150 * $y + 0.7519 * $z
    
    # XYZ (D65) -> linear sRGB
    $r_lin = $X * 3.2406 - $Y * 1.5372 - $Z * 0.4986
    $g_lin = -$X * 0.9689 + $Y * 1.8758 + $Z * 0.0415
    $b_lin = $X * 0.0557 - $Y * 0.2040 + $Z * 1.0570
    
    # Linear -> sRGB (gamma decode), clamp, scale
    $r_lin = if ($r_lin -lt 0) { 0 } elseif ($r_lin -gt 1) { 1 } else { $r_lin }
    $g_lin = if ($g_lin -lt 0) { 0 } elseif ($g_lin -gt 1) { 1 } else { $g_lin }
    $b_lin = if ($b_lin -lt 0) { 0 } elseif ($b_lin -gt 1) { 1 } else { $b_lin }
    
    if ($r_lin -le 0.0031308) { $r_out = 12.92 * $r_lin } else { $r_out = 1.055 * [Math]::Pow($r_lin, 1.0 / 2.4) - 0.055 }
    if ($g_lin -le 0.0031308) { $g_out = 12.92 * $g_lin } else { $g_out = 1.055 * [Math]::Pow($g_lin, 1.0 / 2.4) - 0.055 }
    if ($b_lin -le 0.0031308) { $b_out = 12.92 * $b_lin } else { $b_out = 1.055 * [Math]::Pow($b_lin, 1.0 / 2.4) - 0.055 }
    
    # Clamp 0-1 and scale to 0-255
    $r_out = if ($r_out -lt 0) { 0 } elseif ($r_out -gt 1) { 1 } else { $r_out }
    $g_out = if ($g_out -lt 0) { 0 } elseif ($g_out -gt 1) { 1 } else { $g_out }
    $b_out = if ($b_out -lt 0) { 0 } elseif ($b_out -gt 1) { 1 } else { $b_out }
    
    $r = [int][Math]::Round($r_out * 255.0)
    $g = [int][Math]::Round($g_out * 255.0)
    $bb = [int][Math]::Round($b_out * 255.0)
    
    return "#" + $r.ToString("x2") + $g.ToString("x2") + $bb.ToString("x2")
}

# --- Read CSV and convert ---
$lines = Get-Content $CsvPath
$count = $lines.Count - 1

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('{"name":"PANTONE Solid Coated 2024","source":"https://github.com/Pradeepvina/unofficial-pantone-solid-coated-2024-v5","count":' + $count + ',"colors":[')

$first = $true
$processed = 0

for ($i = 1; $i -lt $lines.Count; $i++) {
    $line = $lines[$i].Trim()
    if (-not $line) { continue }
    
    $parts = $line -split ','
    if ($parts.Count -lt 5) { continue }
    
    $name = $parts[0].Trim()
    $code = $parts[1].Trim()
    $l = [double]$parts[2]
    $a = [double]$parts[3]
    $b = [double]$parts[4]
    
    $hex = ConvertFrom-LabD50 $l $a $b
    
    if (-not $first) { [void]$sb.Append(',') }
    $first = $false
    
    $escName = $name -replace '"', '\"'
    $escCode = $code -replace '"', '\"'
    
    [void]$sb.Append('{"name":"' + $escName + '","code":"' + $escCode + '","hex":"' + $hex.ToLower() + '","lab":[' + $l + ',' + $a + ',' + $b + ']}')
    
    $processed++
    if ($processed % 500 -eq 0) {
        Write-Output "  Processed $processed / $count"
    }
}

[void]$sb.Append(']}')

$dir = Split-Path $OutPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

[System.IO.File]::WriteAllText($OutPath, $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Output "Generated $OutPath ($($sb.Length) bytes, $processed colors)"
