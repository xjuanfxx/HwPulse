# Genera Assets/HwPulse.ico (16–256 px) dibujando el logo: cuadrado redondeado oscuro con la línea de pulso.
# Uso: pwsh -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot "..\Assets\HwPulse.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Fondo: cuadrado redondeado #111111 con borde sutil
    $r = [single]($s * 0.22); $pad = [single]([Math]::Max(0.5, $s * 0.03)); $w = [single]($s - 2 * $pad)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 17, 17, 17))), $path)
    if ($s -ge 32) { $g.DrawPath((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 48, 48, 48)), ([single]($s / 64))), $path) }

    # Línea de pulso (#5B9BD5); más gruesa en tamaños chicos para que se lea
    $thick = if ($s -le 24) { $s * 0.13 } else { $s * 0.085 }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 91, 155, 213)), ([single]$thick)
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $u = $s / 16.0
    $pts = @(
        (New-Object System.Drawing.PointF ([single](2.6 * $u)), ([single](8.6 * $u))),
        (New-Object System.Drawing.PointF ([single](5.2 * $u)), ([single](8.6 * $u))),
        (New-Object System.Drawing.PointF ([single](6.9 * $u)), ([single](4.2 * $u))),
        (New-Object System.Drawing.PointF ([single](9.3 * $u)), ([single](12.0 * $u))),
        (New-Object System.Drawing.PointF ([single](10.9 * $u)), ([single](8.6 * $u))),
        (New-Object System.Drawing.PointF ([single](13.4 * $u)), ([single](8.6 * $u)))
    )
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # 256 px en PNG (ocupa poco y Windows lo admite a ese tamaño)
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Tamaños pequeños como DIB de 32 bits (BMP sin cabecera de archivo): es lo que esperan el
        # Explorador, la barra de tareas y los accesos directos; con PNG algunos muestran el icono genérico.
        $w = New-Object System.IO.BinaryWriter $ms
        $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2))   # alto doble: imagen + máscara
        $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0); $w.Write([uint32]0)
        $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
        $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $row = New-Object byte[] ($s * 4)
        for ($y = $s - 1; $y -ge 0; $y--) {   # de abajo arriba
            [System.Runtime.InteropServices.Marshal]::Copy([IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $s * 4)
            $w.Write($row)
        }
        $bmp.UnlockBits($data)
        $maskRow = [int]([math]::Ceiling($s / 32.0) * 4)                  # máscara AND vacía (manda el canal alfa)
        $w.Write((New-Object byte[] ($maskRow * $s)))
        $w.Flush()
    }
    $pngs += , $ms.ToArray()
    if ($s -eq 256) { $bmp.Save((Join-Path (Split-Path $out) "HwPulse-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}

# Empaquetar como ICO: DIB hasta 128 px y PNG en 256 px
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
"Icono generado: $out"
