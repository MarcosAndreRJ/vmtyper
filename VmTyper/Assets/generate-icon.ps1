# Gera Assets\app.ico (teclado branco sobre quadrado azul) com GDI+. Uso: powershell -File generate-icon.ps1
# Cada tamanho é desenhado direto na resolução final (não é redução de uma imagem grande), e o .ico guarda PNGs.
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 256
$outPath = Join-Path $PSScriptRoot 'app.ico'

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $blue = [System.Drawing.Color]::FromArgb(255, 47, 111, 235)   # AccentFill (#2F6FEB)
    $white = [System.Drawing.Color]::White
    $blueBrush = New-Object System.Drawing.SolidBrush $blue
    $whiteBrush = New-Object System.Drawing.SolidBrush $white

    # Fundo: quadrado arredondado
    $bg = New-RoundedRect 0 0 $s $s ($s * 0.22)
    $g.FillPath($blueBrush, $bg)

    # Corpo do teclado
    $bx = $s * 0.14; $by = $s * 0.29; $bw = $s * 0.72; $bh = $s * 0.42
    $body = New-RoundedRect $bx $by $bw $bh ($s * 0.07)
    $g.FillPath($whiteBrush, $body)

    # Teclas (recortes azuis): 2 fileiras de 4 + barra de espaço
    $pad = $s * 0.05
    $gap = [Math]::Max(1.0, $s * 0.022)
    $areaX = $bx + $pad; $areaW = $bw - 2 * $pad
    $areaY = $by + $pad; $areaH = $bh - 2 * $pad
    $rowH = ($areaH - 2 * $gap) / 3
    $keyW = ($areaW - 3 * $gap) / 4
    $kr = [Math]::Max(0.5, $s * 0.015)
    for ($row = 0; $row -lt 2; $row++) {
        $y = $areaY + $row * ($rowH + $gap)
        for ($col = 0; $col -lt 4; $col++) {
            $x = $areaX + $col * ($keyW + $gap)
            $g.FillPath($blueBrush, (New-RoundedRect $x $y $keyW $rowH $kr))
        }
    }
    $spaceW = $areaW * 0.56
    $g.FillPath($blueBrush, (New-RoundedRect ($areaX + ($areaW - $spaceW) / 2) ($areaY + 2 * ($rowH + $gap)) $spaceW $rowH $kr))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$images = foreach ($s in $sizes) { , (New-IconPng $s) }

$stream = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
[System.IO.File]::WriteAllBytes($outPath, $stream.ToArray())
Write-Output "Gerado: $outPath ($($stream.Length) bytes, tamanhos: $($sizes -join ', '))"
