<#
.SYNOPSIS
    Levanta SQL Server en Docker y crea la base VacunAppTacna desde cero.
.EXAMPLE
    ./desplegar.ps1            # esquema + datos de prueba
    ./desplegar.ps1 -Pruebas   # además ejecuta 07_pruebas.sql
#>
param(
    [switch]$Pruebas,
    [string]$Password = $env:MSSQL_SA_PASSWORD
)
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

# La contraseña no vive en el código: viene de -Password, de la variable de entorno o de .env.
if (-not $Password -and (Test-Path .env)) {
    $linea = Get-Content .env | Where-Object { $_ -match '^\s*MSSQL_SA_PASSWORD\s*=' } | Select-Object -First 1
    if ($linea) { $Password = ($linea -replace '^\s*MSSQL_SA_PASSWORD\s*=\s*', '').Trim().Trim('"') }
}
if (-not $Password) { throw 'Defina MSSQL_SA_PASSWORD (variable de entorno o archivo .env; vea .env.example).' }

Write-Host '> Iniciando contenedor vacunapp-sql...'
docker compose up -d 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No se pudo iniciar Docker Compose.' }

Write-Host '> Esperando a que SQL Server acepte conexiones...'
$listo = $false
for ($i = 0; $i -lt 30; $i++) {
    $estado = docker inspect -f '{{.State.Health.Status}}' vacunapp-sql
    if ($estado -eq 'healthy') { $listo = $true; break }
    Start-Sleep -Seconds 5
}
if (-not $listo) { throw 'SQL Server no respondió a tiempo.' }

$scripts = '01_esquema', '02_catalogos', '03_vistas', '04_procedimientos', '05_triggers', '06_datos_prueba'
if ($Pruebas) { $scripts += '07_pruebas' }

foreach ($s in $scripts) {
    Write-Host "> Ejecutando $s.sql"
    docker exec vacunapp-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $Password -C -b -I -f 65001 -W -i "/scripts/$s.sql"
    if ($LASTEXITCODE -ne 0) { throw "Falló $s.sql" }
}
Write-Host '> Base de datos VacunAppTacna lista en localhost,1433'
