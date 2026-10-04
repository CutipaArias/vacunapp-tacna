<#
.SYNOPSIS
    Genera el paquete de la aplicación para subirlo al hosting (dotnet publish en Release, solo a una carpeta).
.DESCRIPTION
    No se conecta a ningún servidor ni sube nada: deja los archivos en una carpeta (por defecto publicar\panel dentro del
    repositorio, ignorada por git) y, con -Zip, un ZIP con los archivos en la raíz para el administrador de archivos del
    hosting. El paquete es dependiente del marco (el hosting aporta .NET 10).

    Después de publicar revisa el paquete y falla si lleva configuración local (appsettings.Development*.json,
    appsettings.Production*.json, plantillas *.example.json), .env, .pfx, o un appsettings/web.config con claves.

    Limpieza: la carpeta de salida por defecto (publicar\...) se vacía antes de publicar para no arrastrar archivos de una
    versión anterior. Una carpeta indicada con -Salida fuera de publicar\ debe no existir o estar vacía; este script no borra
    carpetas ajenas.
.EXAMPLE
    ./scripts/publicar.ps1 -Zip      # publicar\panel y publicar\panel.zip
    ./scripts/publicar.ps1 -Salida D:\paquete\panel
#>
[CmdletBinding()]
param(
    [string]$Salida,
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$raizPublicar = [IO.Path]::GetFullPath((Join-Path $repo 'publicar'))
if (-not $Salida) { $Salida = Join-Path $raizPublicar 'panel' }
$Salida = [IO.Path]::GetFullPath($Salida).TrimEnd('\')

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'No se encontró dotnet. Instale el SDK de .NET 10.' }

# Carpeta de salida: solo se vacía si está dentro de publicar\ (y un nivel por debajo); en otro lugar debe estar libre.
$propia = $Salida.StartsWith($raizPublicar + '\', [StringComparison]::OrdinalIgnoreCase)
if (Test-Path $Salida) {
    $contenido = @(Get-ChildItem $Salida -Force)
    if ($contenido.Count -gt 0) {
        if (-not $propia) { throw "La carpeta '$Salida' ya tiene archivos. Indique una carpeta nueva o vacía; este script solo limpia la carpeta publicar\ del repositorio." }
        [IO.Directory]::Delete($Salida, $true)
    }
}

Write-Host "> Publicando (Release) en $Salida"
dotnet publish (Join-Path $repo 'panel\VacunApp.Panel.csproj') -c Release -o $Salida --self-contained false --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish falló.' }

# Revisión del paquete: nada de configuración local ni de claves.
$archivos = @(Get-ChildItem $Salida -Recurse -File)
$prohibidos = @($archivos | Where-Object {
        $_.Name -match '^appsettings\.(Development|Production)' -or $_.Name -match '\.example\.json$' -or
        $_.Name -eq '.env' -or $_.Extension -in '.pfx', '.pdb' })
if ($prohibidos.Count -gt 0) { throw "El paquete no debe incluir: $(($prohibidos | ForEach-Object { $_.Name }) -join ', ')" }
$conClave = @($archivos | Where-Object { $_.Extension -in '.json', '.config' } |
        Where-Object { (Get-Content $_.FullName -Raw) -match '(?i)password\s*[=:]\s*["'']?[^"''<\s;]{6,}' })
if ($conClave.Count -gt 0) { throw "Estos archivos del paquete contienen una clave: $(($conClave | ForEach-Object { $_.Name }) -join ', ')" }
foreach ($esperado in 'VacunApp.Panel.dll', 'web.config', 'appsettings.json', 'wwwroot\index.html', 'wwwroot\login.html') {
    if (-not (Test-Path (Join-Path $Salida $esperado))) { throw "Falta $esperado en el paquete." }
}

$tamano = [Math]::Round((($archivos | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "> Paquete listo: $($archivos.Count) archivos, $tamano MB, sin configuración local ni claves."

if ($Zip) {
    $rutaZip = "$Salida.zip"
    if (Test-Path $rutaZip) { [IO.File]::Delete($rutaZip) }
    # Se arma entrada por entrada: ZipFile.CreateFromDirectory de Windows PowerShell 5.1 escribe las rutas con "\" y no todos
    # los descompresores del hosting las reconocen como carpetas.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $flujo = [IO.File]::Create($rutaZip)
    try {
        $z = New-Object IO.Compression.ZipArchive($flujo, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($f in $archivos) {
                $entrada = $f.FullName.Substring($Salida.Length + 1).Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $entrada, [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $z.Dispose() }
    } finally { $flujo.Dispose() }
    Write-Host "> ZIP: $rutaZip ($([Math]::Round((Get-Item $rutaZip).Length / 1MB, 1)) MB). Súbalo y descomprímalo en /wwwroot (docs\despliegue-monsterasp.md)."
}
