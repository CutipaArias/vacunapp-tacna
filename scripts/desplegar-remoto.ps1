<#
.SYNOPSIS
    Carga la base VacunApp Tacna en un SQL Server remoto (p. ej. MonsterASP.NET) que ya tiene la base creada.
.DESCRIPTION
    Pide servidor, base, usuario y contraseña (la contraseña con Read-Host -AsSecureString; nunca se guarda,
    no se imprime ni se pasa por la línea de comandos: viaja en SQLCMDPASSWORD solo durante la ejecución).
    Ejecuta los mismos scripts y en el mismo orden que desplegar.ps1. Como en un hosting compartido no se puede
    crear ni borrar bases, se omite el bloque DROP/CREATE DATABASE de 01_esquema.sql y se trabaja sobre la base
    indicada. Al final deja el modelo de recuperación en SIMPLE y encoge el archivo de registro (límite de 1 GB).
    Si la base ya contiene el esquema [vac] se detiene sin tocar nada.
.EXAMPLE
    ./scripts/desplegar-remoto.ps1                # servidor tipo db71556.public.databaseasp.net
    ./scripts/desplegar-remoto.ps1 -Pruebas       # además ejecuta 07_pruebas.sql
#>
param(
    [switch]$Pruebas,
    [string]$Servidor,
    [string]$Base,
    [string]$Usuario,
    [securestring]$Clave   # opcional: si falta, se pide con Read-Host -AsSecureString
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dirBd = Join-Path $repo 'database'

if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    throw 'No se encontró sqlcmd. Instale las herramientas de línea de comandos de SQL Server.'
}

$servidor = if ($Servidor) { $Servidor.Trim() } else { (Read-Host 'Servidor (p. ej. db71556.public.databaseasp.net)').Trim() }
$base     = if ($Base) { $Base.Trim() } else { (Read-Host 'Base de datos').Trim() }
$usuario  = if ($Usuario) { $Usuario.Trim() } else { (Read-Host 'Usuario').Trim() }
$segura   = if ($Clave) { $Clave } else { Read-Host 'Contraseña' -AsSecureString }
if (-not $servidor -or -not $base -or -not $usuario -or $segura.Length -eq 0) { throw 'Faltan datos de conexión.' }
# El nombre de la base se inserta en "USE [..]": solo caracteres seguros.
if ($base -notmatch '^[A-Za-z0-9_\-\.]+$') { throw 'Nombre de base no válido (use letras, números, _, - o .).' }

# Orden idéntico a desplegar.ps1 (07_pruebas va al final: verifica también 08 y 09).
$scripts = '01_esquema', '02_catalogos', '03_vistas', '04_procedimientos', '05_triggers', '06_datos_prueba', '08_seguridad', '09_stock', '10_agenda', '11_estadisticas'
if ($Pruebas) { $scripts += '07_pruebas' }

$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($segura)
$temporal = $null
try {
    $env:SQLCMDPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)

    function Invoke-Sql([string]$Consulta, [string]$BaseDatos = $base) {
        sqlcmd -S $servidor -d $BaseDatos -U $usuario -N -C -b -I -f 65001 -W -h -1 -Q $Consulta
        if ($LASTEXITCODE -ne 0) { throw "Falló la consulta en $servidor." }
    }

    Write-Host "> Probando conexión con $servidor / $base ..."
    Invoke-Sql 'SET NOCOUNT ON; SELECT DB_NAME();' | Out-Null

    $existe = sqlcmd -S $servidor -d $base -U $usuario -N -C -b -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.schemas WHERE name = N'vac';"
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo consultar la base.' }
    if (($existe | Select-Object -First 1).Trim() -ne '0') {
        throw "La base '$base' ya contiene el esquema [vac]. Vacíela desde el panel del hosting y repita; este script no borra nada."
    }

    $colacion = sqlcmd -S $servidor -d $base -U $usuario -N -C -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS NVARCHAR(100));"
    Write-Host "> Intercalación de la base: $(($colacion | Select-Object -First 1).Trim()) (el proyecto usa Modern_Spanish_CI_AS)"

    $temporal = Join-Path ([IO.Path]::GetTempPath()) ("vacunapp-" + [Guid]::NewGuid().ToString('N') + '.sql')
    $utf8 = New-Object Text.UTF8Encoding($false)

    foreach ($s in $scripts) {
        Write-Host "> Ejecutando $s.sql"
        $sql = [IO.File]::ReadAllText((Join-Path $dirBd "$s.sql"), $utf8)
        if ($s -eq '01_esquema') {
            # Quita desde "USE master;" hasta el USE de la base (DROP/CREATE DATABASE no se permite en el hosting).
            $sql = [regex]::Replace($sql, '(?s)USE master;\s*GO.*?CREATE DATABASE VacunAppTacna[^;]*;\s*GO\s*', '')
            if ($sql -match 'DROP DATABASE|CREATE DATABASE') { throw '01_esquema.sql cambió: revise el recorte del bloque DROP/CREATE DATABASE.' }
        }
        $sql = $sql -replace 'USE VacunAppTacna;', "USE [$base];"
        [IO.File]::WriteAllText($temporal, $sql, $utf8)
        sqlcmd -S $servidor -d $base -U $usuario -N -C -b -I -f 65001 -W -i $temporal
        if ($LASTEXITCODE -ne 0) { throw "Falló $s.sql" }
    }

    Write-Host '> Modelo de recuperación SIMPLE y reducción del registro de transacciones...'
    $ajuste = @"
ALTER DATABASE [$base] SET RECOVERY SIMPLE;
DECLARE @log SYSNAME = (SELECT TOP (1) name FROM sys.database_files WHERE type = 1);
IF @log IS NOT NULL DBCC SHRINKFILE (@log, 1);
"@
    Invoke-Sql $ajuste

    Write-Host '> Tamaño final de la base:'
    Invoke-Sql "SET NOCOUNT ON; SELECT type_desc, CAST(size * 8 / 1024.0 AS DECIMAL(10,1)) AS MB FROM sys.database_files;"
    Write-Host "> Base '$base' lista en $servidor. Cambie las contraseñas semilla desde la aplicación antes de compartir el enlace."
}
finally {
    Remove-Item Env:\SQLCMDPASSWORD -ErrorAction SilentlyContinue
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ($temporal -and (Test-Path $temporal)) { Remove-Item $temporal -Force }
}
